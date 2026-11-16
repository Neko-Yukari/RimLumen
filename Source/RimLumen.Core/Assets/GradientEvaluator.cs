using System;
using System.Numerics;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Assets;

/// <summary>
/// Zero-allocation evaluator for <see cref="ColorGradient"/>. Interpolation is linear in the
/// RGBA components in the vanilla scene's stored encoding; alpha is coverage.
/// </summary>
public static class GradientEvaluator
{
    // Requires: gradient is non-null.
    // Ensures: gradient.Stops.Length == 0 -> Vector4.Zero.
    // Ensures: gradient.Stops.Length == 1 -> Stops[0].Rgba for every time.
    // Ensures: the first-stop test has priority: time <= Stops[0].Time -> Stops[0].Rgba;
    //          otherwise time >= Stops[^1].Time -> Stops[^1].Rgba
    //          (so a degenerate domain with first.Time == last.Time always yields the first stop).
    // Ensures: otherwise component-wise lerp between the bracketing stops; equal adjacent times use the left stop.
    public static Vector4 Evaluate(ColorGradient gradient, float time)
    {
        var stops = gradient.Stops;
        if (stops.Length == 0)
        {
            return Vector4.Zero;
        }

        if (stops.Length == 1 || stops[0].Time == stops[^1].Time || time <= stops[0].Time)
        {
            return stops[0].Rgba;
        }

        if (time >= stops[^1].Time)
        {
            return stops[^1].Rgba;
        }

        int right = 1;
        while (right < stops.Length - 1 && time > stops[right].Time)
        {
            right++;
        }

        var leftStop = stops[right - 1];
        var rightStop = stops[right];
        float dt = rightStop.Time - leftStop.Time;
        if (dt == 0f)
        {
            return leftStop.Rgba;
        }

        return Vector4.Lerp(leftStop.Rgba, rightStop.Rgba, (time - leftStop.Time) / dt);
    }

    // Requires: gradient is non-null; destination.Length > 0.
    // Ensures: samples uniformly over [Stops[0].Time, Stops[^1].Time]:
    //          destination[i] == Evaluate(gradient, first + (last - first) * i / (destination.Length - 1))
    //          when destination.Length > 1, and destination[0] == Evaluate(gradient, first) when it is 1.
    // Ensures: a degenerate domain (no stops, or first == last) fills every slot with Evaluate(gradient, first).
    // Ensures: allocates nothing and does not read outside destination.
    public static void BakeGradientLut(ColorGradient gradient, Span<Vector4> destination)
    {
        var stops = gradient.Stops;
        float first = stops.Length == 0 ? 0f : stops[0].Time;
        float last = stops.Length == 0 ? first : stops[^1].Time;
        if (destination.Length == 1 || first == last)
        {
            destination.Fill(Evaluate(gradient, first));
            return;
        }

        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = Evaluate(gradient, first + (last - first) * i / (destination.Length - 1));
        }
    }
}
