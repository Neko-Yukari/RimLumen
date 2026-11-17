using System.Numerics;
using Waylith.Rendering;

namespace RimLumen.Core.Contracts;

// Native HDR-layer blending, independent of an emitter's material calculation space.
public enum LumenColorBlendSpace : byte
{
    Legacy = 0,
    Linear = 1,
}

/// <summary>Main-thread value snapshot passed to contributors before compute is enqueued.
/// Managed context, not a packed GPU buffer ABI; do not upload its memory verbatim. World distances are map
/// cells, +Y is up. Camera matrices retain Waylith/Unity column-vector semantics despite their Numerics
/// container: clip = Projection * View * worldColumn; there is no implicit transpose here.
/// Copy needed values into contributor-owned immutable frame storage; render recording may lag capture.</summary>
public readonly struct LumenCaptureContext
{
    public LumenCaptureContext() { }
    /// <summary>RimWorld Map.uniqueID, never the index in Find.Maps; -1 means no current map/filter.</summary>
    public int CurrentMapIndex { get; init; } = -1;
    /// <summary>Pre-update frame latch, not the mutable requested setting. Convert straight stored
    /// RGB to this space before premultiplication; retain this value with the captured frame.</summary>
    public LumenColorBlendSpace ColorBlendSpace { get; init; }
    /// <summary>Monotonic host FrameTiming.FrameNumber identifying the captured frame, not the rotating
    /// FrameContext.SubmissionFrameIndex resource slot. Use this identity to pair capture and recording.</summary>
    public required ulong FrameIndex { get; init; }
    /// <summary>Simulation seconds since capture: zero while paused, scaled by the game's tick rate.</summary>
    public required float DeltaTime { get; init; }
    /// <summary>Real seconds since capture, unaffected by pause/tick rate; positive values drive real-time effects.</summary>
    public float UnscaledDeltaTime { get; init; }
    /// <summary>Captured render extent in physical pixels.</summary>
    public required int ViewportWidth { get; init; }
    /// <summary>Captured render extent in physical pixels.</summary>
    public required int ViewportHeight { get; init; }
    /// <summary>World cells to camera space, column-vector convention; camera axes are inverse-view columns.</summary>
    public required Matrix4x4 ViewMatrix { get; init; }
    /// <summary>Camera to clip space using the host's Vulkan projection/depth convention, without transposition.</summary>
    public required Matrix4x4 ProjectionMatrix { get; init; }
    /// <summary>Registered host pass-routing id; not a Vulkan handle or a submission-frame slot.</summary>
    public required byte LumenRenderPassId { get; init; }
}

/// <summary>Main-thread enqueue phase immediately after capture. Managed values, no packed GPU layout;
/// the same frame identity and second-based clocks as LumenCaptureContext. It carries no camera matrix.</summary>
public readonly struct LumenComputeContext
{
    /// <summary>Monotonic host FrameTiming.FrameNumber identifying the captured frame, not the rotating
    /// FrameContext.SubmissionFrameIndex resource slot. Use this identity to pair capture and recording.</summary>
    public required ulong FrameIndex { get; init; }
    /// <summary>Simulation seconds since capture: zero while paused, scaled by the game's tick rate.</summary>
    public required float DeltaTime { get; init; }
    /// <summary>Real seconds since capture, unaffected by pause/tick rate; positive values drive real-time effects.</summary>
    public float UnscaledDeltaTime { get; init; }
}

/// <summary>Render-thread callback before the HDR render pass opens. Managed context, no packed GPU layout
/// or matrices. Frame and its command buffer are borrowed for this callback only; never retain them or
/// issue main-thread game API calls. Resource-ring indexing uses Frame.SubmissionFrameIndex, not FrameIndex.</summary>
public readonly struct LumenBarrierContext
{
    public required FrameContext Frame { get; init; }
    /// <summary>Monotonic host FrameTiming.FrameNumber identifying the captured frame, not the rotating
    /// FrameContext.SubmissionFrameIndex resource slot. Use this identity to pair capture and recording.</summary>
    public required ulong FrameIndex { get; init; }
}

/// <summary>Render-thread draw callback inside the HDR render pass. Managed context, not a packed buffer ABI.
/// All textures and Frame are borrowed for this callback only; do not dispose or retain them. Captured camera
/// matrices use Waylith column vectors, with world distance in map cells (+Y up), not Numerics row-vector transforms.
/// The render thread must only consume captured values, never resolve game content or Find.CurrentMap.</summary>
public readonly struct LumenHdrDrawContext
{
    public LumenHdrDrawContext() { }
    /// <summary>RimWorld Map.uniqueID, never the index in Find.Maps; -1 means no current map/filter.</summary>
    public int CurrentMapIndex { get; init; } = -1;
    /// <summary>The same immutable space as capture. Draws use the shared host shader helper on
    /// straight stored RGB before premultiplication, never reread a host setting during recording.</summary>
    public LumenColorBlendSpace ColorBlendSpace { get; init; }
    public required FrameContext Frame { get; init; }
    /// <summary>Monotonic host FrameTiming.FrameNumber identifying the captured frame, not the rotating
    /// FrameContext.SubmissionFrameIndex resource slot. Use this identity to pair capture and recording.</summary>
    public required ulong FrameIndex { get; init; }
    /// <summary>Current RGBA16F HDR colour attachment; dimensions in physical pixels, alpha is coverage.</summary>
    public required RenderTexture HdrColorTarget { get; init; }
    /// <summary>Read-only scene-depth snapshot for this frame, null when unavailable; do not write shared depth.</summary>
    public required RenderTexture? SceneColorDepthSource { get; init; }
    /// <summary>Projection * View, clip = ViewProjection * worldColumn; already in the host's matrix convention.</summary>
    public required Matrix4x4 ViewProjection { get; init; }
    /// <summary>Near clip (cells), far clip (cells), orthographic flag (1 or 0), host constant 1.
    /// Sequential Vector4 values, not a separately versioned GPU layout; use with the matching captured projection.</summary>
    public required Vector4 SoftParticleDepthParams { get; init; }
}

/// <summary>Supported frame extension: registered membership is snapshotted by the host. Per frame, main
/// thread CaptureMainThread then EnqueuePreCompute precede render-thread RecordBufferBarriers then RecordHdrDraws.
/// Recording may lag or a captured frame may be dropped: freeze owned inputs by FrameIndex and retire them
/// safely rather than mutate in-flight data. FrameIndex is a host frame identity; SubmissionFrameIndex is only
/// the rotating engine resource slot. Contexts are managed contracts, not binary GPU layouts; spatial/clock
/// units and matrix conventions are documented on their members. Map ids always mean RimWorld Map.uniqueID.
/// HasActiveWork=false permits skipping callbacks and all HDR work when no other contributor is active.</summary>
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

/// <summary>Supported replaceable particle backend, registered through LumenHostRegistry. All instance
/// operations run on the main thread; ids are local to that backend and valid until reclamation. Simulation
/// stays alive on other maps but drawing filters by the captured RimWorld Map.uniqueID. Emitter distances
/// are cells and times seconds (see EmitterSpec). Instance world matrices use Numerics row-vector transforms
/// (translation M41..M43); GPU storage conversion, including any transpose, belongs to the backend. No internal
/// GPU struct layout is part of this extension contract. Capture/compute/barrier/draw lifetime and frame-vs-slot
/// rules are inherited from ILumenFrameContributor.</summary>
public interface ILumenGpuParticleEngine : ILumenFrameContributor
{
    /// <summary>Total shared particle slots across this backend's instances/maps, not active-this-frame usage.</summary>
    int Capacity { get; }

    // Requires: validated spec/transform; mapIndex is a resolved RimWorld Map.uniqueID, not a map-list index;
    // emissionSeed is the nonzero service-derived (play seed, node index) seed, never a backend id.
    // Ensures: rents slots and owns the map; realTime uses unscaled seconds when positive. Scheduled-burst
    // acceptance uses the shared CPU scheduler with this seed, independent of particle-birth randomness.
    int CreateInstance(EmitterSpec spec, LumenEmitterTransform initialTransform, int mapIndex, bool realTime, uint emissionSeed);

    // Requires: rateMultiplier is finite and >= 0, validated by the caller's parameter boundary.
    // Ensures: updates transform, emission state, RGB intensity and scheduled particles/s multiplier for next capture.
    //          A zero rateMultiplier does not suppress authored or explicit bursts.
    void UpdateInstance(int instanceId, in LumenEmitterTransform transform, bool emitting, float intensityMultiplier, float rateMultiplier);

    // Ensures: stops emission or reclaims GPU slot when particles expire.
    void DestroyInstance(int instanceId, bool immediate = false);

    // Requires: main thread.
    // Ensures: the instance emits again from the start of its schedule (time 0, rate remainder cleared, time-0
    //          bursts pending, probability PRNG at its initial node seed), exactly like CpuParticleSimulator.RestartEmitter.
    //          Accepted pending work remains; loop wrap never resets the stream. Unknown or destroyed ids are ignored.
    void RestartInstance(int instanceId);

    // Requires: main thread; count >= 0.
    // Ensures: the next captured frame spawns count extra particles (within the instance's slot capacity and the
    //          particle-density scale) whether or not the instance is emitting, like CpuParticleSimulator.EmitBurst.
    //          Unknown or destroyed ids are ignored.
    void EmitBurst(int instanceId, int count);

    // Requires: main thread.
    // Ensures: true from CreateInstance until the capture that reclaims the instance's slots: the next capture after
    //          DestroyInstance(id, true), or the end of the drain after DestroyInstance(id, false). False for unknown ids.
    bool IsInstanceAlive(int instanceId);
}
