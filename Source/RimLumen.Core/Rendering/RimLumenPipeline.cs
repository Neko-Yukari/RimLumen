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

namespace RimLumen.Core.Rendering;

internal sealed class RimLumenPipeline : IDisposable
{
    internal readonly EngineApplication App;
    internal readonly RimLumenPreBackbufferFeature Pre;
    internal readonly HdrTargets Targets;
    // Mirrors the current submission slot's R32_SFLOAT depth snapshot so particle materials can sample it.
    internal readonly BorrowedRenderTexture DepthProxy = new();
    internal ShaderPackRuntime ShaderPack { get; private set; }
    internal bool HasShaderPackRuntime => !disposed && ShaderPack is not null;
    internal bool IsDisposed => disposed;
    internal PipelineFrame? Captured;
    internal L1ParticleBatchRenderer L1Renderer { get; private set; } = null!;
    internal LightPoolRenderer LightPool { get; private set; } = null!;
    private ulong captureIndex;
    private float gameSeconds;
    private float gameDeltaSeconds;
    private float realDeltaSeconds;
    private CameraView view;
    private Vector4 depthParams;
    private int mapIndex = -1;
    private LumenColorBlendSpace colorBlendSpace;
    private ILumenFrameContributor[] contributors = Array.Empty<ILumenFrameContributor>();
    private ILumenFrameContributor[] activeContributors = Array.Empty<ILumenFrameContributor>();
    private readonly List<ILumenFrameContributor> activeContributorBuffer = new();
    // Main-thread scratch ownership for appenders; moved into a rented MeshDrawFrame only when non-empty.
    private readonly List<FinalDrawItem> scratchDraws = new();
    internal readonly RimLumenMapChain MapChain;
    private bool disposed;

    internal RimLumenPipeline(EngineApplication app)
    {
        App = app;
        Targets = new HdrTargets(app);
        ShaderPack = new ShaderPackRuntime(app, RimLumenRenderHost.ActiveShaderPack);
        MapChain = new RimLumenMapChain(this);
        Pre = new RimLumenPreBackbufferFeature(this);
        RimLumenRenderHost.LatchFrameSettings();
        app.PrePollEvents += LatchFrameSettings;
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
        LightPool ??= new LightPoolRenderer();
        LumenSceneBootstrap.Simulator.HdrRendererAttached = true;
        string[] names = App.FrameRenderer.Features.Select(feature => feature.GetType().Name).ToArray();
        int index = FeatureOrderGuard.ComputePreBackbufferInsertIndex(names);
        if (index < 0) throw new InvalidOperationException("MapRenderFeature is unavailable.");
        App.FrameRenderer.InsertFeature(index, Pre);
    }

    internal void Detach()
    {
        App.WaitForRenderThreadIdle();
        App.FrameRenderer.RemoveFeature(Pre);
    }

    // Ensures: any thread. Removal is queued for the main thread: the render thread must not edit the feature list, and Detach()
    //   would deadlock there on WaitForRenderThreadIdle. Frames already captured keep their own feature snapshot and no-op once tripped.
    internal void DetachAfterFailSafe() => App.FrameRenderer.RenderToMainThread.Enqueue(() =>
    {
        App.FrameRenderer.RemoveFeature(Pre);
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

    internal void BeginCapture(in FrameTiming timing)
    {
        // FrameRenderer.BeginMainThreadFrame:220-230 runs all early hooks before CaptureFrame:186-192.
        // This is before ComputeRenderFeature drains Enqueue.
        captureIndex = (ulong)timing.FrameNumber;
        colorBlendSpace = RimLumenRenderHost.CurrentFrameColorBlendSpace;
        mapIndex = -1;
        contributors = Array.Empty<ILumenFrameContributor>();
        var camera = Current.CameraDriver?.MyCamera;
        if (Current.ProgramState != ProgramState.Playing || Current.Game == null || Find.CurrentMap == null || camera == null)
        {
            MapLightFieldCollector.BeginCapture(App, null);
            return;
        }
        MapLightFieldCollector.BeginCapture(App, Find.CurrentMap);
        // TickManagerUpdate counts completed ticks; its time budget may run fewer than the requested speed.
        TickManager tickManager = Current.Game.tickManager;
        float unscaledDt = MathF.Max(0f, timing.DeltaTime);
        // Root_Play skips Game.UpdatePlay while waiting; TicksThisFrame then still belongs to the previous update.
        float simDt = LongEventHandler.ShouldWaitForEvent ? 0f : tickManager.TicksThisFrame / 60f;
        gameSeconds = tickManager.TicksGame / 60f;
        gameDeltaSeconds = simDt;
        realDeltaSeconds = unscaledDt;
        view = CameraViewFactory.GetView(camera);
        depthParams = new Vector4(camera.nearClipPlane, camera.farClipPlane, camera.orthographic ? 1f : 0f, 1f);
        var extent = App.RenderContext!.Extent;
        int currentMapIndex = Find.CurrentMap?.uniqueID ?? -1;
        mapIndex = currentMapIndex;
        var capture = new LumenCaptureContext
        {
            CurrentMapIndex = currentMapIndex,
            ColorBlendSpace = colorBlendSpace,
            FrameIndex = captureIndex, DeltaTime = simDt, UnscaledDeltaTime = unscaledDt,
            ViewportWidth = extent.Width, ViewportHeight = extent.Height,
            ViewMatrix = view.View, ProjectionMatrix = view.Projection,
            LumenRenderPassId = RimLumenRenderHost.LumenRenderPassId
        };
        var compute = new LumenComputeContext { FrameIndex = captureIndex, DeltaTime = simDt, UnscaledDeltaTime = unscaledDt };
        activeContributorBuffer.Clear();
        foreach (ILumenFrameContributor contributor in LumenHostRegistry.GetSharedContributorsSnapshot())
        {
            // The service can activate a later contributor in this very capture.
            if (!contributor.HasActiveWork) continue;
            activeContributorBuffer.Add(contributor);
            contributor.CaptureMainThread(in capture);
            contributor.EnqueuePreCompute(in compute);
            if (ReferenceEquals(contributor, LumenSceneBootstrap.Simulator))
            {
                Matrix4x4 proj = view.Projection;
                L1Renderer.Stage(captureIndex, LumenSceneBootstrap.Simulator.Vertices, LumenSceneBootstrap.Simulator.Indices,
                    LumenSceneBootstrap.Simulator.SubBatches, depthParams,
                    new Vector4(proj.M33, proj.M34, proj.M43, proj.M44));
            }
            if (ReferenceEquals(contributor, LumenSceneBootstrap.Service))
                LightPool.Stage(captureIndex, LumenSceneBootstrap.Service.Lights);
        }
        bool changed = activeContributorBuffer.Count != activeContributors.Length;
        for (int i = 0; !changed && i < activeContributors.Length; i++)
            changed = !ReferenceEquals(activeContributorBuffer[i], activeContributors[i]);
        // Captured frames may still be on the render thread; publish replacements, never mutate their arrays.
        if (changed) activeContributors = activeContributorBuffer.ToArray();
        contributors = activeContributors;
    }

    internal PipelineFrame? Capture(in FrameTiming timing)
    {
        Captured = null;
        if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || Current.CameraDriver?.MyCamera == null)
            return null;
        MeshDrawSubmissionSnapshot snapshot = MeshDrawSubmission.CaptureFlushedSnapshot(App.GraphicsDevice!);
        MeshDrawFrame? mesh = null, glowMesh = null, lightMesh = null, distortionMesh = null;
        try
        {
            Captured = CaptureFrame(in timing, snapshot, ref mesh, ref glowMesh, ref lightMesh, ref distortionMesh);
            return Captured;
        }
        finally
        {
            if (Captured is null)
            {
                mesh?.Dispose();
                glowMesh?.Dispose();
                lightMesh?.Dispose();
                distortionMesh?.Dispose();
            }
        }
    }

    private PipelineFrame? CaptureFrame(in FrameTiming timing, MeshDrawSubmissionSnapshot snapshot,
        ref MeshDrawFrame? mesh, ref MeshDrawFrame? glowMesh, ref MeshDrawFrame? lightMesh,
        ref MeshDrawFrame? distortionMesh)
    {
        try
        {
            DrawItemFactory maker = MeshDrawSubmission.DrawItemMaker ?? throw new InvalidOperationException("DrawItemFactory is unavailable.");
        for (int i = 0; i < snapshot.CommandCount; i++)
        {
            MeshDrawCommand command = snapshot.Commands[i];
            if (command.PassId == RimLumenRenderHost.DistortionPassId)
            {
                FinalDrawItem? offset = maker.TryMake(in command, snapshot.PropertyArena, UnityEngine.Shader.GlobalKeywordKey, i);
                if (offset is null) continue;
                if (offset.RenderQueue == int.MaxValue) FinalDrawItem.Return(offset);
                else
                {
                    distortionMesh ??= new MeshDrawFrame(timing.FrameNumber, view, MeshDrawFrame.RentList(), MeshDrawFrame.RentList(), Vector3.UnitY);
                    distortionMesh.Draws.Add(offset);
                }
                continue;
            }
            bool vanillaGlow = command.PassId != RimLumenRenderHost.LumenRenderPassId;
            if (vanillaGlow)
            {
                if (!RimLumenRenderHost.EnableVanillaAdditiveBloom ||
                    (command.PassId != 0 && command.PassId != RimWorldWaylith.Rendering.RenderPassIds.MapMain)) continue;
                try
                {
                    FinalDrawItem? candidate = maker.TryMake(in command, snapshot.PropertyArena, UnityEngine.Shader.GlobalKeywordKey, i);
                    if (candidate == null) continue;
                    bool keep = candidate.RenderQueue < int.MaxValue &&
                        AdditiveEmissiveFilter.IsAdditiveEmissive(candidate.Binding.PipelineState, candidate.Binding.Shader.Key);
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
                    // Drop the copies this frame already took too, so the tripping frame shows no enhancement either.
                    if (glowMesh is not null)
                    {
                        foreach (FinalDrawItem copy in glowMesh.Draws) FinalDrawItem.Return(copy);
                        glowMesh.Draws.Clear();
                    }
                    continue;
                }
                // Preserve the original MapMain submission; only this fresh glow copy changes pass.
                command = new MeshDrawCommand(command.Mesh, command.Material, command.PropertyRange, command.Matrix,
                    command.SubmeshIndex, RimLumenRenderHost.LumenRenderPassId, command.Instancing,
                    command.RenderQueueOverride, command.Layer, command.Storage);
            }
            FinalDrawItem? draw = maker.TryMake(in command, snapshot.PropertyArena, UnityEngine.Shader.GlobalKeywordKey, i);
            if (draw == null) continue;
            if (draw.RenderQueue == int.MaxValue)
            {
                FinalDrawItem.Return(draw);
                continue;
            }
            if (draw.Binding.PipelineState.Depth.Write)
            {
                FinalDrawItem.Return(draw);
                throw new InvalidOperationException("RimLumen routed materials must not write shared scene depth.");
            }
            if (vanillaGlow)
            {
                glowMesh ??= NewFrame(in timing);
                glowMesh.Draws.Add(draw);
            }
            else
            {
                mesh ??= NewFrame(in timing);
                mesh.Draws.Add(draw);
            }
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
        try
        {
            L1Renderer.AppendDraws(scratchDraws, maker, snapshot.PropertyArena, mesh?.Draws.Count ?? 0,
                captureIndex, colorBlendSpace);
            MoveScratchDraws(ref mesh, in timing);
        }
        finally { ReturnScratchDraws(); }
        foreach (ILumenFrameContributor contributor in contributors)
            if (contributor is ILumenHdrDrawAppender appender)
            {
                try
                {
                    appender.AppendHdrDraws(captureIndex, scratchDraws, maker, snapshot.PropertyArena,
                        mesh?.Draws.Count ?? 0, DepthProxy);
                    MoveScratchDraws(ref mesh, in timing);
                }
                finally { ReturnScratchDraws(); }
            }
        try
        {
            LightPool.AppendDraws(scratchDraws, maker, snapshot.PropertyArena, captureIndex);
            MoveScratchDraws(ref lightMesh, in timing);
        }
        finally { ReturnScratchDraws(); }

        ShaderPackSpec activePack = RimLumenRenderHost.ActiveShaderPack;
        bool runOnEmptyFrames = activePack.RunOnEmptyFrames;
        GlowGroupMask glowGroups = GlowGroupMask.None;
        int earlyGlowCount = 0;
        if (glowMesh is not null)
            foreach (FinalDrawItem glow in glowMesh.Draws)
            {
                GlowGroup group = GlowGroups.Classify(glow.RenderQueue);
                glowGroups |= GlowGroups.Mask(group);
                if (group == GlowGroup.Early) earlyGlowCount++;
            }
        int routedDrawCount = (mesh?.Draws.Count ?? 0) + earlyGlowCount;
        int distortionDrawCount = distortionMesh?.Draws.Count ?? 0;
        bool earlyWork = HdrFrameGate.Decide(contributors.Length > 0, routedDrawCount, distortionDrawCount, runOnEmptyFrames)
            != HdrFrameMode.Skip;
        bool lateWork = activePack.BloomMipLevels > 0 && (glowGroups & ~GlowGroupMask.Early) != 0;
        if (!earlyWork && !lateWork)
        {
            RimLumenRenderHost.SetFrameCapabilities(false, false);
            return null;
        }
        bool hasDirectHdr = HasDirectHdrContributor(contributors);
        bool hasHdrDraws = HdrFrameGate.ShouldRenderHdr(routedDrawCount, hasDirectHdr);
        bool renderHdr = HdrFrameGate.ShouldRenderHdr(routedDrawCount, hasDirectHdr, runOnEmptyFrames, distortionDrawCount);
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
            gameSeconds,
            (float)timing.ElapsedTime,
            gameDeltaSeconds,
            realDeltaSeconds,
            skyGlow,
            zoomRootSize,
            hasHdrDraws,
            hasSceneDepth,
            hasMapLightField,
            isPaused,
            colorBlendSpace == LumenColorBlendSpace.Linear,
            RimLumenRenderHost.CurrentFrameDistortionRefractsEffects,
            in rtParams);

        return new PipelineFrame(captureIndex, view, mesh, glowMesh, lightMesh, distortionMesh, contributors, depthParams, mapIndex, renderHdr,
            hasHdrDraws, hasSceneDepth, activePack.BloomMipLevels, uniforms, glowGroups, earlyWork, colorBlendSpace);
        }
        finally
        {
            ReturnScratchDraws();
        }
    }

    private MeshDrawFrame NewFrame(in FrameTiming timing) => new(timing.FrameNumber, view,
        MeshDrawFrame.RentList(), MeshDrawFrame.RentList(), Vector3.UnitY);

    private void MoveScratchDraws(ref MeshDrawFrame? frame, in FrameTiming timing)
    {
        if (scratchDraws.Count == 0) return;
        frame ??= NewFrame(in timing);
        frame.Draws.AddRange(scratchDraws);
        scratchDraws.Clear();
    }

    private void ReturnScratchDraws()
    {
        foreach (FinalDrawItem draw in scratchDraws) FinalDrawItem.Return(draw);
        scratchDraws.Clear();
    }

    internal static bool HasDirectHdrContributor(IEnumerable<ILumenFrameContributor> frameContributors)
        => frameContributors.Any(contributor => contributor.RequiresDirectHdrPass);

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

    private static void LatchFrameSettings(EngineApplication _) => RimLumenRenderHost.LatchFrameSettings();

    private void Shutdown(EngineApplication _) => Dispose();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        App.PrePollEvents -= LatchFrameSettings;
        App.BeforeGraphicsShutdown -= Shutdown;
        App.OnShutdown -= Quiesce;
        LumenSceneBootstrap.Simulator.HdrRendererAttached = false;
        Release("L1Particles", L1Renderer.Dispose);
        Release("LightPool", LightPool.Dispose);
        Release("MapLightField", () => MapLightFieldCollector.DisposeGpu(App));
        Release("DepthProxy", DepthProxy.Dispose);
        // Destroy framebuffers/textures before the render passes and their pipelines.
        Release("HdrTargets", Targets.Dispose);
        Release("ShaderPackRuntime", () => ShaderPack.Dispose());
        Release("MapChain", MapChain.Dispose);
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
    CameraView view,
    MeshDrawFrame? mesh,
    MeshDrawFrame? glowMesh,
    MeshDrawFrame? lightMesh,
    MeshDrawFrame? distortionMesh,
    ILumenFrameContributor[] contributors,
    Vector4 depthParams,
    int mapIndex,
    bool renderHdr,
    bool hasHdrDraws,
    bool hasSceneDepth,
    int bloomMipLevels,
    ShaderPackUniforms uniforms,
    GlowGroupMask glowGroups,
    bool earlyWork,
    LumenColorBlendSpace colorBlendSpace) : IDisposable
{
    internal LumenColorBlendSpace ColorBlendSpace { get; } = colorBlendSpace;
    internal CameraView View { get; } = view;
    internal bool RenderHdr { get; } = renderHdr;
    internal bool HasHdrDraws { get; } = hasHdrDraws;
    internal bool HasSceneDepth { get; } = hasSceneDepth;
    internal int BloomMipLevels { get; } = bloomMipLevels;
    internal ShaderPackUniforms Uniforms { get; set; } = uniforms;
    internal int MapIndex { get; } = mapIndex;
    internal ulong Index { get; } = index;
    internal MeshDrawFrame? Mesh { get; } = mesh;
    internal MeshDrawFrame? GlowMesh { get; } = glowMesh;
    internal MeshDrawFrame? LightMesh { get; } = lightMesh;
    internal bool HasLightPools => LightMesh is not null;
    internal MeshDrawFrame? DistortionMesh { get; } = distortionMesh;
    internal bool HasGlowDraws => (GlowGroupsCaptured & GlowGroupMask.Early) != 0;
    internal GlowGroupMask GlowGroupsCaptured { get; } = glowGroups;
    internal bool ShouldSplit(GlowGroup group) => GlowGroups.ShouldSplit(group, GlowGroupsCaptured, earlyWork, BloomMipLevels);
    internal ILumenFrameContributor[] Contributors { get; } = contributors;
    internal Vector4 DepthParams { get; } = depthParams;

    public void Dispose()
    {
        Mesh?.Dispose();
        GlowMesh?.Dispose();
        LightMesh?.Dispose();
        DistortionMesh?.Dispose();
    }
}
