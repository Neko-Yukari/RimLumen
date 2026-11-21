using System;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.ParticlesGpu;

// Pure sizing, bucket, lifetime, ratio-PRNG, and deterministic header-slot claim logic shared across L1 & L2 trails.
// L2 compute shader ParticleTrailSim.comp.glsl mirrors ClaimTrailSlot and RollRatio.
internal static class ParticleTrailMath
{
    public const int DefaultHeaderCapacity = 32_768;
    public const int DefaultPointCapacity = 524_288;
    public const int DefaultMapCapacity = 32_768;
    // Fixed bounded probe distance inside overflow banks [P, P * BankCount); bounds worst-case O(1) GPU thread work.
    public const int MaxLinearProbe = 64;
    public const int MaxDetachedBanks = 4;
    public const uint NoTrailAssigned = 0xFFFFFFFFu;
    public const uint TrailRejected = 0xFFFFFFFEu;

    internal static readonly int[] StripBuckets = { 8, 16, 32, 64, 128, 256 };

    public static int ComputeBankCount(ParticleTrailSpec spec)
        => spec.DieWithParticles ? 1 : (int)Math.Clamp(1f + MathF.Ceiling(spec.Lifetime), 2f, MaxDetachedBanks);

    public static int ComputeHeaderCapacity(EmitterSpec spec)
        => checked(spec.MaxParticles * ComputeBankCount(spec.Trails!));

    public static int ComputePointCapacity(EmitterSpec spec)
        => checked(ComputeHeaderCapacity(spec) * spec.Trails!.MaxPoints);

    // Fits up to MaxPoints + 1 visible centres (maxPoints segments + optional extra clipped/head point).
    public static int StripBucketIndex(int maxPoints)
    {
        for (int i = 0; i < StripBuckets.Length; i++)
            if (maxPoints <= StripBuckets[i]) return i;
        return StripBuckets.Length - 1;
    }

    public static int StripBucketSegments(int maxPoints) => StripBuckets[StripBucketIndex(maxPoints)];

    public static float PeakCurveSize(ReadOnlySpan<GpuCurveSample> samples)
    {
        float peak = 0f;
        for (int i = 0; i < samples.Length; i++)
            peak = MathF.Max(peak, samples[i].Size.X);
        return peak;
    }

    // Upper bound on additional seconds detached trails can remain visible after their owning particle dies.
    public static float MaxTrailLifetimeSeconds(EmitterSpec spec, ReadOnlySpan<GpuCurveSample> particleCurves, float sizeScaleFactor)
    {
        if (spec.Trails is not { DieWithParticles: false } trails) return 0f;
        float sizeFactor = trails.SizeAffectsLifetime
            ? spec.StartSizeMax * PeakCurveSize(particleCurves) * sizeScaleFactor
            : 1f;
        return spec.StartLifetimeMax * trails.Lifetime * sizeFactor;
    }

    public static uint Hash(uint value)
    {
        value ^= value >> 16;
        value = unchecked(value * 0x7FEB352Du);
        value ^= value >> 15;
        value = unchecked(value * 0x846CA68Bu);
        return value ^ (value >> 16);
    }

    public static bool RollRatio(uint emitterSeed, uint particleSeed, int localSlot, float ratio)
    {
        if (ratio >= 1f) return true;
        if (ratio <= 0f) return false;
        uint h = Hash(emitterSeed ^ Hash(particleSeed ^ unchecked((uint)localSlot * 0x9E3779B9u)));
        float u = h * (1f / 4294967296f);
        return u < ratio;
    }

    // 4-step deterministic claim inside [0, particleCapacity * bankCount):
    //   1. Try private Bank 0 slot `localSlot` (only ever claimed by this particle slot index `localSlot`).
    //   2. Try own overflow bank slots `b * particleCapacity + localSlot` for b in [1, bankCount).
    //   3. Linearly probe up to MaxLinearProbe (64) slots inside overflow [particleCapacity, headerCount),
    //      never entering another particle's private Bank 0.
    //   4. On overflow saturation, deterministically recycle private Bank 0 slot `localSlot`, evicting this slot's
    //      own oldest detached trail rather than dropping a newly spawned particle's trail or racing peers.
    // Mirrors ParticleTrailSim.comp.glsl claimTrailSlot.
    public static int ClaimTrailSlot(ReadOnlySpan<bool> activeFlags, int particleCapacity, int bankCount, int localSlot,
        out bool recycledBank0)
    {
        recycledBank0 = false;
        if (!activeFlags[localSlot]) return localSlot;
        for (int b = 1; b < bankCount; b++)
        {
            int candidate = b * particleCapacity + localSlot;
            if (!activeFlags[candidate]) return candidate;
        }
        int overflowCount = (bankCount - 1) * particleCapacity;
        if (overflowCount > 0)
        {
            int probes = Math.Min(MaxLinearProbe, overflowCount);
            for (int p = 1; p <= probes; p++)
            {
                int candidate = particleCapacity + ((localSlot + p) % overflowCount);
                if (!activeFlags[candidate]) return candidate;
            }
        }
        recycledBank0 = true;
        return localSlot;
    }
}
