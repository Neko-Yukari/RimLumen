using System;
using System.Numerics;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.ParticlesGpu;

public static class GpuCurveAtlasPacker
{
    public const int SamplesPerRow = 64;

    // Requires: destination has at least 64 samples; keys/stops use normalized time.
    // Ensures: writes one row retaining HDR color values and Hermite scalar tangents.
    public static void Pack(ScalarCurve size, ColorGradient color, System.Span<GpuCurveSample> destination)
    {
        if (destination.Length < SamplesPerRow)
            throw new ArgumentException("A curve row requires 64 samples.", nameof(destination));
        if (size.Keys.Length == 0 || !float.IsFinite(size.Multiplier))
            throw new ArgumentException("A scalar curve requires keys and a finite multiplier.", nameof(size));
        if (color.Stops.Length == 0)
            throw new ArgumentException("A gradient requires color stops.", nameof(color));

        float previous = -1f;
        foreach (var key in size.Keys)
        {
            if (!ValidTime(key.Time, previous) || !float.IsFinite(key.Value) ||
                !float.IsFinite(key.InTangent) || !float.IsFinite(key.OutTangent))
                throw new ArgumentException("Curve keys must be finite and strictly ordered in [0,1].", nameof(size));
            previous = key.Time;
        }
        previous = -1f;
        foreach (var stop in color.Stops)
        {
            var rgba = stop.RgbaLinear;
            if (!ValidTime(stop.Time, previous) || !float.IsFinite(rgba.X) ||
                !float.IsFinite(rgba.Y) || !float.IsFinite(rgba.Z) || !float.IsFinite(rgba.W))
                throw new ArgumentException("Gradient stops must be finite and strictly ordered in [0,1].", nameof(color));
            previous = stop.Time;
        }

        for (int i = 0; i < SamplesPerRow; i++)
        {
            double time = (double)i / (SamplesPerRow - 1);
            float value = (float)(Evaluate(size.Keys, time) * size.Multiplier);
            if (!float.IsFinite(value))
                throw new ArgumentException("Sampled curve exceeds finite GPU scalar range.", nameof(size));
            destination[i] = new GpuCurveSample
            {
                Color = Evaluate(color.Stops, time),
                Size = new Vector4(value, 0f, 0f, 0f),
            };
        }
    }

    private static bool ValidTime(float time, float previous)
        => float.IsFinite(time) && time >= 0f && time <= 1f && time > previous;

    private static double Evaluate(CurveKey[] keys, double time)
    {
        if (time <= keys[0].Time) return keys[0].Value;
        for (int i = 1; i < keys.Length; i++)
        {
            if (time > keys[i].Time) continue;
            var a = keys[i - 1];
            var b = keys[i];
            double width = (double)b.Time - a.Time;
            double t = (time - a.Time) / width;
            double t2 = t * t;
            double t3 = t2 * t;
            return (2 * t3 - 3 * t2 + 1) * a.Value + (t3 - 2 * t2 + t) * width * a.OutTangent +
                   (-2 * t3 + 3 * t2) * b.Value + (t3 - t2) * width * b.InTangent;
        }
        return keys[^1].Value;
    }

    private static Vector4 Evaluate(ColorStop[] stops, double time)
    {
        if (time <= stops[0].Time) return stops[0].RgbaLinear;
        for (int i = 1; i < stops.Length; i++)
        {
            if (time > stops[i].Time) continue;
            var a = stops[i - 1];
            var b = stops[i];
            double t = (time - a.Time) / ((double)b.Time - a.Time);
            return new Vector4(
                (float)((1 - t) * a.RgbaLinear.X + t * b.RgbaLinear.X),
                (float)((1 - t) * a.RgbaLinear.Y + t * b.RgbaLinear.Y),
                (float)((1 - t) * a.RgbaLinear.Z + t * b.RgbaLinear.Z),
                (float)((1 - t) * a.RgbaLinear.W + t * b.RgbaLinear.W));
        }
        return stops[^1].RgbaLinear;
    }
}
