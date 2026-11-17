using System;

namespace RimLumen.Core.Scene;

internal static class ParticleEmissionBudget
{
    internal static double AccumulateRate(ref double remainder, double rate, double dt, float density)
    {
        remainder += rate * dt * density;
        double count = Math.Floor(remainder);
        remainder -= count;
        return count;
    }

    // One Q=65536 fixed-point remainder per emitter is shared by explicit enqueue and scheduled occurrences.
    // Each burst adds count*floor(density*Q), requests ceil(a/Q), then retains the negative excess.
    // Effective density is floor(density*Q)/Q, no greater than the authored setting.
    // Capacity may reject requested particles without replenishing a. Explicit Restart/Play/GoTo reset a,
    // not accepted pending; loop wrap never resets it. Continuous rate keeps its independent floor carry above.
    internal static int AccumulateBurst(ref long remainder, int count, float density)
    {
        const long quantum = 65536;
        remainder += (long)count * (long)MathF.Floor(density * quantum);
        int admitted = (int)(remainder / quantum + (remainder % quantum > 0 ? 1 : 0));
        remainder -= (long)admitted * quantum;
        return admitted;
    }
}
