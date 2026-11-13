using System;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;
using Waylith;

namespace RimLumen.Core.Rendering;

// Draws the L1 CPU quad batch inside the RimLumenHdr pass.
// Threading: CpuParticleSimulator.RecordHdrDraws calls Submit on the render thread, but its vertex/index
// arrays are rewritten (and resized) by the next main-thread CaptureMainThread. So the pipeline stages the
// batch on the main thread right after capture, and Submit only records the draw of the staged frame.
internal sealed class L1ParticleBatchRenderer : IL1ParticleBatchSink, IDisposable
{
    // Requires: main thread; app has a GraphicsDevice and RenderContext.
    // Ensures: allocates no per-frame buffers yet (lazy, sized by Stage).
    internal L1ParticleBatchRenderer(EngineApplication app)
    {
        throw new NotImplementedException();
    }

    // Counters read by the probe session; monotonically increasing.
    internal long VerticesRecorded => throw new NotImplementedException();
    internal int DrawsRecorded => throw new NotImplementedException();

    // Requires: main thread, after the simulator's CaptureMainThread for frameIndex (= (ulong)timing.FrameNumber).
    // Ensures: copies vertices/indices into a host-visible slot owned by frameIndex (slot reuse waits for the
    //   engine fence, like SsboBarrierProbeContributor); vertices.Length == 4n, indices.Length == 6n; n == 0 stages nothing.
    internal void Stage(ulong frameIndex, ReadOnlySpan<ParticleVertex> vertices, ReadOnlySpan<uint> indices)
    {
        throw new NotImplementedException();
    }

    // Requires: render thread, inside the RimLumenHdr pass, before contributors' RecordHdrDraws for context.FrameIndex.
    // Ensures: the next Submit records into context.Frame (pipeline cached per VkRenderPass; premultiplied blend,
    //   depth test reverse-Z GreaterOrEqual, depth write off; soft fade from SceneColorDepthSource via RimLumenDepth.glslinc).
    internal void BeginRecord(in LumenHdrDrawContext context)
    {
        throw new NotImplementedException();
    }

    // Requires: render thread, between BeginRecord and EndRecord.
    // Ensures: records one indexed draw of the batch staged for the current frame; the spans are NOT read
    //   (see threading note). No staged batch or no BeginRecord => records nothing.
    public void Submit(ReadOnlySpan<ParticleVertex> vertices, ReadOnlySpan<uint> indices, int liveParticleCount)
    {
        throw new NotImplementedException();
    }

    // Requires: render thread. Ensures: drops the recording context set by BeginRecord.
    internal void EndRecord()
    {
        throw new NotImplementedException();
    }

    // Requires: render thread idle and GPU idle (called from RimLumenPipeline.Dispose after Quiesce).
    // Ensures: destroys every buffer/pipeline/layout/descriptor pool/sampler it created; idempotent.
    public void Dispose()
    {
        throw new NotImplementedException();
    }
}
