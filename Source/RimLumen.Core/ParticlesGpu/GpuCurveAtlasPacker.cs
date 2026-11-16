using System;
using System.Numerics;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.ParticlesGpu;

public static class GpuCurveAtlasPacker
{
    public const int SamplesPerRow = 64;

    // Requires: destination has at least 64 samples; curves passed LumenAssetRules validation.
    // Ensures: writes one row retaining HDR color values and Hermite scalar tangents.
    public static void Pack(ScalarCurve size, ColorGradient color, System.Span<GpuCurveSample> destination)
    {
        if (destination.Length < SamplesPerRow)
            throw new ArgumentException("A curve row requires 64 samples.", nameof(destination));

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
        if (time <= stops[0].Time) return stops[0].Rgba;
        for (int i = 1; i < stops.Length; i++)
        {
            if (time > stops[i].Time) continue;
            var a = stops[i - 1];
            var b = stops[i];
            double t = (time - a.Time) / ((double)b.Time - a.Time);
            return new Vector4(
                (float)((1 - t) * a.Rgba.X + t * b.Rgba.X),
                (float)((1 - t) * a.Rgba.Y + t * b.Rgba.Y),
                (float)((1 - t) * a.Rgba.Z + t * b.Rgba.Z),
                (float)((1 - t) * a.Rgba.W + t * b.Rgba.W));
        }
        return stops[^1].Rgba;
    }
}
