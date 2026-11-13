using System;
using System.Collections;
using System.Collections.Generic;
using RimLumen.Core.Contracts;
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

internal sealed class ProbeSession
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

    private ProbeSession(ProbeOptions options) => this.options = options;

    internal static void Run(string modRoot, ProbeOptions options)
    {
        new ProbeSession(options).Start(modRoot);
    }

    private void Start(string modRoot)
    {
        try
        {
            Directory.CreateDirectory(options.OutputDirectory);
            var device = app.GraphicsDevice ?? throw new InvalidOperationException("Probe requires a GraphicsDevice.");
            bool validation = device.ValidationEnabled && device.InstanceEnabledLayers.Contains("VK_LAYER_KHRONOS_validation");
            string layers = string.Join(",", device.InstanceEnabledLayers);
            Log.Message($"[RimLumen] ValidationEnabled={device.ValidationEnabled}; InstanceEnabledLayers={layers}");
            checks.Add(new ProbeCheck("validation-layer", validation, layers, new Dictionary<string, double>()));
            CompileShaders(modRoot, checks);
            checks.Add(RoundTrip());
            if (checks.Any(check => !check.Passed)) { Finish(); return; }
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
            ssbo?.Disarm();
            synthetic?.Disarm();
            if (ssbo != null) { LumenHostRegistry.UnregisterContributor(ssbo); ssbo.Dispose(); }
            if (synthetic != null) { LumenHostRegistry.UnregisterContributor(synthetic); synthetic.Dispose(); }
            checks.Add(new ProbeCheck("routed-persistent", pipeline.UnsupportedPersistentDraws == 0,
                "Dynamic MeshDrawCommand supported; routed persistent FinalDrawItem copying has no public API.",
                new Dictionary<string, double> { ["unsupportedPersistentDrawItems"] = pipeline.UnsupportedPersistentDraws }));
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

    private IEnumerator GuardedFrames()
    {
        IEnumerator frames = Frames();
        while (true)
        {
            bool more;
            try { more = frames.MoveNext(); }
            catch (Exception ex)
            {
                checks.Add(new ProbeCheck("frame-session", false, ex.ToString(), new Dictionary<string, double>()));
                break;
            }
            if (!more) break;
            yield return frames.Current;
        }
        Finish();
    }

    private IEnumerator Frames()
    {
        // realtimeSinceStartup is Stopwatch-backed with no public freeze. Do not silently remove
        // visible realtime motes to manufacture equality: reject that unsupported scene explicitly.
        int realTimeMotes = RealTime.moteList.allMotes.Count(mote => mote.Spawned && mote.Map == Find.CurrentMap && mote.def.mote.realTime);
        if (realTimeMotes != 0)
            throw new InvalidOperationException($"Deterministic capture unavailable: {realTimeMotes} realtime motes use an unfrozen Stopwatch clock.");
        var captures = new Dictionary<string, byte[]>();
        var counts = new Dictionary<string, (int Draws, int Persistent)>();
        int width = 0;
        int height = 0;
        var emissiveRect = new NormalizedRect(0.18f, 0.25f, 0.18f, 0.20f);
        var softRect = new NormalizedRect(0.62f, 0.25f, 0.20f, 0.20f);
        ProbeNoOpFeature? orderProbe = null;
        string[] orderBefore = Array.Empty<string>();
        long idleClearDelta = 0, idleDrawDelta = 0;
        foreach (string stage in new[] { "A", "B", "Aprime", "active", "T1b" })
        {
            if (stage == "A" || stage == "Aprime") pipeline.Detach();
            if (stage == "B" || stage == "active") pipeline.Attach();
            if (stage == "active")
            {
                synthetic = new SyntheticHdrQuadContributor(8f, emissiveRect, softRect);
                LumenHostRegistry.RegisterContributor(synthetic);
                synthetic.Arm();
                orderProbe = new ProbeNoOpFeature();
                int imgui = Array.FindIndex(app.FrameRenderer.Features.ToArray(), feature => feature.GetType().Name.Contains("ImGui", StringComparison.OrdinalIgnoreCase));
                app.FrameRenderer.InsertFeature(imgui, orderProbe);
                orderBefore = app.FrameRenderer.Features.Select(feature => feature.GetType().Name).ToArray();
            }
            if (stage == "T1b")
            {
                ssbo = new SsboBarrierProbeContributor(options.DisableBarrier);
                LumenHostRegistry.RegisterContributor(ssbo);
                ssbo.Arm();
            }
            long clearsBefore = pipeline.Pre.Clears;
            long drawsBefore = pipeline.Composite.Draws;
            // Settle feature snapshots and in-flight submissions before requesting the measurement frame.
            for (int i = 0; i < 6; i++) yield return new UnityEngine.WaitForEndOfFrame();
            IScreenCaptureBackend backend = ScreenCaptureBackend.Current ?? throw new InvalidOperationException("Screen capture backend unavailable.");
            if (!backend.RequestCapture()) throw new InvalidOperationException("Swapchain transfer readback unsupported.");
            yield return new UnityEngine.WaitForEndOfFrame();
            // ScreenCaptureFeature._readable exists only inside EndOfFrame.Run (source:115-126), not after FrameEnqueued returns.
            if (!backend.TryReadScreen(out byte[] rgba, out width, out height)) throw new InvalidOperationException("TryReadScreen failed at " + stage);
            captures[stage] = rgba;
            counts[stage] = (mapDraws, mapPersistent);
            if (UnityEngine.Time.time != frozenTime)
                throw new InvalidOperationException("Unity render time advanced during deterministic capture.");
            if (stage == "B")
            {
                idleClearDelta = pipeline.Pre.Clears - clearsBefore;
                idleDrawDelta = pipeline.Composite.Draws - drawsBefore;
            }
            File.WriteAllBytes(Path.Combine(options.OutputDirectory, stage + ".rgba"), rgba);
            if (stage == "active")
            {
                string[] after = app.FrameRenderer.Features.Select(feature => feature.GetType().Name).ToArray();
                int self = Array.IndexOf(after, nameof(RimLumenCompositeFeature));
                bool ordered = self >= 0 && self + 1 < after.Length && after[self + 1].Contains("ImGui", StringComparison.OrdinalIgnoreCase);
                checks.Add(new ProbeCheck("feature-order", ordered, "Before: " + string.Join(" -> ", orderBefore) + "; After: " + string.Join(" -> ", after), new Dictionary<string, double>()));
                if (orderProbe != null) app.FrameRenderer.RemoveFeature(orderProbe);
                HdrTargets.Slot slot = pipeline.Targets.Slots[pipeline.LastRenderedSlot];
                byte[] hdr = GpuTextureService.ReadbackTexture2D(slot.Color.Color, width, height, 8)
                    ?? throw new InvalidOperationException("RGBA16F readback failed.");
                File.WriteAllBytes(Path.Combine(options.OutputDirectory, "active.rgba16f"), hdr);
                MeasureActive(captures["Aprime"], rgba, hdr, width, height, emissiveRect, softRect);
            }
        }
        PixelDiffResult noise = PixelMetrics.Compare(captures["A"], captures["Aprime"], width, height);
        PixelDiffResult idle = PixelMetrics.Compare(captures["A"], captures["B"], width, height);
        bool equalCounts = counts["A"] == counts["B"] && counts["A"] == counts["Aprime"];
        var idleMetrics = new Dictionary<string, double>
        {
            ["noiseMaxAbsDelta"] = noise.MaxAbsDelta, ["noiseDifferingPixels"] = noise.DifferingPixels,
            ["idleMaxAbsDelta"] = idle.MaxAbsDelta, ["idleDifferingPixels"] = idle.DifferingPixels,
            ["width"] = width, ["height"] = height,
            ["idleClears"] = idleClearDelta, ["idleCompositeDraws"] = idleDrawDelta,
            ["frozenUnityTime"] = frozenTime, ["realTimeMotes"] = realTimeMotes,
            ["hiddenWindowCount"] = windowVisibility.Count
        };
        foreach (string stage in new[] { "A", "B", "Aprime" })
        {
            idleMetrics[stage + ".mapMainSubmittedDrawItems.Draws"] = counts[stage].Draws;
            idleMetrics[stage + ".mapMainSubmittedDrawItems.Persistent"] = counts[stage].Persistent;
        }
        checks.Add(new ProbeCheck("T3", true, "RequestCapture/TryReadScreen read RGBA8 in host EndOfFrame; active HDR read back as 8 B/px half.",
            new Dictionary<string, double> { ["width"] = width, ["height"] = height }));
        checks.Add(new ProbeCheck("P1-idle", noise.MaxAbsDelta == 0 && idle.MaxAbsDelta == 0 && equalCounts && idleClearDelta == 0 && idleDrawDelta == 0,
            "Paused fixed camera; maximumDeltaTime=0; host screenshot mode, windows hidden; full-frame A/B/A with exact zero threshold. mapMainSubmittedDrawItems are pre-batching submissions, not vkCmdDraw counts: MeshDrawStats.cs:43 is Conditional(MESH_DRAW_STATS), fields private.", idleMetrics));
        checks.Add(new ProbeCheck("T1b", ssbo!.DispatchesEnqueued > 0 && ssbo.DrawsRecorded > 0 &&
            (options.DisableBarrier ? ssbo.BarriersRecorded == 0 : ssbo.BarriersRecorded > 0),
            options.DisableBarrier ? "No-barrier control; script reports SYNC-HAZARD count separately." : "Enqueue -> explicit buffer barrier -> HDR draw.",
            new Dictionary<string, double> { ["dispatches"] = ssbo.DispatchesEnqueued, ["barriers"] = ssbo.BarriersRecorded, ["draws"] = ssbo.DrawsRecorded }));
        checks.Add(new ProbeCheck("T2", pipeline.Pre.ActiveFrames > 0, "Shared scene depth attached read-only; final validation-error gate is in run-probes.ps1.",
            new Dictionary<string, double> { ["activeFrames"] = pipeline.Pre.ActiveFrames, ["clears"] = pipeline.Pre.Clears, ["compositeDraws"] = pipeline.Composite.Draws }));
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
            ComputeKernel kernel = ComputeKernelCache.Load("RimLumenIdentity.comp.glsl", "main", new[]
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
        bool passed = paths.Length > 0;
        foreach (string path in paths)
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            try
            {
                RuntimeShaderStage stage = path.EndsWith(".comp.glsl", StringComparison.Ordinal) ? RuntimeShaderStage.Compute
                    : path.EndsWith(".vert.glsl", StringComparison.Ordinal) ? RuntimeShaderStage.Vertex
                    : path.EndsWith(".frag.glsl", StringComparison.Ordinal) ? RuntimeShaderStage.Fragment
                    : throw new InvalidDataException("Shader has no recognized stage suffix: " + relative);
                byte[] spirv = RuntimeShaderCompiler.Compile(ShaderSourceLoader.ReadShaderSource(path), path, "main",
                    RuntimeShaderLanguage.Glsl, stage, out bool cacheHit);
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
        checks.Add(new ProbeCheck("T4", passed, $"Compiled {paths.Length} GLSL files through RuntimeShaderCompiler (Slang).",
            new Dictionary<string, double> { ["files"] = paths.Length }));
    }
}
