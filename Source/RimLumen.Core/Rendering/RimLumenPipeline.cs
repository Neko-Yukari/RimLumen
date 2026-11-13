using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    internal PipelineFrame? Captured;
    internal L1ParticleBatchRenderer L1Renderer { get; private set; } = null!;
    internal bool MeasureContributors;
    internal readonly Dictionary<string, List<double>> ContributorMilliseconds = new();
    internal long CompatCapturedItems;
    internal long CompatOptedOut;
    internal long CompatUnresolvedDef;
    internal int UnsupportedPersistentDraws { get; private set; }
    internal int LastRenderedSlot { get; private set; } = -1;
    private ulong captureIndex;
    private CameraView view;
    private Vector4 depthParams;
    private ILumenFrameContributor[] contributors = Array.Empty<ILumenFrameContributor>();
    private bool disposed;

    internal RimLumenPipeline(EngineApplication app)
    {
        App = app;
        SceneComposite = app.FrameRenderer.Features.SelectMany(feature => feature.Passes).OfType<CompositePass>().Single();
        Targets = new HdrTargets(app);
        Pre = new RimLumenPreBackbufferFeature(this);
        Composite = new RimLumenCompositeFeature(this);
        app.BeforeGraphicsShutdown += Shutdown;
        app.OnShutdown += Quiesce;
    }

    internal void Attach()
    {
        LumenSceneBootstrap.EnsureInitialized();
        L1Renderer ??= new L1ParticleBatchRenderer(App);
        LumenSceneBootstrap.Simulator.BatchSink = L1Renderer;
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

    internal void GuardCompositeOrder()
    {
        IRenderFeature[] features = App.FrameRenderer.Features.ToArray();
        int index = FeatureOrderGuard.ComputeCompositeReinsertIndex(features.Select(feature => feature.GetType().Name).ToArray(),
            Array.IndexOf(features, Composite));
        if (index < 0) return;
        App.FrameRenderer.RemoveFeature(Composite);
        App.FrameRenderer.InsertFeature(index, Composite);
    }

    internal void BeginCapture(in FrameTiming timing)
    {
        // FrameRenderer.BeginMainThreadFrame:220-230 runs all early hooks before CaptureFrame:186-192.
        // This is before ComputeRenderFeature drains Enqueue, even though our pass is after Map.
        captureIndex = (ulong)timing.FrameNumber;
        contributors = Array.Empty<ILumenFrameContributor>();
        var camera = Current.CameraDriver?.MyCamera;
        if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || camera == null)
            return;
        view = CameraViewFactory.GetView(camera);
        depthParams = new Vector4(camera.nearClipPlane, camera.farClipPlane, camera.orthographic ? 1f : 0f, 1f);
        contributors = LumenHostRegistry.GetContributorsSnapshot().Where(contributor => contributor.HasActiveWork).ToArray();
        var extent = App.RenderContext!.Extent;
        var capture = new LumenCaptureContext
        {
            FrameIndex = captureIndex, DeltaTime = timing.DeltaTime,
            ViewportWidth = extent.Width, ViewportHeight = extent.Height,
            ViewMatrix = view.View, ProjectionMatrix = view.Projection,
            LumenRenderPassId = RimLumenRenderHost.LumenRenderPassId
        };
        var compute = new LumenComputeContext { FrameIndex = captureIndex, DeltaTime = timing.DeltaTime };
        foreach (ILumenFrameContributor contributor in contributors)
        {
            long start = MeasureContributors ? Stopwatch.GetTimestamp() : 0;
            contributor.CaptureMainThread(in capture);
            contributor.EnqueuePreCompute(in compute);
            if (MeasureContributors)
            {
                if (!ContributorMilliseconds.TryGetValue(contributor.Name, out var samples))
                    ContributorMilliseconds.Add(contributor.Name, samples = new List<double>());
                samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            if (ReferenceEquals(contributor, LumenSceneBootstrap.Simulator))
                L1Renderer.Stage(captureIndex, LumenSceneBootstrap.Simulator.Vertices, LumenSceneBootstrap.Simulator.Indices);
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
                FinalDrawItem? candidate = maker.TryMake(in command, snapshot.PropertyArena, UnityEngine.Shader.GlobalKeywordKey, i);
                if (candidate == null) continue;
                bool keep = AdditiveEmissiveFilter.IsAdditiveEmissive(candidate.Binding.PipelineState, candidate.Binding.Shader.Key);
                if (keep)
                {
                    ShaderDef? def = DefDatabase<ShaderDef>.GetNamedSilentFail(candidate.Binding.Shader.Key);
                    LumenEmissiveModExtension? extension = def?.GetModExtension<LumenEmissiveModExtension>();
                    if (extension == null) CompatUnresolvedDef++;
                    else if (!extension.EnableHdrBloom) { CompatOptedOut++; keep = false; }
                }
                FinalDrawItem.Return(candidate);
                if (!keep) continue;
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
            if (snapshot.Commands[i].PassId != RimLumenRenderHost.LumenRenderPassId) CompatCapturedItems++;
            draws.Add(draw);
        }
        int unsupported = snapshot.PersistentSources.Sum(source => source.DrawItemsReadOnly.Count(draw => draw.PassId == RimLumenRenderHost.LumenRenderPassId));
        if (unsupported > 0)
        {
            UnsupportedPersistentDraws += unsupported;
            Log.Error($"[RimLumen] Unsupported routed persistent draws: {unsupported}. Public-only copy is unavailable; submit dynamic MeshDrawCommand instead.");
        }
        var mesh = new MeshDrawFrame(timing.FrameNumber, view, draws, MeshDrawFrame.RentList(), Vector3.UnitY);
        if (HdrFrameGate.Decide(contributors.Length > 0, draws.Count, false) == HdrFrameMode.Skip)
        {
            mesh.Dispose();
            RimLumenRenderHost.SetFrameCapabilities(false, false);
            return null;
        }
        Captured = new PipelineFrame(captureIndex, mesh, contributors, depthParams);
        return Captured;
    }

    internal void Recorded(int slot)
    {
        LastRenderedSlot = slot;
        RimLumenRenderHost.SetFrameCapabilities(true, true);
    }

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
        LumenSceneBootstrap.Simulator.BatchSink = null;
        Release("L1Particles", L1Renderer.Dispose);
        // Destroy framebuffers/textures before the render passes and their pipelines.
        Release("HdrTargets", Targets.Dispose);
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

internal sealed class PipelineFrame(ulong index, MeshDrawFrame mesh, ILumenFrameContributor[] contributors, Vector4 depthParams) : IDisposable
{
    internal ulong Index { get; } = index;
    internal MeshDrawFrame Mesh { get; } = mesh;
    internal ILumenFrameContributor[] Contributors { get; } = contributors;
    internal Vector4 DepthParams { get; } = depthParams;
    public void Dispose() => Mesh.Dispose();
}
