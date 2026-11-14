using System.Numerics;
using Waylith.Rendering;

namespace RimLumen.Core.Contracts;

public readonly struct LumenCaptureContext
{
    public LumenCaptureContext() { }
    public int CurrentMapIndex { get; init; } = -1;
    public required ulong FrameIndex { get; init; }
    public required float DeltaTime { get; init; }
    public float UnscaledDeltaTime { get; init; }
    public required int ViewportWidth { get; init; }
    public required int ViewportHeight { get; init; }
    public required Matrix4x4 ViewMatrix { get; init; }
    public required Matrix4x4 ProjectionMatrix { get; init; }
    public required byte LumenRenderPassId { get; init; }
}

public readonly struct LumenComputeContext
{
    public required ulong FrameIndex { get; init; }
    public required float DeltaTime { get; init; }
    public float UnscaledDeltaTime { get; init; }
}

public readonly struct LumenBarrierContext
{
    public required FrameContext Frame { get; init; }
    public required ulong FrameIndex { get; init; }
}

public readonly struct LumenHdrDrawContext
{
    public LumenHdrDrawContext() { }
    public int CurrentMapIndex { get; init; } = -1;
    public required FrameContext Frame { get; init; }
    public required ulong FrameIndex { get; init; }
    public required RenderTexture HdrColorTarget { get; init; }
    public required RenderTexture? SceneColorDepthSource { get; init; }
    public required Matrix4x4 ViewProjection { get; init; }
    public required Vector4 SoftParticleDepthParams { get; init; }
}

public interface ILumenFrameContributor
{
    string Name { get; }

    // Ensures: returns false when idle so the HDR pipeline can short-circuit with zero draws/clears.
    bool HasActiveWork { get; }

    // Requires: called on main thread during frame capture.
    // Ensures: snapshots per-frame draw/simulation inputs without mutating engine pass state.
    void CaptureMainThread(in LumenCaptureContext context);

    // Requires: HasActiveWork was true during CaptureMainThread for this frame.
    // Ensures: enqueues any GPU compute dispatches needed before the VFX pass.
    void EnqueuePreCompute(in LumenComputeContext context);

    // Requires: Frame.CommandBuffer is open outside a render pass before RimLumenVfxPass begins.
    // Ensures: records storage/indirect buffer memory barriers for dispatches enqueued this frame.
    void RecordBufferBarriers(in LumenBarrierContext context);

    // Requires: called inside RimLumenVfxPass (RGBA16F color + read-only scene depth).
    // Ensures: records HDR draws using pipelines compatible with Frame.RenderPass.
    void RecordHdrDraws(in LumenHdrDrawContext context);
}

public interface ILumenGpuParticleEngine : ILumenFrameContributor
{
    // Requires: called on main thread; spec.MaxParticles > 0.
    // Ensures: allocates or rents an L2 GPU emitter slot and returns its non-negative runtime id.
    int CreateInstance(EmitterSpec spec, Matrix4x4 initialWorldTransform);

    int CreateInstance(EmitterSpec spec, Matrix4x4 initialWorldTransform, int mapIndex, bool realTime)
        => CreateInstance(spec, initialWorldTransform);

    // Ensures: updates emitter transform and active emission state for next frame capture.
    void UpdateInstance(int instanceId, in Matrix4x4 worldTransform, bool emitting, float intensityMultiplier = 1f);

    // Ensures: stops emission or reclaims GPU slot when particles expire.
    void DestroyInstance(int instanceId, bool immediate = false);
}
