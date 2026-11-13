using System;
using System.Numerics;

namespace RimLumen.Core.Contracts;

public enum LumenSpacePlane : byte
{
    GroundXZ = 0,
    HeightVertical90 = 1,
    Free3D = 2,
}

public enum LumenBlendMode : byte
{
    Additive = 0,
    AlphaBlend = 1,
    Premultiplied = 2,
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

public readonly record struct CurveKey(float Time, float Value, float InTangent = 0f, float OutTangent = 0f);

public sealed class ScalarCurve
{
    public CurveKey[] Keys { get; }
    public float Multiplier { get; }

    public ScalarCurve(CurveKey[] keys, float multiplier = 1f)
    {
        Keys = keys ?? Array.Empty<CurveKey>();
        Multiplier = multiplier;
    }

    public static ScalarCurve Constant(float value) => new(new[] { new CurveKey(0f, 1f) }, value);
}

public readonly record struct ColorStop(float Time, Vector4 RgbaLinear);

public sealed class ColorGradient
{
    public ColorStop[] Stops { get; }

    public ColorGradient(ColorStop[] stops)
    {
        Stops = stops ?? Array.Empty<ColorStop>();
    }

    public static ColorGradient Constant(Vector4 rgbaLinear) => new(new[] { new ColorStop(0f, rgbaLinear) });
}

public readonly record struct ForceFieldSpec(
    ForceFieldKind Kind,
    Vector3 CenterOrAxis,
    Vector3 Parameters,
    float Radius,
    float Strength,
    float Frequency = 1f);

public sealed class EmitterSpec
{
    public required string Name { get; init; }
    public int MaxParticles { get; init; } = 1024;
    public float Duration { get; init; } = 1f;
    public bool Looping { get; init; }
    public bool WorldSpaceSimulation { get; init; } = true;
    public float RateOverTime { get; init; }
    public (float Time, int Count)[] Bursts { get; init; } = Array.Empty<(float, int)>();

    public LumenShapeKind Shape { get; init; } = LumenShapeKind.Point;
    public Vector3 ShapeExtents { get; init; } = Vector3.One;
    public float ConeAngleDegrees { get; init; } = 25f;

    public float StartLifetimeMin { get; init; } = 1f;
    public float StartLifetimeMax { get; init; } = 1f;
    public float StartSpeedMin { get; init; }
    public float StartSpeedMax { get; init; }
    public float StartSizeMin { get; init; } = 1f;
    public float StartSizeMax { get; init; } = 1f;
    public Vector4 StartColorLinear { get; init; } = Vector4.One;

    public ScalarCurve SizeOverLifetime { get; init; } = ScalarCurve.Constant(1f);
    public ColorGradient ColorOverLifetime { get; init; } = ColorGradient.Constant(Vector4.One);
    public ForceFieldSpec[] ForceFields { get; init; } = Array.Empty<ForceFieldSpec>();

    public string? TextureKey { get; init; }
    public int FlipbookTilesX { get; init; } = 1;
    public int FlipbookTilesY { get; init; } = 1;
    public float FlipbookCycles { get; init; } = 1f;

    public LumenBlendMode BlendMode { get; init; } = LumenBlendMode.Additive;
    public LumenBillboardMode BillboardMode { get; init; } = LumenBillboardMode.Camera;
    public float VelocityStretchFactor { get; init; }
    public float LengthScale { get; init; } = 1f;
    public float SoftParticleFadeDistance { get; init; } = 0.5f;
    public float HdrIntensity { get; init; } = 1f;
    public float BloomWeight { get; init; } = 1f;
    public LumenParticleTier PreferredTier { get; init; } = LumenParticleTier.L2_GpuCompute;
}

public readonly record struct PointLightSpec(
    float GlowRadius,
    float OverlightRadius,
    Vector3 ColorRgb,
    ScalarCurve? IntensityOverTime = null);

public readonly record struct TimelineEventSpec(float TimeSeconds, string EventName, int TargetNodeIndex = -1);

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
}

public interface ILumenAssetProvider
{
    // Requires: effectId is non-empty.
    // Ensures: returns true with resolved native EffectAsset if handled by this provider, else false.
    bool TryResolveEffect(string effectId, out EffectAsset? asset);
}
