using System;

namespace RimLumen.Core.Scene;

internal static class ParticleEmissionBudget
{
    // Preserve the CPU path's near-default density behavior without renormalizing host settings.
    private static float EffectiveDensity(float density) => density < 0.999f ? density : 1f;

    internal static double AccumulateRate(ref double remainder, double rate, double dt, float density)
    {
        remainder += rate * dt * EffectiveDensity(density);
        double count = Math.Floor(remainder);
        remainder -= count;
        return count;
    }

    internal static int ScaleBurst(int count, float density)
    {
        density = EffectiveDensity(density);
        return count > 0 && density < 1f ? Math.Max(1, (int)MathF.Round(count * density)) : count;
    }
}
