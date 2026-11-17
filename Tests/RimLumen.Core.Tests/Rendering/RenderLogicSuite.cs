using System;
using System.Numerics;
using RimLumen.Core.Compat;
using RimLumen.Core.Contracts;
using RimLumen.Core.Lighting;
using RimLumen.Core.Rendering;
using RimLumen.Core.Rendering.Logic;
using Waylith.Rendering;

namespace RimLumen.Core.Tests.Rendering;

[LumenTestSuite]
public static class RenderLogicSuite
{
    [LumenFact]
    public static void BlendSpaceRequestIsLatchedAndRejectsUndefinedValues()
    {
        LumenColorBlendSpace original = RimLumenRenderHost.ColorBlendSpace;
        LumenColorBlendSpace originalFrame = RimLumenRenderHost.CurrentFrameColorBlendSpace;
        try
        {
            RimLumenRenderHost.ColorBlendSpace = LumenColorBlendSpace.Legacy;
            RimLumenRenderHost.LatchColorBlendSpace();
            RimLumenRenderHost.ColorBlendSpace = LumenColorBlendSpace.Linear;
            Equal(LumenColorBlendSpace.Legacy, RimLumenRenderHost.CurrentFrameColorBlendSpace);
            ThrowsOutOfRange(() => RimLumenRenderHost.ColorBlendSpace = (LumenColorBlendSpace)255);
            Equal(LumenColorBlendSpace.Linear, RimLumenRenderHost.ColorBlendSpace);
            Equal(LumenColorBlendSpace.Legacy, RimLumenRenderHost.CurrentFrameColorBlendSpace);
            RimLumenRenderHost.LatchColorBlendSpace();
            Equal(LumenColorBlendSpace.Linear, RimLumenRenderHost.CurrentFrameColorBlendSpace);
        }
        finally
        {
            RimLumenRenderHost.ColorBlendSpace = originalFrame;
            RimLumenRenderHost.LatchColorBlendSpace();
            RimLumenRenderHost.ColorBlendSpace = original;
        }
    }

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
    public static void GravshipSplitPrecedesEverySameQueueGrab()
    {
        var split = (Queue: 3178, Name: "split");
        var gravship = (Queue: 3178, Name: "gravship");
        var other = (Queue: 3178, Name: "other");
        var grabs = SceneGrabSplit.Insert(new[] { gravship, other }, split, static p => p.Queue);
        Equal(split, grabs[0]);
        Equal(gravship, grabs[1]);
        Equal(other, grabs[2]);
    }

    [LumenFact]
    public static void GlowGroupsPartitionEffectiveQueuesWithoutGaps()
    {
        Equal(GlowGroup.Early, GlowGroups.Classify(int.MinValue));
        Equal(GlowGroup.Early, GlowGroups.Classify(3151));
        Equal(GlowGroup.BeforeWeather, GlowGroups.Classify(3152));
        Equal(GlowGroup.BeforeWeather, GlowGroups.Classify(3168));
        Equal(GlowGroup.BeforeGravship, GlowGroups.Classify(3169));
        Equal(GlowGroup.BeforeGravship, GlowGroups.Classify(3177));
        Equal(GlowGroup.MapEnd, GlowGroups.Classify(3178));
        Equal(GlowGroup.MapEnd, GlowGroups.Classify(int.MaxValue - 1));
        GlowQueueRange early = GlowGroups.Range(GlowGroup.Early);
        GlowQueueRange weather = GlowGroups.Range(GlowGroup.BeforeWeather);
        GlowQueueRange gravship = GlowGroups.Range(GlowGroup.BeforeGravship);
        GlowQueueRange end = GlowGroups.Range(GlowGroup.MapEnd);
        Equal(int.MinValue, early.MinInclusive);
        Equal(early.MaxExclusive, weather.MinInclusive);
        Equal(weather.MaxExclusive, gravship.MinInclusive);
        Equal(gravship.MaxExclusive, end.MinInclusive);
        Equal(int.MaxValue, end.MaxExclusive);
        Equal(true, 3152 >= weather.MinInclusive && 3152 < weather.MaxExclusive);
        Equal(false, 3169 >= weather.MinInclusive && 3169 < weather.MaxExclusive);
        Equal(true, 3178 >= end.MinInclusive && 3178 < end.MaxExclusive);
        Equal(false, int.MaxValue >= end.MinInclusive && int.MaxValue < end.MaxExclusive);
        // Verified candle, orbital beam and later distortion/portal/ritual/psychic queues.
        Equal(GlowGroup.BeforeWeather, GlowGroups.Classify(3155));
        Equal(GlowGroup.MapEnd, GlowGroups.Classify(3181));
        Equal(GlowGroup.MapEnd, GlowGroups.Classify(3185));
        Equal(GlowGroup.MapEnd, GlowGroups.Classify(3201));
        Equal(GlowGroup.MapEnd, GlowGroups.Classify(4000));
    }

    [LumenFact]
    public static void GlowGroupsScheduleOnlyOwnedWorkAndPositiveMipLateCopies()
    {
        GlowGroupMask captured = GlowGroups.Mask(GlowGroup.BeforeWeather) | GlowGroups.Mask(GlowGroup.MapEnd);
        Equal(false, GlowGroups.ShouldSplit(GlowGroup.Early, captured, earlyWork: false, 5));
        // Early work includes native draws, compute-only barriers or RunOnEmptyFrames independently of glow bits.
        Equal(true, GlowGroups.ShouldSplit(GlowGroup.Early, GlowGroupMask.None, earlyWork: true, 0));
        Equal(false, GlowGroups.ShouldSplit(GlowGroup.Early, GlowGroupMask.Early, earlyWork: false, 5));
        Equal(true, GlowGroups.ShouldSplit(GlowGroup.BeforeWeather, captured, earlyWork: false, 5));
        Equal(false, GlowGroups.ShouldSplit(GlowGroup.BeforeGravship, captured, earlyWork: true, 5));
        Equal(true, GlowGroups.ShouldSplit(GlowGroup.MapEnd, captured, earlyWork: false, 1));
        Equal(false, GlowGroups.ShouldSplit(GlowGroup.MapEnd, captured, earlyWork: true, 0));
        Equal(false, GlowGroups.ShouldSplit(GlowGroup.BeforeWeather, GlowGroupMask.None, earlyWork: false, 5));
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

    [LumenFact]
    public static void LightPoolFalloffIsUnitAtCentreZeroAtAndBeyondRadiusAndStrictlyMonotonic()
    {
        const float radius = 6f;
        Equal(1f, VisualPointLight.Falloff(0f, radius));
        Equal(0f, VisualPointLight.Falloff(radius, radius));
        Equal(0f, VisualPointLight.Falloff(radius + 0.001f, radius));
        Equal(0f, VisualPointLight.Falloff(100f, radius));
        Equal(0f, VisualPointLight.Falloff(0f, 0f));
        Equal(0f, VisualPointLight.Falloff(0f, -2f));
        ThrowsOutOfRange(() => VisualPointLight.Falloff(-0.1f, radius));

        // Half-radius closed form: (1 - 0.5^2)^2 = 9/16 = 0.5625; sub-cell offsets must not quantize to cells.
        Require(MathF.Abs(VisualPointLight.Falloff(3f, radius) - 0.5625f) < 1e-6f, "Expected exact (1 - (d/R)^2)^2 at d = R/2.");
        float previous = VisualPointLight.Falloff(0f, radius);
        foreach (float sample in new[] { 0.05f, 0.37f, 1.0f, 1.49f, 2.5f, 4.13f, 5.95f, radius })
        {
            float current = VisualPointLight.Falloff(sample, radius);
            Require(current < previous, $"Falloff must strictly decrease: f({sample})={current} >= previous {previous}.");
            previous = current;
        }

        var pool = new VisualPointLight(10.25f, -4.5f, 4f, new Vector3(2f, 0.5f, 0f));
        Equal(new Vector3(2f, 0.5f, 0f), pool.EvaluateAt(10.25f, -4.5f));
        Equal(Vector3.Zero, pool.EvaluateAt(14.25f, -4.5f));
    }

    [LumenFact]
    public static void LightPoolDefinitionFollowsLightOverlayWithSceneMultiplierBlendAndSkipsHdrRedraw()
    {
        ShaderProgramDefinition definition = LightPoolRenderer.CreateDefinition();
        Equal(3101, definition.renderQueue.Value);
        Equal(GlowGroup.Early, GlowGroups.Classify(definition.renderQueue.Value));
        PipelineState state = definition.ToPipelineState();
        Require(state.Blend.Enabled
            && state.Blend.SrcColor == BlendFactor.DstColor
            && state.Blend.DstColor == BlendFactor.One
            && state.Blend.ColorOp == BlendOp.Add
            && state.Depth.Test && !state.Depth.Write,
            "Light pool must blend DstColor + One with depth test and no depth write.");
        Equal(false, AdditiveEmissiveFilter.IsAdditiveEmissive(in state, "RimLumen.LightPool"));
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
