using System;
using System.Numerics;

namespace RimLumen.Core.Contracts;

// Runtime formulas for the emitter fields in this file (environment channels, collision, sub-emitters, RingWave) are
// owned by particle-world-physics; distortion nodes by screen-distortion; ribbon nodes by ribbon-trails.

// Environment force sources an emitter's particles react to. Values outside the named bits reject the asset.
[Flags]
public enum LumenEnvironmentChannel : byte
{
    None = 0,
    // Vanilla explosions push particles outward.
    Explosion = 1,
    // The map cursor pushes particles.
    Cursor = 2,
}

public enum LumenCollisionMode : byte
{
    None = 0,
    // Reflect off blocking map cells with Bounce restitution and Dampen tangential loss.
    Bounce = 1,
    // Expire on the first hit.
    Kill = 2,
}

// Particle-vs-map collision (Unity ParticleSystem.CollisionModule meaning). The blocking-cell predicate and wall
// height are defined by the runtime owner. Bounce, Dampen and LifetimeLoss are finite [0,1]; LifetimeLoss is the
// fraction of the particle's total lifetime removed per hit. KillUnderRoof is independent of Mode: a particle that
// enters a roofed cell expires (outdoor weather such as rain).
public readonly record struct CollisionSpec
{
    public CollisionSpec()
    {
    }

    public LumenCollisionMode Mode { get; init; } = LumenCollisionMode.None;
    public float Bounce { get; init; } = 1f;
    public float Dampen { get; init; }
    public float LifetimeLoss { get; init; }
    public bool KillUnderRoof { get; init; }
}

public enum LumenSubEmitterTrigger : byte
{
    // A particle of the source emitter expires.
    Death = 0,
    // A particle of the source emitter collides; requires the source Collision.Mode != None.
    Collision = 1,
}

// Unity ParticleSystemSubEmitterProperties meaning: spawned particles take the parent particle's current value.
[Flags]
public enum LumenSubEmitterInherit : byte
{
    None = 0,
    Color = 1,
    Size = 2,
}

// On each Trigger event of a source particle, spawns Count particles (each event draws Probability once) on the
// emitter of node TargetNodeIndex at the event position. The target is another node with an Emitter that has
// RateOverTime 0 and no Bursts and shares the source's PreferredTier; the sub-emitter graph is acyclic.
// Count >= 0; Probability finite [0,1].
public readonly record struct SubEmitterSpec(
    LumenSubEmitterTrigger Trigger,
    int TargetNodeIndex,
    int Count,
    float Probability = 1f,
    LumenSubEmitterInherit Inherit = LumenSubEmitterInherit.None);

public enum LumenDistortionKind : byte
{
    // A single shock ring expanding from the node at Speed for Duration seconds.
    RingWave = 0,
    // Concentric decaying ripples expanding at Speed, spaced by Frequency (e.g. a shield hit).
    Ripple = 1,
    // Continuous noise shimmer of spatial Frequency inside Radius.
    HeatHaze = 2,
}

// Screen-space distortion centred on the node's world position, projected to the ground.
// Kind-specific fields use 0 for "not used": a field a kind requires must be > 0, a field it does not use must be 0.
//   RingWave: Thickness, Speed, Duration > 0;      Frequency 0.
//   Ripple:   Thickness, Speed, Frequency > 0;     Duration >= 0.
//   HeatHaze: Frequency > 0;                       Thickness 0, Speed 0, Duration >= 0.
// Duration 0 means "while the node is active". StrengthOverTime (null = 1) is Hermite in seconds since the node's
// last (re)start, endpoint-held, keys and multiplier finite >= 0; it multiplies Strength.
public sealed class DistortionSpec
{
    public required LumenDistortionKind Kind { get; init; }
    // Cells, finite > 0: the affected area's outer radius.
    public required float Radius { get; init; }
    // Cells, finite > 0: apparent screen displacement at full strength.
    public required float Strength { get; init; }
    // Cells: ring half-width (RingWave, Ripple).
    public float Thickness { get; init; }
    // Cells/s: ring expansion speed (RingWave, Ripple).
    public float Speed { get; init; }
    // 1/cell: ripple spacing or haze noise frequency (Ripple, HeatHaze).
    public float Frequency { get; init; }
    // Seconds since node activation.
    public float Duration { get; init; }
    public ScalarCurve? StrengthOverTime { get; init; }
}

public enum LumenRibbonMode : byte
{
    // Follows the node's world position (Unity TrailRenderer).
    Trail = 0,
    // From the node to an endpoint (Unity LineRenderer).
    Beam = 1,
}

public enum LumenRibbonTextureMode : byte
{
    // The texture U spans the whole ribbon once.
    Stretch = 0,
    // The texture repeats once per cell of ribbon length.
    Tile = 1,
}

public enum LumenRibbonAlignment : byte
{
    // The ribbon faces the camera.
    View = 0,
    // The ribbon lies flat in the world XZ ground plane.
    Ground = 1,
}

// Native trail / beam ribbon. Mode-specific fields use 0 / null / false for "not used": the fields of the
// node's Mode are required, the fields of the other mode must stay unset.
//   Trail: Lifetime > 0, MinVertexDistance > 0, MaxPoints > 1.
//   Beam:  exactly one of EndLocal / EndAtTarget, Segments >= 1, NoiseAmplitude >= 0, NoiseFrequency >= 0.
// EndAtTarget uses LumenPlayRequest.TargetWorld; Play without a TargetWorld throws ArgumentException.
// Length is normalized along the ribbon: 0 = head (the node), 1 = tail or endpoint.
public sealed class RibbonSpec
{
    public required LumenRibbonMode Mode { get; init; }
    // Seconds a trail point lives.
    public float Lifetime { get; init; }
    // Cells the node must move before a new trail point is added.
    public float MinVertexDistance { get; init; }
    // Trail point capacity; the oldest point is dropped when full.
    public int MaxPoints { get; init; }
    // Beam endpoint in node-local cells.
    public Vector3? EndLocal { get; init; }
    // Beam endpoint at LumenPlayRequest.TargetWorld.
    public bool EndAtTarget { get; init; }
    // Beam subdivisions between the node and the endpoint.
    public int Segments { get; init; }
    // Cells of perpendicular jitter per interior beam vertex (jagged arcs).
    public float NoiseAmplitude { get; init; }
    // Hz: jitter re-roll rate; 0 rolls once per (re)start.
    public float NoiseFrequency { get; init; }
    // Cells, finite > 0: full width before WidthOverLength.
    public float Width { get; init; } = 1f;
    // Dimensionless width multiplier over normalized length [0,1]; keys and multiplier finite >= 0.
    public ScalarCurve WidthOverLength { get; init; } = ScalarCurve.Constant(1f);
    // Stored RGBA vertex factor over normalized length; same colour contract as ColorStop.
    public ColorGradient ColorOverLength { get; init; } = ColorGradient.Constant(Vector4.One);
    // Finite >= 0 RGB-only scale, combined with the runtime "intensity" parameter.
    public float HdrIntensity { get; init; } = 1f;
    // Texture asset key; null draws untextured.
    public string? TextureKey { get; init; }
    public LumenBlendMode BlendMode { get; init; } = LumenBlendMode.Additive;
    public LumenRibbonTextureMode TextureMode { get; init; } = LumenRibbonTextureMode.Stretch;
    public LumenRibbonAlignment Alignment { get; init; } = LumenRibbonAlignment.View;
    // Eye-space cells, finite >= 0: same soft fade as EmitterSpec.SoftParticleFadeDistance.
    public float SoftParticleFadeDistance { get; init; } = 0.5f;
}
