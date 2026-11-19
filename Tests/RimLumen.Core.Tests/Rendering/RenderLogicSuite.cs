using System;
using System.Numerics;
using RimLumen.Core.Compat;
using RimLumen.Core.Contracts;
using RimLumen.Core.Lighting;
using RimLumen.Core.Lighting.Occlusion;
using RimLumen.Core.Rendering;
using RimLumen.Core.Rendering.Logic;
using RimLumen.Core.Scene;
using Waylith.Rendering;

namespace RimLumen.Core.Tests.Rendering;

[LumenTestSuite]
public static class RenderLogicSuite
{
    [LumenFact]
    public static void FrameSettingsAreLatchedAndBlendSpaceRejectsUndefinedValues()
    {
        LumenColorBlendSpace original = RimLumenRenderHost.ColorBlendSpace;
        LumenColorBlendSpace originalFrame = RimLumenRenderHost.CurrentFrameColorBlendSpace;
        bool originalRefract = RimLumenRenderHost.DistortionRefractsEffects;
        bool originalFrameRefract = RimLumenRenderHost.CurrentFrameDistortionRefractsEffects;
        try
        {
            RimLumenRenderHost.ColorBlendSpace = LumenColorBlendSpace.Legacy;
            RimLumenRenderHost.DistortionRefractsEffects = true;
            RimLumenRenderHost.LatchFrameSettings();
            RimLumenRenderHost.ColorBlendSpace = LumenColorBlendSpace.Linear;
            RimLumenRenderHost.DistortionRefractsEffects = false;
            Equal(LumenColorBlendSpace.Legacy, RimLumenRenderHost.CurrentFrameColorBlendSpace);
            Equal(true, RimLumenRenderHost.CurrentFrameDistortionRefractsEffects);
            ThrowsOutOfRange(() => RimLumenRenderHost.ColorBlendSpace = (LumenColorBlendSpace)255);
            Equal(LumenColorBlendSpace.Linear, RimLumenRenderHost.ColorBlendSpace);
            Equal(LumenColorBlendSpace.Legacy, RimLumenRenderHost.CurrentFrameColorBlendSpace);
            RimLumenRenderHost.LatchFrameSettings();
            Equal(LumenColorBlendSpace.Linear, RimLumenRenderHost.CurrentFrameColorBlendSpace);
            Equal(false, RimLumenRenderHost.CurrentFrameDistortionRefractsEffects);
        }
        finally
        {
            RimLumenRenderHost.ColorBlendSpace = originalFrame;
            RimLumenRenderHost.DistortionRefractsEffects = originalFrameRefract;
            RimLumenRenderHost.LatchFrameSettings();
            RimLumenRenderHost.ColorBlendSpace = original;
            RimLumenRenderHost.DistortionRefractsEffects = originalRefract;
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
        var captureOnly = new TestFrameContributor(requiresDirectHdrPass: false);
        Require(!HdrFrameGate.ShouldRenderHdr(0, RimLumenPipeline.HasDirectHdrContributor([captureOnly]), false, 0),
            "Active lifecycle work without draws (such as an External ribbon) does not request an HDR pass.");
        Require(!RimLumenPipeline.HasDirectHdrContributor([simulator]), "L1 particles append mesh draws, not direct HDR draws.");
        Require(!RimLumenPipeline.HasDirectHdrContributor([service]), "VFX service draws go through its appenders.");
        using var gpu = new RimLumen.Core.ParticlesGpu.LumenGpuParticleEngine(8);
        Require(!RimLumenPipeline.HasDirectHdrContributor([gpu]),
            "L2 particles append mesh draws, not direct HDR draws.");
        Require(RimLumenPipeline.HasDirectHdrContributor([new TestFrameContributor(requiresDirectHdrPass: true)]),
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

        var pool = new VisualPointLight(10.25f, 0f, -4.5f, 4f, new Vector3(2f, 0.5f, 0f), VisualPointLightKind.Effect);
        Equal(new Vector3(2f, 0.5f, 0f), pool.EvaluateAt(10.25f, -4.5f, Walls()));
        Equal(Vector3.Zero, pool.EvaluateAt(14.25f, -4.5f, Walls()));
    }

    [LumenFact]
    public static void OcclusionWallColumnHasNoBanding()
    {
        OcclusionFrame walls = Walls((5, 2), (5, 3), (5, 4), (5, 5), (5, 6), (5, 7));
        float near = Transmission(walls, new(3.5f, 3.5f), new(5.5f, 3.5f));
        float far = Transmission(walls, new(3.5f, 3.5f), new(5.5f, 6.5f));
        Equal(near, far);
    }

    [LumenFact]
    public static void OcclusionClosedDoorBlocksLight()
    {
        var builder = new OcclusionBuilder(10, 10);
        builder.StampSolidFootprint(new CellBox(5, 3, 5, 3), 1f);
        builder.StampSolidFootprint(new CellBox(5, 5, 5, 5), 1f);
        builder.StampBlocker(new AlphaMask(1, 1, [255]), new MaskPlacement(5.5f, 4.5f, 1f, 1f), 1f, DoorAxis.Z);
        builder.RecomputeDerived(new CellBox(0, 0, 9, 9));
        var state = new byte[10 * 10 * 4];
        state[(4 * 10 + 5) * 4 + 2] = 1;
        var frame = new OcclusionFrame(builder.Publish(), new DoorStates(10, 10, state));
        Equal(0f, Transmission(frame, new(3.5f, 4.5f), new(7.5f, 4.5f)));
    }

    [LumenFact]
    public static void LightPoolIsBlockedByAWallCellAndUnchangedAlongAClearLine()
    {
        OcclusionFrame walls = Walls((5, 4));
        var lamp = new VisualPointLight(3.5f, 0f, 4.5f, 6f, new Vector3(1f, 0.5f, 0.25f), VisualPointLightKind.Effect);
        Equal(Vector3.Zero, VisualPointLight.AccumulateAndCompress([lamp], 7.5f, 4.5f, walls));
        Equal(lamp.Light * VisualPointLight.Falloff(4f, 6f), VisualPointLight.AccumulateAndCompress([lamp], 3.5f, 8.5f, walls));
    }

    [LumenFact]
    public static void LightPoolLightsTheWallCellItReachesAndIsNotBlockedByItsOwnCell()
    {
        OcclusionFrame walls = Walls((5, 4));
        Equal(1f, Transmission(walls, new(3.5f, 4.5f), new(5.2f, 4.5f)));
        Equal(1f, Transmission(walls, new(3.5f, 4.5f), new(5.9f, 4.5f)));
        Equal(1f, Transmission(walls, new(5.5f, 4.5f), new(7.5f, 4.5f)));
        Equal(1f, Transmission(walls, new(5.5f, 4.5f), new(3.5f, 4.5f)));
    }

    private static float Transmission(OcclusionFrame frame, Vector2 light, Vector2 target) => LightTransport.Visibility(frame, light, target, out float cover) * cover;

    private static OcclusionFrame Walls(params (int X, int Z)[] cells)
    {
        const int size = 10;
        var builder = new OcclusionBuilder(size, size);
        foreach ((int x, int z) in cells) builder.StampSolidFootprint(new CellBox(x, z, x, z), 1f);
        builder.RecomputeDerived(new CellBox(0, 0, size - 1, size - 1));
        return new OcclusionFrame(builder.Publish(), new DoorStates(size, size, new byte[size * size * 4]));
    }

    [LumenFact]
    public static void LightPoolsSumLinearlyAndCompressOnceWithinTwiceTheScene()
    {
        var single = new VisualPointLight(0f, 0f, 0f, 4f, new Vector3(0.6f, 0.4f, 0.2f), VisualPointLightKind.Effect);
        Equal(new Vector3(0.6f, 0.4f, 0.2f), VisualPointLight.AccumulateAndCompress([single], 0f, 0f, Walls()));

        // Two overlapping pools: 0.3 + 0.2 = 0.5, not the stacked (1.3 * 1.2) - 1 = 0.56.
        VisualPointLight[] pair =
        [
            new(0f, 0f, 0f, 4f, new Vector3(0.3f, 0.2f, 0.1f), VisualPointLightKind.Effect),
            new(0f, 0f, 0f, 6f, new Vector3(0.2f, 0.1f, 0.05f), VisualPointLightKind.Effect),
        ];
        Vector3 summed = VisualPointLight.AccumulateAndCompress(pair, 0f, 0f, Walls());
        Require(Vector3.Distance(summed, new Vector3(0.5f, 0.3f, 0.15f)) < 1e-6f, $"Pools must add linearly; got {summed}.");

        // Five warm corridor lamps at GlowGrid's 3.6 scale: stacked multipliers would reach 4.6^5 ~ 2060x on red.
        var warm = new Vector3(1f, 0.8f, 0.5f) * 3.6f;
        var corridor = new VisualPointLight[5];
        for (int i = 0; i < corridor.Length; i++) corridor[i] = new VisualPointLight(0.4f * i, 0f, 0f, 6f, warm, VisualPointLightKind.Effect);
        Vector3 mapped = VisualPointLight.AccumulateAndCompress(corridor, 0.8f, 0f, Walls());
        Require(mapped.X <= 1f && mapped.Y <= 1f && mapped.Z <= 1f && mapped.Z >= 0f,
            $"Compressed pool term must stay in [0, 1] (scene at most 2x); got {mapped}.");
        Require(mapped.X > mapped.Y && mapped.Y > mapped.Z,
            $"Compression must keep the warm hue order instead of clipping every channel to white; got {mapped}.");
    }

    [LumenFact]
    public static void SunShadowInsideALightPoolRemovesSunlightOnly()
    {
        var shadow = new Vector4(0.5f, 0.5f, 0.5f, 0.7f);
        Equal(shadow, VisualPointLight.ShadowOverPool(shadow, Vector3.Zero));

        // Scene 0.4 under a pool L = (1, 0, 0.5): the composite makes it 0.4 * (1 + L) = (0.8, 0.4, 0.6); vanilla's
        // shadow then multiplies by c = 0.5 to (0.4, 0.2, 0.3), the lifted shadow by (c + L) / (1 + L) to 0.4 * (c + L).
        Vector3 pool = VisualPointLight.CompressHighlights(new Vector3(1f, 0f, 0.5f));
        Vector3 lit = new Vector3(0.4f) * (Vector3.One + pool);
        Vector4 factor = VisualPointLight.ShadowOverPool(shadow, pool);
        Vector3 shaded = lit * new Vector3(factor.X, factor.Y, factor.Z);
        Require(Vector3.Distance(shaded, new Vector3(0.6f, 0.2f, 0.4f)) < 1e-6f,
            $"A sun shadow over a pool must keep the pool's light: expected (0.6, 0.2, 0.4), vanilla (0.4, 0.2, 0.3); got {shaded}.");
        Equal(0.7f, factor.W);
    }

    [LumenFact]
    public static void LightPoolAccumulationAddsLinearlyAtLightOverlay()
    {
        ShaderProgramDefinition definition = LightPoolRenderer.CreateDefinition();
        Equal(3101, definition.renderQueue.Value);
        Equal(GlowGroup.Early, GlowGroups.Classify(definition.renderQueue.Value));
        PipelineState state = definition.ToPipelineState();
        Require(state.Blend.Enabled
            && state.Blend.SrcColor == BlendFactor.One
            && state.Blend.DstColor == BlendFactor.One
            && state.Blend.ColorOp == BlendOp.Add
            && state.Depth.Test && !state.Depth.Write,
            "Light-pool accumulation must blend One + One with depth test and no depth write.");
    }

    private sealed class TestFrameContributor(bool requiresDirectHdrPass) : ILumenFrameContributor
    {
        public string Name => "test-contributor";
        public bool HasActiveWork => true;
        public bool RequiresDirectHdrPass => requiresDirectHdrPass;
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
