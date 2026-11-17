using System;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.ParticlesGpu;

namespace RimLumen.Core.Scene;

internal readonly record struct ParticleSubEvent(int Source, LumenSubEmitterTrigger Trigger, Vector3 Position,
    Vector4 Color, float Size);

internal static class ParticleSubEmitterMath
{
    // Current appearance, including the endpoint at death; the next BuildBatch has not run yet.
    internal static (Vector4 Color, float Size) Appearance(GpuCurveSample[] curves, in CpuParticle particle,
        float worldScale)
    {
        float t = MathF.Min(particle.Age / particle.Lifetime, 1f) * 63f;
        int lower = (int)t;
        GpuCurveSample a = curves[lower], b = curves[Math.Min(lower + 1, 63)];
        return (particle.StartColor * Vector4.Lerp(a.Color, b.Color, t - lower),
            particle.StartSize * float.Lerp(a.Size.X, b.Size.X, t - lower) * worldScale);
    }
}
