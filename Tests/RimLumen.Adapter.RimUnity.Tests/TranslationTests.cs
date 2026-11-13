using System;
using System.Linq;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimUnity.Serialization;
using static RimLumen.Adapter.RimUnity.Tests.Export;

namespace RimLumen.Adapter.RimUnity.Tests;

internal static class TranslationTests
{
    internal static void TierMatrix()
    {
        var classifier = new RimUnityTierClassifier();
        var small = Particles();
        Check.Equal(LumenParticleTier.L1_NativeCpu, classifier.Classify(small, null).Tier, "32 sparks");
        Check.Reasons([], classifier.Classify(small, null).Reasons, "small reasons");
        var large = Particles(256);
        large.members.Single(m => m.name == "shape").value = Data(("enabled", Number(1)), ("shapeType", Number(0)));
        large.members.Add(Member("noise", Data(("enabled", Number(1)), ("strength", Constant(3)), ("frequency", Number(2)))));
        large.members.Add(Member("velocityOverLifetime", Data(("enabled", Number(1)), ("orbitalY", Constant(4)))));
        var asset = new RimUnityPrefabTranslator().Translate("curl-vortex", Prefab(large), _ => null, _ => null, out var report);
        Check.Equal(LumenParticleTier.L2_GpuCompute, report.Nodes[0].Tier, "high curl/vortex");
        Check.Reasons(["maxParticles >= L2ParticleThreshold"], report.Nodes[0].Reasons, "high reasons");
        Check.True(asset.Nodes[0].Emitter!.ForceFields.Any(f => f.Kind == ForceFieldKind.CurlTurbulence), "curl retained");
        Check.True(asset.Nodes[0].Emitter!.ForceFields.Any(f => f.Kind == ForceFieldKind.Vortex), "vortex retained");
        var peakParticles = Particles(32, 100, 2);
        peakParticles.members.Single(m => m.name == "shape").value = Data(("enabled", Number(1)), ("shapeType", Number(0)));
        var peak = classifier.Classify(peakParticles, null);
        Check.Equal(LumenParticleTier.L2_GpuCompute, peak.Tier, "peak concurrent threshold");
        Check.Reasons(["peakConcurrent >= L2ParticleThreshold"], peak.Reasons, "peak reasons");
        foreach (string module in new[] { "trails", "subEmitters", "collision", "trigger" })
            AssertFallback(module, Data(("enabled", Number(1))), module);
        var mesh = new ComponentData { id = "renderer", type = "RimUnity.ParticleSystemRenderer", members = [Member("renderMode", Number(4))] };
        var meshResult = classifier.Classify(small, mesh);
        Check.Equal(LumenParticleTier.L0_ExternalCpu, meshResult.Tier, "mesh tier");
        Check.Reasons(["renderMode.Mesh"], meshResult.Reasons, "mesh reasons");
        AssertFallback("velocityOverLifetime", Data(("enabled", Number(1)), ("x", Constant(2))), "velocityOverLifetime.linear");
        AssertFallback("rotationOverLifetime", Data(("enabled", Number(1))), "rotationOverLifetime");
        AssertFallback("forceOverLifetime", Data(("enabled", Number(1)), ("y", Curve(0, 1))), "forceOverLifetime.curve");
        AssertFallback("lights", Data(("enabled", Number(1)), ("ratio", Number(0.5f))), "lights.ratio");
        var probabilistic = Particles();
        probabilistic.members.RemoveAll(m => m.name == "emission");
        probabilistic.members.Add(Member("emission", Data(("enabled", Number(1)), ("bursts", Array(Data(("probability", Number(0.5f)), ("count", Constant(3))))))));
        Check.Reasons(["emission.bursts.probability"], classifier.Classify(probabilistic, null).Reasons, "probability reason");
        var combined = Particles();
        foreach (string module in new[] { "trails", "subEmitters", "collision", "trigger" }) combined.members.Add(Member(module, Data(("enabled", Number(1)))));
        Check.Reasons(["trails", "subEmitters", "collision", "trigger", "renderMode.Mesh"], classifier.Classify(combined, mesh).Reasons, "all blockers retained");
        Console.WriteLine("MATRIX small32=L1 highCurlVortex=L2 peak200=L2; structural=5; conservative=6; all-blockers=5 reasons");
    }

    internal static void Shapes()
    {
        (int Max, ValueData Shape, string[] Reasons)[] cases =
        [
            (32, Data(("enabled", Number(1)), ("shapeType", Number(4)), ("radius", Number(1)), ("angle", Number(0))), ["shape.coneRadius"]),
            (32, Data(("enabled", Number(1)), ("shapeType", Number(4)), ("radius", Number(0)), ("angle", Number(25))), ["shape.coneDistribution"]),
            (256, Data(("enabled", Number(0))), ["shape.directionAxis"]),
            (256, Data(("enabled", Number(1)), ("shapeType", Number(5))), ["shape.directionAxis"]),
            (256, Data(("enabled", Number(1)), ("shapeType", Number(4)), ("radius", Number(0)), ("angle", Number(0))), ["shape.directionAxis"]),
            (32, Data(("enabled", Number(1)), ("shapeType", Number(10))), ["shape.circlePlane"]),
            (32, Data(("enabled", Number(1)), ("shapeType", Number(2))), ["shape.hemisphere"]),
            (32, Data(("enabled", Number(1)), ("shapeType", Number(0)), ("position", Vector(1, 0, 0))), ["shape.localTransform"]),
            (32, Data(("enabled", Number(1)), ("shapeType", Number(0)), ("rotation", Vector(90, 0, 0))), ["shape.localTransform"]),
            (32, Data(("enabled", Number(1)), ("shapeType", Number(0)), ("scale", Vector(2, 1, 1))), ["shape.localTransform"]),
            (32, Data(("enabled", Number(1)), ("shapeType", Number(0)), ("radiusThickness", Number(.5f))), ["shape.radiusThickness"]),
            (32, Data(("enabled", Number(1)), ("shapeType", Number(0)), ("arc", Number(180))), ["shape.arc"]),
            (32, Data(("enabled", Number(1)), ("shapeType", Number(0)), ("randomDirectionAmount", Number(.5f))), ["shape.direction"]),
            (32, Data(("enabled", Number(1)), ("shapeType", Number(0)), ("randomPositionAmount", Number(.5f))), ["shape.randomPosition"]),
            (32, Data(("enabled", Number(1)), ("shapeType", Number(0)), ("alignToDirection", Number(1))), ["shape.alignToDirection"]),
        ];
        foreach (var test in cases)
        {
            var particles = Particles(test.Max);
            particles.members.Single(m => m.name == "shape").value = test.Shape;
            particles.members.Add(Member("lights", Data(("enabled", Number(1)), ("ratio", Number(1)))));
            var asset = new RimUnityPrefabTranslator().Translate("shape", Prefab(particles), _ => null, _ => null, out var report);
            Check.Equal(LumenParticleTier.L0_ExternalCpu, report.Nodes.Single().Tier, "unrepresentable shape");
            Check.Reasons(test.Reasons, report.Nodes.Single().Reasons, "exact shape reason");
            Check.True(asset.Nodes[0].Emitter is null && asset.Nodes[0].PointLight is null, "shape fallback external ownership");
        }
        // A zero-angle, zero-radius cone is representable on CPU; arbitrary box extents are retained.
        var cone = Particles();
        cone.members.Single(m => m.name == "shape").value = Data(("enabled", Number(1)), ("shapeType", Number(4)), ("radius", Number(0)), ("angle", Number(0)));
        Check.Equal(LumenParticleTier.L1_NativeCpu, new RimUnityTierClassifier().Classify(cone, null).Tier, "degenerate CPU cone");
        var box = Particles();
        box.members.Single(m => m.name == "shape").value = Data(("enabled", Number(1)), ("shapeType", Number(5)), ("scale", Vector(2, 4, 6)));
        var boxAsset = new RimUnityPrefabTranslator().Translate("box", Prefab(box), _ => null, _ => null, out _);
        Check.Equal(new Vector3(1, 2, 3), boxAsset.Nodes[0].Emitter!.ShapeExtents, "CPU box half extents");
        Console.WriteLine($"SHAPES exact L0 cases={cases.Length}; representable CPU cone/box retained");
    }

    internal static void Gravity()
    {
        ForceFieldSpec? first = null;
        foreach (int max in new[] { 32, 256 })
        {
            var particles = Particles(max);
            particles.members.Single(m => m.name == "shape").value = Data(("enabled", Number(1)), ("shapeType", Number(0)), ("radius", Number(0)));
            particles.members.Single(m => m.name == "main").value.fields.Add(Member("gravityModifier", Constant(2)));
            particles.members.Single(m => m.name == "main").value.fields.RemoveAll(m => m.name == "startSpeed");
            particles.members.Single(m => m.name == "main").value.fields.Add(Member("startSpeed", Constant(0)));
            particles.members.Add(Member("forceOverLifetime", Data(("enabled", Number(1)), ("x", Constant(4)), ("z", Constant(-2)))));
            particles.members.Add(Member("limitVelocityOverLifetime", Data(("enabled", Number(1)), ("dampen", Number(.25f)))));
            var asset = new RimUnityPrefabTranslator().Translate("gravity", Prefab(particles), _ => null, _ => null, out var report);
            Check.Equal(max == 32 ? LumenParticleTier.L1_NativeCpu : LumenParticleTier.L2_GpuCompute, report.Nodes.Single().Tier, "gravity tier");
            ForceFieldSpec force = asset.Nodes.Single().Emitter!.ForceFields.Single();
            Check.Equal(ForceFieldKind.GravityDrag, force.Kind, "gravity kind");
            Check.Equal(new Vector3(4, -19.62f, -2), force.CenterOrAxis, "canonical acceleration");
            Check.Equal(new Vector3(.25f, 0, 0), force.Parameters, "canonical drag");
            Check.Equal(0f, force.Radius, "canonical radius");
            Check.Equal(1f, force.Strength, "canonical strength");
            if (first.HasValue) Check.Equal(first.Value, force, "L1 and L2 canonical fields identical");
            first = force;

            var sim = new RimLumen.Core.Scene.CpuParticleSimulator();
            int emitterId = sim.AcquireEmitter(asset.Nodes.Single().Emitter!, Matrix4x4.Identity, emitting: false);
            sim.EmitBurst(emitterId, 1);
            float dt = 0.5f;
            sim.CaptureMainThread(new LumenCaptureContext
            {
                FrameIndex = 1,
                DeltaTime = dt,
                ViewportWidth = 1920,
                ViewportHeight = 1080,
                ViewMatrix = Matrix4x4.Identity,
                ProjectionMatrix = Matrix4x4.Identity,
                LumenRenderPassId = 0,
            });
            Vector3 expectedVel = new Vector3(4, -19.62f, -2) * dt * MathF.Exp(-0.25f * dt);
            Check.True(Vector3.Distance(expectedVel, sim.LiveParticles[0].Velocity) < 1e-4f, "CPU simulator matches canonical 3D GravityDrag velocity");
        }
        Console.WriteLine("GRAVITY fields L1==L2 accel=(4,-19.62,-2) drag=(0.25,0,0) radius=0 strength=1; CPU physics verified on L1 + L2->CPU fallback");
    }

    private static void AssertFallback(string module, ValueData value, string reason)
    {
        var particles = Particles();
        particles.members.Add(Member(module, value));
        if (module != "lights") particles.members.Add(Member("lights", Data(("enabled", Number(1)), ("ratio", Number(1)))));
        var asset = new RimUnityPrefabTranslator().Translate(module, Prefab(particles), _ => null, _ => null, out var report);
        Check.Equal(LumenParticleTier.L0_ExternalCpu, report.Nodes.Single().Tier, reason);
        Check.Reasons([reason], report.Nodes.Single().Reasons, reason);
        Check.True(asset.Nodes[0].Emitter is null && asset.Nodes[0].PointLight is null, reason + " must leave particles AND lights to external");
    }

    internal static void SubEmitterClosure()
    {
        var a = Particles(); a.id = "a.ps";
        var b = Particles(); b.id = "b.ps";
        var c = Particles(); c.id = "c.ps";
        a.members.Add(Member("subEmitters", SubEmitter("b.ps")));
        b.members.Add(Member("subEmitters", SubEmitter("c.ps")));
        c.members.Add(Member("lights", Data(("enabled", Number(1)), ("ratio", Number(1)))));
        var prefab = new PrefabData { nodes = [Node("c", c, "a"), Node("b", b, "a"), Node("a", a)] };
        var asset = new RimUnityPrefabTranslator().Translate("closure", prefab, _ => null, _ => null, out var report);
        Check.Equal(3, report.Nodes.Count, "closure report count");
        Check.Reasons(["subEmitters"], report.Nodes.Single(n => n.NodeId == "a").Reasons, "source");
        Check.Reasons(["subEmitters", "subEmitters.dependency"], report.Nodes.Single(n => n.NodeId == "b").Reasons, "middle");
        Check.Reasons(["subEmitters.dependency"], report.Nodes.Single(n => n.NodeId == "c").Reasons, "transitive target");
        foreach (var node in report.Nodes)
        {
            Check.Equal(LumenParticleTier.L0_ExternalCpu, node.Tier, "closure tier");
            Check.True(asset.Nodes[node.NodeIndex].Emitter is null && asset.Nodes[node.NodeIndex].PointLight is null, "closure duplicate native ownership");
        }
        Check.Equal(-1, asset.Nodes[0].ParentIndex, "topological root");
        Check.True(asset.Nodes.Skip(1).All(n => n.ParentIndex == 0), "topological children");

        var siblingSource = Particles(); siblingSource.id = "s1.ps";
        var siblingTarget = Particles(); siblingTarget.id = "s2.ps";
        siblingTarget.members.Add(Member("lights", Data(("enabled", Number(1)), ("ratio", Number(1)))));
        siblingSource.members.Add(Member("subEmitters", SubEmitter("s2.ps")));
        var siblings = new PrefabData
        {
            nodes =
            [
                new NodeData { id = "r", transformId = "r.t", name = "r", active = true, position = [0, 0, 0], rotation = [0, 0, 0, 1], scale = [1, 1, 1], components = [] },
                Node("s1", siblingSource, "r"), Node("s2", siblingTarget, "r"),
            ]
        };
        var siblingAsset = new RimUnityPrefabTranslator().Translate("siblings", siblings, _ => null, _ => null, out var siblingReport);
        Check.Equal(2, siblingReport.Nodes.Count, "sibling report count");
        Check.Reasons(["subEmitters"], siblingReport.Nodes.Single(n => n.NodeId == "s1").Reasons, "sibling source");
        Check.Reasons(["subEmitters.dependency"], siblingReport.Nodes.Single(n => n.NodeId == "s2").Reasons, "sibling target");
        Check.True(siblingAsset.Nodes.Where(n => n.Emitter is not null).All(n => n.Name == "s1"), "sibling target has no native emitter");
    }

    private static ValueData SubEmitter(string id) => Data(("enabled", Number(1)),
        ("entries", Array(Data(("system", new ValueData { kind = "object", text = id })))));
    private static ValueData Key(float time, float value, float slope) => Data(("time", Number(time)), ("value", Number(value)), ("inTangent", Number(slope)), ("outTangent", Number(slope)));
    private static ValueData Curve(float start, float end) => Data(("mode", Number(1)), ("curveMultiplier", Number(2)),
        ("curve", Data(("keys", Array(Key(1, end, end - start), Key(0, start, end - start))))));

    internal static void Values()
    {
        Vector4 linear = RimUnityValueConversion.ToLinearColor(new Vector4(0.5f, 0.04045f, 2, 0.25f));
        Check.Near(0.21404114f, linear.X, "sRGB .5");
        Check.Near(0.003130805f, linear.Y, "sRGB threshold");
        Check.Near(4.95384575f, linear.Z, "unclamped HDR sRGB 2");
        Check.Near(0.25f, linear.W, "alpha unchanged");
        var normalized = RimUnityValueConversion.ExtractHdrIntensity(linear, out float hdr);
        Check.Near(4.95384575f, hdr, "HDR intensity");
        Check.Near(1, normalized.Z, "HDR normalized blue");
        Check.Near(0.21404114f, normalized.X * hdr, "HDR reconstruction");
        Check.Near(0.25f, normalized.W, "HDR alpha unchanged");
        var curve = RimUnityValueConversion.ToScalarCurve(Curve(1, 3));
        float[] expected = [2, 3, 4, 5, 6];
        for (int i = 0; i < expected.Length; i++) Check.Near(expected[i], CurveEvaluator.Evaluate(curve, i / 4f), "linear curve quarter " + i);
        var range = RimUnityValueConversion.CurveRange(Curve(1, 3));
        Check.Near(2, range.Min, "curve min"); Check.Near(6, range.Max, "curve max");
        var two = Data(("mode", Number(3)), ("constantMin", Number(2)), ("constantMax", Number(7)));
        Check.Near(7, CurveEvaluator.Evaluate(RimUnityValueConversion.ToScalarCurve(two), .5f), "two constants upper");
        Check.Equal((2f, 7f), RimUnityValueConversion.CurveRange(two), "two constants bounds");
        Check.Near(3, CurveEvaluator.Evaluate(RimUnityValueConversion.ToScalarCurve(Constant(3)), .25f), "constant");
        var gradient = Data(("mode", Number(1)), ("gradient", Data(
            ("colorKeys", Array(Data(("time", Number(0)), ("color", Vector(0, 0, 0, 1))), Data(("time", Number(1)), ("color", Vector(2, 1, .5f, 1))))),
            ("alphaKeys", Array(Data(("time", Number(0)), ("alpha", Number(.2f))), Data(("time", Number(.5f)), ("alpha", Number(.6f))), Data(("time", Number(1)), ("alpha", Number(1))))))));
        var colors = RimUnityValueConversion.ToColorGradient(gradient);
        Vector4 middle = GradientEvaluator.Evaluate(colors, .5f);
        Check.Near(2.47692288f, middle.X, "HDR gradient half");
        Check.Near(.5f, middle.Y, "green half"); Check.Near(.10702057f, middle.Z, "blue half"); Check.Near(.6f, middle.W, "independent alpha keys");
        Check.Near(1.23846144f, GradientEvaluator.Evaluate(colors, .25f).X, "HDR gradient quarter");
        Check.Near(4.95384575f, GradientEvaluator.Evaluate(colors, 1).X, "HDR end unclamped");
    }
}
