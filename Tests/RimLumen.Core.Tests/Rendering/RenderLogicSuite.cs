using System;
using RimLumen.Core.Contracts;
using RimLumen.Core.Rendering;
using RimLumen.Core.Rendering.Logic;

namespace RimLumen.Core.Tests.Rendering;

[LumenTestSuite]
public static class RenderLogicSuite
{
    [LumenFact]
    public static void CaptureFeatureIsInsertedBeforeMap()
    {
        Equal(2, FeatureOrderGuard.ComputePreBackbufferInsertIndex(["ComputeRenderFeature", "WorldRenderFeature", "MapRenderFeature", "PostFxFeature"]));
        Equal(-1, FeatureOrderGuard.ComputePreBackbufferInsertIndex(["PostFxFeature"]));
        Equal(0, FeatureOrderGuard.ComputePreBackbufferInsertIndex(["MapRenderFeature"]));
        Equal(-1, FeatureOrderGuard.ComputePreBackbufferInsertIndex([]));
    }

    [LumenFact]
    public static void SplitGrabIsFirstAtItsQueueAndCreatesMissingList()
    {
        var sentinel = (Queue: 3152, Name: "split");
        var before = (Queue: 3100, Name: "before");
        var distortion = (Queue: 3152, Name: "distortion");
        var after = (Queue: 3200, Name: "after");
        var grabs = SceneGrabSplit.Insert(new[] { before, distortion, after }, sentinel, static p => p.Queue);
        Equal(4, grabs.Count);
        Equal(before, grabs[0]);
        Equal(sentinel, grabs[1]);
        Equal(distortion, grabs[2]);
        Equal(after, grabs[3]);
        var created = SceneGrabSplit.Insert<(int Queue, string Name)>(null, sentinel, static p => p.Queue);
        Equal(1, created.Count);
        Equal(sentinel, created[0]);
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
        Equal(HdrFrameMode.Skip, HdrFrameGate.Decide(false, 0));
        Equal(HdrFrameMode.Render, HdrFrameGate.Decide(false, 1));
        Equal(HdrFrameMode.Render, HdrFrameGate.Decide(true, 0));
        Equal(HdrFrameMode.Render, HdrFrameGate.Decide(true, 1));
        Equal(HdrFrameMode.Render, HdrFrameGate.Decide(false, 7));
    }

    // Background-only capture keeps compute advancing but skips the whole HDR chain (no clear, depth copy,
    // bloom or composite): only a routed/appended draw or a direct-drawing contributor needs an HDR pass.
    [LumenFact]
    public static void BackgroundComputeOnlyFramesSkipHdrWork()
    {
        Equal(false, HdrFrameGate.ShouldRenderHdr(0, false));
        Equal(true, HdrFrameGate.ShouldRenderHdr(1, false));
        Equal(true, HdrFrameGate.ShouldRenderHdr(0, true));
        var simulator = new RimLumen.Core.Scene.CpuParticleSimulator();
        var service = new RimLumen.Core.Scene.LumenVfxService(new RimLumen.Core.Scene.CpuParticleSimulator());
        var directHdr = new DirectHdrContributor();
        Require(!RimLumenPipeline.HasDirectHdrContributor([simulator]), "L1 particles append mesh draws, not direct HDR draws.");
        Require(!RimLumenPipeline.HasDirectHdrContributor([service]), "VFX service draws go through its appenders.");
        using var gpu = new RimLumen.Core.ParticlesGpu.LumenGpuParticleEngine(8);
        Require(!RimLumenPipeline.HasDirectHdrContributor([gpu]),
            "L2 particles append mesh draws, not direct HDR draws.");
        Require(RimLumenPipeline.HasDirectHdrContributor([directHdr]),
            "A contributor that records its own HDR draws needs the pass.");
    }

    // Minimal third-party contributor: not a built-in appender or simulator, so it owns its HDR draws.
    private sealed class DirectHdrContributor : ILumenFrameContributor
    {
        public string Name => "test-direct-hdr";
        public bool HasActiveWork => true;
        public void CaptureMainThread(in LumenCaptureContext context) { }
        public void EnqueuePreCompute(in LumenComputeContext context) { }
        public void RecordBufferBarriers(in LumenBarrierContext context) { }
        public void RecordHdrDraws(in LumenHdrDrawContext context) { }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}; actual {actual}.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
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
