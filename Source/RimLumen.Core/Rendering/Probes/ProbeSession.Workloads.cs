using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Lighting;
using RimLumen.Core.ParticlesGpu;
using RimLumen.Core.Rendering.Logic;
using RimLumen.Core.Scene;
using UnityStubs.Rendering;
using Verse;
using Waylith.Rendering;

namespace RimLumen.Core.Rendering.Probes;

internal sealed partial class ProbeSession
{
    private ILumenEffectHandle? workload;
    private UnityEngine.Material? compatMaterial;
    private UnityEngine.Vector3 compatPosition;

    private IEnumerator GpuWorkload(int count)
    {
        string id = "P2-L2-" + (count / 1000) + "k";
        var gpu = LumenHostRegistry.GpuParticleEngine as LumenGpuParticleEngine
            ?? throw new InvalidOperationException("Native GPU engine was not bootstrapped.");
        IntVec3 cell = Current.CameraDriver.MapPosition;
        if (!cell.InBounds(Find.CurrentMap)) throw new InvalidOperationException("Camera centre is outside the map.");
        Vector3 origin = new(cell.x + 0.5f, 0f, cell.z + 0.5f);
        yield return CaptureScreen(id + "-reference", 6);
        RegisterWorkload(id, count, LumenParticleTier.L2_GpuCompute, false);
        try
        {
            workload = LumenSceneBootstrap.Service.Play(new LumenPlayRequest
            { EffectId = id, MapIndex = Find.CurrentMap.uniqueID, OriginWorld = origin });
            if (!workload.IsAlive) throw new InvalidOperationException("Workload failed to play: " + id);
            for (int i = 0; i < 3; i++) yield return new UnityEngine.WaitForEndOfFrame();
            pipeline.ContributorMilliseconds.Clear();
            pipeline.MeasureContributors = true;
            var frameMs = new List<double>();
            for (int i = 0; i < 12; i++)
            {
                long start = Stopwatch.GetTimestamp();
                yield return new UnityEngine.WaitForEndOfFrame();
                frameMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            pipeline.MeasureContributors = false;
            var metrics = new Dictionary<string, double>
            {
                ["MaxParticles"] = count, ["measuredFrames"] = frameMs.Count,
                ["frameMeanMs"] = frameMs.Average(), ["frameMaxMs"] = frameMs.Max()
            };
            foreach (var pair in pipeline.ContributorMilliseconds)
            {
                metrics[pair.Key + ".captureEnqueueMeanMs"] = pair.Value.Average();
                metrics[pair.Key + ".captureEnqueueMaxMs"] = pair.Value.Max();
                metrics[pair.Key + ".samples"] = pair.Value.Count;
            }
            int live = gpu.CountLiveParticles();
            metrics["liveParticles"] = live;
            yield return CaptureScreen(id, 1);
            ReadWorkloadPixels(id, captures[id], metrics);
            workload.Stop(allowFadeOut: false);
            yield return new UnityEngine.WaitForEndOfFrame();
            bool drained = !gpu.HasActiveWork && !LumenSceneBootstrap.Service.HasActiveWork;
            metrics["drainedToIdle"] = drained ? 1 : 0;
            bool measured = pipeline.ContributorMilliseconds.TryGetValue(gpu.Name, out var samples) && samples.Count >= 10;
            checks.Add(new ProbeCheck(id, measured && drained && live == count && metrics["hdrEmissivePixels"] > 0 && metrics["bloomPixels"] > 0 && metrics["swapchainBrightenedPixels"] > 0,
                "3 warm-up + 12 measured EndOfFrame intervals; CPU capture/enqueue per contributor measured separately. Synchronization and image readbacks happen after timing. Actual live and MaxParticles recorded separately; no parameter retuning.", metrics));
        }
        finally
        {
            workload?.Stop(allowFadeOut: false);
            workload = null;
            LumenSceneBootstrap.Catalog.Unregister(id);
        }

    }

    private IEnumerator L1Workload()
    {
        yield return CaptureScreen("P2-L1-Glower-reference", 6);
        var driver = Find.CurrentMap.GetComponent<LumenMapLightingComponent>().Driver;
        int beforeActive = driver.ActiveLightCount, beforeRegister = driver.RegisterCallCount, beforeDeregister = driver.DeregisterCallCount;
        long vertices = pipeline.L1Renderer.VerticesRecorded;
        int draws = pipeline.L1Renderer.DrawsRecorded;
        var origin = new Vector3(Current.CameraDriver.MapPosition.x + 0.5f, 0f, Current.CameraDriver.MapPosition.z + 0.5f);
        RegisterWorkload("P2-L1-Glower", 128, LumenParticleTier.L1_NativeCpu, true);
        try
        {
            workload = LumenSceneBootstrap.Service.Play(new LumenPlayRequest
            { EffectId = "P2-L1-Glower", MapIndex = Find.CurrentMap.uniqueID, OriginWorld = origin });
            for (int i = 0; i < 12; i++) yield return new UnityEngine.WaitForEndOfFrame();
            bool present = driver.ActiveLightCount == beforeActive + 1 && driver.RegisterCallCount > beforeRegister;
            yield return CaptureScreen("P2-L1-Glower", 0);
            var metrics = new Dictionary<string, double>
            {
                ["verticesRecorded"] = pipeline.L1Renderer.VerticesRecorded - vertices,
                ["drawsRecorded"] = pipeline.L1Renderer.DrawsRecorded - draws,
                ["liveParticles"] = LumenSceneBootstrap.Simulator.LiveParticleCount,
                ["registrationPresent"] = present ? 1 : 0, ["registerCalls"] = driver.RegisterCallCount - beforeRegister
            };
            ReadWorkloadPixels("P2-L1-Glower", captures["P2-L1-Glower"], metrics);
            workload.Stop(allowFadeOut: false);
            yield return new UnityEngine.WaitForEndOfFrame();
            // Live-map roundtrip, not a mock sink: LumenMapLightingComponent owns VerseGlowGridSink.
            bool absent = driver.ActiveLightCount == beforeActive && driver.DeregisterCallCount > beforeDeregister;
            metrics["registrationAbsentAfterStop"] = absent ? 1 : 0;
            metrics["deregisterCalls"] = driver.DeregisterCallCount - beforeDeregister;
            metrics["drainedToIdle"] = !LumenSceneBootstrap.Simulator.HasActiveWork && !LumenSceneBootstrap.Service.HasActiveWork ? 1 : 0;
            checks.Add(new ProbeCheck("P2-L1-Glower", present && absent && metrics["verticesRecorded"] > 0 &&
                metrics["drawsRecorded"] > 0 && metrics["hdrEmissivePixels"] > 0 && metrics["drainedToIdle"] == 1,
                "Glow proof is the live MapGlowerDriver/VerseGlowGridSink Register->Deregister roundtrip; VisualGlowAt delta is not used because quicktest lighting is not forced dark. Sink calls are synchronous and exceptions propagate.", metrics));
        }
        finally
        {
            workload?.Stop(allowFadeOut: false);
            workload = null;
            LumenSceneBootstrap.Catalog.Unregister("P2-L1-Glower");
        }
    }

    private IEnumerator CompatFrames()
    {
        yield return CaptureScreen("Compat-reference", 6);
        var origin = new Vector3(Current.CameraDriver.MapPosition.x + 0.5f, 0f, Current.CameraDriver.MapPosition.z + 0.5f);
        // Real vanilla additive material, same family used by vanilla motes (Assembly-CSharp:549844).
        compatMaterial = MaterialPool.MatFrom("Other/ForceField", ShaderDatabase.MoteGlow);
        compatPosition = new UnityEngine.Vector3(origin.X, AltitudeLayer.MoteOverhead.AltitudeFor(), origin.Z);
        long copied = pipeline.CompatCapturedItems, optOut = pipeline.CompatOptedOut, unresolved = pipeline.CompatUnresolvedDef;
        long routed = pipeline.Pre.RoutedDrawItems;
        RimLumenRenderHost.EnableVanillaAdditiveBloom = true;
        app.OnUpdate += SubmitCompatQuad;
        try
        {
            for (int i = 0; i < 6; i++) yield return new UnityEngine.WaitForEndOfFrame();
            app.WaitForRenderThreadIdle();
        }
        finally
        {
            app.OnUpdate -= SubmitCompatQuad;
            RimLumenRenderHost.EnableVanillaAdditiveBloom = false;
        }
        var metrics = new Dictionary<string, double>
        {
            ["redrawnItems"] = pipeline.Pre.RoutedDrawItems - routed,
            ["capturedItems"] = pipeline.CompatCapturedItems - copied,
            ["optedOut"] = pipeline.CompatOptedOut - optOut,
            ["unresolvedDef"] = pipeline.CompatUnresolvedDef - unresolved
        };
        // First newly captured idle frame; no settling frames and no region masking.
        yield return CaptureScreen("post-compat-idle", 0);
        PixelDiffResult diff = PixelMetrics.Compare(captures["Compat-reference"], captures["post-compat-idle"], width, height);
        bool originalIdleAvailable = captures.TryGetValue("Aprime", out byte[]? original);
        PixelDiffResult? originalDiff = originalIdleAvailable ? PixelMetrics.Compare(original!, captures["post-compat-idle"], width, height) : null;
        metrics["originalIdleAvailable"] = originalIdleAvailable ? 1 : 0;
        if (originalDiff != null)
        {
            metrics["originalIdleMaxAbsDelta"] = originalDiff.Value.MaxAbsDelta;
            metrics["originalIdleDifferingPixels"] = originalDiff.Value.DifferingPixels;
        }
        metrics["idleMaxAbsDelta"] = diff.MaxAbsDelta;
        metrics["idleDifferingPixels"] = diff.DifferingPixels;
        checks.Add(new ProbeCheck("Compat-VanillaAdditive", metrics["redrawnItems"] > 0 && diff.MaxAbsDelta == 0 && diff.DifferingPixels == 0 && originalDiff?.MaxAbsDelta == 0,
            "Vanilla MoteGlow submitted on MapMain; fresh HDR copy while flag=true, then flag=false. Exact first post-disable idle frame. ShaderDef-level opt-out only; HdrMultiplier not applied.", metrics));
    }

    private void SubmitCompatQuad() => UnityEngine.Graphics.DrawMesh(MeshPool.plane10,
        UnityEngine.Matrix4x4.TRS(compatPosition, UnityEngine.Quaternion.identity, new UnityEngine.Vector3(4f, 1f, 4f)), compatMaterial!, 0);

    private static void RegisterWorkload(string id, int count, LumenParticleTier tier, bool light)
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
                        Name = id, PreferredTier = tier, MaxParticles = count, Duration = 120f, Looping = true,
                        RateOverTime = count, WorldSpaceSimulation = false,
                        Bursts = new[] { (0f, count) },
                        Shape = LumenShapeKind.Box, ShapeExtents = new Vector3(3f, 0.15f, 3f),
                        StartLifetimeMin = 120f, StartLifetimeMax = 120f, StartSizeMin = 0.15f, StartSizeMax = 0.3f,
                        StartSpeedMin = 0.1f, StartSpeedMax = 0.2f, StartColorLinear = new Vector4(1f, 0.4f, 0.1f, 1f),
                        HdrIntensity = 4f, SoftParticleFadeDistance = 0.5f,
                        ForceFields = new[]
                        {
                            new ForceFieldSpec(ForceFieldKind.CurlTurbulence, Vector3.Zero, Vector3.Zero, 8f, 0.2f, 0.8f),
                            new ForceFieldSpec(ForceFieldKind.Vortex, Vector3.UnitY, Vector3.Zero, 8f, 0.2f),
                            new ForceFieldSpec(ForceFieldKind.PointAttractor, Vector3.Zero, Vector3.Zero, 8f, 0.1f)
                        }
                    }
                },
                new EffectNodeSpec { Name = "light", PointLight = light ? new PointLightSpec(8f, 0f, new Vector3(1f, 0.4f, 0.1f)) : null }
            }
        });
    }

    private void ReadWorkloadPixels(string id, byte[] screen, Dictionary<string, double> metrics)
    {
        app.WaitForRenderThreadIdle();
        HdrTargets.Slot slot = pipeline.Targets.Slots[pipeline.LastRenderedSlot];
        byte[] hdr = GpuTextureService.ReadbackTexture2D(slot.Color.Color, width, height, 8)
            ?? throw new InvalidOperationException("HDR readback failed: " + id);
        RenderTexture bloomTex = slot.Bloom ?? throw new InvalidOperationException("Bloom target unavailable: " + id);
        byte[] glow = GpuTextureService.ReadbackTexture2D(bloomTex.Color, bloomTex.Width, bloomTex.Height, 8)
            ?? throw new InvalidOperationException("Bloom readback failed: " + id);
        byte[] idle = captures[id + "-reference"];
        int emissive = 0, halo = 0, brightened = 0;
        for (int i = 0; i < hdr.Length; i += 8)
            if (Half(hdr, i) > 0f || Half(hdr, i + 2) > 0f || Half(hdr, i + 4) > 0f) emissive++;
        for (int i = 0; i < glow.Length; i += 8) if (Half(glow, i) > 0f) halo++;
        for (int i = 0; i < screen.Length; i += 4)
            if (screen[i] > idle[i] || screen[i + 1] > idle[i + 1] || screen[i + 2] > idle[i + 2]) brightened++;
        metrics["hdrEmissivePixels"] = emissive;
        metrics["bloomPixels"] = halo;
        metrics["swapchainBrightenedPixels"] = brightened;
        File.WriteAllBytes(Path.Combine(options.OutputDirectory, id + ".rgba16f"), hdr);
    }

    private static float Half(byte[] data, int offset) => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data, offset));

    private IEnumerator RunBenchmarkSchedule()
    {
        var steps = new List<object>();
        object? poolOverflowResult = null;
        var gpu = LumenHostRegistry.GpuParticleEngine as LumenGpuParticleEngine
            ?? throw new InvalidOperationException("Native GPU engine was not bootstrapped.");
        IntVec3 cell = Current.CameraDriver.MapPosition;
        if (!cell.InBounds(Find.CurrentMap)) throw new InvalidOperationException("Camera centre is outside the map.");
        Vector3 origin = new(cell.x + 0.5f, 0f, cell.z + 0.5f);
        int mapId = Find.CurrentMap.uniqueID;

        try
        {
            foreach (int targetCount in new[] { 0, 10_000, 50_000, 100_000, 250_000, 500_000 })
            {
                string id = targetCount == 0 ? "Bench-0-Idle" : $"Bench-{targetCount / 1000}k";
                if (targetCount > 0) RegisterWorkload(id, targetCount, LumenParticleTier.L2_GpuCompute, false);
                try
                {
                    if (targetCount > 0)
                    {
                        workload = LumenSceneBootstrap.Service.Play(new LumenPlayRequest
                        { EffectId = id, MapIndex = mapId, OriginWorld = origin });
                        if (!workload.IsAlive) throw new InvalidOperationException("Benchmark workload failed to start: " + id);
                    }
                    int warmup = targetCount == 0 ? 15 : 8;
                    for (int i = 0; i < warmup; i++) yield return new UnityEngine.WaitForEndOfFrame();

                    pipeline.ContributorMilliseconds.Clear();
                    pipeline.MeasureContributors = true;
                    var frameMs = new List<double>(60);
                    for (int i = 0; i < 60; i++)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        yield return new UnityEngine.WaitForEndOfFrame();
                        frameMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                    }
                    pipeline.MeasureContributors = false;

                    int live = targetCount == 0 ? 0 : gpu.CountLiveParticles();
                    double meanMs = frameMs.Average();
                    var sorted = frameMs.OrderBy(v => v).ToArray();
                    double p95Ms = sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.95) - 1)];
                    double maxMs = sorted[^1];
                    double captureEnqueueMs = pipeline.ContributorMilliseconds.TryGetValue(gpu.Name, out var samples) && samples.Count > 0
                        ? samples.Average() : 0.0;

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
                        CpuCaptureEnqueueMeanMs = Math.Round(captureEnqueueMs, 4),
                    });

                    workload?.Stop(allowFadeOut: false);
                    workload = null;
                    yield return new UnityEngine.WaitForEndOfFrame();
                    yield return new UnityEngine.WaitForEndOfFrame();
                }
                finally
                {
                    workload?.Stop(allowFadeOut: false);
                    workload = null;
                    if (targetCount > 0) LumenSceneBootstrap.Catalog.Unregister(id);
                }
            }

            // Real-device 1M pool overflow protection + 10s yellow warning throttle check.
            RegisterWorkload("Bench-Pool900k", 900_000, LumenParticleTier.L2_GpuCompute, false);
            RegisterWorkload("Bench-Pool200k", 200_000, LumenParticleTier.L2_GpuCompute, false);
            var service = LumenSceneBootstrap.Service;
            Func<long> prevClock = service.ClockMsProvider;
            Action<string> prevSink = service.PoolExhaustedWarningSink;
            ILumenEffectHandle? baseHandle = null;
            try
            {
                long fakeNowMs = 1_000;
                var capturedWarnings = new List<string>();
                service.ClockMsProvider = () => fakeNowMs;
                service.PoolExhaustedWarningSink = msg =>
                {
                    capturedWarnings.Add(msg);
                    Verse.Log.Warning(msg);
                };
                baseHandle = service.Play(new LumenPlayRequest { EffectId = "Bench-Pool900k", MapIndex = mapId, OriginWorld = origin });
                bool baseAlive = baseHandle.IsAlive;
                using var overflow1 = service.Play(new LumenPlayRequest { EffectId = "Bench-Pool200k", MapIndex = mapId, OriginWorld = origin });
                using var overflow2 = service.Play(new LumenPlayRequest { EffectId = "Bench-Pool200k", MapIndex = mapId, OriginWorld = origin });
                int warningsInWindow = capturedWarnings.Count;
                fakeNowMs += LumenVfxService.PoolExhaustedWarningCooldownMs;
                using var overflow3 = service.Play(new LumenPlayRequest { EffectId = "Bench-Pool200k", MapIndex = mapId, OriginWorld = origin });
                int warningsAfterCooldown = capturedWarnings.Count;
                poolOverflowResult = new
                {
                    DefaultPoolCapacity = EmitterSlotAllocator.DefaultCapacity,
                    Base900kAlive = baseAlive,
                    Overflow1Alive = overflow1.IsAlive,
                    Overflow2Alive = overflow2.IsAlive,
                    Overflow3Alive = overflow3.IsAlive,
                    WarningsIn10sWindow = warningsInWindow,
                    WarningsAfter10sCooldown = warningsAfterCooldown,
                };
            }
            finally
            {
                baseHandle?.Stop(allowFadeOut: false);
                service.ClockMsProvider = prevClock;
                service.PoolExhaustedWarningSink = prevSink;
                LumenSceneBootstrap.Catalog.Unregister("Bench-Pool900k");
                LumenSceneBootstrap.Catalog.Unregister("Bench-Pool200k");
            }
            yield return new UnityEngine.WaitForEndOfFrame();
        }
        finally
        {
            app.OnUpdate -= PrepareDeterministicFrame;
            Find.UIRoot.screenshotMode.Active = savedScreenshotMode;
            foreach (var item in windowVisibility) item.Key.drawInScreenshotMode = item.Value;
            pipeline.MeasureContributors = false;
            RimLumenRenderHost.ForceUnscaledSimulationTime = false;
            app.WaitForRenderThreadIdle();
            app.GraphicsDevice!.WaitIdleAndRun(() => { });
        }

        var device = app.GraphicsDevice!;
        Directory.CreateDirectory(options.OutputDirectory);
        string outPath = Path.Combine(options.OutputDirectory, "bench-result.json");
        File.WriteAllText(outPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            StartedUtc = started,
            DurationSeconds = Math.Round(timer.Elapsed.TotalSeconds, 3),
            GpuName = UnityEngine.SystemInfo.graphicsDeviceName ?? "Unknown",
            ValidationEnabled = device.ValidationEnabled,
            InstanceEnabledLayers = device.InstanceEnabledLayers.ToArray(),
            VanillaAdditiveBloom = RimLumenRenderHost.EnableVanillaAdditiveBloom,
            Resolution = $"{width}x{height}",
            DefaultSlotCapacity = EmitterSlotAllocator.DefaultCapacity,
            Steps = steps,
            PoolOverflowCheck = poolOverflowResult,
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Log.Message($"[RimLumen] Benchmark finished in {timer.Elapsed.TotalSeconds:F2}s -> {outPath}");
        Waylith.UnityCompat.UnityRuntime.Current!.App.ExitCode = 0;
        Root.Shutdown();
    }
}
