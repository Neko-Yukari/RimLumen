using System;
using System.Numerics;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Scene;

/// <summary>
/// Runtime instance of one played <see cref="EffectAsset"/>: owns the per-node world transforms, the
/// emitter bindings (L1 or L2), the point-light bindings and the timeline playback.
/// Created by <see cref="LumenVfxService.Play"/>; advanced once per frame by the service.
/// </summary>
public sealed class EffectInstanceHandle : ILumenEffectHandle
{
    internal EffectInstanceHandle(LumenVfxService owner, EffectAsset asset, in LumenPlayRequest request) =>
        throw new NotImplementedException();

    public string EffectId => throw new NotImplementedException();

    // Ensures: true until the handle is stopped immediately/disposed, or until a fade-out stop has
    //          released every emitter and every light.
    public bool IsAlive => throw new NotImplementedException();

    // Ensures: false after Stop(true) (no new particles/lights) and after any immediate stop.
    public bool IsEmitting => throw new NotImplementedException();

    // Ensures: seconds since Play, wrapped by EffectAsset.DefaultDuration only for looping emitters.
    public float PlaybackTime => throw new NotImplementedException();

    // Ensures: the root world matrix for the current frame (Follow provider applied, Scale/rotation applied).
    public Matrix4x4 RootWorld => throw new NotImplementedException();

    // Ensures: one entry per EffectAsset.Nodes entry, parent composed, as of the current frame.
    public ReadOnlySpan<Matrix4x4> NodeWorldMatrices => throw new NotImplementedException();

    // Ensures: emitters currently routed to CpuParticleSimulator / to LumenHostRegistry.GpuParticleEngine.
    public int CpuEmitterCount => throw new NotImplementedException();
    public int GpuEmitterCount => throw new NotImplementedException();

    // Ensures: point lights currently registered on a MapGlowerDriver.
    public int LightCount => throw new NotImplementedException();

    // Ensures: number of timeline events fired so far (automatic crossings plus manual Trigger hits).
    public int TimelineEventFireCount => throw new NotImplementedException();

    // Requires: eventOrStateName is non-empty.
    // Ensures: for every TimelineEventSpec whose EventName matches: fires it immediately (counts it and
    //          emits one burst on TargetNodeIndex's emitter when that node has one: Burst[0].Count if
    //          the emitter declares bursts, else round(RateOverTime), clamped to [0, MaxParticles]).
    //          For every node whose Name matches: activates that node's emitter and emits the same burst.
    //          Unmatched names are a no-op (WotF may trigger animator states RimLumen does not own).
    //          L2 emitters are only activated: ILumenGpuParticleEngine exposes no burst entry point.
    //          Safe no-op once the handle is dead.
    public void Trigger(string eventOrStateName) => throw new NotImplementedException();

    // Requires: name is non-empty; value is finite.
    // Ensures: "intensity" scales light colour and particle vertex colour, "rate" scales emission rate,
    //          "scale" multiplies the root scale. Unknown names are recorded and retrievable through
    //          TryGetParam without affecting simulation. Safe no-op once the handle is dead.
    public void SetParam(string name, float value) => throw new NotImplementedException();

    // Ensures: allowFadeOut == true stops new emission immediately, fades the point lights out over
    //          EffectInstanceHandle.LightFadeSeconds, and lets live particles finish; the handle becomes
    //          dead once nothing is left. allowFadeOut == false tears down emitters and lights now.
    public void Stop(bool allowFadeOut = true) => throw new NotImplementedException();

    // Ensures: equivalent to Stop(false); idempotent.
    public void Dispose() => throw new NotImplementedException();

    // Ensures: exposes a value stored by SetParam, including the three built-in names.
    public bool TryGetParam(string name, out float value) => throw new NotImplementedException();

    // Ensures: seconds over which Stop(true) fades point lights to zero before removing them.
    public const float LightFadeSeconds = 0.25f;

    // Requires: called once per frame by LumenVfxService.CaptureMainThread on the main thread.
    // Ensures: updates the root transform (Follow provider), recomposes node matrices, fires timeline
    //          events crossed by this step, pushes emitter state to the L1/L2 backend, and drives the
    //          point-light bindings; marks the handle dead when a fade-out stop has fully drained.
    internal void Advance(float deltaTime) => throw new NotImplementedException();
}
