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
    public string? InitialState { get; init; }
    public bool RealTime { get; init; }
}

public interface ILumenEffectHandle : IDisposable
{
    bool IsAlive { get; }

    // Requires: eventOrStateName is non-empty; safe no-op if handle already stopped.
    // Ensures: advances matching timeline/emitter trigger on main thread.
    void Trigger(string eventOrStateName);

    // Requires: name is non-empty; value is finite.
    // Ensures: updates runtime scalar parameter for emitters/materials/lights bound to name.
    void SetParam(string name, float value);

    // Ensures: stops new emissions when allowFadeOut is true, or terminates immediately when false.
    void Stop(bool allowFadeOut = true);
}

public interface ILumenVfxService
{
    LumenCapabilities Capabilities { get; }

    // Requires: called on main thread; request.EffectId is non-empty.
    // Ensures: returns an active handle when EffectId resolves, or an inert dead handle when missing.
    ILumenEffectHandle Play(in LumenPlayRequest request);
}
