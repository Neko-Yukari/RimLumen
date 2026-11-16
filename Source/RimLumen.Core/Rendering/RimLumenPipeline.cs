using System;
using System.Collections.Generic;
using RimLumen.Core.Lighting;
using RimLumen.Core.Scene;
using RimLumen.Core.Compat;
using System.Linq;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Rendering.Logic;
using UnityStubs.Rendering;
using Verse;
using Waylith;
using Waylith.Rendering;
using Waylith.Rendering.PostFx;

namespace RimLumen.Core.Rendering;

internal sealed class RimLumenPipeline : IDisposable
{
    internal readonly EngineApplication App;
    internal readonly CompositePass SceneComposite;
    internal readonly RimLumenPreBackbufferFeature Pre;
    internal readonly RimLumenCompositeFeature Composite;
    internal readonly HdrTargets Targets;
    // Mirrors the current submission slot's R32_SFLOAT depth snapshot so particle materials can sample it.
    internal readonly BorrowedRenderTexture DepthProxy = new();
    internal ShaderPackRuntime ShaderPack { get; private set; }
    internal bool HasShaderPackRuntime => !disposed && ShaderPack is not null;
    internal bool IsDisposed => disposed;
    internal PipelineFrame? Captured;
    internal L1ParticleBatchRenderer L1Renderer { get; private set; } = null!;
    private ulong captureIndex;
    private CameraView view;
    private Vector4 depthParams;
    private int mapIndex = -1;
    private ILumenFrameContributor[] contributors = Array.Empty<ILumenFrameContributor>();
    private ILumenFrameContributor[] activeContributors = Array.Empty<ILumenFrameContributor>();
    private readonly List<ILumenFrameContributor> activeContributorBuffer = new();
    private readonly List<string> featureNames = new();
    private bool disposed;

    internal RimLumenPipeline(EngineApplication app)
    {
        App = app;
        SceneComposite = app.FrameRenderer.Features.SelectMany(feature => feature.Passes).OfType<CompositePass>().Single();
        Targets = new HdrTargets(app);
        ShaderPack = new ShaderPackRuntime(app, RimLumenRenderHost.ActiveShaderPack);
        Pre = new RimLumenPreBackbufferFeature(this);
        Composite = new RimLumenCompositeFeature(this);
        app.BeforeGraphicsShutdown += Shutdown;
        app.OnShutdown += Quiesce;
    }

    internal void ReplaceShaderPack(ShaderPackRuntime nextRuntime)
    {
        // Callers wait for the render thread and device first; the HDR framebuffers reference the previous
        // runtime's bloom layout, so retire them instead of accumulating stale VkFramebuffer objects.
        Targets.Dispose();
        // DepthProxy pointed at a slot of those targets; drop the pointer now instead of leaving a freed
        // GpuTextureAllocation reachable until the next HDR record rebinds it.
        DepthProxy.Clear();
        ShaderPackRuntime? old = ShaderPack;
        ShaderPack = nextRuntime;
        old?.Dispose();
    }

    internal void Attach()
    {
        LumenSceneBootstrap.EnsureInitialized();
        L1Renderer ??= new L1ParticleBatchRenderer(DepthProxy, App.RenderContext?.FramesInFlight ?? 3);
        LumenSceneBootstrap.Simulator.HdrRendererAttached = true;
        string[] names = App.FrameRenderer.Features.Select(feature => feature.GetType().Name).ToArray();
        int index = FeatureOrderGuard.ComputePreBackbufferInsertIndex(names);
        if (index < 0) throw new InvalidOperationException("Map/PostFx feature order is unavailable.");
        App.FrameRenderer.InsertFeature(index, Pre);
        GuardCompositeOrder();
    }

    internal void Detach()
    {
        App.WaitForRenderThreadIdle();
        App.FrameRenderer.RemoveFeature(Pre);
        App.FrameRenderer.RemoveFeature(Composite);
    }

    // Ensures: any thread. Removal is queued for the main thread: the render thread must not edit the feature list, and Detach()
    //   would deadlock there on WaitForRenderThreadIdle. Frames already captured keep their own feature snapshot and no-op once tripped.
    internal void DetachAfterFailSafe() => App.FrameRenderer.RenderToMainThread.Enqueue(() =>
    {
        App.FrameRenderer.RemoveFeature(Pre);
        App.FrameRenderer.RemoveFeature(Composite);
        // The render thread may already be faulted and would never answer WaitForRenderThreadIdle; the CPU
        // light-field state must still be dropped, so release without waiting for it.
        try { MapLightFieldCollector.Release(App, waitIdle: false); }
        catch (Exception ex) { Log.Error($"[RimLumen] MapLightField release after fail-safe failed: {ex}"); }
    });

    // Ensures: any thread; only the call that actually trips the fail-safe logs.
    internal void TripRenderFailSafe(Exception ex)
    {
        if (!RimLumenRenderHost.TripFailSafe()) return;
        // Logged from the main thread: this can run on the render thread.
        string message = $"[RimLumen] Render pipeline fail-safe tripped; detaching RimLumen features: {ex}";
        App.FrameRenderer.RenderToMainThread.Enqueue(() => Log.Error(message));
    }

    internal void GuardCompositeOrder()
    {
        IReadOnlyList<IRenderFeature> features = App.FrameRenderer.Features;
        featureNames.Clear();
        int selfIndex = -1;
        for (int i = 0; i < features.Count; i++)
        {
            IRenderFeature feature = features[i];
            featureNames.Add(feature.GetType().Name);
            if (selfIndex < 0 && EqualityComparer<IRenderFeature>.Default.Equals(feature, Composite)) selfIndex = i;
        }
        int index = FeatureOrderGuard.ComputeCompositeReinsertIndex(featureNames, selfIndex);
        if (index < 0) return;
        App.FrameRenderer.RemoveFeature(Composite);
        App.FrameRenderer.InsertFeature(index, Composite);
    }

    internal void BeginCapture(in FrameTiming timing)
    {
        // FrameRenderer.BeginMainThreadFrame:220-230 runs all early hooks before CaptureFrame:186-192.
        // This is before ComputeRenderFeature drains Enqueue, even though our pass is after Map.
        captureIndex = (ulong)timing.FrameNumber;
        mapIndex = -1;
        contributors = Array.Empty<ILumenFrameContributor>();
        var camera = Current.CameraDriver?.MyCamera;
        if (Current.ProgramState != ProgramState.Playing || Current.Game == null || Find.CurrentMap == null || camera == null)
        {
            MapLightFieldCollector.BeginCapture(App, null);
            return;
        }
        MapLightFieldCollector.BeginCapture(App, Find.CurrentMap);
        // Gameplay effects follow the game clock: dt = 0 while paused (frozen in mid-air, still rendered every frame),
        // scaled by TickRateMultiplier (1x/2x/3x).
        float unscaledDt = MathF.Max(0f, timing.DeltaTime);
        TickManager? tickManager = Current.Game.tickManager;
        float simDt = tickManager != null && tickManager.Paused ? 0f : unscaledDt * (tickManager?.TickRateMultiplier ?? 1f);
        view = CameraViewFactory.GetView(camera);
        depthParams = new Vector4(camera.nearClipPlane, camera.farClipPlane, camera.orthographic ? 1f : 0f, 1f);
        activeContributorBuffer.Clear();
        foreach (ILumenFrameContributor contributor in LumenHostRegistry.GetSharedContributorsSnapshot())
            if (contributor.HasActiveWork) activeContributorBuffer.Add(contributor);
        bool changed = activeContributorBuffer.Count != activeContributors.Length;
        for (int i = 0; !changed && i < activeContributors.Length; i++)
            changed = !ReferenceEquals(activeContributorBuffer[i], activeContributors[i]);
        // Captured frames may still be on the render thread; publish replacements, never mutate their arrays.
        if (changed) activeContributors = activeContributorBuffer.ToArray();
        contributors = activeContributors;
        var extent = App.RenderContext!.Extent;
        int currentMapIndex = Find.CurrentMap?.uniqueID ?? -1;
        mapIndex = currentMapIndex;
        var capture = new LumenCaptureContext
        {
            CurrentMapIndex = currentMapIndex,
            FrameIndex = captureIndex, DeltaTime = simDt, UnscaledDeltaTime = unscaledDt,
            ViewportWidth = extent.Width, ViewportHeight = extent.Height,
            ViewMatrix = view.View, ProjectionMatrix = view.Projection,
            LumenRenderPassId = RimLumenRenderHost.LumenRenderPassId
        };
        var compute = new LumenComputeContext { FrameIndex = captureIndex, DeltaTime = simDt, UnscaledDeltaTime = unscaledDt };
        foreach (ILumenFrameContributor contributor in contributors)
        {
            contributor.CaptureMainThread(in capture);
            contributor.EnqueuePreCompute(in compute);
            if (ReferenceEquals(contributor, LumenSceneBootstrap.Simulator))
            {
                Matrix4x4 proj = view.Projection;
                L1Renderer.Stage(captureIndex, LumenSceneBootstrap.Simulator.Vertices, LumenSceneBootstrap.Simulator.Indices,
                    LumenSceneBootstrap.Simulator.SubBatches, depthParams,
                    new Vector4(proj.M33, proj.M34, proj.M43, proj.M44));
            }
        }
    }

    internal PipelineFrame? Capture(in FrameTiming timing)
    {
        GuardCompositeOrder();
        Captured = null;
        if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || Current.CameraDriver?.MyCamera == null)
            return null;
        MeshDrawSubmissionSnapshot snapshot = MeshDrawSubmission.CaptureFlushedSnapshot(App.GraphicsDevice!);
        var draws = MeshDrawFrame.RentList();
        DrawItemFactory maker = MeshDrawSubmission.DrawItemMaker ?? throw new InvalidOperationException("DrawItemFactory is unavailable.");
        for (int i = 0; i < snapshot.CommandCount; i++)
        {
            MeshDrawCommand command = snapshot.Commands[i];
            if (command.PassId != RimLumenRenderHost.LumenRenderPassId)
            {
                if (!RimLumenRenderHost.EnableVanillaAdditiveBloom) continue;
                try
                {
                    FinalDrawItem? candidate = maker.TryMake(in command, snapshot.PropertyArena, UnityEngine.Shader.GlobalKeywordKey, i);
                    if (candidate == null) continue;
                    bool keep = AdditiveEmissiveFilter.IsAdditiveEmissive(candidate.Binding.PipelineState, candidate.Binding.Shader.Key);
                    if (keep)
                    {
                        ShaderDef? def = DefDatabase<ShaderDef>.GetNamedSilentFail(candidate.Binding.Shader.Key);
                        if (def?.GetModExtension<LumenEmissiveModExtension>() is { EnableHdrBloom: false }) keep = false;
                    }
                    FinalDrawItem.Return(candidate);
                    if (!keep) continue;
                }
                catch (Exception ex)
                {
                    // Native RimLumen effects keep rendering; only the third-party/vanilla glow enhancement is dropped.
                    Log.Error($"[RimLumen] Vanilla additive compatibility fail-safe tripped; disabling vanilla glow enhancement: {ex}");
                    RimLumenRenderHost.EnableVanillaAdditiveBloom = false;
                    continue;
                }
                // Preserve the original MapMain submission; only this fresh HDR copy changes pass.
                command = new MeshDrawCommand(command.Mesh, command.Material, command.PropertyRange, command.Matrix,
                    command.SubmeshIndex, RimLumenRenderHost.LumenRenderPassId, command.Instancing,
                    command.RenderQueueOverride, command.Layer, command.Storage);
            }
            FinalDrawItem? draw = maker.TryMake(in command, snapshot.PropertyArena, UnityEngine.Shader.GlobalKeywordKey, i);
            if (draw == null) continue;
            if (draw.Binding.PipelineState.Depth.Write)
            {
                FinalDrawItem.Return(draw);
                throw new InvalidOperationException("RimLumen routed materials must not write shared scene depth.");
            }
            draws.Add(draw);
        }
        int unsupported = 0;
        foreach (PersistentDrawCallSource source in snapshot.PersistentSources)
        {
            IReadOnlyList<FinalDrawItem> items = source.DrawItemsReadOnly;
            for (int i = 0; i < items.Count; i++)
                if (items[i].PassId == RimLumenRenderHost.LumenRenderPassId) unsupported++;
        }
        if (unsupported > 0)
            Log.Error($"[RimLumen] Unsupported routed persistent draws: {unsupported}. Public-only copy is unavailable; submit dynamic MeshDrawCommand instead.");
        // Particles were staged on the main thread in BeginCapture and their mesh upload was just drained
        // by CaptureFlushedSnapshot, so they join this frame's sorted pass-0 list instead of recording
        // their own command-buffer work inside the HDR pass.
        L1Renderer.AppendDraws(draws, maker, snapshot.PropertyArena, draws.Count, captureIndex);
        foreach (ILumenFrameContributor contributor in contributors)
            if (contributor is ILumenHdrDrawAppender appender)
                appender.AppendHdrDraws(captureIndex, draws, maker, snapshot.PropertyArena, draws.Count, DepthProxy);
        var mesh = new MeshDrawFrame(timing.FrameNumber, view, draws, MeshDrawFrame.RentList(), Vector3.UnitY);

        ShaderPackSpec activePack = RimLumenRenderHost.ActiveShaderPack;
        bool runOnEmptyFrames = activePack.RunOnEmptyFrames;
        if (HdrFrameGate.Decide(contributors.Length > 0, draws.Count, runOnEmptyFrames) == HdrFrameMode.Skip)
        {
            mesh.Dispose();
            RimLumenRenderHost.SetFrameCapabilities(false, false);
            return null;
        }
        bool hasDirectHdr = HasDirectHdrContributor(contributors);
        bool hasHdrDraws = HdrFrameGate.ShouldRenderHdr(draws.Count, hasDirectHdr);
        bool renderHdr = HdrFrameGate.ShouldRenderHdr(draws.Count, hasDirectHdr, runOnEmptyFrames);
        if (!renderHdr) RimLumenRenderHost.SetFrameCapabilities(false, false);

        Map? map = Find.CurrentMap;
        int mapSizeX = map?.Size.x ?? 0;
        int mapSizeZ = map?.Size.z ?? 0;
        float skyGlow = map?.skyManager?.CurSkyGlow ?? 1f;
        float zoomRootSize = Current.CameraDriver?.ZoomRootSize ?? 24f;
        bool isPaused = Current.Game?.tickManager?.Paused ?? false;
        bool hasSceneDepth = activePack.RequiresSceneDepth || hasHdrDraws;
        bool hasMapLightField = RimLumenRenderHost.IsMapLightFieldActive && map is not null;
        var extent = App.RenderContext!.Extent;
        ShaderPackUniformBlockSnapshot rtParams = RimLumenRenderHost.ShaderPackParams.CaptureSnapshot();
        Matrix4x4 viewProjection = view.ViewProjection;
        ShaderPackUniforms uniforms = ShaderPackUniforms.Build(
            in viewProjection,
            (int)extent.Width,
            (int)extent.Height,
            mapSizeX,
            mapSizeZ,
            (float)timing.ElapsedTime,
            skyGlow,
            zoomRootSize,
            hasHdrDraws,
            hasSceneDepth,
            hasMapLightField,
            isPaused,
            in rtParams);

        Captured = new PipelineFrame(captureIndex, mesh, contributors, depthParams, mapIndex, renderHdr,
            hasHdrDraws, hasSceneDepth, activePack.BloomMipLevels, uniforms);
        return Captured;
    }

    internal static bool HasDirectHdrContributor(IEnumerable<ILumenFrameContributor> frameContributors)
        => frameContributors.Any(contributor => contributor is not CpuParticleSimulator
            and not LumenVfxService and not ILumenHdrDrawAppender);

    internal void Recorded() => RimLumenRenderHost.SetFrameCapabilities(true, true);

    private void Quiesce()
    {
        // EngineApplication.Dispose:866 runs other features before BeforeGraphicsShutdown:868 inside
        // one catch-all. Retire our work earlier too, so another feature throwing cannot skip cleanup.
        App.WaitForRenderThreadIdle();
        App.GraphicsDevice?.WaitIdleAndRun(() => { });
        Detach();
        Dispose();
    }

    private void Shutdown(EngineApplication _) => Dispose();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        App.BeforeGraphicsShutdown -= Shutdown;
        App.OnShutdown -= Quiesce;
        LumenSceneBootstrap.Simulator.HdrRendererAttached = false;
        Release("L1Particles", L1Renderer.Dispose);
        Release("MapLightField", () => MapLightFieldCollector.DisposeGpu(App));
        Release("DepthProxy", DepthProxy.Dispose);
        // Destroy framebuffers/textures before the render passes and their pipelines.
        Release("HdrTargets", Targets.Dispose);
        Release("ShaderPackRuntime", () => ShaderPack.Dispose());
        Release("PreBackbuffer", Pre.Dispose);
        Release("Composite", Composite.Dispose);
        Log.Message("[RimLumen] Vulkan resource shutdown completed.");
    }

    private static void Release(string name, Action release)
    {
        try { release(); }
        catch (Exception ex) { Log.Error("[RimLumen] Vulkan resource shutdown failed: " + name + ": " + ex); }
    }
}

internal sealed class PipelineFrame(
    ulong index,
    MeshDrawFrame mesh,
    ILumenFrameContributor[] contributors,
    Vector4 depthParams,
    int mapIndex,
    bool renderHdr,
    bool hasHdrDraws,
    bool hasSceneDepth,
    int bloomMipLevels,
    ShaderPackUniforms uniforms) : IDisposable
{
    internal bool RenderHdr { get; } = renderHdr;
    internal bool HasHdrDraws { get; } = hasHdrDraws;
    internal bool HasSceneDepth { get; } = hasSceneDepth;
    internal int BloomMipLevels { get; } = bloomMipLevels;
    internal ShaderPackUniforms Uniforms { get; set; } = uniforms;
    internal int MapIndex { get; } = mapIndex;
    internal ulong Index { get; } = index;
    internal MeshDrawFrame Mesh { get; } = mesh;
    internal ILumenFrameContributor[] Contributors { get; } = contributors;
    internal Vector4 DepthParams { get; } = depthParams;
    public void Dispose() => Mesh.Dispose();
}
