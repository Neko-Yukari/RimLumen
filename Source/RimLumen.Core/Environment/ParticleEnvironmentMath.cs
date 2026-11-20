using System.Numerics;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.ParticleWorld;

// World-space environment forces; conversion to simulation space happens only after summing world accelerations.
internal static class ParticleEnvironmentMath
{
    // A pulse crosses its authored radius in 0.5 simulation seconds; triangular shell width is 1/4 radius.
    // Centre lowered by radius/4 gives particles at ground height a genuine spherical upward component.
    internal static LumenEnvironmentForce Explosion(Vector3 position, float radius, float elapsed)
        => new(LumenEnvironmentChannel.Explosion, LumenEnvironmentForceShape.SphericalWave,
            position - Vector3.UnitY * (radius * 0.25f), radius,
            24f * radius, elapsed * (2f * radius), radius * 0.125f);

    internal static Vector3 Acceleration(System.ReadOnlySpan<LumenEnvironmentForce> sources, EmitterSpec spec,
        in Matrix4x4 world, in Matrix4x4 inverse, Vector3 position)
    {
        Vector3 worldPosition = spec.WorldSpaceSimulation ? position : Vector3.Transform(position, world);
        Vector3 total = Vector3.Zero;
        foreach (LumenEnvironmentForce source in sources)
            if ((source.Channel & spec.EnvironmentChannels) != 0) total += source.Acceleration(worldPosition);
        total *= spec.EnvironmentForceScale;
        return spec.WorldSpaceSimulation ? total : Vector3.TransformNormal(total, inverse);
    }
}
