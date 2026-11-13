using System;
using System.Numerics;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Scene;

/// <summary>
/// L1 (<see cref="LumenParticleTier.L1_NativeCpu"/>) pooled CPU particle simulator.
/// Steady-state frames allocate nothing: capacity is reserved per emitter on
/// <see cref="AcquireEmitter"/> and reused for the life of the emitter.
/// </summary>
public sealed class CpuParticleSimulator : ILumenFrameContributor
{
    // Requires: seed is any int; identical seeds and inputs produce identical particle states.
    public CpuParticleSimulator(int seed = 0x5EED) => throw new NotImplementedException();

    // Ensures: always "L1_NativeCpu".
    public string Name => throw new NotImplementedException();

    // Ensures: optional HDR submission target; null means "no HDR pass owner installed yet".
    public IL1ParticleBatchSink? BatchSink { get; set; }

    // Ensures: true while at least one emitter is emitting or at least one particle is alive;
    //          false once every emitter is drained and every particle expired.
    public bool HasActiveWork => throw new NotImplementedException();

    // Ensures: number of emitters that have not been released (emitting or draining).
    public int ActiveEmitterCount => throw new NotImplementedException();

    // Ensures: number of live particles after the last CaptureMainThread.
    public int LiveParticleCount => throw new NotImplementedException();

    // Ensures: read-only view of the live particles, ordered as stored (swap-remove compaction order).
    public ReadOnlySpan<CpuParticle> LiveParticles => throw new NotImplementedException();

    // Ensures: 4 vertices and 6 indices per live particle from the last CaptureMainThread.
    public ReadOnlySpan<ParticleVertex> Vertices => throw new NotImplementedException();

    public ReadOnlySpan<uint> Indices => throw new NotImplementedException();

    // Requires: spec is non-null; spec.MaxParticles > 0; worldTransform is finite.
    // Ensures: reserves capacity, seeds a deterministic per-emitter PRNG from (seed, emitterId),
    //          returns a non-negative emitter id that stays valid until ReleaseEmitter.
    public int AcquireEmitter(EmitterSpec spec, Matrix4x4 worldTransform, bool emitting = true, float rateMultiplier = 1f)
        => throw new NotImplementedException();

    // Ensures: updates the emitter transform used for spawn shapes and (for local-space emitters)
    //          for transforming particle positions at vertex-build time. Unknown ids are ignored.
    public void SetEmitterTransform(int emitterId, Matrix4x4 worldTransform) => throw new NotImplementedException();

    // Ensures: sets emission on/off plus the runtime rate/intensity multipliers. Turning emission off
    //          never kills live particles. Unknown ids are ignored.
    public void SetEmitterParams(int emitterId, bool emitting, float rateMultiplier, float intensityMultiplier)
        => throw new NotImplementedException();

    // Requires: count >= 0.
    // Ensures: spawns min(count, spec.MaxParticles - liveOfThisEmitter) particles immediately from the
    //          emitter's shape with the same deterministic PRNG stream as timed emission.
    public void EmitBurst(int emitterId, int count) => throw new NotImplementedException();

    // Ensures: live particle count owned by this emitter; 0 for unknown ids.
    public int GetLiveParticleCount(int emitterId) => throw new NotImplementedException();

    // Ensures: immediate == true removes the emitter and all of its particles now; immediate == false
    //          stops new emission and lets existing particles finish their lifetime.
    public void ReleaseEmitter(int emitterId, bool immediate = false) => throw new NotImplementedException();

    // Requires: main thread; context.DeltaTime >= 0.
    // Ensures: advances emitter time, spawns rate/burst particles, integrates the four force-field
    //          kinds, expires particles, and rebuilds the quad/vertex/index batch for this frame.
    public void CaptureMainThread(in LumenCaptureContext context) => throw new NotImplementedException();

    // Ensures: no-op; L1 does no GPU compute.
    public void EnqueuePreCompute(in LumenComputeContext context) => throw new NotImplementedException();

    // Ensures: no-op; L1 records no buffer barriers.
    public void RecordBufferBarriers(in LumenBarrierContext context) => throw new NotImplementedException();

    // Requires: called inside RimLumenVfxPass; HasActiveWork was true for this frame.
    // Ensures: forwards the current batch to BatchSink when one is installed, otherwise submits nothing.
    public void RecordHdrDraws(in LumenHdrDrawContext context) => throw new NotImplementedException();
}
