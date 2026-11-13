using System;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class CurveEvaluatorSuite
{
    [LumenFact]
    public static void HermiteUsesSlopesInSecondsAndMultiplier()
    {
        var curve = new ScalarCurve(new[]
        {
            new CurveKey(2f, 2f, InTangent: 99f, OutTangent: 2f),
            new CurveKey(6f, 10f, InTangent: -1f, OutTangent: -99f),
        }, 3f);

        // At t=3, Hermite weights are 27/32, 9/64, 5/32, -3/64.
        AssertClose(13.6875f, CurveEvaluator.Evaluate(curve, 3f));
        AssertClose(22.5f, CurveEvaluator.Evaluate(curve, 4f));
        AssertClose(29.0625f, CurveEvaluator.Evaluate(curve, 5f));
    }

    [LumenFact]
    public static void EmptyConstantAndClampBoundaries()
    {
        var empty = new ScalarCurve(Array.Empty<CurveKey>(), float.PositiveInfinity);
        AssertClose(0f, CurveEvaluator.Evaluate(empty, -100f));
        AssertClose(0f, CurveEvaluator.Evaluate(empty, 100f));

        var single = new ScalarCurve(new[] { new CurveKey(5f, 7f, 20f, -20f) }, -2f);
        AssertClose(-14f, CurveEvaluator.Evaluate(single, -100f));
        AssertClose(-14f, CurveEvaluator.Evaluate(single, 5f));
        AssertClose(-14f, CurveEvaluator.Evaluate(single, 100f));
        AssertClose(9f, CurveEvaluator.Evaluate(ScalarCurve.Constant(9f), 20f));

        var curve = new ScalarCurve(new[] { new CurveKey(2f, 3f), new CurveKey(6f, 9f) }, 2f);
        AssertClose(6f, CurveEvaluator.Evaluate(curve, -10f));
        AssertClose(6f, CurveEvaluator.Evaluate(curve, 2f));
        AssertClose(18f, CurveEvaluator.Evaluate(curve, 6f));
        AssertClose(18f, CurveEvaluator.Evaluate(curve, 10f));
    }

    [LumenFact]
    public static void BracketsMultipleKeysAndUsesFirstAtDuplicateTime()
    {
        var curve = new ScalarCurve(new[]
        {
            new CurveKey(0f, 0f), new CurveKey(2f, 8f), new CurveKey(6f, 0f),
        });
        AssertClose(4f, CurveEvaluator.Evaluate(curve, 1f));
        AssertClose(8f, CurveEvaluator.Evaluate(curve, 2f));
        AssertClose(4f, CurveEvaluator.Evaluate(curve, 4f));

        var duplicate = new ScalarCurve(new[]
        {
            new CurveKey(0f, 0f), new CurveKey(2f, 8f),
            new CurveKey(2f, 20f), new CurveKey(4f, 40f),
        });
        AssertClose(8f, CurveEvaluator.Evaluate(duplicate, 2f));
        AssertClose(30f, CurveEvaluator.Evaluate(duplicate, 3f));
    }

    [LumenFact]
    public static void DegenerateDomainAlwaysUsesFirstKey()
    {
        var curve = new ScalarCurve(new[] { new CurveKey(2f, 3f), new CurveKey(2f, 9f) }, 2f);
        AssertClose(6f, CurveEvaluator.Evaluate(curve, -10f));
        AssertClose(6f, CurveEvaluator.Evaluate(curve, 2f));
        AssertClose(6f, CurveEvaluator.Evaluate(curve, 10f));
        Span<float> lut = stackalloc float[3];
        CurveEvaluator.BakeScalarLut(curve, lut);
        foreach (float value in lut)
        {
            AssertClose(6f, value);
        }
    }

    [LumenFact]
    public static void LutSamplesMappedTimesWithoutTouchingAdjacentSlots()
    {
        var curve = new ScalarCurve(new[]
        {
            new CurveKey(-2f, 1f, OutTangent: 3f),
            new CurveKey(1f, 5f, InTangent: -2f),
            new CurveKey(5f, 9f),
        }, 2f);
        Span<float> storage = stackalloc float[9];
        storage.Fill(-777f);
        var lut = storage.Slice(1, 7);
        CurveEvaluator.BakeScalarLut(curve, lut);
        for (int i = 0; i < lut.Length; i++)
        {
            float expected = CurveEvaluator.Evaluate(curve, -2f + 7f * i / 6f);
            if (lut[i] != expected)
            {
                throw new InvalidOperationException($"LUT slot {i} does not match Evaluate at its mapped time.");
            }
        }
        AssertClose(-777f, storage[0]);
        AssertClose(-777f, storage[^1]);
        AssertClose(2f, lut[0]);
        AssertClose(18f, lut[^1]);

        CurveEvaluator.BakeScalarLut(curve, storage.Slice(1, 1));
        AssertClose(2f, storage[1]);
        CurveEvaluator.BakeScalarLut(new ScalarCurve(Array.Empty<CurveKey>(), float.NaN), lut);
        foreach (float value in lut)
        {
            AssertClose(0f, value);
        }
        CurveEvaluator.BakeScalarLut(ScalarCurve.Constant(7f), lut);
        foreach (float value in lut)
        {
            AssertClose(7f, value);
        }
    }

    [LumenFact]
    public static void EvaluateAndBakeAllocateNothing()
    {
        var curve = new ScalarCurve(new[] { new CurveKey(-1f, 0f), new CurveKey(3f, 8f) });
        var empty = new ScalarCurve(Array.Empty<CurveKey>());
        var constant = ScalarCurve.Constant(4f);
        Span<float> lut = stackalloc float[5];
        CurveEvaluator.Evaluate(curve, 1f);
        CurveEvaluator.BakeScalarLut(curve, lut);
        CurveEvaluator.BakeScalarLut(curve, lut[..1]);
        CurveEvaluator.BakeScalarLut(empty, lut);
        CurveEvaluator.BakeScalarLut(constant, lut);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            CurveEvaluator.Evaluate(curve, 1f);
            CurveEvaluator.BakeScalarLut(curve, lut);
            CurveEvaluator.BakeScalarLut(curve, lut[..1]);
            CurveEvaluator.BakeScalarLut(empty, lut);
            CurveEvaluator.BakeScalarLut(constant, lut);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (allocated != 0)
        {
            throw new InvalidOperationException($"Curve evaluation/LUT baking allocated {allocated} bytes.");
        }
    }

    private static void AssertClose(float expected, float actual)
    {
        if (!(MathF.Abs(expected - actual) <= 0.00001f))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }
}
