using System;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Assets;

/// <summary>
/// Zero-allocation evaluator for <see cref="ScalarCurve"/>.
/// </summary>
public static class CurveEvaluator
{
    // Requires: curve is non-null.
    // Ensures: curve.Keys.Length == 0 -> 0f (Multiplier is not applied to the empty case).
    // Ensures: curve.Keys.Length == 1 -> curve.Keys[0].Value * curve.Multiplier, for every time.
    // Ensures: time <= curve.Keys[0].Time -> curve.Keys[0].Value * curve.Multiplier.
    // Ensures: time >= curve.Keys[^1].Time -> curve.Keys[^1].Value * curve.Multiplier.
    // Ensures: otherwise cubic Hermite interpolation on the bracketing pair, using the left key's
    //          OutTangent and the right key's InTangent as value-per-second slopes, multiplied by
    //          curve.Multiplier. Keys must be sorted by Time; equal adjacent times use the left value.
    public static float Evaluate(ScalarCurve curve, float time) => throw new NotImplementedException();

    // Requires: curve is non-null; destination.Length > 0.
    // Ensures: samples the curve uniformly over [Keys[0].Time, Keys[^1].Time]:
    //          destination[i] == Evaluate(curve, first + (last - first) * i / (destination.Length - 1))
    //          when destination.Length > 1, and destination[0] == Evaluate(curve, first) when it is 1.
    // Ensures: a degenerate domain (no keys, or first == last) fills every slot with Evaluate(curve, first).
    // Ensures: allocates nothing and does not read outside destination.
    public static void BakeScalarLut(ScalarCurve curve, Span<float> destination) => throw new NotImplementedException();
}
