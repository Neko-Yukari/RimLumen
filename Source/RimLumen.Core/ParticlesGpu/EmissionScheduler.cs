using System;
using RimLumen.Core.Contracts;

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

    public EmissionScheduler(EmitterSpec spec)
    {
        if (!float.IsFinite(spec.Duration) || spec.Duration <= 0f)
            throw new ArgumentOutOfRangeException(nameof(spec), "Duration must be finite and positive.");
        if (!float.IsFinite(spec.RateOverTime) || spec.RateOverTime < 0f)
            throw new ArgumentOutOfRangeException(nameof(spec), "Emission rate must be finite and nonnegative.");
        foreach (var burst in spec.Bursts)
        {
            if (!float.IsFinite(burst.Time) || burst.Time < 0f || burst.Time > spec.Duration || burst.Count < 0)
                throw new ArgumentOutOfRangeException(nameof(spec), "Burst times must be within duration and counts nonnegative.");
        }
        duration = spec.Duration;
        rate = spec.RateOverTime;
        looping = spec.Looping;
        bursts = ((float Time, int Count)[])spec.Bursts.Clone();
    }

    public bool Finished => !looping && time >= duration;

    // Requires: dt >= 0; called once per captured frame.
    // Ensures: advances emitter time; preserves fractional rate and counts all crossed bursts.
    public int Advance(float dt, bool emitting)
    {
        if (!float.IsFinite(dt) || dt < 0f) throw new ArgumentOutOfRangeException(nameof(dt));
        double end = looping ? time + dt : Math.Min(time + dt, duration);
        double remainder = fractional;
        double count = 0;
        if (emitting)
        {
            double generated = fractional + (end - time) * rate;
            count = Math.Floor(generated);
            remainder = generated - count;
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
                count += occurrences * burst.Count;
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
