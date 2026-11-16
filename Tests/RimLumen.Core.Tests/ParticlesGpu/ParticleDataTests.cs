using System;
using System.Numerics;
using System.IO;
using RimLumen.Core.Assets;
using System.Runtime.InteropServices;
using RimLumen.Core.Contracts;
using RimLumen.Core.ParticlesGpu;
using RimLumen.Core.Scene;

namespace RimLumen.Core.Tests.ParticlesGpu;

[LumenTestSuite]
public static class ParticleDataTests
{
    [LumenFact]
    public static void CurveRowPreservesConstantsHdrAndDestinationTail()
    {
        var samples = new GpuCurveSample[65];
        samples[64].Size = new Vector4(99f);
        GpuCurveAtlasPacker.Pack(ScalarCurve.Constant(2.5f),
            ColorGradient.Constant(new Vector4(8f, 3f, 1.5f, 0.25f)), samples);
        Equal(64, GpuCurveAtlasPacker.SamplesPerRow);
        for (int i = 0; i < 64; i++)
        {
            Equal(new Vector4(2.5f, 0f, 0f, 0f), samples[i].Size);
            Equal(new Vector4(8f, 3f, 1.5f, 0.25f), samples[i].Color);
        }
        Equal(new Vector4(99f), samples[64].Size);
    }

    [LumenFact]
    public static void CurveRowUsesHermiteTangentsAndSegmentWidthWithClampedEndpoints()
    {
        // These endpoint values/slopes describe y=t^2; with multiplier 9, t=1/3 gives 1 and t=2/3 gives 4.
        var curve = new ScalarCurve(new[]
        {
            new CurveKey(0.25f, 0.0625f, 0f, 0.5f),
            new CurveKey(0.75f, 0.5625f, 1.5f, 0f),
        }, 9f);
        var samples = new GpuCurveSample[64];
        GpuCurveAtlasPacker.Pack(curve, ColorGradient.Constant(Vector4.One), samples);
        Near(0.5625f, samples[0].Size.X);
        Near(1f, samples[21].Size.X);
        Near(4f, samples[42].Size.X);
        Near(5.0625f, samples[63].Size.X);
    }

    [LumenFact]
    public static void GradientInterpolatesLinearHdrRgbaAndClampsEndpoints()
    {
        var gradient = new ColorGradient(new[]
        {
            new ColorStop(0.25f, new Vector4(6f, 0f, 0f, 0.25f)),
            new ColorStop(0.75f, new Vector4(0f, 12f, 3f, 1f)),
        });
        var samples = new GpuCurveSample[64];
        GpuCurveAtlasPacker.Pack(ScalarCurve.Constant(1f), gradient, samples);
        Equal(new Vector4(6f, 0f, 0f, 0.25f), samples[0].Color);
        Equal(new Vector4(5f, 2f, 0.5f, 0.375f), samples[21].Color);
        Equal(new Vector4(1f, 10f, 2.5f, 0.875f), samples[42].Color);
        Equal(new Vector4(0f, 12f, 3f, 1f), samples[63].Color);
    }

    [LumenFact]
    public static void AssetBoundaryRejectsMalformedCurvesAndPackerRejectsShortDestination()
    {
        var color = ColorGradient.Constant(Vector4.One);
        Throws<ArgumentException>(() => GpuCurveAtlasPacker.Pack(ScalarCurve.Constant(1f), color, new GpuCurveSample[63]));
        Throws<InvalidDataException>(() => Validate(new EmitterSpec { Name = "bad", SizeOverLifetime = new ScalarCurve(Array.Empty<CurveKey>()) }));
        Throws<InvalidDataException>(() => Validate(new EmitterSpec { Name = "bad", ColorOverLifetime = new ColorGradient(Array.Empty<ColorStop>()) }));
        Throws<InvalidDataException>(() => Validate(new EmitterSpec { Name = "bad", SizeOverLifetime = new ScalarCurve(new[] { new CurveKey(0.4f, 1f), new CurveKey(0f, 2f) }) }));
        Throws<InvalidDataException>(() => Validate(new EmitterSpec { Name = "bad", SizeOverLifetime = new ScalarCurve(new[] { new CurveKey(1f, 1f), new CurveKey(0f, 2f) }) }));
        Throws<InvalidDataException>(() => Validate(new EmitterSpec { Name = "bad", SizeOverLifetime = new ScalarCurve(new[] { new CurveKey(0f, 1f, OutTangent: float.NaN) }) }));
        Throws<InvalidDataException>(() => Validate(new EmitterSpec { Name = "bad", SizeOverLifetime = ScalarCurve.Constant(float.PositiveInfinity) }));
        Throws<InvalidDataException>(() => Validate(new EmitterSpec { Name = "bad", ColorOverLifetime = ColorGradient.Constant(new Vector4(float.NaN)) }));
        Throws<InvalidDataException>(() => Validate(new EmitterSpec { Name = "bad", ColorOverLifetime = new ColorGradient(new[] { new ColorStop(2f, Vector4.One) }) }));
    }

    private static void Validate(EmitterSpec emitter)
        => LumenAssetRules.Validate(new EffectAsset { EffectId = "particle.data", Nodes = new[] { new EffectNodeSpec { Name = "root", Emitter = emitter } } }, "particle.data");

    [LumenFact]
    public static void AllocatorReturnsExclusiveContiguousRangesAndReusesReleasedSlots()
    {
        var allocator = new EmitterSlotAllocator(8);
        var first = allocator.Allocate(3);
        var second = allocator.Allocate(5);
        Equal(new EmitterSlotRange(0, 3), first);
        Equal(new EmitterSlotRange(3, 5), second);
        Throws<InvalidOperationException>(() => allocator.Allocate(1));
        allocator.Free(first);
        Equal(first, allocator.Allocate(3));
        Throws<InvalidOperationException>(() => allocator.Allocate(1));
    }

    [LumenFact]
    public static void AllocatorRejectsFragmentedRequestAndCoalescesBothNeighborsWithoutLeaks()
    {
        var allocator = new EmitterSlotAllocator(12);
        for (int i = 0; i < 32; i++)
        {
            var left = allocator.Allocate(4);
            var middle = allocator.Allocate(4);
            var right = allocator.Allocate(4);
            allocator.Free(left);
            allocator.Free(right);
            Throws<InvalidOperationException>(() => allocator.Allocate(6));
            allocator.Free(middle);
            var all = allocator.Allocate(12);
            Equal(new EmitterSlotRange(0, 12), all);
            allocator.Free(all);
        }
    }

    [LumenFact]
    public static void AllocatorRejectsInvalidRequestsAndDoubleFreeWithoutCorruptingPool()
    {
        Throws<ArgumentOutOfRangeException>(() => new EmitterSlotAllocator(0));
        var allocator = new EmitterSlotAllocator(4);
        Throws<ArgumentOutOfRangeException>(() => allocator.Allocate(0));
        Throws<ArgumentOutOfRangeException>(() => allocator.Allocate(-1));
        var all = allocator.Allocate(4);
        Throws<ArgumentException>(() => allocator.Free(new EmitterSlotRange(0, 2)));
        Throws<InvalidOperationException>(() => allocator.Allocate(1));
        allocator.Free(all);
        Throws<ArgumentException>(() => allocator.Free(all));
        Equal(all, allocator.Allocate(4));
    }

    [LumenFact]
    public static void RateRetainsFractionalParticlesAcrossFrames()
    {
        var spec = new EmitterSpec { Name = "rate", Duration = 1f, Looping = true, RateOverTime = 2.5f };
        var scheduler = new EmissionScheduler(spec, 1u);
        Equal(0, scheduler.Advance(0.25f, true));
        Equal(1, scheduler.Advance(0.25f, true));
        Equal(1, scheduler.Advance(0.5f, true));
        Equal(3, scheduler.Advance(1f, true));
        Equal(5, new EmissionScheduler(spec, 1u).Advance(2f, true));
        Equal(false, scheduler.Finished);
    }

    [LumenFact]
    public static void BurstsIncludeTimeZeroOnceAndHandleZeroDelta()
    {
        var scheduler = new EmissionScheduler(new EmitterSpec
        {
            Name = "initial", Bursts = new[] { new ParticleBurstSpec(0f, 7) },
        }, 1u);
        Equal(7, scheduler.Advance(0f, true));
        Equal(0, scheduler.Advance(0f, true));
        Equal(0, scheduler.Advance(0.5f, true));
    }

    [LumenFact]
    public static void BurstsCountEveryCrossedLoopAndBothBoundaryEventsWithoutDoubleCounting()
    {
        var spec = new EmitterSpec
        {
            Name = "loops", Duration = 2f, Looping = true, RateOverTime = 1.5f,
            Bursts = new[] { new ParticleBurstSpec(0.5f, 3), new ParticleBurstSpec(2f, 5), new ParticleBurstSpec(0f, 2) },
        };
        var whole = new EmissionScheduler(spec, 1u);
        Equal(31, whole.Advance(4.5f, true));
        Equal(0, whole.Advance(0f, true));
        Equal(1, whole.Advance(0.5f, true));
        var split = new EmissionScheduler(spec, 1u);
        Equal(15, split.Advance(2f, true));
        Equal(13, split.Advance(2f, true));
        Equal(3, split.Advance(0.5f, true));
        Equal(1, split.Advance(0.5f, true));
    }

    [LumenFact]
    public static void NonloopClampsRateToDurationAndIncludesFinalBurst()
    {
        var scheduler = new EmissionScheduler(new EmitterSpec
        {
            Name = "once", Duration = 1f, RateOverTime = 4f,
            Bursts = new[] { new ParticleBurstSpec(0f, 2), new ParticleBurstSpec(0.5f, 3), new ParticleBurstSpec(1f, 5) },
        }, 1u);
        Equal(false, scheduler.Finished);
        Equal(14, scheduler.Advance(2f, true));
        Equal(true, scheduler.Finished);
        Equal(0, scheduler.Advance(2f, true));
    }

    [LumenFact]
    public static void PausedEmissionAdvancesClockWithoutBacklogAndKeepsExistingFraction()
    {
        var scheduler = new EmissionScheduler(new EmitterSpec
        {
            Name = "paused", Duration = 1f, Looping = true, RateOverTime = 3f,
            Bursts = new[] { new ParticleBurstSpec(0.5f, 7), new ParticleBurstSpec(0.75f, 11) },
        }, 1u);
        Equal(0, scheduler.Advance(0.25f, true));
        Equal(0, scheduler.Advance(4.5f, false));
        Equal(1, scheduler.Advance(0.25f, true));
        Equal(0, scheduler.Advance(0f, true));
    }

    [LumenFact]
    public static void PausedFirstFrameConsumesInitialBurstAndCanFinishNonloop()
    {
        var spec = new EmitterSpec { Name = "paused-start", Bursts = new[] { new ParticleBurstSpec(0f, 7), new ParticleBurstSpec(1f, 4) } };
        var scheduler = new EmissionScheduler(spec, 1u);
        Equal(0, scheduler.Advance(0f, false));
        Equal(0, scheduler.Advance(0f, true));
        Equal(0, scheduler.Advance(1f, false));
        Equal(true, scheduler.Finished);
        Equal(0, scheduler.Advance(0f, true));
    }

    [LumenFact]
    public static void AssetBoundaryAndSchedulerRejectMalformedSpecAndDelta()
    {
        Throws<InvalidDataException>(() => Validate(new EmitterSpec { Name = "bad", Duration = 0f }));
        Throws<InvalidDataException>(() => Validate(new EmitterSpec { Name = "bad", Duration = float.NaN }));
        Throws<InvalidDataException>(() => Validate(new EmitterSpec { Name = "bad", RateOverTime = -1f }));
        Throws<InvalidDataException>(() => Validate(new EmitterSpec { Name = "bad", RateOverTime = float.PositiveInfinity }));
        Throws<InvalidDataException>(() => Validate(new EmitterSpec { Name = "bad", Bursts = new[] { new ParticleBurstSpec(2f, 1) } }));
        Throws<InvalidDataException>(() => Validate(new EmitterSpec { Name = "bad", Bursts = new[] { new ParticleBurstSpec(0f, -1) } }));
        Throws<InvalidDataException>(() => Validate(new EmitterSpec { Name = "bad", Bursts = new[] { new ParticleBurstSpec(float.NaN, 1) } }));
        var scheduler = new EmissionScheduler(new EmitterSpec { Name = "valid", Bursts = new[] { new ParticleBurstSpec(0f, 2) } }, 1u);
        Throws<ArgumentOutOfRangeException>(() => scheduler.Advance(-1f, true));
        Throws<ArgumentOutOfRangeException>(() => scheduler.Advance(float.NaN, true));
        Throws<ArgumentOutOfRangeException>(() => scheduler.Advance(float.PositiveInfinity, true));
        Equal(2, scheduler.Advance(0f, true));
    }

    [LumenFact]
    public static void ProbabilitySchedulesAreGroupingIndependentAndRestartResetsRandomAndCarry()
    {
        var spec = new EmitterSpec
        {
            Name = "probability", Duration = 1f,
            Bursts = new[] { new ParticleBurstSpec(0f, 1, 0f), new ParticleBurstSpec(.25f, 1, 1f),
                new ParticleBurstSpec(.5f, 1, 0f), new ParticleBurstSpec(.75f, 1, 1f) },
        };
        var grouped = new EmissionScheduler(spec, 123u);
        var split = new EmissionScheduler(spec, 123u);
        Equal(1, grouped.Advance(1f, true, .5f, 1f));
        Equal(0, split.Advance(0f, true, .5f, 1f));
        Equal(1, split.Advance(.25f, true, .5f, 1f));
        Equal(0, split.Advance(.25f, true, .5f, 1f));
        Equal(0, split.Advance(.25f, true, .5f, 1f));
        split.Restart();
        Equal(1, split.Advance(1f, true, .5f, 1f));
        var randomSpec = new EmitterSpec { Name = "random", Duration = 1f, Looping = true,
            Bursts = new[] { new ParticleBurstSpec(.25f, 1, .5f), new ParticleBurstSpec(.75f, 1, .5f) } };
        var whole = new EmissionScheduler(randomSpec, 123u);
        var pieces = new EmissionScheduler(randomSpec, 123u);
        double total = whole.Advance(4f, true);
        Equal(total, pieces.Advance(1f, true) + pieces.Advance(1f, true) + pieces.Advance(1f, true) + pieces.Advance(1f, true));
        pieces.Restart();
        Equal(total, pieces.Advance(4f, true));
    }

    [LumenFact]
    public static void StaticShapeSamplingRespectsShellAndArcBoundaries()
    {
        var shell = new EmitterSpec { Name = "hemisphere", Shape = LumenShapeKind.HemisphereShell, ShapeExtents = Vector3.One,
            ArcDegrees = 90f };
        uint seed = 123u;
        ParticleShapeSampler.Sample(shell, ref seed, out var position, out var direction);
        Near(1f, position.Length());
        Near(1f, direction.Length());
        if (position.X < -1e-6f || position.Y < 0f || position.Z > 1e-6f)
            throw new InvalidOperationException($"Hemisphere shell escaped the +Y, +X/-Z quarter: {position}");
        var edge = new EmitterSpec { Name = "edge", Shape = LumenShapeKind.SingleSidedEdge,
            ShapeExtents = new Vector3(2f), RadiusSpread = 1f };
        ParticleShapeSampler.Sample(edge, ref seed, out position, out direction);
        Equal(new Vector3(-2f, 0f, 0f), position);
        Equal(-Vector3.UnitZ, direction);
    }

    [LumenFact]
    public static void BillboardVerticalOverheadAndLocalProjectionRemainGeometric()
    {
        ParticleBillboardMath.Vertical(Vector3.UnitX, Vector3.UnitZ, 1f, false, out var right, out var up);
        Equal(Vector3.UnitX, right);
        Equal(Vector3.UnitY, up);
        ParticleBillboardMath.Local(Quaternion.Identity, true, true, 1f, out right, out up);
        Equal(Vector3.UnitX, right);
        Equal(Vector3.UnitZ, up);
    }

    [LumenFact]
    public static void SimulationDispatchCoversTailBlocksAndSizesScratchWithoutOverflow()
    {
        Equal(1, ParticleSimulationDispatch.BlockCount(1));
        Equal(1, ParticleSimulationDispatch.BlockCount(64));
        Equal(2, ParticleSimulationDispatch.BlockCount(65));
        Equal(7813, ParticleSimulationDispatch.BlockCount(500000));
        Equal(31252L, ParticleSimulationDispatch.ScratchBytes(500000));
        Equal(33554432, ParticleSimulationDispatch.BlockCount(int.MaxValue));
    }

    [LumenFact]
    public static void GpuStructSizesAndOffsetsMatchShaderStorageLayout()
    {
        Equal(64, Marshal.SizeOf<GpuParticle>());
        Offset<GpuParticle>(nameof(GpuParticle.PositionAndAge), 0);
        Offset<GpuParticle>(nameof(GpuParticle.VelocityAndLifetime), 16);
        Offset<GpuParticle>(nameof(GpuParticle.StartColor), 32);
        Offset<GpuParticle>(nameof(GpuParticle.SizeSeedEmitterFlags), 48);
        Equal(320, Marshal.SizeOf<GpuEmitterParams>());
        Offset<GpuEmitterParams>(nameof(GpuEmitterParams.World), 0);
        Offset<GpuEmitterParams>(nameof(GpuEmitterParams.Slots), 64);
        Offset<GpuEmitterParams>(nameof(GpuEmitterParams.Shape), 80);
        Offset<GpuEmitterParams>(nameof(GpuEmitterParams.Ranges), 96);
        Offset<GpuEmitterParams>(nameof(GpuEmitterParams.SizeCone), 112);
        Offset<GpuEmitterParams>(nameof(GpuEmitterParams.StartColor), 128);
        Offset<GpuEmitterParams>(nameof(GpuEmitterParams.Flipbook), 144);
        Offset<GpuEmitterParams>(nameof(GpuEmitterParams.Appearance), 160);
        Offset<GpuEmitterParams>(nameof(GpuEmitterParams.Tables), 176);
        Offset<GpuEmitterParams>(nameof(GpuEmitterParams.State), 192);
        Offset<GpuEmitterParams>(nameof(GpuEmitterParams.ShapePosition), 208);
        Offset<GpuEmitterParams>(nameof(GpuEmitterParams.ShapeRotation), 224);
        Offset<GpuEmitterParams>(nameof(GpuEmitterParams.ShapeScale), 240);
        Offset<GpuEmitterParams>(nameof(GpuEmitterParams.ShapeOptions), 256);
        Offset<GpuEmitterParams>(nameof(GpuEmitterParams.ShapeMix), 272);
        Offset<GpuEmitterParams>(nameof(GpuEmitterParams.BoxThicknessDonut), 288);
        Offset<GpuEmitterParams>(nameof(GpuEmitterParams.WorldRotation), 304);
        Equal(64, Marshal.SizeOf<Matrix4x4>());
        Offset<Matrix4x4>(nameof(Matrix4x4.M11), 0);
        Offset<Matrix4x4>(nameof(Matrix4x4.M21), 16);
        Offset<Matrix4x4>(nameof(Matrix4x4.M31), 32);
        Offset<Matrix4x4>(nameof(Matrix4x4.M41), 48);
        Equal(48, Marshal.SizeOf<GpuForceField>());
        Offset<GpuForceField>(nameof(GpuForceField.CenterOrAxisAndKind), 0);
        Offset<GpuForceField>(nameof(GpuForceField.ParametersAndRadius), 16);
        Offset<GpuForceField>(nameof(GpuForceField.StrengthAndFrequency), 32);
        Equal(32, Marshal.SizeOf<GpuCurveSample>());
        Offset<GpuCurveSample>(nameof(GpuCurveSample.Color), 0);
        Offset<GpuCurveSample>(nameof(GpuCurveSample.Size), 16);
        Equal(16, Marshal.SizeOf<GpuSimPushConstants>());
        Offset<GpuSimPushConstants>(nameof(GpuSimPushConstants.EmitterIndex), 0);
        Offset<GpuSimPushConstants>(nameof(GpuSimPushConstants.FrameSeed), 4);
        Offset<GpuSimPushConstants>(nameof(GpuSimPushConstants.Padding0), 8);
        Offset<GpuSimPushConstants>(nameof(GpuSimPushConstants.Padding1), 12);
    }

    private static void Offset<T>(string field, int expected) where T : struct
        => Equal(expected, Marshal.OffsetOf<T>(field).ToInt32());

    private static void Equal<T>(T expected, T actual) where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }

    private static void Near(float expected, float actual)
    {
        if (!float.IsFinite(actual) || Math.Abs(expected - actual) > 0.00001f)
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
