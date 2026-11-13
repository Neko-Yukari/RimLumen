using System;
using RimLumen.Core.Rendering.Logic;

namespace RimLumen.Core.Tests.Rendering;

[LumenTestSuite]
public static class RenderLogicSuite
{
    [LumenFact]
    public static void CompositeOrderHandlesInsertedMissingAndDisplacedSelf()
    {
        string[] ordered = ["ComputeRenderFeature", "WorldRenderFeature", "MapRenderFeature", "CameraCaptureFeature", "PostFxFeature", "Composite", "ImGuiRenderFeature", "ScreenCaptureFeature"];
        Equal(-1, FeatureOrderGuard.ComputeCompositeReinsertIndex(ordered, 5));
        string[] iris = ["ComputeRenderFeature", "WorldRenderFeature", "MapRenderFeature", "CameraCaptureFeature", "PostFxFeature", "Composite", "RimIris", "ImGuiRenderFeature", "ScreenCaptureFeature"];
        Equal(6, FeatureOrderGuard.ComputeCompositeReinsertIndex(iris, 5));
        Equal(1, FeatureOrderGuard.ComputeCompositeReinsertIndex(["PostFxFeature", "ImGuiRenderFeature"], -1));
        Equal(0, FeatureOrderGuard.ComputeCompositeReinsertIndex(["ImGuiRenderFeature"], -1));
        Equal(-1, FeatureOrderGuard.ComputeCompositeReinsertIndex(["Composite", "PostFxFeature"], 0));
        Equal(1, FeatureOrderGuard.ComputeCompositeReinsertIndex(["PostFxFeature", "ImGuiRenderFeature", "Composite"], 2));
        Equal(0, FeatureOrderGuard.ComputeCompositeReinsertIndex(["customIMGUIfeature", "OtherImGui", "Composite"], 2));
        Equal(-1, FeatureOrderGuard.ComputeCompositeReinsertIndex([], -1));
        Equal("Composite", iris[5]);
    }

    [LumenFact]
    public static void PreBackbufferRequiresMapBeforePostFx()
    {
        Equal(3, FeatureOrderGuard.ComputePreBackbufferInsertIndex(["ComputeRenderFeature", "WorldRenderFeature", "MapRenderFeature", "CameraCaptureFeature", "PostFxFeature", "ImGuiRenderFeature", "ScreenCaptureFeature"]));
        Equal(-1, FeatureOrderGuard.ComputePreBackbufferInsertIndex(["PostFxFeature"]));
        Equal(-1, FeatureOrderGuard.ComputePreBackbufferInsertIndex(["MapRenderFeature"]));
        Equal(-1, FeatureOrderGuard.ComputePreBackbufferInsertIndex(["PostFxFeature", "MapRenderFeature"]));
        Equal(-1, FeatureOrderGuard.ComputePreBackbufferInsertIndex([]));
    }

    [LumenFact]
    public static void MipChainCeilsDimensionsAndHonorsLimits()
    {
        EqualMips([new(960, 540), new(480, 270), new(240, 135), new(120, 68), new(60, 34), new(30, 17), new(15, 9), new(8, 5), new(4, 3), new(2, 2)], BloomMipChain.Compute(1920, 1080, 20, 2));
        EqualMips([new(4, 3), new(2, 2)], BloomMipChain.Compute(7, 5, 8, 2));
        EqualMips([new(1, 1)], BloomMipChain.Compute(1, 1, 8, 2));
        EqualMips([new(1, 1), new(1, 1), new(1, 1)], BloomMipChain.Compute(1, 1, 3, 1));
        EqualMips([new(960, 540), new(480, 270)], BloomMipChain.Compute(1920, 1080, 2, 1));
        EqualMips([new(2, 1)], BloomMipChain.Compute(3, 1, 8, 8));
    }

    [LumenFact]
    public static void MipChainRejectsNonPositiveArguments()
    {
        ThrowsOutOfRange(() => BloomMipChain.Compute(0, 1, 1, 1));
        ThrowsOutOfRange(() => BloomMipChain.Compute(1, 0, 1, 1));
        ThrowsOutOfRange(() => BloomMipChain.Compute(1, 1, 0, 1));
        ThrowsOutOfRange(() => BloomMipChain.Compute(1, 1, 1, 0));
        ThrowsOutOfRange(() => BloomMipChain.Compute(-1, 1, 1, 1));
        ThrowsOutOfRange(() => BloomMipChain.Compute(1, -1, 1, 1));
        ThrowsOutOfRange(() => BloomMipChain.Compute(1, 1, -1, 1));
        ThrowsOutOfRange(() => BloomMipChain.Compute(1, 1, 1, -1));
    }

    [LumenFact]
    public static void FrameGateTruthTable()
    {
        Equal(HdrFrameMode.Skip, HdrFrameGate.Decide(false, 0, false));
        Equal(HdrFrameMode.Render, HdrFrameGate.Decide(false, 0, true));
        Equal(HdrFrameMode.Render, HdrFrameGate.Decide(false, 1, false));
        Equal(HdrFrameMode.Render, HdrFrameGate.Decide(false, 1, true));
        Equal(HdrFrameMode.Render, HdrFrameGate.Decide(true, 0, false));
        Equal(HdrFrameMode.Render, HdrFrameGate.Decide(true, 0, true));
        Equal(HdrFrameMode.Render, HdrFrameGate.Decide(true, 1, false));
        Equal(HdrFrameMode.Render, HdrFrameGate.Decide(true, 1, true));
        Equal(HdrFrameMode.Render, HdrFrameGate.Decide(false, 7, false));
    }

    [LumenFact]
    public static void PixelDiffCountsRgbChangesAndIgnoresAlpha()
    {
        byte[] baseline = [10, 20, 30, 0, 255, 0, 100, 255];
        Equal(new PixelDiffResult(0, 0, 2), PixelMetrics.Compare(baseline, baseline, 2, 1));
        Equal(new PixelDiffResult(5, 1, 2), PixelMetrics.Compare(baseline, [10, 25, 30, 0, 255, 0, 100, 255], 2, 1));
        Equal(new PixelDiffResult(0, 0, 2), PixelMetrics.Compare(baseline, [10, 20, 30, 255, 255, 0, 100, 0], 2, 1));
        Equal(new PixelDiffResult(255, 2, 2), PixelMetrics.Compare(baseline, [0, 0, 0, 0, 0, 255, 0, 255], 2, 1));
    }

    [LumenFact]
    public static void HaloRingExcludesQuadAndClipsImageEdges()
    {
        byte[] black = new byte[5 * 5 * 4];
        byte[] white = new byte[black.Length];
        Array.Fill(white, (byte)255);
        Equal(8, PixelMetrics.CountHaloPixels(black, white, 5, 5, new(2, 2, 1, 1), 1, 0));
        Equal(24, PixelMetrics.CountHaloPixels(black, white, 5, 5, new(2, 2, 1, 1), 2, 0));
        Equal(3, PixelMetrics.CountHaloPixels(black, white, 5, 5, new(0, 0, 1, 1), 1, 0));
        Equal(3, PixelMetrics.CountHaloPixels(black, white, 5, 5, new(4, 4, 1, 1), 1, 0));
        Equal(5, PixelMetrics.CountHaloPixels(black, white, 5, 5, new(0, 2, 1, 1), 1, 0));
        Equal(16, PixelMetrics.CountHaloPixels(black, white, 5, 5, new(1, 1, 3, 3), 1, 0));
        Equal(0, PixelMetrics.CountHaloPixels(black, white, 5, 5, new(0, 0, 5, 5), 2, 0));
    }

    [LumenFact]
    public static void HaloUsesRec709SignedLumaAndStrictThreshold()
    {
        byte[] black = new byte[3 * 4];
        Equal(1, PixelMetrics.CountHaloPixels(black, [255, 0, 0, 0, 255, 255, 255, 0, 0, 0, 255, 0], 3, 1, new(1, 0, 1, 1), 1, 50));
        Equal(1, PixelMetrics.CountHaloPixels(black, [0, 255, 0, 0, 0, 0, 0, 0, 255, 0, 0, 0], 3, 1, new(1, 0, 1, 1), 1, 100));
        Equal(0, PixelMetrics.CountHaloPixels(black, [10, 10, 10, 255, 0, 0, 0, 0, 10, 10, 10, 255], 3, 1, new(1, 0, 1, 1), 1, 10));
        Equal(0, PixelMetrics.CountHaloPixels([255, 255, 255, 0, 0, 0, 0, 0, 255, 255, 255, 0], black, 3, 1, new(1, 0, 1, 1), 1, 0));
        Equal(0, PixelMetrics.CountHaloPixels(black, [0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255], 3, 1, new(1, 0, 1, 1), 1, 0));
    }

    [LumenFact]
    public static void ProbeParsesQuotedUnquotedAndCaseInsensitiveKeys()
    {
        Equal(true, ProbeOptionsParser.TryParse(["RimWorld.exe", "-rimlumen-probe=C:/captures"], out var plain));
        Equal(new ProbeOptions("C:/captures", false), plain!);
        Equal(true, ProbeOptionsParser.TryParse(["-RIMLUMEN-PROBE-NOBARRIER", "-RiMLuMeN-PrObE=\"C:/capture files\""], out var quoted));
        Equal(new ProbeOptions("C:/capture files", true), quoted!);
        Equal(true, ProbeOptionsParser.TryParse(["-rimlumen-probe=C:/captures", "-rimlumen-probe-nobarrier"], out var flagAfter));
        Equal(new ProbeOptions("C:/captures", true), flagAfter!);
    }

    [LumenFact]
    public static void ProbeRejectsMissingOrEmptyDirectoryEvenWithNoBarrier()
    {
        Equal(false, ProbeOptionsParser.TryParse(["-rimlumen-probe-nobarrier"], out var absent));
        Equal<ProbeOptions?>(null, absent);
        Equal(false, ProbeOptionsParser.TryParse(["-rimlumen-probe="], out var empty));
        Equal<ProbeOptions?>(null, empty);
        Equal(false, ProbeOptionsParser.TryParse(["-rimlumen-probe=\"\"", "-rimlumen-probe-nobarrier"], out var emptyQuoted));
        Equal<ProbeOptions?>(null, emptyQuoted);
        Equal(false, ProbeOptionsParser.TryParse(["-rimlumen-probe-other=C:/captures"], out var other));
        Equal<ProbeOptions?>(null, other);
        Equal(true, ProbeOptionsParser.TryParse(["-rimlumen-probe=", "-rimlumen-probe=C:/valid"], out var laterValid));
        Equal(new ProbeOptions("C:/valid", false), laterValid!);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}; actual {actual}.");
        }
    }

    private static void EqualMips(MipSize[] expected, MipSize[] actual)
    {
        Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Equal(expected[i], actual[i]);
        }
    }

    private static void ThrowsOutOfRange(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }
        throw new InvalidOperationException("Expected ArgumentOutOfRangeException.");
    }
}
