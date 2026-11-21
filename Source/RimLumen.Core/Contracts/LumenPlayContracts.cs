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
    /// <summary>RimWorld Map.uniqueID (not the map list index). -1 resolves to the current map once at Play;
    /// no current map throws InvalidOperationException. Other negative values throw ArgumentOutOfRangeException.
    /// The resolved id owns particles and lights for the handle's entire lifetime, even after a map switch.</summary>
    public int MapIndex { get; init; } = -1;
    public Vector3 OriginWorld { get; init; }
    public Vector3? TargetWorld { get; init; }
    // World Z of the LumenDepthMask reference line (world cells). Play records it as an offset from OriginWorld.Z, so
    // it moves with Follow; Play throws ArgumentException when a node declares a depthMask and this is null.
    public float? DepthReferenceZ { get; init; }
    public float Scale { get; init; } = 1f;
    public float RotationDegrees { get; init; }
    public LumenFollowProvider? Follow { get; init; }

    // Name of the asset state to start in, overriding EffectAsset.InitialState; null = the asset's own initial
    // state. A name the asset does not declare (any name, for an asset without states) makes Play throw.
    public string? InitialState { get; init; }
    public InteractionEventSpec[]? InteractionEvents { get; init; }
    public bool RealTime { get; init; }
}

public interface ILumenEffectHandle : IDisposable
{
    bool IsAlive { get; }

    // Requires: main thread; name is non-empty; safe no-op once the effect drains or is dead.
    // Ensures: fires every timeline event named name now (target-node pulse, Notify) and restarts the emission of
    //          every node named name, without changing the current state. Unmatched names are a no-op.
    void Trigger(string name);

    // Requires: main thread. For an asset-backed handle, an undeclared stateName throws ArgumentException,
    //          including after Stop(true), while draining, or once dead. Inert handles have no asset state table and are no-ops.
    // Ensures: a declared state is entered (or restarted) only while playing before Stop(true); otherwise no-op.
    //          Stop(false) tears down immediately. An inert handle always does nothing.
    void GoTo(string stateName);

    // Requires: name is non-empty; value is finite.
    // Ensures: updates runtime scalar parameter for emitters/materials/lights bound to name.
    //          "rate" must be finite and >= 0 (otherwise ArgumentOutOfRangeException); zero pauses
    //          continuous emission only, never authored or explicit bursts.
    void SetParam(string name, float value);

    // Requires: main thread; intensity is finite and >= 0 (otherwise ArgumentOutOfRangeException before mutation).
    // Ensures: multiplies point-light intensity on this handle without affecting particles or other node features.
    void SetLightIntensity(float intensity);

    // Requires: main thread; each RGB component of colorRgb is finite and in [0, 1]
    //          (otherwise ArgumentOutOfRangeException before mutation).
    // Ensures: overrides the RGB colour of point lights on this handle without affecting particles.
    void SetLightColor(Vector3 colorRgb);

    // Ensures: allowFadeOut == true plays the asset's StopState to its end when it declares one, then stops new
    //          emission and lets particles and lights fade; without a StopState it fades at once.
    //          allowFadeOut == false terminates immediately.
    void Stop(bool allowFadeOut = true);

    // Ensures: raised with EventName when a timeline event or interaction event with Notify fires.
    event Action<string>? Notify;

    // Ensures: raised with EventName, Trigger and Cursor source when an interaction event with Notify fires.
    event Action<LumenInteractionNotification>? InteractionNotify;

    // Ensures: raised with the state name each time a declared state is entered, including the initial state.
    event Action<string>? StateChanged;

    // Ensures: raised exactly once, when IsAlive becomes false for any reason (the effect ended, Stop, Dispose).
    // All events are raised on the main thread from the per-frame advance, in the order they happened,
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
