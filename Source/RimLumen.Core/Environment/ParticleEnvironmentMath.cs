using System;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;

namespace RimLumen.Core.ParticleWorld;

public enum ParticleCursorMode { Off, Repel, Attract }

// World-space inputs; conversion to simulation space happens only after summing the world accelerations.
internal readonly record struct ParticleEnvironmentSource(LumenEnvironmentChannel Channel, Vector3 Center,
    float Radius, float Strength, float WaveRadius, float Thickness)
{
    internal Vector3 Acceleration(Vector3 worldPosition)
    {
        Vector3 delta = worldPosition - Center;
        if (Channel == LumenEnvironmentChannel.Explosion)
            return ParticleForceMath.RingWave(delta, WaveRadius, Thickness, Radius, Strength);
        delta.Y = 0;
        float distance = delta.Length();
        return distance > 1e-6f ? delta / distance * (Strength * MathF.Max(0f, 1f - distance / Radius)) : Vector3.Zero;
    }
}

internal static class ParticleEnvironmentMath
{
    // A pulse crosses its authored radius in 0.5 simulation seconds; triangular shell width is 1/4 radius.
    // Centre lowered by radius/4 gives particles at ground height a genuine spherical upward component.
    internal static ParticleEnvironmentSource Explosion(Vector3 position, float radius, float elapsed, float multiplier)
        => new(LumenEnvironmentChannel.Explosion, position - Vector3.UnitY * (radius * 0.25f), radius,
            24f * radius * multiplier, elapsed * (2f * radius), radius * 0.125f);

    internal static Vector3 Acceleration(ReadOnlySpan<ParticleEnvironmentSource> sources, EmitterSpec spec,
        in Matrix4x4 world, in Matrix4x4 inverse, Vector3 position)
    {
        Vector3 worldPosition = spec.WorldSpaceSimulation ? position : Vector3.Transform(position, world);
        Vector3 total = Vector3.Zero;
        foreach (ParticleEnvironmentSource source in sources)
            if ((source.Channel & spec.EnvironmentChannels) != 0) total += source.Acceleration(worldPosition);
        total *= spec.EnvironmentForceScale;
        return spec.WorldSpaceSimulation ? total : Vector3.TransformNormal(total, inverse);
    }
}
