using System;
using System.Numerics;

namespace RimLumen.Core.Contracts;

public delegate bool LumenFollowProvider(out Vector3 worldPosition, out float rotationDegrees);

public readonly struct LumenPlayRequest
{
    public LumenPlayRequest()
    {
    }

    public required string EffectId { get; init; }
    public int MapIndex { get; init; } = -1;
    public Vector3 OriginWorld { get; init; }
    public Vector3? TargetWorld { get; init; }
    public float Scale { get; init; } = 1f;
    public float RotationDegrees { get; init; }
    public LumenFollowProvider? Follow { get; init; }

    // Name of the asset state to start in, overriding EffectAsset.InitialState; null = the asset's own initial
    // state. A name the asset does not declare (any name, for an asset without states) makes Play throw.
    public string? InitialState { get; init; }
    public bool RealTime { get; init; }
}

public interface ILumenEffectHandle : IDisposable
{
    bool IsAlive { get; }

    // Requires: main thread; name is non-empty; safe no-op once the effect drains or is dead.
    // Ensures: fires every timeline event named name now (target-node pulse, Notify) and restarts the emission of
    //          every node named name, without changing the current state. Unmatched names are a no-op.
    void Trigger(string name);

    // Requires: main thread; stateName names a state the asset declares (otherwise ArgumentException).
    // Ensures: enters stateName now (re-entering the current state restarts it). No-op after Stop(true) or once dead.
    void GoTo(string stateName);

    // Requires: name is non-empty; value is finite.
    // Ensures: updates runtime scalar parameter for emitters/materials/lights bound to name.
    void SetParam(string name, float value);

    // Ensures: allowFadeOut == true plays the asset's StopState to its end when it declares one, then stops new
    //          emission and lets particles and lights fade; without a StopState it fades at once.
    //          allowFadeOut == false terminates immediately.
    void Stop(bool allowFadeOut = true);

    // Ensures: raised with EventName when a timeline event with Notify fires (on its time or through Trigger).
    event Action<string>? Notify;

    // Ensures: raised with the state name each time a declared state is entered, including the initial state.
    event Action<string>? StateChanged;

    // Ensures: raised exactly once, when IsAlive becomes false for any reason (the effect ended, Stop, Dispose).
    // All three events are raised on the main thread from the per-frame advance, in the order they happened,
    // so a call made from a handler (GoTo, Stop) never re-enters the playback that raised it.
    event Action? Finished;
}

public interface ILumenVfxService
{
    LumenCapabilities Capabilities { get; }

    // Requires: called on main thread; request.EffectId is non-empty.
    // Ensures: returns an active handle when EffectId resolves, or an inert dead handle when missing.
    ILumenEffectHandle Play(in LumenPlayRequest request);
}
