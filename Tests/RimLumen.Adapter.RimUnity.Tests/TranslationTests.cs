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
        AssertFallback("lights", Data(("enabled", Number(1)), ("ratio", Number(0.5f))), "lights.perParticle");
        var probabilistic = Particles();
        probabilistic.members.RemoveAll(m => m.name == "emission");
        probabilistic.members.Add(Member("emission", Data(("enabled", Number(1)), ("bursts", Array(Data(("probability", Number(0.5f)), ("count", Constant(3))))))));
        Check.Reasons(["emission.bursts.probability"], classifier.Classify(probabilistic, null).Reasons, "probability reason");
        var combined = Particles();
        foreach (string module in new[] { "trails", "subEmitters", "collision", "trigger" }) combined.members.Add(Member(module, Data(("enabled", Number(1)))));
        Check.Reasons(["trails", "subEmitters", "collision", "trigger", "renderMode.Mesh"], classifier.Classify(combined, mesh).Reasons, "all blockers retained");
        Console.WriteLine("MATRIX small32=L1 highCurlVortex=L2 peak200=L2; structural=5; conservative=6; all-blockers=5 reasons");
    }

    internal static void RenderModeFallback()
    {
        AssertRenderModeFallback(3, "renderMode.Vertical");
        AssertRenderModeFallback(-1, "renderMode.-1");
        AssertRenderModeFallback(9, "renderMode.9");
    }

    private static void AssertRenderModeFallback(int mode, string reason)
    {
        var particles = Particles();
        var prefab = Prefab(particles);
        prefab.nodes[0].components.Add(new ComponentData
        {
            id = "renderer", type = "RimUnity.ParticleSystemRenderer", members = [Member("renderMode", Number(mode))]
        });
        var asset = new RimUnityPrefabTranslator().Translate("mode", prefab, _ => null, _ => null, out var report);
        Check.Equal(LumenParticleTier.L0_ExternalCpu, report.Nodes.Single().Tier, "unsupported render mode tier");
        Check.Reasons([reason], report.Nodes.Single().Reasons, "render mode reason");
        Check.True(asset.Nodes[0].Emitter is null && asset.Nodes[0].PointLight is null, "unsupported render mode leaves particles and lights external");
    }

    internal static void Shapes()
    {
        (int Max, ValueData Shape, string[] Reasons)[] cases =
        [
            (32, Data(("enabled", Number(1)), ("shapeType", Number(4)), ("radius", Number(1)), ("angle", Number(0))), ["shape.coneRadius"]),
            (32, Data(("enabled", Number(1)), ("shapeType", Number(4)), ("radius", Number(0)), ("angle", Number(25))), ["shape.coneDistribution"]),
            (32, Data(("enabled", Number(1)), ("shapeType", Number(10))), ["shape.circlePlane"]),
            (32, Data(("enabled", Number(1)), ("shapeType", Number(2))), ["shape.hemisphere"]),
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
        var boxEmitter = boxAsset.Nodes[0].Emitter!;
        Vector3 corner = Vector3.Transform(boxEmitter.ShapeExtents * boxEmitter.ShapeScale, boxEmitter.ShapeRotation);
        Check.True(Vector3.Distance(new Vector3(1, 2, 3), Vector3.Abs(corner)) < 1e-6f, "box half extents after shape transform");
        Console.WriteLine($"SHAPES exact L0 cases={cases.Length}; representable CPU cone/box retained");
    }

    internal static void ShapeAxis()
    {
        AssertShapeDirection(new Vector3(0, 0, 0), Vector3.UnitZ);
        AssertShapeDirection(new Vector3(90, 0, 0), -Vector3.UnitY);
    }

    private static void AssertShapeDirection(Vector3 rotation, Vector3 unityDirection)
    {
        var particles = Particles(256);
        particles.members.Single(m => m.name == "shape").value = Data(("enabled", Number(1)), ("shapeType", Number(4)),
            ("radius", Number(0)), ("angle", Number(0)), ("rotation", Vector(rotation.X, rotation.Y, rotation.Z)));
        var prefab = Prefab(particles);
        prefab.nodes[0].rotation = [0, .70710677f, 0, .70710677f];
        var asset = new RimUnityPrefabTranslator().Translate("axis", prefab, _ => null, _ => null, out var report);
        Check.Equal(LumenParticleTier.L2_GpuCompute, report.Nodes[0].Tier, "cone no axis/localTransform fallback");
        var node = asset.Nodes[0];
        var emitter = node.Emitter!;
        Vector3 worldDirection = Vector3.Transform(Vector3.Transform(Vector3.UnitY, emitter.ShapeRotation), node.LocalRotation);
        Vector3 expected = Vector3.Transform(unityDirection, new Quaternion(0, .70710677f, 0, .70710677f));
        Check.True(Vector3.Distance(expected, worldDirection) < 1e-6f, $"Core +Y maps to Unity R*+Z in world: expected={expected}, actual={worldDirection}");
        Check.Equal(new Quaternion(0, .70710677f, 0, .70710677f), node.LocalRotation, "original node rotation retained");
    }

    internal static void ShapeScale()
    {
        var particles = Particles();
        particles.members.Single(m => m.name == "shape").value = Data(("enabled", Number(1)), ("shapeType", Number(5)),
            ("scale", Vector(2, 4, 6)), ("position", Vector(1, 2, 3)), ("rotation", Vector(0, 90, 0)));
        var asset = new RimUnityPrefabTranslator().Translate("scale", Prefab(particles), _ => null, _ => null, out _);
        var emitter = asset.Nodes[0].Emitter!;
        // Core cube sample (.5,.25,-.75) corresponds to Unity (.5,.75,.25) after the basis rotation.
        Vector3 position = Vector3.Transform(new Vector3(.5f, .25f, -.75f) * emitter.ShapeExtents * emitter.ShapeScale,
            emitter.ShapeRotation) + emitter.ShapePosition;
        Check.True(Vector3.Distance(new Vector3(1.75f, 3.5f, 2.5f), position) < 1e-6f, $"nonuniform shape scale applied once with matching axes: actual={position}");
        Check.Equal(new Vector3(.5f), emitter.ShapeExtents, "box base half extents");
        Check.Equal(new Vector3(2, 6, 4), emitter.ShapeScale, "basis permutes shape scale axes");
    }

    internal static void DuplicateCurve()
    {
        var particles = Particles();
        particles.members.Add(Member("sizeOverLifetime", Data(("enabled", Number(1)), ("size", Data(("mode", Number(1)),
            ("curveMultiplier", Number(2)), ("curve", Data(("keys", Array(Key(.5f, 1, 4), Key(.5f, 3, -2))))))))));
        var asset = new RimUnityPrefabTranslator().Translate("duplicate.curve", Prefab(particles), _ => null, _ => null, out _);
        RimLumen.Core.Api.RimLumenApi.RegisterEffect(asset);
        Check.Near(6, CurveEvaluator.Evaluate(asset.Nodes[0].Emitter!.SizeOverLifetime, 0), "all-equal times retain last value and multiplier before time");
        Check.Near(6, CurveEvaluator.Evaluate(asset.Nodes[0].Emitter!.SizeOverLifetime, 1), "all-equal times retain last value and multiplier after time");
    }

    internal static void MixedRepeatedCurves()
    {
        AssertMixedRepeatedCurve("two", [new(0, 1, 2, 2), new(.5f, 2, 2, 4), new(.5f, 4, -2, -2), new(1, 5, 2, 2)]);
        AssertMixedRepeatedCurve("three", [new(0, 1, 2, 2), new(.5f, 2, 2, 4), new(.5f, 3, -1, 3), new(.5f, 4, -2, -2), new(1, 5, 2, 2)]);
    }

    private static void AssertMixedRepeatedCurve(string name, global::RimUnity.Keyframe[] sourceKeys)
    {
        var source = new global::RimUnity.AnimationCurve(sourceKeys);
        var particles = Particles();
        ValueData keys = Array(sourceKeys.Select(key => Data(
            ("time", Number(key.time)), ("value", Number(key.value)), ("inTangent", Number(key.inTangent)),
            ("outTangent", Number(key.outTangent)))).ToArray());
        particles.members.Add(Member("sizeOverLifetime", Data(("enabled", Number(1)), ("size", Data(("mode", Number(1)),
            ("curveMultiplier", Number(2)), ("curve", Data(("keys", keys))))))));
        var asset = new RimUnityPrefabTranslator().Translate("mixed.curve." + name, Prefab(particles), _ => null, _ => null, out var report);
        RimLumen.Core.Api.RimLumenApi.RegisterEffect(asset);
        ScalarCurve translated = asset.Nodes[0].Emitter!.SizeOverLifetime;
        Check.Equal(LumenParticleTier.L1_NativeCpu, report.Nodes.Single().Tier, "mixed repeat retains native tier");
        Check.Equal(sourceKeys.Length, translated.Keys.Length, "mixed repeat keeps every source key");
        var sorted = source.keys;
        for (int i = 0; i < sorted.Length; i++)
        {
            Check.Equal(sorted[i].time, translated.Keys[i].Time, "source time preserved");
            Check.Equal(sorted[i].value, translated.Keys[i].Value, "source value preserved");
            Check.Equal(sorted[i].inTangent, translated.Keys[i].InTangent, "source in tangent preserved");
            Check.Equal(sorted[i].outTangent, translated.Keys[i].OutTangent, "source out tangent preserved");
        }
        foreach (float time in new[] { 0, .25f, MathF.BitDecrement(.5f), .5f, MathF.BitIncrement(.5f), .75f, 1 })
            Check.Near(2 * source.Evaluate(time), CurveEvaluator.Evaluate(translated, time), "mixed repeat source evaluation " + time);
        Console.WriteLine($"MIXED {name}: sourceKeys={sourceKeys.Length} translatedKeys={translated.Keys.Length} RegisterEffect=success exactMidpoint={CurveEvaluator.Evaluate(translated, .5f)}");
    }

    internal static void AdjacentFixedGradient()
    {
        float next = MathF.BitIncrement(.5f);
        var particles = Particles();
        particles.members.Add(Member("colorOverLifetime", Data(("enabled", Number(1)), ("color", Data(("mode", Number(1)),
            ("gradient", Data(("mode", Number(1)),
                ("colorKeys", Array(Data(("time", Number(0)), ("color", Vector(1, 0, 0, 1))), Data(("time", Number(.5f)), ("color", Vector(0, 1, 0, 1))))),
                ("alphaKeys", Array(Data(("time", Number(0)), ("alpha", Number(.25f))), Data(("time", Number(next)), ("alpha", Number(.75f))))))))))));
        var asset = new RimUnityPrefabTranslator().Translate("adjacent.gradient", Prefab(particles), _ => null, _ => null, out _);
        RimLumen.Core.Api.RimLumenApi.RegisterEffect(asset);
        ColorGradient gradient = asset.Nodes[0].Emitter!.ColorOverLifetime;
        Check.Equal(new Vector4(1, 0, 0, .25f), GradientEvaluator.Evaluate(gradient, 0), "Fixed initial value");
        Check.Equal(new Vector4(0, 1, 0, .25f), GradientEvaluator.Evaluate(gradient, .5f), "RGB source key retains current alpha");
        Check.Equal(new Vector4(0, 1, 0, .75f), GradientEvaluator.Evaluate(gradient, next), "adjacent alpha key retains current RGB");
        Check.Equal(new Vector4(0, 1, 0, .75f), GradientEvaluator.Evaluate(gradient, 1), "Fixed endpoint value");
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
            // D7: a particle born in this capture starts at age 0 and does not advance that frame.
            sim.CaptureMainThread(new LumenCaptureContext
            {
                FrameIndex = 0,
                DeltaTime = 0f,
                ViewportWidth = 1920,
                ViewportHeight = 1080,
                ViewMatrix = Matrix4x4.Identity,
                ProjectionMatrix = Matrix4x4.Identity,
                LumenRenderPassId = 0,
            });
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

    internal static void VortexRadial()
    {
        ForceFieldSpec cpu = TranslateVortex(32);
        ForceFieldSpec gpu = TranslateVortex(256);
        Check.Near(-3f, cpu.Parameters.X, "L1 radial outward acceleration is not divided by orbital strength");
        Check.Equal(cpu, gpu, "L1/L2 vortex fields");
        Check.Equal(Vector3.UnitY, cpu.CenterOrAxis, "vortex axis");
        Check.Near(4f, cpu.Strength, "orbital acceleration");
        Check.Near(0f, cpu.Radius, "no radial falloff");
        Check.Near(0f, cpu.Parameters.Y, "no axial lift");
        Console.WriteLine($"VORTEX L1==L2 radial={cpu.Parameters.X} orbital={cpu.Strength} radius={cpu.Radius} lift={cpu.Parameters.Y}");
    }

    private static ForceFieldSpec TranslateVortex(int max)
    {
        var particles = Particles(max);
        particles.members.Single(m => m.name == "shape").value = Data(("enabled", Number(1)), ("shapeType", Number(0)));
        particles.members.Add(Member("velocityOverLifetime", Data(("enabled", Number(1)), ("orbitalY", Constant(4)), ("radial", Constant(3)))));
        var asset = new RimUnityPrefabTranslator().Translate("vortex", Prefab(particles), _ => null, _ => null, out _);
        return asset.Nodes.Single().Emitter!.ForceFields.Single();
    }

    private static void AssertFallback(string module, ValueData value, string reason)
    {
        var particles = Particles();
        particles.members.Add(Member(module, value));
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

    internal static void LinearMaterial()
    {
        var particles = Particles();
        var prefab = Prefab(particles);
        prefab.nodes[0].components.Add(new ComponentData { id = "renderer", type = "RimUnity.ParticleSystemRenderer",
            members = [Member("sharedMaterial", new ValueData { kind = "asset", text = "linear" })] });
        var material = new ResourceData { type = "Material", colorSpace = "Linear", members = [] };
        var asset = new RimUnityPrefabTranslator().Translate("linear", prefab, _ => material, _ => null, out var report);
        Check.Reasons(["material.linearColorSpace"], report.Nodes.Single().Reasons, "Linear material reason");
        Check.Equal(LumenParticleTier.L0_ExternalCpu, report.Nodes.Single().Tier, "Linear material external");
        Check.True(asset.Nodes[0].Emitter is null && asset.Nodes[0].PointLight is null, "Linear owns particles and lights externally");
    }

    internal static void ParticleLights()
    {
        AssertFallback("lights", Data(("enabled", Number(1)), ("ratio", Number(1))), "lights.perParticle");
        AssertFallback("lights", Data(("enabled", Number(1)), ("ratio", Number(.5f))), "lights.perParticle");
        var particles = Particles();
        particles.members.Add(Member("lights", Data(("enabled", Number(0)), ("ratio", Number(1)))));
        var asset = new RimUnityPrefabTranslator().Translate("disabled.lights", Prefab(particles), _ => null, _ => null, out var report);
        Check.Equal(LumenParticleTier.L1_NativeCpu, report.Nodes.Single().Tier, "disabled lights don't force external");
        Check.True(asset.Nodes[0].Emitter is not null && asset.Nodes[0].PointLight is null, "disabled lights create no native light");
    }

    internal static void SceneColor()
    {
        EmitterSpec gamma = TranslateColor("Gamma");
        Check.Near(.2f, gamma.StartColor.X * gamma.HdrIntensity, "Gamma stored start times tint");
        Check.Near(.6f, gamma.StartColor.W, "Gamma alpha product");
        Check.Near(.5f, GradientEvaluator.Evaluate(gamma.ColorOverLifetime, .5f).X, "Gamma stored lifetime midpoint");
        Console.WriteLine($"COLOUR gammaStart={gamma.StartColor.X * gamma.HdrIntensity} lifetimeMidpoint={GradientEvaluator.Evaluate(gamma.ColorOverLifetime, .5f).X}");
    }

    private static EmitterSpec TranslateColor(string colorSpace)
    {
        var particles = Particles();
        particles.members.Single(m => m.name == "main").value.fields.Add(Member("startColor", Vector(.4f, .4f, .4f, .8f)));
        particles.members.Add(Member("colorOverLifetime", Data(("enabled", Number(1)), ("color", Data(("mode", Number(1)), ("gradient", Data(
            ("colorKeys", Array(Data(("time", Number(0)), ("color", Vector(.446f, .446f, .446f, 1))), Data(("time", Number(1)), ("color", Vector(.554f, .554f, .554f, 1))))),
            ("alphaKeys", Array(Data(("time", Number(0)), ("alpha", Number(1))))))))))));
        var prefab = Prefab(particles);
        prefab.nodes[0].components.Add(new ComponentData
        {
            id = "renderer", type = "RimUnity.ParticleSystemRenderer", members = [Member("sharedMaterial", new ValueData { kind = "asset", text = "material" })]
        });
        var material = new ResourceData { type = "Material", colorSpace = colorSpace, members = [Member("_Color", Vector(.5f, .5f, .5f, .75f))] };
        var asset = new RimUnityPrefabTranslator().Translate("color", prefab, _ => material, _ => null, out _);
        return asset.Nodes.Single().Emitter!;
    }

    internal static void Values()
    {
        Vector4 stored = new(0.5f, 0.04045f, 2, 0.25f);
        var normalized = RimUnityValueConversion.ExtractHdrIntensity(stored, out float hdr);
        Check.Near(2f, hdr, "HDR intensity");
        Check.Near(1, normalized.Z, "HDR normalized blue");
        Check.Near(0.5f, normalized.X * hdr, "HDR reconstruction");
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
        Check.Near(1f, middle.X, "HDR gradient half");
        Check.Near(.5f, middle.Y, "green half"); Check.Near(.25f, middle.Z, "blue half"); Check.Near(.6f, middle.W, "independent alpha keys");
        Check.Near(.5f, GradientEvaluator.Evaluate(colors, .25f).X, "HDR gradient quarter");
        Check.Near(2f, GradientEvaluator.Evaluate(colors, 1).X, "HDR end unclamped");
    }
}
