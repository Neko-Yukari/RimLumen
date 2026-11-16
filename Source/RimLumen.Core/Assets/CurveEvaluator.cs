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
    // Mirrors pinned RimUnity AnimationCurve.cs:23,30-35 (45ca90987f7d3721715b45c021cab986ee14c468):
    // Ensures: a degenerate domain (first.Time == last.Time, including 2 or >=3 equal keys) -> curve.Keys[^1].Value * curve.Multiplier for every time.
    // Ensures: otherwise time <= curve.Keys[0].Time -> curve.Keys[0].Value * curve.Multiplier (duplicated first key returns the FIRST).
    // Ensures: otherwise time >= curve.Keys[^1].Time -> curve.Keys[^1].Value * curve.Multiplier (duplicated final key returns the LAST).
    // Ensures: otherwise cubic Hermite interpolation on the bracketing pair, using the left key's
    //          OutTangent and the right key's InTangent as value-per-second slopes, multiplied by
    //          curve.Multiplier. Keys must be non-decreasing in Time. An interior exact repeated time
    //          selects the LAST equal key, so the left-hand segment ends at the FIRST equal key.
    //          Not universally right-continuous: the two endpoint exceptions above return FIRST/LAST.
    public static float Evaluate(ScalarCurve curve, float time)
    {
        var keys = curve.Keys;
        if (keys.Length == 0)
        {
            return 0f;
        }

        if (keys.Length == 1 || keys[0].Time >= keys[^1].Time)
        {
            return keys[^1].Value * curve.Multiplier;
        }

        if (time <= keys[0].Time)
        {
            return keys[0].Value * curve.Multiplier;
        }

        if (time >= keys[^1].Time)
        {
            return keys[^1].Value * curve.Multiplier;
        }

        // Upstream binary search: lo ends at the last key whose Time <= time.
        int lo = 0, hi = keys.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (keys[mid].Time <= time) lo = mid;
            else hi = mid;
        }

        var leftKey = keys[lo];
        var rightKey = keys[hi];
        float dt = rightKey.Time - leftKey.Time;

        float t = (time - leftKey.Time) / dt;
        float t2 = t * t;
        float t3 = t2 * t;
        float h00 = 2f * t3 - 3f * t2 + 1f;
        float h10 = t3 - 2f * t2 + t;
        float h01 = -2f * t3 + 3f * t2;
        float h11 = t3 - t2;
        return (h00 * leftKey.Value + h10 * dt * leftKey.OutTangent +
                h01 * rightKey.Value + h11 * dt * rightKey.InTangent) * curve.Multiplier;
    }

    // Requires: curve is non-null; destination.Length > 0.
    // Ensures: samples the curve uniformly over [Keys[0].Time, Keys[^1].Time]:
    //          destination[i] == Evaluate(curve, first + (last - first) * i / (destination.Length - 1))
    //          when destination.Length > 1, and destination[0] == Evaluate(curve, first) when it is 1.
    // Ensures: a degenerate domain (no keys, or first == last) fills every slot with Evaluate(curve, first).
    // Ensures: allocates nothing and does not read outside destination.
    public static void BakeScalarLut(ScalarCurve curve, Span<float> destination)
    {
        var keys = curve.Keys;
        float first = keys.Length == 0 ? 0f : keys[0].Time;
        float last = keys.Length == 0 ? first : keys[^1].Time;
        if (destination.Length == 1 || first == last)
        {
            destination.Fill(Evaluate(curve, first));
            return;
        }

        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = Evaluate(curve, first + (last - first) * i / (destination.Length - 1));
        }
    }
}
