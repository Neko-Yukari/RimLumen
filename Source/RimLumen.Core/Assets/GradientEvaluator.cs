using System;
using System.Numerics;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Assets;

/// <summary>
/// Zero-allocation evaluator for <see cref="ColorGradient"/>. Interpolation is linear in the
/// RGBA components (the stops are already linear-space values).
/// </summary>
public static class GradientEvaluator
{
    // Requires: gradient is non-null.
    // Ensures: gradient.Stops.Length == 0 -> Vector4.Zero.
    // Ensures: gradient.Stops.Length == 1 -> Stops[0].RgbaLinear for every time.
    // Ensures: time <= Stops[0].Time -> Stops[0].RgbaLinear; time >= Stops[^1].Time -> Stops[^1].RgbaLinear.
    // Ensures: otherwise component-wise lerp between the bracketing stops; equal adjacent times use the left stop.
    public static Vector4 Evaluate(ColorGradient gradient, float time) => throw new NotImplementedException();

    // Requires: gradient is non-null; destination.Length > 0.
    // Ensures: samples uniformly over [Stops[0].Time, Stops[^1].Time]:
    //          destination[i] == Evaluate(gradient, first + (last - first) * i / (destination.Length - 1))
    //          when destination.Length > 1, and destination[0] == Evaluate(gradient, first) when it is 1.
    // Ensures: a degenerate domain (no stops, or first == last) fills every slot with Evaluate(gradient, first).
    // Ensures: allocates nothing and does not read outside destination.
    public static void BakeGradientLut(ColorGradient gradient, Span<Vector4> destination) => throw new NotImplementedException();
}
