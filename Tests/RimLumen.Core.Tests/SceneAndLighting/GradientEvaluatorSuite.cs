using System;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class GradientEvaluatorSuite
{
    [LumenFact]
    public static void InterpolatesLinearRgbaWithoutClampingHdrValues()
    {
        var gradient = new ColorGradient(new[]
        {
            new ColorStop(2f, new Vector4(0f, 2f, -4f, 0.25f)),
            new ColorStop(6f, new Vector4(8f, -2f, 4f, 0.75f)),
            new ColorStop(8f, new Vector4(0f, 2f, 0f, 0.25f)),
        });
        AssertClose(new Vector4(2f, 1f, -2f, 0.375f), GradientEvaluator.Evaluate(gradient, 3f));
        AssertClose(new Vector4(4f, 0f, 0f, 0.5f), GradientEvaluator.Evaluate(gradient, 4f));
        AssertClose(new Vector4(8f, -2f, 4f, 0.75f), GradientEvaluator.Evaluate(gradient, 6f));
        AssertClose(new Vector4(4f, 0f, 2f, 0.5f), GradientEvaluator.Evaluate(gradient, 7f));
    }

    [LumenFact]
    public static void EmptyConstantAndClampBoundaries()
    {
        var empty = new ColorGradient(Array.Empty<ColorStop>());
        AssertClose(Vector4.Zero, GradientEvaluator.Evaluate(empty, -100f));
        AssertClose(Vector4.Zero, GradientEvaluator.Evaluate(empty, 100f));
        var color = new Vector4(2f, 3f, 4f, 0.5f);
        var single = new ColorGradient(new[] { new ColorStop(5f, color) });
        AssertClose(color, GradientEvaluator.Evaluate(single, -100f));
        AssertClose(color, GradientEvaluator.Evaluate(single, 5f));
        AssertClose(color, GradientEvaluator.Evaluate(single, 100f));
        AssertClose(color, GradientEvaluator.Evaluate(ColorGradient.Constant(color), 20f));

        var gradient = new ColorGradient(new[] { new ColorStop(2f, color), new ColorStop(6f, Vector4.One) });
        AssertClose(color, GradientEvaluator.Evaluate(gradient, -10f));
        AssertClose(color, GradientEvaluator.Evaluate(gradient, 2f));
        AssertClose(Vector4.One, GradientEvaluator.Evaluate(gradient, 6f));
        AssertClose(Vector4.One, GradientEvaluator.Evaluate(gradient, 10f));
    }

    [LumenFact]
    public static void DuplicateTimesUseFirstStopAndDegenerateDomainStaysConstant()
    {
        var gradient = new ColorGradient(new[]
        {
            new ColorStop(0f, Vector4.Zero), new ColorStop(2f, new Vector4(8f)),
            new ColorStop(2f, new Vector4(20f)), new ColorStop(4f, new Vector4(40f)),
        });
        AssertClose(new Vector4(8f), GradientEvaluator.Evaluate(gradient, 2f));
        AssertClose(new Vector4(30f), GradientEvaluator.Evaluate(gradient, 3f));

        var degenerate = new ColorGradient(new[]
        {
            new ColorStop(2f, Vector4.One), new ColorStop(2f, new Vector4(9f)),
        });
        AssertClose(Vector4.One, GradientEvaluator.Evaluate(degenerate, -10f));
        AssertClose(Vector4.One, GradientEvaluator.Evaluate(degenerate, 2f));
        AssertClose(Vector4.One, GradientEvaluator.Evaluate(degenerate, 10f));
        Span<Vector4> lut = stackalloc Vector4[3];
        GradientEvaluator.BakeGradientLut(degenerate, lut);
        foreach (var value in lut)
        {
            AssertClose(Vector4.One, value);
        }
    }

    [LumenFact]
    public static void LutSamplesMappedTimesWithoutTouchingAdjacentSlots()
    {
        var gradient = new ColorGradient(new[]
        {
            new ColorStop(-2f, new Vector4(1f, 0f, 0f, 0.25f)),
            new ColorStop(1f, new Vector4(0f, 2f, 0f, 0.5f)),
            new ColorStop(5f, new Vector4(0f, 0f, 4f, 1f)),
        });
        Span<Vector4> storage = stackalloc Vector4[9];
        storage.Fill(new Vector4(-777f));
        var lut = storage.Slice(1, 7);
        GradientEvaluator.BakeGradientLut(gradient, lut);
        for (int i = 0; i < lut.Length; i++)
        {
            var expected = GradientEvaluator.Evaluate(gradient, -2f + 7f * i / 6f);
            if (lut[i] != expected)
            {
                throw new InvalidOperationException($"LUT slot {i} does not match Evaluate at its mapped time.");
            }
        }
        AssertClose(new Vector4(-777f), storage[0]);
        AssertClose(new Vector4(-777f), storage[^1]);
        AssertClose(new Vector4(1f, 0f, 0f, 0.25f), lut[0]);
        AssertClose(new Vector4(0f, 0f, 4f, 1f), lut[^1]);

        GradientEvaluator.BakeGradientLut(gradient, storage.Slice(1, 1));
        AssertClose(new Vector4(1f, 0f, 0f, 0.25f), storage[1]);
        GradientEvaluator.BakeGradientLut(new ColorGradient(Array.Empty<ColorStop>()), lut);
        foreach (var value in lut)
        {
            AssertClose(Vector4.Zero, value);
        }
        GradientEvaluator.BakeGradientLut(ColorGradient.Constant(Vector4.One), lut);
        foreach (var value in lut)
        {
            AssertClose(Vector4.One, value);
        }
    }

    [LumenFact]
    public static void EvaluateAndBakeAllocateNothing()
    {
        var gradient = new ColorGradient(new[] { new ColorStop(-1f, Vector4.Zero), new ColorStop(3f, Vector4.One) });
        var empty = new ColorGradient(Array.Empty<ColorStop>());
        var constant = ColorGradient.Constant(Vector4.One);
        Span<Vector4> lut = stackalloc Vector4[5];
        GradientEvaluator.Evaluate(gradient, 1f);
        GradientEvaluator.BakeGradientLut(gradient, lut);
        GradientEvaluator.BakeGradientLut(gradient, lut[..1]);
        GradientEvaluator.BakeGradientLut(empty, lut);
        GradientEvaluator.BakeGradientLut(constant, lut);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            GradientEvaluator.Evaluate(gradient, 1f);
            GradientEvaluator.BakeGradientLut(gradient, lut);
            GradientEvaluator.BakeGradientLut(gradient, lut[..1]);
            GradientEvaluator.BakeGradientLut(empty, lut);
            GradientEvaluator.BakeGradientLut(constant, lut);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (allocated != 0)
        {
            throw new InvalidOperationException($"Gradient evaluation/LUT baking allocated {allocated} bytes.");
        }
    }

    private static void AssertClose(Vector4 expected, Vector4 actual)
    {
        if (!(MathF.Abs(expected.X - actual.X) <= 0.00001f &&
              MathF.Abs(expected.Y - actual.Y) <= 0.00001f &&
              MathF.Abs(expected.Z - actual.Z) <= 0.00001f &&
              MathF.Abs(expected.W - actual.W) <= 0.00001f))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }
}
