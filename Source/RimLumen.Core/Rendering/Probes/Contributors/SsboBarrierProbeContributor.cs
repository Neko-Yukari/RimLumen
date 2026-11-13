using System;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Rendering.Probes.Contributors;

// T1b: in-frame ComputeDispatchQueue.Enqueue writes per-instance quad data into a GpuComputeBuffer,
// a VkBufferMemoryBarrier (ComputeShader -> VertexShader|DrawIndirect, ShaderWrite -> ShaderRead|IndirectCommandRead)
// is recorded outside the render pass, then the HDR pass draws instances by reading that SSBO in the vertex shader.
public sealed class SsboBarrierProbeContributor : ILumenFrameContributor, IDisposable
{
    // Requires: disableBarrier only for the red/control run (-rimlumen-probe-nobarrier).
    public SsboBarrierProbeContributor(bool disableBarrier)
    {
        throw new NotImplementedException();
    }

    public string Name => "RimLumen.Probe.SsboBarrier";

    // Ensures: true while the probe is armed (Arm() called, Disarm() not yet).
    public bool HasActiveWork => throw new NotImplementedException();

    // Counters read by the probe session after the run; monotonically increasing.
    public int DispatchesEnqueued => throw new NotImplementedException();
    public int BarriersRecorded => throw new NotImplementedException();
    public int DrawsRecorded => throw new NotImplementedException();

    // Requires: main thread. Ensures: HasActiveWork becomes true from the next CaptureMainThread.
    public void Arm() => throw new NotImplementedException();

    // Requires: main thread. Ensures: HasActiveWork false; GPU resources kept until Dispose.
    public void Disarm() => throw new NotImplementedException();

    public void CaptureMainThread(in LumenCaptureContext context) => throw new NotImplementedException();

    public void EnqueuePreCompute(in LumenComputeContext context) => throw new NotImplementedException();

    public void RecordBufferBarriers(in LumenBarrierContext context) => throw new NotImplementedException();

    public void RecordHdrDraws(in LumenHdrDrawContext context) => throw new NotImplementedException();

    // Ensures: GPU buffers/pipelines released via the engine's deferred disposal.
    public void Dispose() => throw new NotImplementedException();
}
