using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Brutal.VulkanApi;
using RimLumen.Core.Contracts;
using RimLumen.Core.ParticlesGpu;
using RimLumen.Core.Rendering;
using RimLumen.Core.Scene;
using Verse;
using Waylith;
using Waylith.Graphics;
using Waylith.Rendering;
using Waylith.UnityCompat;

namespace RimLumen.Core.Showcase;

// Real-device L2 GPU particle ladder. Tiers, warmup, the 60 measured frames per tier, the workload asset
// and the statistics are fixed so a run stays comparable with earlier results under .scratch/bench-results.
// Headless only: entered from RimLumenStartup's #if !RIMLUMEN_SHIP_RELEASE block through
// "-rimlumen-showcase-bench=<jsonPath>" and driven by Tools/launch-showcase.ps1 -Benchmark.
internal static class ShowcaseBenchmarkSession
{
    private const string OutputPrefix = "-rimlumen-showcase-bench=";
    private static readonly int[] Tiers = { 0, 10_000, 50_000, 100_000, 250_000, 500_000 };

    // Many-emitter tiers run after the ladder, so the ladder's own numbers stay comparable. Every emitter is the
    // ladder workload at ParticlesPerEmitter, stacked on the ladder's origin: the total matches Bench-500k and
    // only the emitter count differs. Game-time emitters stand still for the measured frames (ticks are paused).
    private const int ParticlesPerEmitter = 1_000;
    private static readonly (string Id, int RealTime, int GameTime)[] EmitterTiers =
    {
        ("Bench-500x1k", 500, 0),
        ("Bench-Paused-250rt+250gt", 250, 250),
    };

    // Ensures: true iff args carries "-rimlumen-showcase-bench=<jsonPath>" (key case-insensitive, one
    //   optional pair of surrounding quotes on the value); jsonPath is empty when it returns false.
    internal static bool TryParseOutputPath(IReadOnlyList<string> args, out string jsonPath)
    {
        jsonPath = string.Empty;
        if (args is null) return false;
        foreach (string arg in args)
        {
            if (arg is null || !arg.StartsWith(OutputPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            string raw = arg.Substring(OutputPrefix.Length);
            if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
                raw = raw.Substring(1, raw.Length - 2);
            if (raw.Length == 0) return false;
            jsonPath = raw;
            return true;
        }
        return false;
    }

    // Requires: main thread; the game is playing the map that was just loaded; jsonPath is writable.
    // Ensures: freezes the simulation, starts the ladder and returns immediately; the coroutine writes
    //   the JSON and shuts the game down with exit code 0 on success or 1 on failure. Never throws: a
    //   failure that would otherwise abort the coroutine is logged and still ends the session.
    internal static void Run(Map map, string jsonPath)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(map);
            ArgumentException.ThrowIfNullOrEmpty(jsonPath);
            Current.CameraDriver.StartCoroutine(RunLadder(map, jsonPath));
        }
        catch (Exception ex)
        {
            Log.Error("[RimLumen] Showcase benchmark could not start: " + ex);
            Shutdown(exitCode: 1);
        }
    }

    private static IEnumerator RunLadder(Map map, string jsonPath)
    {
        var timer = Stopwatch.StartNew();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var steps = new List<object>();
        EngineApplication app = EngineApplication.Instance;
        SessionState? session = null;
        IEnumerator? ladder = null;

        try
        {
            session = SessionState.Capture(app);
            session.Apply();
            ladder = RunTiers(map, steps);
        }
        catch (Exception ex)
        {
            Log.Error("[RimLumen] Showcase benchmark setup failed: " + ex);
        }

        // A coroutine cannot catch around a yield, so its failures are caught at the MoveNext boundary.
        bool completed = false;
        if (ladder is not null)
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = ladder.MoveNext();
                }
                catch (Exception ex)
                {
                    // The ladder's own finally already released its workload and catalog entries.
                    Log.Error("[RimLumen] Showcase benchmark failed: " + ex);
                    break;
                }
                if (!moved)
                {
                    completed = true;
                    break;
                }
                yield return ladder.Current;
            }
        }

        if (session is not null)
        {
            try
            {
                session.Restore();
            }
            catch (Exception ex)
            {
                Log.Error("[RimLumen] Showcase benchmark cleanup failed: " + ex);
                completed = false;
            }
        }

        if (!completed)
        {
            Shutdown(exitCode: 1);
            yield break;
        }

        // Serialized before the path is touched, so a throwing step never leaves a partial result behind.
        int exitCode = 1;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath)) ?? ".");
            File.WriteAllText(jsonPath, SerializeResult(started, Math.Round(timer.Elapsed.TotalSeconds, 3), steps));
            Log.Message($"[RimLumen] Showcase benchmark finished in {timer.Elapsed.TotalSeconds:F2}s -> {jsonPath}");
            exitCode = 0;
        }
        catch (Exception ex)
        {
            Log.Error("[RimLumen] Showcase benchmark could not write its result: " + ex);
        }

        Shutdown(exitCode);
    }

    // Measured frames carry no game ticks, camera input or UI: paused ticks, camera driver off,
    // screenshot mode on, cleared fader, windows opted out of screenshot mode.
    private sealed class SessionState
    {
        private readonly EngineApplication app;
        private readonly TickManager tickManager;
        private readonly TimeSpeed savedTimeSpeed;
        private readonly bool savedCameraDriver;
        private readonly bool savedScreenshotMode;
        private readonly VkPresentModeKHR savedPresentMode;
        private readonly Dictionary<Window, bool> windowVisibility = new();

        private SessionState(EngineApplication app)
        {
            this.app = app;
            tickManager = Find.TickManager;
            savedTimeSpeed = tickManager.CurTimeSpeed;
            savedCameraDriver = Current.CameraDriver.enabled;
            savedScreenshotMode = Find.UIRoot.screenshotMode.Active;
            savedPresentMode = app.RenderContext!.PresentMode;
        }

        internal static SessionState Capture(EngineApplication app) => new(app);

        internal void Apply()
        {
            tickManager.Pause();
            Current.CameraDriver.enabled = false;
            Find.UIRoot.screenshotMode.Active = true;
            ScreenFader.SetColor(UnityEngine.Color.clear);
            // FIFO present paces every tier to the monitor refresh rate; launch-showcase.ps1 lifts the engine cap with -TargetFrameRate=0.
            EngineWindow.SetVSyncMode(VSyncMode.Off);
            app.OnUpdate += PrepareDeterministicFrame;
            PrepareDeterministicFrame();
        }

        private void PrepareDeterministicFrame()
        {
            // ScreenshotMode hides letters/messages/alerts; windows opt out individually.
            foreach (Window window in Find.WindowStack.Windows)
            {
                if (!windowVisibility.ContainsKey(window)) windowVisibility.Add(window, window.drawInScreenshotMode);
                window.drawInScreenshotMode = false;
            }
        }

        internal void Restore()
        {
            app.OnUpdate -= PrepareDeterministicFrame;
            EngineWindow.SetVSyncMode(savedPresentMode switch
            {
                VkPresentModeKHR.ImmediateKHR => VSyncMode.Off,
                VkPresentModeKHR.MailboxKHR => VSyncMode.TripleBuffer,
                _ => VSyncMode.VSync,
            });
            Find.UIRoot.screenshotMode.Active = savedScreenshotMode;
            foreach (KeyValuePair<Window, bool> entry in windowVisibility)
                entry.Key.drawInScreenshotMode = entry.Value;
            Current.CameraDriver.enabled = savedCameraDriver;
            tickManager.CurTimeSpeed = savedTimeSpeed;
            app.WaitForRenderThreadIdle();
            app.GraphicsDevice!.WaitIdleAndRun(() => { });
        }
    }

    // Ensures: plays each tier for 60 measured frames after warmup and appends its statistics to steps;
    //   always stops the workload and unregisters the tier's asset.
    private static IEnumerator RunTiers(Map map, List<object> steps)
    {
        var gpu = LumenHostRegistry.GpuParticleEngine as LumenGpuParticleEngine
            ?? throw new InvalidOperationException("Native GPU engine was not bootstrapped.");
        IntVec3 cell = Current.CameraDriver.MapPosition;
        if (!cell.InBounds(map)) throw new InvalidOperationException("Camera centre is outside the map.");
        Vector3 origin = new(cell.x + 0.5f, 0f, cell.z + 0.5f);
        int mapId = map.uniqueID;

        // The present-mode switch rebuilds the swapchain a few frames later; keep that out of the first tier.
        for (int i = 0; i < 30; i++) yield return new UnityEngine.WaitForEndOfFrame();

        foreach (int targetCount in Tiers)
        {
            string id = targetCount == 0 ? "Bench-0-Idle" : $"Bench-{targetCount / 1000}k";
            if (targetCount > 0) RegisterWorkload(id, targetCount);
            ILumenEffectHandle? workload = null;
            try
            {
                if (targetCount > 0)
                {
                    // RealTime keeps the ladder advancing on the paused engine clock (LumenPlayRequest.RealTime).
                    workload = LumenSceneBootstrap.Service.Play(new LumenPlayRequest
                    {
                        EffectId = id, MapIndex = mapId, OriginWorld = origin, RealTime = true
                    });
                    if (!workload.IsAlive) throw new InvalidOperationException("Benchmark workload failed to start: " + id);
                }
                int warmup = targetCount == 0 ? 15 : 8;
                for (int i = 0; i < warmup; i++) yield return new UnityEngine.WaitForEndOfFrame();

                var frameMs = new List<double>(60);
                for (int i = 0; i < 60; i++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    yield return new UnityEngine.WaitForEndOfFrame();
                    frameMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                }

                // Read live particles only after the measured frames, so the readback never enters the frame times.
                int live = targetCount == 0 ? 0 : gpu.CountLiveParticles();
                (double meanMs, double p95Ms, double maxMs) = Summarize(frameMs);
                steps.Add(new
                {
                    Id = id,
                    TargetParticles = targetCount,
                    LiveParticles = live,
                    MeasuredFrames = frameMs.Count,
                    FrameMeanMs = Math.Round(meanMs, 3),
                    FrameP95Ms = Math.Round(p95Ms, 3),
                    FrameMaxMs = Math.Round(maxMs, 3),
                    ApproxFps = Math.Round(1000.0 / Math.Max(0.001, meanMs), 1),
                });

                workload?.Stop(allowFadeOut: false);
                workload = null;
                yield return new UnityEngine.WaitForEndOfFrame();
                yield return new UnityEngine.WaitForEndOfFrame();
            }
            finally
            {
                workload?.Stop(allowFadeOut: false);
                if (targetCount > 0) LumenSceneBootstrap.Catalog.Unregister(id);
            }
        }

        TickManager tickManager = Find.TickManager;
        foreach ((string id, int realTime, int gameTime) in EmitterTiers)
        {
            RegisterWorkload(id, ParticlesPerEmitter);
            var workloads = new List<ILumenEffectHandle>(realTime + gameTime);
            try
            {
                for (int i = 0; i < realTime + gameTime; i++)
                {
                    ILumenEffectHandle handle = LumenSceneBootstrap.Service.Play(new LumenPlayRequest
                    {
                        EffectId = id, MapIndex = mapId, OriginWorld = origin, RealTime = i < realTime
                    });
                    if (!handle.IsAlive) throw new InvalidOperationException($"Benchmark workload failed to start: {id} #{i}");
                    workloads.Add(handle);
                }
                if (gameTime > 0)
                {
                    // Game-time emitters only spawn while ticks run: fill them, then pause again before measuring.
                    tickManager.CurTimeSpeed = TimeSpeed.Normal;
                    for (int i = 0; i < 10; i++) yield return new UnityEngine.WaitForEndOfFrame();
                    tickManager.Pause();
                }
                for (int i = 0; i < 8; i++) yield return new UnityEngine.WaitForEndOfFrame();

                var frameMs = new List<double>(60);
                for (int i = 0; i < 60; i++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    yield return new UnityEngine.WaitForEndOfFrame();
                    frameMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                }

                int live = gpu.CountLiveParticles();
                (double meanMs, double p95Ms, double maxMs) = Summarize(frameMs);
                steps.Add(new
                {
                    Id = id,
                    TargetParticles = (realTime + gameTime) * ParticlesPerEmitter,
                    LiveParticles = live,
                    RealTimeEmitters = realTime,
                    GameTimeEmitters = gameTime,
                    MeasuredFrames = frameMs.Count,
                    FrameMeanMs = Math.Round(meanMs, 3),
                    FrameP95Ms = Math.Round(p95Ms, 3),
                    FrameMaxMs = Math.Round(maxMs, 3),
                    ApproxFps = Math.Round(1000.0 / Math.Max(0.001, meanMs), 1),
                });
            }
            finally
            {
                tickManager.Pause();
                foreach (ILumenEffectHandle handle in workloads) handle.Stop(allowFadeOut: false);
                LumenSceneBootstrap.Catalog.Unregister(id);
            }
            yield return new UnityEngine.WaitForEndOfFrame();
            yield return new UnityEngine.WaitForEndOfFrame();
        }
        yield return new UnityEngine.WaitForEndOfFrame();
    }

    private static (double MeanMs, double P95Ms, double MaxMs) Summarize(List<double> frameMs)
    {
        double[] sorted = frameMs.OrderBy(v => v).ToArray();
        return (frameMs.Average(), sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.95) - 1)], sorted[^1]);
    }

    // Ensures: registers the L2 GPU workload asset; changing any field breaks comparability with earlier results.
    private static void RegisterWorkload(string id, int count)
    {
        LumenSceneBootstrap.Catalog.Register(new EffectAsset
        {
            EffectId = id, DefaultDuration = 120f,
            Nodes = new[]
            {
                new EffectNodeSpec
                {
                    Name = "particles", LocalPosition = new Vector3(0, AltitudeLayer.MoteOverhead.AltitudeFor(), 0),
                    Emitter = new EmitterSpec
                    {
                        Name = id, PreferredTier = LumenParticleTier.L2_GpuCompute, MaxParticles = count, Duration = 120f, Looping = true,
                        RateOverTime = count, WorldSpaceSimulation = false,
                        Bursts = new[] { new ParticleBurstSpec(0f, count) },
                        Shape = LumenShapeKind.Box, ShapeExtents = new Vector3(3f, 0.15f, 3f),
                        StartLifetimeMin = 120f, StartLifetimeMax = 120f, StartSizeMin = 0.15f, StartSizeMax = 0.3f,
                        StartSpeedMin = 0.1f, StartSpeedMax = 0.2f, StartColor = new Vector4(1f, 0.4f, 0.1f, 1f),
                        HdrIntensity = 4f, SoftParticleFadeDistance = 0.5f,
                        ForceFields = new[]
                        {
                            new ForceFieldSpec(ForceFieldKind.CurlTurbulence, Vector3.Zero, Vector3.Zero, 8f, 0.2f, 0.8f),
                            new ForceFieldSpec(ForceFieldKind.Vortex, Vector3.UnitY, Vector3.Zero, 8f, 0.2f),
                            new ForceFieldSpec(ForceFieldKind.PointAttractor, Vector3.Zero, Vector3.Zero, 8f, 0.1f)
                        }
                    }
                }
            }
        });
    }

    // Ensures: the baseline's top-level fields plus the pack id and density, so a run can be tied to the
    //   settings it measured; resolution comes from the live render extent.
    private static string SerializeResult(DateTimeOffset started, double durationSeconds, IReadOnlyList<object> steps)
    {
        EngineApplication app = EngineApplication.Instance;
        var device = app.GraphicsDevice!;
        var extent = app.RenderContext!.Extent;
        return JsonSerializer.Serialize(new
        {
            StartedUtc = started,
            DurationSeconds = durationSeconds,
            GpuName = UnityEngine.SystemInfo.graphicsDeviceName ?? "Unknown",
            ValidationEnabled = device.ValidationEnabled,
            InstanceEnabledLayers = device.InstanceEnabledLayers.ToArray(),
            VanillaAdditiveBloom = RimLumenRenderHost.EnableVanillaAdditiveBloom,
            Resolution = $"{extent.Width}x{extent.Height}",
            DefaultSlotCapacity = EmitterSlotAllocator.DefaultCapacity,
            ShaderPackId = RimLumenRenderHost.ActiveShaderPack.Id,
            ParticleDensityScale = RimLumenRenderHost.ParticleDensityScale,
            Steps = steps,
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void Shutdown(int exitCode)
    {
        if (UnityRuntime.Current is { } runtime)
            runtime.App.ExitCode = exitCode;
        try
        {
            Root.Shutdown();
        }
        catch (Exception ex)
        {
            // Quit hooks can throw; the exit code is already recorded, so at least report it instead of hanging silently.
            Log.Error("[RimLumen] Showcase benchmark shutdown failed: " + ex);
        }
    }
}
