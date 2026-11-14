using System;
using System.Collections;
using System.Collections.Generic;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;
using RimLumen.Core.ParticlesGpu;
using System.Runtime.CompilerServices;
using RimLumen.Core.Rendering.Probes.Contributors;
using RimWorldWaylith.Rendering;
using UnityStubs.Rendering;
using Waylith.Graphics;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using RimLumen.Core.Rendering.Logic;
using Verse;
using Waylith;
using Waylith.Logging;
using Waylith.Rendering;
using Waylith.Rendering.Compute;

namespace RimLumen.Core.Rendering.Probes;

internal sealed partial class ProbeSession
{
    private readonly ProbeOptions options;
    private readonly List<ProbeCheck> checks = new();
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly Stopwatch timer = Stopwatch.StartNew();
    private readonly EngineApplication app = EngineApplication.Instance;
    private readonly RimLumenPipeline pipeline = RimLumenRenderHost.Pipeline!;
    private int mapDraws;
    private int mapPersistent;
    private int mapIndex;
    private SyntheticHdrQuadContributor? synthetic;
    private SsboBarrierProbeContributor? ssbo;
    private float? savedMaximumDeltaTime;
    private bool savedScreenshotMode;
    private readonly Dictionary<Window, bool> windowVisibility = new();
    private float frozenTime;
    private bool savedVanillaAdditiveBloom;
    private string modRoot = string.Empty;

    private ProbeSession(ProbeOptions options) => this.options = options;

    internal static void Run(string modRoot, ProbeOptions options)
    {
        new ProbeSession(options).Start(modRoot);
    }

    private void Start(string modRoot)
    {
        this.modRoot = modRoot;
        // Probes pause TickManager for deterministic scene capture but still need P2 particle workloads to advance.
        RimLumenRenderHost.ForceUnscaledSimulationTime = true;
        savedVanillaAdditiveBloom = RimLumenRenderHost.EnableVanillaAdditiveBloom;
        try
        {
            Directory.CreateDirectory(options.OutputDirectory);
            LumenSceneBootstrap.EnsureInitialized();
            LumenGpuParticlesBootstrap.EnsureInitialized();
            var device = app.GraphicsDevice ?? throw new InvalidOperationException("Probe requires a GraphicsDevice.");
            if (options.BenchmarkOnly)
            {
                Log.Message($"[RimLumen] Benchmark session started; ValidationEnabled={device.ValidationEnabled}; VanillaBloom={RimLumenRenderHost.EnableVanillaAdditiveBloom}");
                Find.TickManager.Pause();
                Current.CameraDriver.enabled = false;
                savedScreenshotMode = Find.UIRoot.screenshotMode.Active;
                Find.UIRoot.screenshotMode.Active = true;
                ScreenFader.SetColor(UnityEngine.Color.clear);
                app.OnUpdate += PrepareDeterministicFrame;
                PrepareDeterministicFrame();
                Current.CameraDriver.StartCoroutine(RunBenchmarkSchedule());
                return;
            }
            // P1-idle must observe the silent framework state (max|delta| == 0); the player's setting is restored in Finish.
            RimLumenRenderHost.EnableVanillaAdditiveBloom = false;
            bool validation = device.ValidationEnabled && device.InstanceEnabledLayers.Contains("VK_LAYER_KHRONOS_validation");
            string layers = string.Join(",", device.InstanceEnabledLayers);
            Log.Message($"[RimLumen] ValidationEnabled={device.ValidationEnabled}; InstanceEnabledLayers={layers}");
            checks.Add(new ProbeCheck("validation-layer", validation, layers, new Dictionary<string, double>()));
            try { CompileShaders(modRoot, checks); }
            catch (Exception ex) { CheckFailed("T4", ex); }
            checks.Add(RoundTrip());
            Find.TickManager.Pause();
            Current.CameraDriver.enabled = false;
            // Time.timeScale is a stub. maximumDeltaTime also caps UnityTimeState.Time accumulation.
            savedMaximumDeltaTime = UnityEngine.Time.maximumDeltaTime;
            UnityEngine.Time.maximumDeltaTime = 0f;
            frozenTime = UnityEngine.Time.time;
            savedScreenshotMode = Find.UIRoot.screenshotMode.Active;
            Find.UIRoot.screenshotMode.Active = true;
            ScreenFader.SetColor(UnityEngine.Color.clear);
            app.OnUpdate += PrepareDeterministicFrame;
            PrepareDeterministicFrame();
            mapIndex = Array.FindIndex(app.FrameRenderer.Features.ToArray(), feature => feature.GetType().Name == "MapRenderFeature");
            app.FrameRenderer.RenderThreadFrameEnded += RecordMapCounts;
            Current.CameraDriver.StartCoroutine(GuardedFrames());
            return; // Recorded failures do not terminate the independent frame checks.
        }
        catch (Exception ex)
        {
            checks.Add(new ProbeCheck("session", false, ex.ToString(), new Dictionary<string, double>()));
            Log.Error("[RimLumen] Probe session failed: " + ex);
        }
        if (checks.Any(check => !check.Passed)) Finish();
    }

    private void PrepareDeterministicFrame()
    {
        // ScreenshotMode hides letters/messages/alerts. Windows opt out individually, including
        // EditWindow_Log (drawInScreenshotMode=true), which may open again on validation errors.
        foreach (Window window in Find.WindowStack.Windows)
        {
            if (!windowVisibility.ContainsKey(window)) windowVisibility.Add(window, window.drawInScreenshotMode);
            window.drawInScreenshotMode = false;
        }
    }

    private void Finish()
    {
            app.OnUpdate -= PrepareDeterministicFrame;
            if (savedMaximumDeltaTime.HasValue)
            {
                UnityEngine.Time.maximumDeltaTime = savedMaximumDeltaTime.Value;
                Find.UIRoot.screenshotMode.Active = savedScreenshotMode;
                foreach (var item in windowVisibility) item.Key.drawInScreenshotMode = item.Value;
            }
            app.FrameRenderer.RenderThreadFrameEnded -= RecordMapCounts;
            pipeline.MeasureContributors = false;
            app.OnUpdate -= SubmitCompatQuad;
            RimLumenRenderHost.EnableVanillaAdditiveBloom = savedVanillaAdditiveBloom;
            RimLumenRenderHost.ForceUnscaledSimulationTime = false;
            workload?.Stop(allowFadeOut: false);
            ssbo?.Disarm();
            synthetic?.Disarm();
            app.WaitForRenderThreadIdle();
            app.GraphicsDevice!.WaitIdleAndRun(() => { });
            if (ssbo != null) { LumenHostRegistry.UnregisterContributor(ssbo); ssbo.Dispose(); }
            if (synthetic != null) { LumenHostRegistry.UnregisterContributor(synthetic); synthetic.Dispose(); }
            checks.Add(new ProbeCheck("routed-persistent", pipeline.UnsupportedPersistentDraws == 0,
                "Dynamic MeshDrawCommand supported; routed persistent FinalDrawItem copying has no public API.",
                new Dictionary<string, double> { ["unsupportedPersistentDrawItems"] = pipeline.UnsupportedPersistentDraws }));
            foreach (string required in new[] { "T1a", "T1b", "T2", "T3", "T4", "P1-idle", "P1-active", "feature-order",
                "P2-L2-10k", "P2-L2-50k", "P2-L1-Glower", "Compat-VanillaAdditive", "ShaderPack-RouteB", "ShaderPack-RouteB-fallback", "ShaderPack-BuiltIns", "validation-layer" })
                if (!checks.Any(check => check.Id == required))
                    CheckFailed(required, new InvalidOperationException("Required dependent observation unavailable; see failed setup/capture check details."));
            bool passed = checks.Count > 0 && checks.All(check => check.Passed);
            try
            {
                Directory.CreateDirectory(options.OutputDirectory);
                var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                    .Where(module => module.ModuleName.Contains("vulkan", StringComparison.OrdinalIgnoreCase) ||
                        module.ModuleName.Contains("VkLayer", StringComparison.OrdinalIgnoreCase))
                    .Select(module => module.FileName).ToArray();
                File.WriteAllText(Path.Combine(options.OutputDirectory, "probe-result.json"), JsonSerializer.Serialize(new
                {
                    StartedUtc = started,
                    DurationSeconds = timer.Elapsed.TotalSeconds,
                    ProcessId = Environment.ProcessId,
                    ProgramState = Current.ProgramState.ToString(),
                    MapId = Find.CurrentMap?.uniqueID,
                    options.DisableBarrier,
                    LogRoot = FileLog.StartedRoot,
                    QuitPath = "Root.Shutdown/Application.Quit",
                    ExpectedProcessExitCode = passed ? 0 : 1,
                    ValidationClean = false, // Final log validation (including shutdown) belongs to run-probes.ps1.
                    LoadedVulkanModules = modules,
                    Checks = checks,
                    Passed = passed
                }, new JsonSerializerOptions { WriteIndented = true }));
                Log.Message($"[RimLumen] Probe finished: passed={passed}; seconds={timer.Elapsed.TotalSeconds:F3}");
            }
            catch (Exception ex)
            {
                passed = false;
                Log.Error("[RimLumen] Could not write probe result: " + ex);
            }
            // Match Bootstrap -QuitAfterBoot: Steam shutdown and Application.quitting also run.
            Waylith.UnityCompat.UnityRuntime.Current!.App.ExitCode = passed ? 0 : 1;
            Log.Message($"[RimLumen] QuitPath=Root.Shutdown/Application.Quit; exit={ (passed ? 0 : 1) }");
            Root.Shutdown();
    }

    private void RecordMapCounts(RenderFrame frame)
    {
        if (frame.GetFeaturePayload(mapIndex) is not MeshDrawFrame mesh) return;
        mapDraws = mesh.Draws.Count(draw => draw.PassId == RenderPassIds.MapMain);
        mapPersistent = mesh.PersistentDraws.Count(draw => draw.PassId == RenderPassIds.MapMain);
    }

    private void MeasureActive(byte[] baseline, byte[] active, byte[] hdr, int width, int height, NormalizedRect emissive, NormalizedRect soft)
    {
        PixelRect quad = new((int)(emissive.X * width), (int)(emissive.Y * height), (int)(emissive.Width * width), (int)(emissive.Height * height));
        int halo = PixelMetrics.CountHaloPixels(baseline, active, width, height, quad, 40, 1);
        int center = ((quad.Y + quad.Height / 2) * width + quad.X + quad.Width / 2);
        int edge = ((quad.Y + quad.Height / 2) * width + quad.X + 1);
        float HalfChannel(int pixel, int channel) => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(hdr, pixel * 8 + channel * 2));
        double Ratio(int pixel, int channel) => (double)active[pixel * 4 + channel] / Math.Max(1, (int)active[pixel * 4]);
        double coreSaturation = 1.0 - Math.Min(Ratio(center, 1), Ratio(center, 2));
        double edgeG = Ratio(edge, 1);
        double edgeB = Ratio(edge, 2);
        int occluded = 0, feathered = 0, visible = 0;
        for (int y = (int)(soft.Y * height); y < (int)((soft.Y + soft.Height) * height); y++)
            for (int x = (int)(soft.X * width); x < (int)((soft.X + soft.Width) * width); x++)
            {
                float alpha = HalfChannel(y * width + x, 3);
                if (alpha <= 0.001f) occluded++;
                else if (alpha < 0.749f) feathered++;
                else visible++;
            }
        checks.Add(new ProbeCheck("P1-active", halo > 0 && HalfChannel(center, 0) > 1f && coreSaturation < 0.85 &&
            Math.Abs(edgeG - 0.55) < 0.1 && Math.Abs(edgeB - 0.15) < 0.1,
            "Emissive center/edge measured from swapchain and raw HDR separately. Soft alpha classes are observations; arbitrary map depth need not contain all three classes.",
            new Dictionary<string, double>
            {
                ["haloPixels"] = halo, ["hdrCoreR"] = HalfChannel(center, 0), ["hdrCoreG"] = HalfChannel(center, 1), ["hdrCoreB"] = HalfChannel(center, 2),
                ["hdrEdgeR"] = HalfChannel(edge, 0), ["hdrEdgeG"] = HalfChannel(edge, 1), ["hdrEdgeB"] = HalfChannel(edge, 2),
                ["coreSaturation"] = coreSaturation, ["inputSaturation"] = 0.85, ["edgeGOverR"] = edgeG, ["edgeBOverR"] = edgeB,
                ["softOccludedPixels"] = occluded, ["softFeatheredPixels"] = feathered, ["softVisiblePixels"] = visible,
                ["width"] = width, ["height"] = height
            }));
    }

    private sealed class ProbeNoOpFeature : IRenderFeature
    {
        public bool IsActive => false;
        public IReadOnlyList<IRenderPass> Passes => Array.Empty<IRenderPass>();
        public object? CaptureFrame(in FrameTiming timing) => null;
        public void OnRenderTargetRebuilt(IRenderTarget target) { }
    }

    private static ProbeCheck RoundTrip()
    {
        var timer = Stopwatch.StartNew();
        try
        {
            const int count = 256;
            var input = new uint[count];
            var output = new uint[count];
            for (int i = 0; i < count; i++)
                input[i] = unchecked(0x9e3779b9u * (uint)(i + 1));
            using GpuComputeBuffer source = GpuTextureService.CreateComputeBuffer("RimLumen T1a input", count * sizeof(uint))
                ?? throw new InvalidOperationException("Could not create T1a input buffer.");
            using GpuComputeBuffer destination = GpuTextureService.CreateComputeBuffer("RimLumen T1a output", count * sizeof(uint))
                ?? throw new InvalidOperationException("Could not create T1a output buffer.");
            source.Upload<uint>(input);
            destination.Upload<uint>(output);
            ComputeKernel kernel = ComputeKernelCache.Load("Probes/RimLumenIdentity.comp.glsl", "main", new[]
            {
                new ComputeBinding(0, ComputeBindingType.StorageBuffer),
                new ComputeBinding(1, ComputeBindingType.StorageBuffer)
            });
            ComputeDispatch dispatch = ComputeDispatch.Rent(kernel, count / 64, 1);
            dispatch.SetStorageBuffer(0, source);
            dispatch.SetStorageBuffer(1, destination);
            ComputeDispatchQueue.DispatchNow(dispatch);
            destination.Read(MemoryMarshal.AsBytes(output.AsSpan()));
            int mismatches = input.Where((value, index) => value != output[index]).Count();
            return new ProbeCheck("T1a", mismatches == 0, $"DispatchNow identity round-trip: {count} uints, {mismatches} mismatches.",
                new Dictionary<string, double> { ["elements"] = count, ["mismatches"] = mismatches, ["milliseconds"] = timer.Elapsed.TotalMilliseconds });
        }
        catch (Exception ex)
        {
            return new ProbeCheck("T1a", false, ex.ToString(), new Dictionary<string, double>());
        }
    }

    private static void CompileShaders(string modRoot, List<ProbeCheck> checks)
    {
        string root = Path.Combine(modRoot, "1.7", "Shaders");
        string[] paths = Directory.GetFiles(root, "*.glsl", SearchOption.AllDirectories);
        Array.Sort(paths, StringComparer.Ordinal);
        bool passed = true;
        int stages = 0;
        var includes = new List<string>();
        foreach (string path in paths)
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            try
            {
                RuntimeShaderStage? stage = ShaderProbeFiles.Classify(path, File.ReadAllText(path));
                if (stage == null)
                {
                    includes.Add(relative);
                    continue;
                }
                stages++;
                byte[] spirv = RuntimeShaderCompiler.Compile(ShaderSourceLoader.ReadShaderSource(path), path, "main",
                    RuntimeShaderLanguage.Glsl, stage.Value, out bool cacheHit);
                checks.Add(new ProbeCheck("T4/" + relative, spirv.Length > 0, $"Slang {stage}; cacheHit={cacheHit}",
                    new Dictionary<string, double> { ["spirvBytes"] = spirv.Length, ["cacheHit"] = cacheHit ? 1 : 0 }));
                passed &= spirv.Length > 0;
            }
            catch (Exception ex)
            {
                passed = false;
                checks.Add(new ProbeCheck("T4/" + relative, false, ex.ToString(), new Dictionary<string, double>()));
            }
        }
        checks.Add(new ProbeCheck("T4", passed && stages > 0,
            $"Compiled {stages} stage files through Slang. Include-only (no stage suffix, no main entry point, compiled via consumers): {string.Join(", ", includes)}.",
            new Dictionary<string, double> { ["stageFiles"] = stages, ["includeOnlyFiles"] = includes.Count }));
    }
}
