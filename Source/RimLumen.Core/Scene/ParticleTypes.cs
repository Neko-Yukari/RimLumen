using System.Numerics;

namespace RimLumen.Core.Scene;

/// <summary>
/// One billboard corner submitted to the HDR pass. <see cref="Color"/> is the straight authored colour;
/// <see cref="HdrGain"/> (<c>HdrIntensity</c> times the runtime intensity multiplier) multiplies RGB only after
/// the shader's colour-space conversion. RadialUv and HdrGain are contiguous: they upload as one float3 attribute.
/// <see cref="DepthMask"/> is CameraBillboardBasis.DepthMaskAttribute: (occlusion lift, reference Z), zero when off.
/// </summary>
public readonly struct ParticleVertex
{
    public readonly Vector3 Position;
    public readonly Vector2 Uv;
    public readonly Vector4 Color;
    public readonly Vector2 RadialUv;
    public readonly float HdrGain;
    public readonly Vector2 DepthMask;

    public ParticleVertex(Vector3 position, Vector2 uv, Vector4 color, Vector2 radialUv, float hdrGain, Vector2 depthMask)
    {
        Position = position;
        Uv = uv;
        Color = color;
        RadialUv = radialUv;
        HdrGain = hdrGain;
        DepthMask = depthMask;
    }
}

/// <summary>
/// Read-only snapshot of one live L1 particle. Values are the post-integration state of the last
/// <c>CaptureMainThread</c>; <see cref="Color"/> is StartColor times the colour curve (the scalar
/// <c>HdrIntensity</c> gain travels separately as <see cref="ParticleVertex.HdrGain"/>), and <see cref="Size"/> already
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
    public Vector4 StartColor;
    public float Size;
    public Vector4 Color;
    public int FlipbookFrame;
    // Vertical3D world-space particles: the node's ground position (X, Z) at birth, or the event point for a
    // sub-emitter birth.
    internal Vector2 SpawnAnchorXZ;
}

/// <summary>Contiguous index range sharing texture, blend, soft fade and material colour calculation.</summary>
public readonly record struct L1SubBatch(string? TextureKey, RimLumen.Core.Contracts.LumenBlendMode BlendMode, int IndexStart, int IndexCount, float SoftParticleFadeDistance = 0.5f)
{
    public Vector4 MaterialTint { get; init; } = Vector4.One;
    public RimLumen.Core.Contracts.LumenColorSpace ColorSpace { get; init; }
}
