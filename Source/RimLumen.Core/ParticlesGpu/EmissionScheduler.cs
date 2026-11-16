using System;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;

namespace RimLumen.Core.ParticlesGpu;

public sealed class EmissionScheduler
{
    private readonly double duration;
    private readonly double rate;
    private readonly bool looping;
    private readonly (float Time, int Count)[] bursts;
    private double time;
    private double fractional;
    private bool started;

    // Requires: spec passed LumenAssetRules validation.
    public EmissionScheduler(EmitterSpec spec)
    {
        foreach (var burst in spec.Bursts)
        {
            if (burst.Time > spec.Duration)
                throw new ArgumentOutOfRangeException(nameof(spec), "Burst time must not exceed duration.");
        }
        duration = spec.Duration;
        rate = spec.RateOverTime;
        looping = spec.Looping;
        bursts = ((float Time, int Count)[])spec.Bursts.Clone();
    }

    public bool Finished => !looping && time >= duration;

    // Ensures: the schedule starts over as if newly constructed: time 0, no fractional spawn, the time-0 burst pending.
    public void Restart()
    {
        time = 0d;
        fractional = 0d;
        started = false;
    }

    // Requires: dt >= 0; called once per captured frame.
    // Ensures: advances emitter time; preserves fractional rate and counts all crossed bursts.
    public int Advance(float dt, bool emitting) => Advance(dt, emitting, 1f);

    internal int Advance(float dt, bool emitting, float density)
    {
        if (!float.IsFinite(dt) || dt < 0f) throw new ArgumentOutOfRangeException(nameof(dt));
        double end = looping ? time + dt : Math.Min(time + dt, duration);
        double remainder = fractional;
        double count = 0;
        if (emitting)
        {
            count = ParticleEmissionBudget.AccumulateRate(ref remainder, rate, end - time, density);
            foreach (var burst in bursts)
            {
                if (burst.Count == 0) continue;
                double occurrences;
                if (looping)
                {
                    // For each burst, subtract events at/before the old time from those at/before the new time.
                    double before = time < burst.Time ? 0 : Math.Floor((time - burst.Time) / duration) + 1;
                    double after = end < burst.Time ? 0 : Math.Floor((end - burst.Time) / duration) + 1;
                    occurrences = after - before;
                }
                else
                {
                    occurrences = burst.Time > time && burst.Time <= end ? 1 : 0;
                }
                if (!started && burst.Time == 0f) occurrences++;
                count += occurrences * ParticleEmissionBudget.ScaleBurst(burst.Count, density);
            }
            if (count > int.MaxValue)
                throw new OverflowException("Frame emission exceeds the integer spawn budget.");
        }
        time = end;
        fractional = remainder;
        started = true;
        return (int)count;
    }
}
