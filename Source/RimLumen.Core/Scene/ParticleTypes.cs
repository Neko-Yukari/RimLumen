using System.Numerics;

namespace RimLumen.Core.Scene;

/// <summary>One billboard corner submitted to the HDR pass.</summary>
public readonly struct ParticleVertex
{
    public readonly Vector3 Position;
    public readonly Vector2 Uv;
    public readonly Vector4 Color;
    public readonly Vector2 RadialUv;

    public ParticleVertex(Vector3 position, Vector2 uv, Vector4 color, Vector2 radialUv)
    {
        Position = position;
        Uv = uv;
        Color = color;
        RadialUv = radialUv;
    }
}

/// <summary>
/// Read-only snapshot of one live L1 particle. Values are the post-integration state of the last
/// <c>CaptureMainThread</c>; <see cref="Color"/> already includes StartColor, the colour
/// curve, <c>HdrIntensity</c> and the runtime intensity multiplier, and <see cref="Size"/> already
/// includes StartSize, the size curve and the emitter's determinant-based world size scale.
/// </summary>
public struct CpuParticle
{
    public int EmitterId;
    public Vector3 Position;
    public Vector3 Velocity;
    public float Age;
    public float Lifetime;
    public float StartSize;
    public float Size;
    public Vector4 Color;
    public int FlipbookFrame;
    // Vertical3D: the emitter's play-origin ground position (X, Z) when this world-space particle was born.
    internal Vector2 SpawnAnchorXZ;
}

/// <summary>Contiguous index range sharing texture, blend, soft fade and material colour calculation.</summary>
public readonly record struct L1SubBatch(string? TextureKey, RimLumen.Core.Contracts.LumenBlendMode BlendMode, int IndexStart, int IndexCount, float SoftParticleFadeDistance = 0.5f)
{
    public Vector4 MaterialTint { get; init; } = Vector4.One;
    public RimLumen.Core.Contracts.LumenColorSpace ColorSpace { get; init; }
}
