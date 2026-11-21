using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Rendering;
using RimLumen.Core.Scene;
using Verse;
using Waylith;
using Waylith.Rendering;

namespace RimLumen.Core.Ribbons;

// Registers the ribbon backend and its HDR frame contributor once at game start.
[StaticConstructorOnStartup]
internal static class LumenRibbonBootstrap
{
    static LumenRibbonBootstrap()
    {
        var backend = new LumenRibbonBackend();
        var contributor = new LumenRibbonContributor(backend, EngineApplication.Instance);
        LumenHostRegistry.RegisterNodeFeatureBackend(backend);
        // The VFX service contributor must push this frame's node updates before ribbons build their strips.
        LumenSceneBootstrap.EnsureInitialized();
        LumenHostRegistry.RegisterContributor(contributor);
    }
}

// Host shell: builds the backend's strips at capture and stages them in an L1 particle batch renderer, whose mesh
// upload the engine drains before the draw snapshot; AppendHdrDraws then adds them to the RimLumenHdr pass.
internal sealed class LumenRibbonContributor : ILumenFrameContributor, ILumenHdrDrawAppender
{
    private readonly LumenRibbonBackend backend;
    private readonly EngineApplication app;
    private L1ParticleBatchRenderer? batches;
    private LumenColorBlendSpace colorBlendSpace;
    private bool disposed;

    internal LumenRibbonContributor(LumenRibbonBackend backend, EngineApplication app)
    {
        this.backend = backend;
        this.app = app;
        app.BeforeGraphicsShutdown += Shutdown;
        app.OnShutdown += Quiesce;
    }

    public string Name => "RimLumen Ribbons";

    public bool HasActiveWork => backend.HasInstances;

    public void CaptureMainThread(in LumenCaptureContext context)
    {
        backend.Build(context);
        if (!context.RendersMap) return;
        // Only the pipeline calls capture, so it and its depth proxy exist by now.
        batches ??= new L1ParticleBatchRenderer(RimLumenRenderHost.Pipeline!.DepthProxy, app.RenderContext?.FramesInFlight ?? 3);
        colorBlendSpace = context.ColorBlendSpace;
        Matrix4x4 projection = context.ProjectionMatrix;
        // Same soft-fade depth parameters as RimLumenPipeline.BeginCapture (:132), which captures only with a camera.
        var camera = Current.CameraDriver.MyCamera;
        batches.Stage(context.FrameIndex, backend.Vertices, backend.Indices, backend.SubBatches,
            new Vector4(camera.nearClipPlane, camera.farClipPlane, camera.orthographic ? 1f : 0f, 1f),
            new Vector4(projection.M33, projection.M34, projection.M43, projection.M44));
    }

    public void EnqueuePreCompute(in LumenComputeContext context) { }
    public void RecordBufferBarriers(in LumenBarrierContext context) { }
    public void RecordHdrDraws(in LumenHdrDrawContext context) { }

    void ILumenHdrDrawAppender.AppendHdrDraws(ulong frameIndex, List<FinalDrawItem> draws, DrawItemFactory maker,
        DrawPropertyArena arena, int queueOrder, Texture sceneDepth)
        => batches!.AppendDraws(draws, maker, arena, queueOrder, frameIndex, colorBlendSpace);

    // Same order as RimLumenPipeline.Quiesce: retire GPU use before another feature's shutdown can throw.
    private void Quiesce()
    {
        app.WaitForRenderThreadIdle();
        app.GraphicsDevice?.WaitIdleAndRun(() => { });
        Shutdown(app);
    }

    private void Shutdown(EngineApplication _)
    {
        if (disposed) return;
        disposed = true;
        app.BeforeGraphicsShutdown -= Shutdown;
        app.OnShutdown -= Quiesce;
        LumenHostRegistry.UnregisterContributor(this);
        batches?.Dispose();
    }
}
