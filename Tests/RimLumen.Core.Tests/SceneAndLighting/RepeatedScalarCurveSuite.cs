using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.ParticlesGpu;
using RimLumen.Core.Scene;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class RepeatedScalarCurveSuite
{
    [LumenFact]
    public static void InteriorRepeatUsesFirstOnEntryAndLastAtExactTimeAndAfter()
    {
        var curve = new ScalarCurve(new[]
        {
            new CurveKey(0f, 0f, OutTangent: 4f),
            new CurveKey(0.5f, 2f, InTangent: 4f), new CurveKey(0.5f, 6f),
            new CurveKey(0.5f, 10f, OutTangent: 4f), new CurveKey(1f, 12f, InTangent: 4f),
        }, 2f);
        Close(3.9992f, CurveEvaluator.Evaluate(curve, 0.4999f));
        Close(20f, CurveEvaluator.Evaluate(curve, 0.5f));
        Close(20.0008f, CurveEvaluator.Evaluate(curve, 0.5001f));
        Close(2f, CurveEvaluator.Evaluate(curve, 0.25f));
        Close(22f, CurveEvaluator.Evaluate(curve, 0.75f));
    }

    [LumenFact]
    public static void RepeatedFirstAndFinalKeysKeepUpstreamEndpointExceptions()
    {
        var curve = new ScalarCurve(new[]
        {
            new CurveKey(0f, 2f), new CurveKey(0f, 6f),
            new CurveKey(1f, 10f), new CurveKey(1f, 14f),
        });
        Close(2f, CurveEvaluator.Evaluate(curve, -1f));
        Close(2f, CurveEvaluator.Evaluate(curve, 0f));
        Close(6f, CurveEvaluator.Evaluate(curve, 0.00001f));
        Close(10f, CurveEvaluator.Evaluate(curve, 0.99999f));
        Close(14f, CurveEvaluator.Evaluate(curve, 1f));
        Close(14f, CurveEvaluator.Evaluate(curve, 2f));
    }

    [LumenFact]
    public static void AllEqualDomainUsesLastForEveryTimeAndBake()
    {
        var curve = new ScalarCurve(new[] { new CurveKey(0.5f, 1f), new CurveKey(0.5f, 3f), new CurveKey(0.5f, 7f) }, 2f);
        Close(14f, CurveEvaluator.Evaluate(curve, -10f));
        Close(14f, CurveEvaluator.Evaluate(curve, 0.5f));
        Close(14f, CurveEvaluator.Evaluate(curve, 10f));
        var row = new GpuCurveSample[64];
        GpuCurveAtlasPacker.Pack(curve, ColorGradient.Constant(Vector4.One), row);
        Close(14f, row[0].Size.X);
        Close(14f, row[32].Size.X);
        Close(14f, row[63].Size.X);
    }

    [LumenFact]
    public static void JsonXmlAndCsharpAcceptRepeatedScalarKeysWithoutMovingOrDroppingThem()
    {
        const string json = """{ "nodes": [{ "name": "root", "emitter": { "sizeOverLifetime": [[0,2],[0.5,2],[0.5,6],[0.5,10],[1,10]] }, "pointLight": { "intensityOverTime": [[0,2],[2,2],[2,6],[2,10],[4,10]] } }] }""";
        EffectAsset parsed = LumenJsonAssetLoader.Parse(json, "repeat-json");
        EffectAsset xml = new LumenEffectDef
        {
            defName = "repeat-xml",
            nodes = new List<LumenNodeXml> { new()
            {
                name = "root", emitter = new LumenEmitterXml { sizeOverLifetime = XmlKeys(0.5f, 1f) },
                pointLight = new LumenPointLightXml { intensityOverTime = XmlKeys(2f, 4f) },
            } },
        }.ToEffectAsset();
        var native = new EffectAsset
        {
            EffectId = "repeat-csharp", Nodes = new[] { new EffectNodeSpec
            {
                Name = "root", Emitter = new EmitterSpec { Name = "repeat", SizeOverLifetime = StepCurve() },
                PointLight = new PointLightSpec(1f, Vector3.One, new ScalarCurve(new[]
                { new CurveKey(0f, 2f), new CurveKey(2f, 2f), new CurveKey(2f, 6f), new CurveKey(2f, 10f), new CurveKey(4f, 10f) })),
            } },
        };
        LumenAssetRules.Validate(native, "repeat-csharp");
        AssertParsed(parsed); AssertParsed(xml); AssertParsed(native);
    }

    [LumenFact]
    public static void LutBakesAtIOver63AndCpuConsumesItsOneIntervalSmear()
    {
        ScalarCurve curve = StepCurve();
        var row = new GpuCurveSample[64];
        GpuCurveAtlasPacker.Pack(curve, ColorGradient.Constant(Vector4.One), row);
        Close(2f, row[31].Size.X); Close(10f, row[32].Size.X);
        Close(10f, CurveEvaluator.Evaluate(curve, 0.5f));
        var simulator = new CpuParticleSimulator();
        int id = simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "repeat-lut", SizeOverLifetime = curve, StartSizeMin = 1f, StartSizeMax = 1f,
            StartLifetimeMin = 1f, StartLifetimeMax = 1f,
        }, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u, false);
        simulator.EmitBurst(id, 1);
        Step(simulator, 0f);
        Step(simulator, 31f / 63f);
        Close(2f, simulator.LiveParticles[0].Size);
        Step(simulator, 0.5f - 31f / 63f);
        Close(6f, simulator.LiveParticles[0].Size);
        Step(simulator, 32f / 63f - 0.5f);
        Close(10f, simulator.LiveParticles[0].Size);
    }

    [LumenFact]
    public static void PackerKeepsDoubleSamplePositionAndRepeatedEndpointExceptions()
    {
        var row = new GpuCurveSample[64];
        var curve = new ScalarCurve(new[]
        {
            new CurveKey(0f, 2f), new CurveKey(0f, 4f),
            new CurveKey(1f / 3f, 4f), new CurveKey(1f / 3f, 8f),
            new CurveKey(1f, 8f), new CurveKey(1f, 12f),
        });
        GpuCurveAtlasPacker.Pack(curve, ColorGradient.Constant(Vector4.One), row);
        Close(2f, row[0].Size.X); Close(4f, row[21].Size.X);
        Close(8f, row[22].Size.X); Close(12f, row[63].Size.X);
        // 1/3 is not exactly representable in CurveKey's float; the double LUT sample at21/63 is just LEFT of it.
        GpuCurveAtlasPacker.Pack(new ScalarCurve(new[] { new CurveKey(0f, 2f), new CurveKey(0f, 6f) }), ColorGradient.Constant(Vector4.One), row);
        Close(6f, row[0].Size.X); Close(6f, row[63].Size.X);
    }

    private static ScalarCurve StepCurve() => new(new[]
    {
        new CurveKey(0f, 2f), new CurveKey(0.5f, 2f), new CurveKey(0.5f, 6f),
        new CurveKey(0.5f, 10f), new CurveKey(1f, 10f),
    });

    private static List<LumenCurveKeyXml> XmlKeys(float middle, float last) => new()
    {
        new() { time = 0f, value = 2f }, new() { time = middle, value = 2f },
        new() { time = middle, value = 6f }, new() { time = middle, value = 10f }, new() { time = last, value = 10f },
    };

    private static void AssertParsed(EffectAsset asset)
    {
        ScalarCurve size = asset.Nodes[0].Emitter!.SizeOverLifetime;
        if (size.Keys.Length != 5 || size.Keys[2].Time != 0.5f || size.Keys[2].Value != 6f)
            throw new InvalidOperationException("Repeated scalar keys were dropped, reordered or moved.");
        Close(10f, CurveEvaluator.Evaluate(size, 0.5f));
        Close(10f, CurveEvaluator.Evaluate(asset.Nodes[0].PointLight!.Value.IntensityOverTime!, 2f));
    }

    private static void Step(CpuParticleSimulator simulator, float dt) => simulator.CaptureMainThread(new LumenCaptureContext
    {
        CurrentMapIndex = -1, FrameIndex = 0, LumenRenderPassId = 0, DeltaTime = dt, ViewportWidth = 100, ViewportHeight = 100,
        ViewMatrix = Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity,
    });

    private static void Close(float expected, float actual)
    {
        if (!(MathF.Abs(expected - actual) <= 1e-4f))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
