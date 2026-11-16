using System;
using System.Numerics;

namespace RimLumen.Core.Contracts;

public enum LumenSpacePlane : byte
{
    // The node's LocalRotation verbatim; the display is not adapted to the camera.
    GroundXZ = 0,
    // Same node matrix as GroundXZ, but the emitter simulates in true 3D (+Y up) and its display is adapted to the
    // active camera: un-squashed under IsoRim, an oblique +Z projection under a straight-down 2D camera.
    // The value stays 3: integer-encoded assets and tools outside this repository use it.
    Vertical3D = 3,
}

public enum LumenBlendMode : byte
{
    Additive = 0,
    AlphaBlend = 1,
    Premultiplied = 2,
}

// Material calculation only; authored colours remain stored values in both modes.
public enum LumenColorSpace : byte
{
    Stored = 0,
    Linear = 1,
}

public enum LumenBillboardMode : byte
{
    Camera = 0,
    Horizontal = 1,
    StretchedVelocity = 2,
    Mesh = 3,
}

public enum LumenShapeKind : byte
{
    Point = 0,
    Sphere = 1,
    CircleXZ = 2,
    Cone = 3,
    Box = 4,
}

public enum ForceFieldKind : byte
{
    GravityDrag = 0,
    Vortex = 1,
    CurlTurbulence = 2,
    PointAttractor = 3,
}

// Time: normalized particle lifetime [0,1], or light age in seconds >= 0; nondecreasing under D22.
// Value: finite curve value. InTangent/OutTangent: finite Hermite slopes per time unit, default 0.
public readonly record struct CurveKey(float Time, float Value, float InTangent = 0f, float OutTangent = 0f);

public sealed class ScalarCurve
{
    // Nonempty keys in nondecreasing time order (D22). All times equal: last value for every time.
    // Otherwise t <= first gives first, t >= last gives last; at an interior repeated time use the last
    // equal key, approached from the left via the first equal key.
    public CurveKey[] Keys { get; }
    // Finite dimensionless scale applied after Hermite evaluation; default 1.
    public float Multiplier { get; }

    public ScalarCurve(CurveKey[] keys, float multiplier = 1f)
    {
        Keys = keys ?? Array.Empty<CurveKey>();
        Multiplier = multiplier;
    }

    public static ScalarCurve Constant(float value) => new(new[] { new CurveKey(0f, 1f) }, value);
}

// Time: normalized lifetime [0,1], strictly increasing. Rgba: stored vertex factor, not decoded;
// RGB finite >= 0, 1 = full brightness, >1 = HDR; alpha is finite [0,1] coverage.
public readonly record struct ColorStop(float Time, Vector4 Rgba);

public sealed class ColorGradient
{
    // Nonempty ordered stops; components interpolate with endpoint values held outside the stop range.
    public ColorStop[] Stops { get; }

    public ColorGradient(ColorStop[] stops)
    {
        Stops = stops ?? Array.Empty<ColorStop>();
    }

    public static ColorGradient Constant(Vector4 rgba) => new(new[] { new ColorStop(0f, rgba) });
}

// Kind must be GravityDrag, Vortex, CurlTurbulence or PointAttractor; undefined values reject the asset.
// CenterOrAxis is gravity direction
// (not normalized), vortex axis (normalized; zero uses +Y), or attractor node-local centre in cells;
// unused by curl. Vortex centre is the node origin. World-space centres follow current node World;
// local-space centres stay local. Authored directions stay in simulation space.
// Parameters: gravity X = drag (1/s, negative treated as zero); vortex X = inward and Y = axial
// acceleration (cells/s²); curl XYZ = simulation-space offset (cells); attractor unused.
// Radius: finite >= 0 cells; vortex/attractor linear falloff to zero at radius, 0 = unlimited; otherwise unused.
// Strength: required in C#/JSON/XML; finite acceleration in cells/s² for gravity, vortex and attractor; curl potential scale in
// cells²/s², multiplied by Frequency (1/cell). Frequency is finite, default 1, and used only by curl.
public readonly record struct ForceFieldSpec(
    ForceFieldKind Kind,
    Vector3 CenterOrAxis,
    Vector3 Parameters,
    float Radius,
    float Strength,
    float Frequency = 1f);

public sealed class EmitterSpec
{
    // Identifier; required in C#, JSON defaults to the node name.
    public required string Name { get; init; }
    // Positive particle capacity; expired particles are reclaimed before admission.
    public int MaxParticles { get; init; } = 1024;
    // Finite positive emission-cycle length in seconds.
    public float Duration { get; init; } = 1f;
    // Repeat the emission cycle; already emitted particles finish their lifetimes.
    public bool Looping { get; init; }
    // True transforms birth positions/velocities to world; false particles follow current node transform.
    public bool WorldSpaceSimulation { get; init; } = true;
    // Finite >= 0 particles/s; density and runtime rate multiplier scale the accumulated rate.
    public float RateOverTime { get; init; }
    // Time: finite >= 0 seconds in cycle; Count: >= 0 particles. Per-emitter Int64 a starts at 0,
    // in 1/65536-particle units. q = floor(density * 65536); each nonzero burst uses a += count * q,
    // n = ceil(a / 65536) by integer arithmetic, then a -= n * 65536; a <= 0. Effective density is
    // floor(density * 65536) / 65536, never above authored; n is a request budget.
    // Scale once on CPU enqueue, not again in GPU capture. Play/explicit Restart/GoTo state re-entry
    // reset a; loop wrap does not. Runtime rate does not scale bursts; continuous rate has double carry.
    public (float Time, int Count)[] Bursts { get; init; } = Array.Empty<(float, int)>();

    // Point/Box emit +Y; Sphere emits radially; CircleXZ emits radially in XZ; Cone emits about +Y.
    public LumenShapeKind Shape { get; init; } = LumenShapeKind.Point;
    // Cells: Sphere ellipsoid XYZ radii; CircleXZ/Cone disc X/Z radii; Box XYZ half-extents; Point unused.
    public Vector3 ShapeExtents { get; init; } = Vector3.One;
    // Shape-only transform: position = R(scale * sampledPosition) + position; direction = R(sampledDirection).
    // Does not transform node children, forces or billboard size. Rotation is a unit quaternion (x,y,z,w).
    // Finite shape-local offset in cells, before the node transform.
    public Vector3 ShapePosition { get; init; }
    // Unit quaternion xyzw; parsers normalize nondegenerate input; C# requires |length²-1| <= 1e-5.
    public Quaternion ShapeRotation { get; init; } = Quaternion.Identity;
    // Finite dimensionless position-only scale; zero and negative values are allowed.
    public Vector3 ShapeScale { get; init; } = Vector3.One;
    // Cone half-angle in degrees, finite [0,180]; unused by other shapes.
    public float ConeAngleDegrees { get; init; } = 25f;

    // Uniform birth lifetime range in seconds: finite minimum > 0, maximum >= minimum.
    public float StartLifetimeMin { get; init; } = 1f;
    public float StartLifetimeMax { get; init; } = 1f;
    // Uniform birth speed range in cells/s. Node world scale changes velocity; shape scale does not.
    public float StartSpeedMin { get; init; }
    public float StartSpeedMax { get; init; }
    // Uniform birth full-width range in cells. Size uses cbrt(abs(det(node World 3x3))): at birth for
    // world simulation, at draw for local simulation; shape scale does not change size.
    public float StartSizeMin { get; init; } = 1f;
    public float StartSizeMax { get; init; } = 1f;
    // Stored vertex factor: finite RGB >= 0, 1 = vanilla full brightness, >1 = HDR; alpha finite [0,1].
    public Vector4 StartColor { get; init; } = Vector4.One;
    // Stored RGBA material factor, separate from StartColor and lifetime vertex factors.
    // RGB is finite >= 0 without an upper bound; alpha is finite [0,1].
    public Vector4 MaterialTint { get; init; } = Vector4.One;
    // Stored multiplies raw factors; Linear decodes tint and sRGB texture RGB, not vertex RGB,
    // and encodes the product back to stored values. Alpha is never gamma-converted.
    public LumenColorSpace ColorSpace { get; init; } = LumenColorSpace.Stored;

    // Dimensionless size multiplier over normalized age; 64 samples at i/63, interpolated at draw.
    // A step can be smoothed across a 1/63 sample interval.
    public ScalarCurve SizeOverLifetime { get; init; } = ScalarCurve.Constant(1f);
    // RGBA multiplier over normalized age; same 64-sample interpolation as size.
    public ColorGradient ColorOverLifetime { get; init; } = ColorGradient.Constant(Vector4.One);
    // Fields sum acceleration and drag; v = (v + acceleration * dt) * exp(-drag * dt).
    public ForceFieldSpec[] ForceFields { get; init; } = Array.Empty<ForceFieldSpec>();

    // Texture asset key; null uses alpha = max(0, 1 - dot(radial, radial))², independent of flipbook UV.
    public string? TextureKey { get; init; }
    // Atlas columns and rows, each >= 1; frame 0 is top-left and rows proceed top to bottom.
    public int FlipbookTilesX { get; init; } = 1;
    public int FlipbookTilesY { get; init; } = 1;
    // Cycles across normalized lifetime; frame=floor(age*cycles*tiles), wrapped only when cycles > 1.
    public float FlipbookCycles { get; init; } = 1f;

    // Additive, AlphaBlend or Premultiplied; RGB is premultiplied by final alpha for Additive/Premultiplied.
    public LumenBlendMode BlendMode { get; init; } = LumenBlendMode.Additive;
    // Camera, fixed-world-XZ Horizontal, or velocity-aligned StretchedVelocity; Mesh is deferred:
    // L1 draws a camera quad and L2 rejects Mesh.
    public LumenBillboardMode BillboardMode { get; init; } = LumenBillboardMode.Camera;
    // Seconds: stretched height = current full width * LengthScale + world speed * this factor.
    public float VelocityStretchFactor { get; init; }
    // Dimensionless stretched-height scale; does not change width.
    public float LengthScale { get; init; } = 1f;
    // Eye-space cells: positive values feather alpha by clamp(depth separation / distance, 0, 1).
    public float SoftParticleFadeDistance { get; init; } = 0.5f;
    // Finite >= 0 RGB-only scale, combined with StartColor, lifetime gradient and runtime intensity
    // into the vertex factor before MaterialTint/texture composition in ColorSpace.
    // Soft fade scales final alpha, followed by blend-mode RGB premultiplication.
    public float HdrIntensity { get; init; } = 1f;
    // Expires a particle that is descending (world vy < 0) below the effect's play-origin height.
    public bool KillBelowFloor { get; init; }
    // Defined tier required at asset entry. L2 selects GPU when available, otherwise L1; L0/L1 select native L1.
    public LumenParticleTier PreferredTier { get; init; } = LumenParticleTier.L2_GpuCompute;
}

// Radii [0,40] cells: vanilla GlowGrid.RegisterCustomGlower clamps [0,40] (Assembly-CSharp:69649),
// and CompProperties_Glower.ConfigErrors rejects >40 (:344536). Colour components are [0,1]; ColorInt stores 1/255 steps.
// MapGlowerDriver's 0.05-cell radius / 4/255 colour steps are RimLumen re-register (re-flood) thresholds,
// not vanilla storage precision: they prevent steady lights from re-flooding every frame. Curve time is seconds.
// GlowRadius and OverlightRadius: finite [0,40] cells; ColorRgb: finite [0,1] per component.
// IntensityOverTime: null means 1; otherwise Hermite in elapsed node-light seconds, endpoint-held. Effective
// intensity <= 0 deregisters; otherwise scaled RGB is clamped to [0,1] before channel quantization.
public readonly record struct PointLightSpec(
    float GlowRadius,
    float OverlightRadius,
    Vector3 ColorRgb,
    ScalarCurve? IntensityOverTime = null);

// TimeSeconds is measured from the entry of State (null = the asset's initial state). Notify raises
// ILumenEffectHandle.Notify(EventName) when the event fires.
public readonly record struct TimelineEventSpec(
    float TimeSeconds,
    string EventName,
    int TargetNodeIndex = -1,
    string? State = null,
    bool Notify = false);

// One named playback segment. Nodes lists node names (every node with that name belongs to the state);
// Duration null = EffectAsset.DefaultDuration. A Loop state never ends on its own and Duration is its
// timeline-event cycle; otherwise the state ends after Duration and enters Next, or finishes the effect.
public sealed class EffectStateSpec
{
    public required string Name { get; init; }
    public string[] Nodes { get; init; } = Array.Empty<string>();
    public float? Duration { get; init; }
    public bool Loop { get; init; }
    public string? Next { get; init; }
}

public sealed class EffectNodeSpec
{
    public required string Name { get; init; }
    public int ParentIndex { get; init; } = -1;
    public LumenSpacePlane SpacePlane { get; init; } = LumenSpacePlane.GroundXZ;
    public Vector3 LocalPosition { get; init; }
    public Quaternion LocalRotation { get; init; } = Quaternion.Identity;
    public Vector3 LocalScale { get; init; } = Vector3.One;
    public EmitterSpec? Emitter { get; init; }
    public PointLightSpec? PointLight { get; init; }
}

public sealed class EffectAsset
{
    public required string EffectId { get; init; }
    public float DefaultDuration { get; init; } = 2f;
    public EffectNodeSpec[] Nodes { get; init; } = Array.Empty<EffectNodeSpec>();
    public TimelineEventSpec[] TimelineEvents { get; init; } = Array.Empty<TimelineEventSpec>();

    // Empty = one implicit unnamed state holding every node, Duration = DefaultDuration, Loop = any emitter Looping.
    public EffectStateSpec[] States { get; init; } = Array.Empty<EffectStateSpec>();

    // Null = States[0]. Both must be null when States is empty.
    public string? InitialState { get; init; }

    // State entered by ILumenEffectHandle.Stop(true); null = stop drains at once.
    public string? StopState { get; init; }
}

/// <summary>Supplies assets validated at resolution. Treat each returned asset and its nested arrays as immutable
/// once resolved; hot reload must return a new EffectAsset instance so validation runs again.</summary>
public interface ILumenAssetProvider
{
    // Requires: effectId is non-empty.
    // Ensures: returns true with resolved native EffectAsset if handled by this provider, else false.
    bool TryResolveEffect(string effectId, out EffectAsset? asset);
}
