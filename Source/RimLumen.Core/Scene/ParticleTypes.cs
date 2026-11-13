using System;
using System.Numerics;

namespace RimLumen.Core.Scene;

/// <summary>One billboard corner submitted to the HDR pass.</summary>
public readonly struct ParticleVertex
{
    public readonly Vector3 Position;
    public readonly Vector2 Uv;
    public readonly Vector4 Color;

    public ParticleVertex(Vector3 position, Vector2 uv, Vector4 color)
    {
        Position = position;
        Uv = uv;
        Color = color;
    }
}

/// <summary>
/// Read-only snapshot of one live L1 particle. Values are the post-integration state of the last
/// <c>CaptureMainThread</c>; <see cref="Color"/> already includes StartColorLinear, the colour
/// curve, <c>HdrIntensity</c> and the runtime intensity multiplier, and <see cref="Size"/> already
/// includes StartSize, the size curve and <c>LengthScale</c>.
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
}

/// <summary>
/// Sink the HDR pass owner installs on <see cref="CpuParticleSimulator"/> to consume the L1 quad
/// batch. When no sink is installed the simulator produces vertices but submits no draw.
/// </summary>
public interface IL1ParticleBatchSink
{
    // Requires: called on the main thread inside the RimLumen VFX pass.
    // Ensures: submits one dynamic quad batch (4 vertices per particle, 6 indices per particle)
    //          using the given spans; the spans are only valid for the duration of the call.
    void Submit(ReadOnlySpan<ParticleVertex> vertices, ReadOnlySpan<uint> indices, int liveParticleCount);
}
