using System;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;

namespace RimLumen.Core.ParticlesGpu;

internal sealed class EmissionScheduler
{
    private readonly double duration;
    private readonly double rate;
    private readonly bool looping;
    private readonly ParticleBurstSpec[] bursts;
    private readonly uint initialSeed;
    private uint randomState;
    private double time;
    private double fractional;
    private long burstRemainder;
    private bool started;

    // Requires: spec passed LumenAssetRules validation; emissionSeed is nonzero.
    public EmissionScheduler(EmitterSpec spec, uint emissionSeed)
    {
        duration = spec.Duration;
        rate = spec.RateOverTime;
        looping = spec.Looping;
        bursts = (ParticleBurstSpec[])spec.Bursts.Clone();
        for (int i = 1; i < bursts.Length; i++)
        {
            ParticleBurstSpec value = bursts[i];
            int j = i;
            while (j > 0 && bursts[j - 1].Time > value.Time)
            {
                bursts[j] = bursts[j - 1];
                j--;
            }
            bursts[j] = value;
        }
        initialSeed = emissionSeed;
        randomState = emissionSeed;
    }

    public bool Finished => !looping && time >= duration;

    // Ensures: the schedule starts over as if newly constructed: time 0, no fractional spawn, the time-0 burst pending.
    public void Restart()
    {
        time = 0d;
        fractional = 0d;
        burstRemainder = 0;
        started = false;
        randomState = initialSeed;
    }

    internal int AcceptBurst(int count, float density)
        => ParticleEmissionBudget.AccumulateBurst(ref burstRemainder, count, density);

    // Requires: dt >= 0; called once per captured frame.
    // Ensures: advances emitter time; preserves fractional rate and counts all crossed bursts.
    public double Advance(float dt, bool emitting) => Advance(dt, emitting, 1f, 1f);

    internal double Advance(float dt, bool emitting, float density, float rateMultiplier)
    {
        if (!float.IsFinite(dt) || dt < 0f) throw new ArgumentOutOfRangeException(nameof(dt));
        double end = looping ? time + dt : Math.Min(time + dt, duration);
        double remainder = fractional;
        long nextBurstRemainder = burstRemainder;
        uint nextRandomState = randomState;
        double count = 0;
        if (emitting)
        {
            count = ParticleEmissionBudget.AccumulateRate(ref remainder, rate * rateMultiplier, end - time, density);
            long firstCycle = looping && bursts.Length != 0 ? Math.Max(0L, (long)Math.Floor(time / duration) - 1) : 0;
            long lastCycle = looping && bursts.Length != 0 ? (long)Math.Floor(end / duration) : 0;
            for (long cycle = firstCycle; bursts.Length != 0 && cycle <= lastCycle; cycle++)
            {
                foreach (var burst in bursts)
                {
                    double occurrenceTime = cycle * duration + burst.Time;
                    if ((occurrenceTime > time || (!started && occurrenceTime == 0d)) && occurrenceTime <= end)
                    {
                        if (ParticleRandom.Next01(ref nextRandomState) < burst.Probability)
                            count += ParticleEmissionBudget.AccumulateBurst(ref nextBurstRemainder, burst.Count, density);
                    }
                }
            }
        }
        time = end;
        fractional = remainder;
        burstRemainder = nextBurstRemainder;
        randomState = nextRandomState;
        started = true;
        return count;
    }
}
