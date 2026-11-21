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
        foreach (string module in new[] { "subEmitters", "collision", "trigger" })
            AssertFallback(module, Data(("enabled", Number(1))), module);
        AssertFallback("trails", Data(("enabled", Number(1))), "trails.trailMaterial.null");
        var mesh = new ComponentData { id = "renderer", type = "RimUnity.ParticleSystemRenderer", members = [Member("renderMode", Number(4))] };
        var meshResult = classifier.Classify(small, mesh);
        Check.Equal(LumenParticleTier.External, meshResult.Tier, "mesh tier");
        Check.Reasons(["renderMode.Mesh"], meshResult.Reasons, "mesh reasons");
        AssertFallback("velocityOverLifetime", Data(("enabled", Number(1)), ("x", Constant(2))), "velocityOverLifetime.linear");
        AssertFallback("rotationOverLifetime", Data(("enabled", Number(1))), "rotationOverLifetime");
        AssertFallback("forceOverLifetime", Data(("enabled", Number(1)), ("y", Curve(0, 1))), "forceOverLifetime.curve");
        AssertFallback("lights", Data(("enabled", Number(1)), ("ratio", Number(0.5f))), "lights.perParticle");
        var probabilistic = Particles();
        probabilistic.members.RemoveAll(m => m.name == "emission");
        probabilistic.members.Add(Member("emission", Data(("enabled", Number(1)), ("bursts", Array(Data(("probability", Number(0.5f)), ("count", Constant(3))))))));
        Check.Reasons([], classifier.Classify(probabilistic, null).Reasons, "probability supported");
        var burstAsset = new RimUnityPrefabTranslator().Translate("probability", Prefab(probabilistic), _ => null, _ => null, out _);
        Check.Equal(.5f, burstAsset.Nodes[0].Emitter!.Bursts.Single().Probability, "probability preserved");
        RimLumen.Core.Api.RimLumenApi.RegisterEffect(burstAsset);
        var burstValue = probabilistic.members.Single(m => m.name == "emission").value.fields.Single(m => m.name == "bursts").value.items.Single();
        burstValue.fields.Add(Member("cycleCount", Number(0)));
        burstValue.fields.Add(Member("repeatInterval", Number(2)));
        var repeating = new RimUnityPrefabTranslator().Translate("repeating", Prefab(probabilistic), _ => null, _ => null, out _);
        Check.Equal(3, repeating.Nodes[0].Emitter!.Bursts.Length, "zero cycles repeats below duration");
        burstValue.fields.Single(m => m.name == "probability").value = Number(1.1f);
        Check.Reasons(["emission.bursts.probability.domain"], classifier.Classify(probabilistic, null).Reasons, "invalid probability external");
        burstValue.fields.Single(m => m.name == "probability").value = Number(.5f);
        burstValue.fields.Single(m => m.name == "count").value = Data(("mode", Number(3)), ("constantMin", Number(1)), ("constantMax", Number(3)));
        Check.Reasons(["emission.bursts.count.variable"], classifier.Classify(probabilistic, null).Reasons, "random burst count external");
        var combined = Particles();
        foreach (string module in new[] { "trails", "subEmitters", "collision", "trigger" }) combined.members.Add(Member(module, Data(("enabled", Number(1)))));
        Check.Reasons(["trails.trailMaterial.null", "subEmitters", "collision", "trigger", "renderMode.Mesh"], classifier.Classify(combined, mesh).Reasons, "all blockers retained");
        Console.WriteLine("MATRIX small32=L1 highCurlVortex=L2 peak200=L2; structural=5; conservative=6; all-blockers=5 reasons");
    }

    internal static void ActiveExportFields()
    {
        ValueData dual = Data(("mode", Number(2)), ("curveMultiplier", Number(1)),
            ("curveMin", Data(("keys", Array(Key(0, 0, 0), Key(1, 0, 0))))),
            ("curveMax", Data(("keys", Array(Key(0, 1, 0), Key(1, 1, 0))))));
        (string Field, ValueData Value, string[] Reasons)[] cases =
        [
            ("startSizeX", dual, []), ("startSizeY", dual, []), ("startSizeZ", dual, []),
            ("startRotationX", dual, []), ("startRotationY", dual, []), ("startRotationZ", dual, []),
            ("startRotation", Constant(0), []),
            ("startSize3D", Number(1), ["main.startSize3D"]),
            ("startRotation3D", Number(1), ["main.startRotation3D"]),
            ("startRotation", Constant(.25f), ["main.startRotation"])
        ];
        foreach (var test in cases)
        {
            var particles = Particles();
            particles.members.Single(m => m.name == "main").value.fields.Add(Member(test.Field, test.Value));
            var asset = new RimUnityPrefabTranslator().Translate("active." + test.Field, Prefab(particles), _ => null, _ => null, out var report);
            Check.Reasons(test.Reasons, report.Nodes.Single().Reasons, "active field reasons");
            Check.Equal(test.Reasons.Length == 0, asset.Nodes[0].Emitter is not null, "active field native ownership");
        }
        var disabled = Particles();
        disabled.members.Single(m => m.name == "emission").value = Data(("enabled", Number(0)), ("rateOverTime", dual));
        var inactiveAsset = new RimUnityPrefabTranslator().Translate("inactive.emission", Prefab(disabled), _ => null, _ => null, out var inactiveReport);
        Check.Reasons([], inactiveReport.Nodes.Single().Reasons, "disabled emission ignores serialized dual curve");
        Check.Near(0, inactiveAsset.Nodes[0].Emitter!.RateOverTime, "disabled emission rate");
        disabled.members.Single(m => m.name == "emission").value.fields.Single(m => m.name == "enabled").value = Number(1);
        new RimUnityPrefabTranslator().Translate("active.emission", Prefab(disabled), _ => null, _ => null, out var activeReport);
        Check.Reasons(["emission.rateOverTime.dualCurve"], activeReport.Nodes.Single().Reasons, "enabled emission retains dual reason");
    }

    internal static void DeferredWeightedExports()
    {
        ValueData key = Key(0, 1, 0);
        key.fields.Add(Member("weightedMode", Number(1)));
        ValueData dual = Data(("mode", Number(2)), ("curveMultiplier", Number(1)),
            ("curveMin", Data(("keys", Array(key)))), ("curveMax", Data(("keys", Array(key)))));
        foreach (float probability in new[] { 0f, .5f })
        {
            ValueData burst = Burst(0, 0, 1);
            burst.fields.Single(m => m.name == "count").value = dual;
            burst.fields.Add(Member("probability", Number(probability)));
            var asset = TranslateBursts(1, [burst], out var report);
            Check.Reasons(["emission.bursts.count.dualCurve"], report.Nodes.Single().Reasons, "weighted deferred count classified before conversion");
            Check.True(asset.Nodes[0].Emitter is null, "weighted count remains external without native evaluation");
        }
        foreach (string field in new[] { "startLifetime", "startSpeed", "startSize", "startRotation", "gravityModifier" })
        {
            var particles = Particles();
            var main = particles.members.Single(m => m.name == "main").value;
            main.fields.RemoveAll(m => m.name == field);
            main.fields.Add(Member(field, dual));
            var asset = new RimUnityPrefabTranslator().Translate("deferred." + field, Prefab(particles), _ => null, _ => null, out var report);
            Check.Reasons(["main." + field + ".dualCurve"], report.Nodes.Single().Reasons, "selected weighted dual curve never evaluated natively");
            Check.True(asset.Nodes[0].Emitter is null, "selected weighted curve external");
        }
        foreach (string module in new[] { "velocityOverLifetime", "forceOverLifetime", "noise" })
        {
            string field = module == "noise" ? "strength" : "x";
            var particles = Particles();
            particles.members.Add(Member(module, Data(("enabled", Number(1)), (field, dual))));
            var asset = new RimUnityPrefabTranslator().Translate("deferred." + module, Prefab(particles), _ => null, _ => null, out var report);
            Check.True(report.Nodes.Single().Reasons.Contains(module + "." + field + ".dualCurve"), "selected module dual reason retained");
            Check.True(asset.Nodes[0].Emitter is null, "selected module external without weighted evaluation");
        }
        foreach (string field in new[] { "rateOverTime", "rateOverDistance" })
        {
            foreach (bool enabled in new[] { false, true })
            {
                var particles = Particles();
                particles.members.Single(m => m.name == "emission").value = Data(("enabled", Number(enabled ? 1 : 0)), (field, dual));
                var asset = new RimUnityPrefabTranslator().Translate("deferred." + field, Prefab(particles), _ => null, _ => null, out var report);
                Check.Reasons(enabled ? ["emission." + field + ".dualCurve"] : [], report.Nodes.Single().Reasons, "distance/time active selection");
                Check.Equal(!enabled, asset.Nodes[0].Emitter is not null, "distance/time external ownership");
            }
        }
    }

    internal static void BoundedImportedBursts()
    {
        (float Duration, float Time, float Interval, int Cycles, float[] Times)[] schedules =
        [
            (1, 0, .25f, 0, [0, .25f, .5f, .75f]),
            (1, 0, .3f, 0, [0, .3f, .6f, .9f]),
            (1, .1f, .3f, 4, [.1f, .4f, .7f]),
            (1, 1, 0, 1, []),
            (1, 0, -2, 1, [0]),
            (1, 0, 0, 1, [0])
        ];
        foreach (var test in schedules)
        {
            EffectAsset asset = TranslateBursts(test.Duration, [Burst(test.Time, test.Interval, test.Cycles)], out var report);
            Check.Reasons([], report.Nodes.Single().Reasons, "bounded schedule native");
            Check.Equal(test.Times.Length, asset.Nodes[0].Emitter!.Bursts.Length, "strict endpoint count");
            for (int i = 0; i < test.Times.Length; i++)
                Check.Near(test.Times[i], asset.Nodes[0].Emitter!.Bursts[i].Time, "source occurrence phase");
            RimLumen.Core.Api.RimLumenApi.RegisterEffect(asset);
        }
        var loopAsset = TranslateBursts(1, [Burst(0, .3f, 0)], out _);
        var sim = new RimLumen.Core.Scene.CpuParticleSimulator();
        sim.AcquireEmitter(loopAsset.Nodes[0].Emitter!, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u);
        CaptureBurstFrame(sim, .95f);
        Check.Equal(4, sim.LiveParticleCount, "first nondividing loop");
        CaptureBurstFrame(sim, .1f);
        Check.Equal(5, sim.LiveParticleCount, "next loop restarts at zero phase");
        CaptureBurstFrame(sim, .9f);
        Check.Equal(8, sim.LiveParticleCount, "second nondividing loop");
        var accepted = TranslateBursts(4096, [Burst(0, 1, 2048), Burst(2048, 1, 2048)], out _);
        Check.Equal(4096, accepted.Nodes[0].Emitter!.Bursts.Length, "4096 total occurrences accepted");
        var over = TranslateBursts(4097, [Burst(0, 1, 2048), Burst(2048, 1, 2049)], out var overReport);
        Check.Reasons(["emission.bursts.occurrenceLimit"], overReport.Nodes.Single().Reasons, "4097 summed across definitions external");
        Check.True(over.Nodes[0].Emitter is null, "overlimit never truncates into native ownership");
        var zeroCycleOver = TranslateBursts(4097, [Burst(0, 1, 0)], out var zeroCycleReport);
        Check.Reasons(["emission.bursts.occurrenceLimit"], zeroCycleReport.Nodes.Single().Reasons, "zero cycles use same total bound");
        Check.True(zeroCycleOver.Nodes[0].Emitter is null, "zero-cycle overlimit external");
        foreach (var burst in new[] { Burst(0, 0, 0), Burst(0, float.PositiveInfinity, 1), Burst(0, float.NaN, 1), Burst(float.NaN, 1, 1), Burst(-1, 1, 1), Burst(2, 1, 1) })
        {
            var invalid = TranslateBursts(1, [burst], out var report);
            Check.Reasons(["emission.bursts.schedule.domain"], report.Nodes.Single().Reasons, "invalid schedule reason");
            Check.True(invalid.Nodes[0].Emitter is null, "invalid schedule external");
        }
    }

    private static ValueData Burst(float time, float interval, int cycles) => Data(("time", Number(time)),
        ("repeatInterval", Number(interval)), ("cycleCount", Number(cycles)), ("count", Constant(1)));

    private static EffectAsset TranslateBursts(float duration, ValueData[] bursts, out RimUnityTranslationReport report)
    {
        var particles = Particles(lifetime: 20f);
        particles.members.Single(m => m.name == "main").value.fields.Single(m => m.name == "duration").value = Number(duration);
        particles.members.Single(m => m.name == "emission").value = Data(("enabled", Number(1)), ("rateOverTime", Constant(0)), ("bursts", Array(bursts)));
        return new RimUnityPrefabTranslator().Translate("burst.schedule", Prefab(particles), _ => null, _ => null, out report);
    }

    private static void CaptureBurstFrame(RimLumen.Core.Scene.CpuParticleSimulator sim, float dt) => sim.CaptureMainThread(new LumenCaptureContext
    {
        FrameIndex = 0, DeltaTime = dt, ViewportWidth = 64, ViewportHeight = 64,
        ViewMatrix = Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity, LumenRenderPassId = 0,
    });

    internal static void RenderModeFallback()
    {
        var vertical = Prefab(Particles());
        vertical.nodes[0].components.Add(new ComponentData { id = "vertical", type = "RimUnity.ParticleSystemRenderer", members = [Member("renderMode", Number(3))] });
        var asset = new RimUnityPrefabTranslator().Translate("vertical", vertical, _ => null, _ => null, out _);
        Check.Equal(LumenBillboardMode.Vertical, asset.Nodes[0].Emitter!.BillboardMode, "vertical mode");
        RimLumen.Core.Api.RimLumenApi.RegisterEffect(asset);
        vertical.nodes[0].components[1].members.Add(Member("alignment", Number(2)));
        var precedence = new RimUnityPrefabTranslator().Translate("vertical.local", vertical, _ => null, _ => null, out _);
        Check.Equal(LumenBillboardMode.Vertical, precedence.Nodes[0].Emitter!.BillboardMode, "vertical takes precedence over local");
        vertical.nodes[0].components[1].members.Single(m => m.name == "renderMode").value = Number(0);
        var local = new RimUnityPrefabTranslator().Translate("local", vertical, _ => null, _ => null, out _);
        Check.Equal(LumenBillboardMode.Local, local.Nodes[0].Emitter!.BillboardMode, "local alignment on billboard");
        RimLumen.Core.Api.RimLumenApi.RegisterEffect(local);
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
        Check.Equal(LumenParticleTier.External, report.Nodes.Single().Tier, "unsupported render mode tier");
        Check.Reasons([reason], report.Nodes.Single().Reasons, "render mode reason");
        Check.True(asset.Nodes[0].Emitter is null && asset.Nodes[0].PointLight is null, "unsupported render mode leaves particles and lights external");
    }

    internal static void Shapes()
    {
        (int Max, ValueData Shape, string[] Reasons)[] cases =
        [
            (32, Data(("enabled", Number(1)), ("shapeType", Number(0)), ("radiusThickness", Number(1.1f))), ["shape.radiusThickness.domain"]),
            (32, Data(("enabled", Number(1)), ("shapeType", Number(0)), ("arcMode", Number(1))), ["shape.arcMode"]),
            (32, Data(("enabled", Number(1)), ("shapeType", Number(12)), ("radiusMode", Number(1))), ["shape.radiusMode"]),
            (32, Data(("enabled", Number(1)), ("shapeType", Number(0)), ("randomPositionAmount", Number(-.5f))), ["shape.randomPositionAmount.domain"]),
            (32, Data(("enabled", Number(1)), ("shapeType", Number(0)), ("alignToDirection", Number(1))), ["shape.alignToDirection"]),
        ];
        foreach (var test in cases)
        {
            var particles = Particles(test.Max);
            particles.members.Single(m => m.name == "shape").value = test.Shape;
                var asset = new RimUnityPrefabTranslator().Translate("shape", Prefab(particles), _ => null, _ => null, out var report);
            Check.Equal(LumenParticleTier.External, report.Nodes.Single().Tier, "unrepresentable shape");
            Check.Reasons(test.Reasons, report.Nodes.Single().Reasons, "exact shape reason");
            Check.True(asset.Nodes[0].Emitter is null && asset.Nodes[0].PointLight is null, "shape fallback external ownership");
        }
        (int Kind, LumenShapeKind Expected)[] supported =
        [
            (0, LumenShapeKind.Sphere), (1, LumenShapeKind.SphereShell), (2, LumenShapeKind.Hemisphere),
            (3, LumenShapeKind.HemisphereShell), (4, LumenShapeKind.Cone), (5, LumenShapeKind.Box),
            (7, LumenShapeKind.ConeShell), (8, LumenShapeKind.ConeVolume), (9, LumenShapeKind.ConeVolumeShell),
            (10, LumenShapeKind.CircleXZ), (11, LumenShapeKind.CircleEdgeXZ), (12, LumenShapeKind.SingleSidedEdge),
            (15, LumenShapeKind.BoxShell), (16, LumenShapeKind.BoxEdge), (17, LumenShapeKind.Donut), (18, LumenShapeKind.Rectangle)
        ];
        foreach (var test in supported)
        {
            var source = Particles();
            source.members.Single(m => m.name == "shape").value = Data(("enabled", Number(1)), ("shapeType", Number(test.Kind)), ("arc", Number(90)));
            var translated = new RimUnityPrefabTranslator().Translate("shape." + test.Kind, Prefab(source), _ => null, _ => null, out _);
            Check.Equal(test.Expected, translated.Nodes[0].Emitter!.Shape, "shape kind");
            Check.Near(90, translated.Nodes[0].Emitter!.ArcDegrees, "static arc");
            RimLumen.Core.Api.RimLumenApi.RegisterEffect(translated);
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
        Console.WriteLine($"SHAPES exact External cases={cases.Length}; representable CPU cone/box retained");
    }

    internal static void ShapeAxis()
    {
        AssertShapeDirection(new Vector3(0, 0, 0), Vector3.UnitZ);
        AssertShapeDirection(new Vector3(90, 0, 0), -Vector3.UnitY);
        var particles = Particles();
        particles.members.Single(m => m.name == "shape").value = Data(("enabled", Number(1)), ("shapeType", Number(10)), ("arc", Number(90)));
        var asset = new RimUnityPrefabTranslator().Translate("arc90", Prefab(particles), _ => null, _ => null, out _);
        var emitter = asset.Nodes[0].Emitter!;
        Check.True(Vector3.Distance(Vector3.UnitY, Vector3.Transform(-Vector3.UnitZ, emitter.ShapeRotation)) < 1e-6f,
            "native 90 degree arc endpoint maps to source +Y");
        RimLumen.Core.Api.RimLumenApi.RegisterEffect(asset);
        var sim = new RimLumen.Core.Scene.CpuParticleSimulator();
        int id = sim.AcquireEmitter(emitter, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u, emitting: false);
        sim.EmitBurst(id, 32);
        sim.CaptureMainThread(new LumenCaptureContext
        {
            FrameIndex = 0, DeltaTime = 0, ViewportWidth = 1920, ViewportHeight = 1080,
            ViewMatrix = Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity, LumenRenderPassId = 0,
        });
        var live = sim.LiveParticles.ToArray();
        Check.True(live.Length == 32, "circle burst admitted");
        Check.True(live.All(p => p.Position.X >= -1e-5f && p.Position.Y >= -1e-5f && MathF.Abs(p.Position.Z) < 1e-5f),
            "90 degree source arc occupies +X/+Y quadrant");
        Check.True(live.Any(p => p.Position.Y > .01f), "source arc reaches +Y");
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
            int emitterId = sim.AcquireEmitter(asset.Nodes.Single().Emitter!, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u, emitting: false);
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
        Check.Equal(LumenParticleTier.External, report.Nodes.Single().Tier, reason);
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
            Check.Equal(LumenParticleTier.External, node.Tier, "closure tier");
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
        AssertLinearFactors(1f, ".323865139 (white texture)");
        AssertLinearFactors(.5f, ".094312444 (sRGB texture .5)");
    }

    private static void AssertLinearFactors(float lifetime, string sourceFormulaReference)
    {
        var asset = TranslateMaterial("Linear", new Vector4(.5f, .5f, .5f, .75f), new Vector4(.4f, .4f, .4f, .8f),
            new Vector4(lifetime, lifetime, lifetime, 1), out var report);
        Check.Equal(LumenParticleTier.L1_NativeCpu, report.Nodes.Single().Tier, "supported Linear material stays native");
        Check.Reasons([], report.Nodes.Single().Reasons, "no temporary Linear fallback");
        var emitter = asset.Nodes[0].Emitter!;
        Check.Equal(LumenColorSpace.Linear, emitter.ColorSpace, "native Linear calculation selected");
        Check.Equal(new Vector4(.5f, .5f, .5f, .75f), emitter.MaterialTint, "material tint stays separate from vertex");
        Check.Near(.4f, emitter.StartColor.X * emitter.HdrIntensity, "stored start RGB is not decoded or tint-folded");
        Check.Near(.8f, emitter.StartColor.W, "stored start alpha not tint-folded");
        Check.Near(lifetime, GradientEvaluator.Evaluate(emitter.ColorOverLifetime, .5f).X, "lifetime RGB remains a vertex factor");
        Check.Equal("test-texture", emitter.TextureKey, "texture identity reaches native sampling metadata resolution");
        RimLumen.Core.Api.RimLumenApi.RegisterEffect(asset);
        Console.WriteLine($"LINEAR translated start=.4 lifetime={lifetime} tint=.5; pinned formula reference={sourceFormulaReference}; shader output not executed");
    }

    internal static void MaterialDomain()
    {
        AssertMaterialDomain(new Vector4(-.1f, 1, 1, 1));
        AssertMaterialDomain(new Vector4(float.NaN, 1, 1, 1));
        AssertMaterialDomain(new Vector4(1, float.PositiveInfinity, 1, 1));
        AssertMaterialDomain(new Vector4(1, 1, 1, -.1f));
        AssertMaterialDomain(new Vector4(2, 1.7f, .8f, 2));
        AssertMaterialDomain(new Vector4(1, 1, 1, float.NaN));
        var valid = TranslateMaterial("Linear", new Vector4(2, 1.7f, .8f, 1), Vector4.One, Vector4.One, out var report);
        Check.Equal(LumenParticleTier.L1_NativeCpu, report.Nodes.Single().Tier, "finite HDR tint is native");
        Check.Equal(new Vector4(2, 1.7f, .8f, 1), valid.Nodes[0].Emitter!.MaterialTint, "HDR tint is not clamped");
        RimLumen.Core.Api.RimLumenApi.RegisterEffect(valid);
    }

    private static void AssertMaterialDomain(Vector4 tint)
    {
        var asset = TranslateMaterial("Gamma", tint, Vector4.One, Vector4.One, out var report);
        Check.Equal(LumenParticleTier.External, report.Nodes.Single().Tier, "invalid material tint stays external");
        Check.Reasons(["material[test-material]._Color.domain"], report.Nodes.Single().Reasons, "reason names material identity and source field");
        Check.True(asset.Nodes[0].Emitter is null && asset.Nodes[0].PointLight is null, "invalid tint does not create native particles or light");
        RimLumen.Core.Api.RimLumenApi.RegisterEffect(asset);
    }

    private static EffectAsset TranslateMaterial(string colorSpace, Vector4 tint, Vector4 start, Vector4 lifetime, out RimUnityTranslationReport report)
    {
        var particles = Particles();
        particles.members.Single(m => m.name == "main").value.fields.Add(Member("startColor", Vector(start.X, start.Y, start.Z, start.W)));
        particles.members.Add(Member("colorOverLifetime", Data(("enabled", Number(1)), ("color", Vector(lifetime.X, lifetime.Y, lifetime.Z, lifetime.W)))));
        var prefab = Prefab(particles);
        prefab.nodes[0].components.Add(new ComponentData { id = "renderer", type = "RimUnity.ParticleSystemRenderer",
            members = [Member("sharedMaterial", new ValueData { kind = "asset", text = "test-material" })] });
        var material = new ResourceData { type = "Material", colorSpace = colorSpace, members = [
            Member("_Color", Vector(tint.X, tint.Y, tint.Z, tint.W)),
            Member("_MainTex", new ValueData { kind = "asset", text = "test-texture" })] };
        return new RimUnityPrefabTranslator().Translate("material." + colorSpace, prefab, _ => material, _ => null, out report);
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
        Check.Near(.4f, gamma.StartColor.X * gamma.HdrIntensity, "Gamma stored start before tint");
        Check.Near(.8f, gamma.StartColor.W, "Gamma stored alpha before tint");
        Check.Equal(new Vector4(.5f, .5f, .5f, .75f), gamma.MaterialTint, "Gamma material tint remains separate");
        Check.Near(.5f, GradientEvaluator.Evaluate(gamma.ColorOverLifetime, .5f).X, "Gamma stored lifetime midpoint");
        Check.Equal(LumenColorSpace.Stored, gamma.ColorSpace, "Gamma stays Stored");
        AssertStoredFactorIdentity("Gamma");
        AssertStoredFactorIdentity("existing-other");
    }

    private static void AssertStoredFactorIdentity(string colorSpace)
    {
        Vector4 start = new(2, .4f, 1, .8f), lifetime = new(4, 2, .5f, .5f), tint = new(.5f, 1.5f, 2, .75f);
        var asset = TranslateMaterial(colorSpace, tint, start, lifetime, out _);
        var emitter = asset.Nodes[0].Emitter!;
        Check.Equal(tint, emitter.MaterialTint, "Stored tint retained independently");
        Check.Equal(LumenColorSpace.Stored, emitter.ColorSpace, "non-Linear stays Stored");
        Vector4 vertex = emitter.StartColor * GradientEvaluator.Evaluate(emitter.ColorOverLifetime, .5f);
        vertex = new Vector4(vertex.X * emitter.HdrIntensity, vertex.Y * emitter.HdrIntensity, vertex.Z * emitter.HdrIntensity, vertex.W);
        Vector4 split = vertex * emitter.MaterialTint;
        Vector4 oldFolded = (start * tint) * lifetime;
        Check.True(Vector4.Distance(oldFolded, split) < 1e-6f, $"Stored split equals old folded: old={oldFolded}; split={split}");
        RimLumen.Core.Api.RimLumenApi.RegisterEffect(asset);
        Console.WriteLine($"STORED {colorSpace}: oldFolded={oldFolded} split={split} HDR={emitter.HdrIntensity}");
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

    internal static void TrailsNativeTranslation()
    {
        // Case 1: full supported PerParticle trail + trailMaterial with explicit curve/gradient/Linear material.
        var particles = Particles(max: 16, rate: 4, lifetime: 2f);
        particles.members.Add(Member("trails", Data(
            ("enabled", Number(1)),
            ("mode", Number(0)),
            ("ratio", Number(0.5f)),
            ("lifetime", Constant(0.75f)),
            ("minVertexDistance", Number(0f)), // 0 clamps to 1e-5f matching TrailRenderer
            ("dieWithParticles", Number(0)),
            ("sizeAffectsWidth", Number(0)),
            ("sizeAffectsLifetime", Number(1)),
            ("inheritParticleColor", Number(0)),
            ("generateLightingData", Number(0)),
            ("widthOverTrail", Data(("mode", Number(1)), ("curveMultiplier", Number(0.4f)),
                ("curve", Data(("keys", Array(Key(0, 1, 0), Key(1, 0, 0))))))),
            ("colorOverTrail", Vector(1, 0.8f, 0.4f, 0.9f)),
            ("colorOverLifetime", Vector(0.5f, 1, 0.5f, 0.6f)),
            ("textureMode", Number(1)))));
        var prefab = Prefab(particles);
        prefab.nodes[0].components.Add(new ComponentData
        {
            id = "renderer",
            type = "RimUnity.ParticleSystemRenderer",
            members = [Member("trailMaterial", new ValueData { kind = "asset", text = "trail-mat" })]
        });
        var material = new ResourceData
        {
            type = "Material",
            colorSpace = "Linear",
            members =
            [
                Member("shader", new ValueData { kind = "asset", text = "trail-shader" }),
                Member("_Color", Vector(2f, 0.9f, 0.7f, 0.8f)),
                Member("_MainTex", new ValueData { kind = "asset", text = "trail-tex" }),
                Member("_SoftParticlesNearFadeDistance", Number(0.25f))
            ]
        };
        var shader = new ShaderData { blend = true, dstColor = "One" };
        var asset = new RimUnityPrefabTranslator().Translate("trails.native", prefab,
            id => id == "trail-mat" ? material : null, _ => shader, out var report);
        Check.Equal(LumenParticleTier.L1_NativeCpu, report.Nodes.Single().Tier, "supported PerParticle trails translate natively");
        Check.Reasons([], report.Nodes.Single().Reasons, "native trails produce no fallback reasons");
        ParticleTrailSpec trails = asset.Nodes[0].Emitter!.Trails!;
        Check.Near(0.5f, trails.Ratio, "ratio");
        Check.Near(0.75f, trails.Lifetime, "lifetime");
        Check.Near(1e-5f, trails.MinVertexDistance, "minVertexDistance zero clamps to 1e-5");
        Check.Equal(false, trails.DieWithParticles, "dieWithParticles");
        // Decompiled RimUnity.dll TrailersModule.worldSpace defaults false when omitted:
        Check.Equal(false, trails.WorldSpace, "omitted RimUnity worldSpace defaults false");
        Check.Equal(false, trails.SizeAffectsWidth, "sizeAffectsWidth");
        Check.Equal(true, trails.SizeAffectsLifetime, "sizeAffectsLifetime");
        Check.Equal(false, trails.InheritParticleColor, "inheritParticleColor");
        Check.Near(1f, trails.Width, "curve widthOverTrail uses Width=1");
        Check.Near(0.4f, CurveEvaluator.Evaluate(trails.WidthOverTrail, 0f), "widthOverTrail start");
        Check.Equal(LumenRibbonTextureMode.Tile, trails.TextureMode, "textureMode");
        Check.Equal(LumenBlendMode.Additive, trails.BlendMode, "blendMode from trailMaterial shader");
        Check.Equal(LumenColorSpace.Linear, trails.ColorSpace, "Linear trail material retained");
        Check.Equal(new Vector4(2f, 0.9f, 0.7f, 0.8f), trails.MaterialTint, "materialTint from trailMaterial");
        Check.Equal("trail-tex", trails.TextureKey, "trail TextureKey");
        Check.Near(0.25f, trails.SoftParticleFadeDistance, "softFade");
        // MaxPoints = clamp(ceil(2 * 0.75 * 1 * 60) + 2, 16, 256) = 92
        Check.Equal(92, trails.MaxPoints, "derived MaxPoints");
        RimLumen.Core.Api.RimLumenApi.RegisterEffect(asset);

        // Case 2: high particle count routes PerParticle trails to L2_GpuCompute natively.
        var big = Particles(max: 256, rate: 40, lifetime: 1f);
        big.members.Add(Member("trails", Data(("enabled", Number(1)), ("worldSpace", Number(1)))));
        var bigPrefab = Prefab(big);
        bigPrefab.nodes[0].components.Add(new ComponentData
        {
            id = "renderer", type = "RimUnity.ParticleSystemRenderer",
            members = [Member("trailMaterial", new ValueData { kind = "asset", text = "trail-mat" })]
        });
        var bigAsset = new RimUnityPrefabTranslator().Translate("trails.l2", bigPrefab,
            id => id == "trail-mat" ? material : null, _ => shader, out var bigReport);
        Check.Equal(LumenParticleTier.L2_GpuCompute, bigReport.Nodes.Single().Tier, "high-count PerParticle trails route to L2");
        Check.True(bigAsset.Nodes[0].Emitter!.Trails!.WorldSpace, "explicit worldSpace=1 maps true");

        // Case 3: null-kind trail gradients default to white (1,1,1,1), and huge finite durations saturate MaxPoints to 256 without int overflow.
        var hugeNull = Particles(max: 32, rate: 8, lifetime: 1e10f);
        hugeNull.members.Add(Member("trails", Data(
            ("enabled", Number(1)),
            ("lifetime", Constant(1e10f)),
            ("colorOverTrail", new ValueData { kind = "null" }),
            ("colorOverLifetime", new ValueData { kind = "null" }))));
        var hugePrefab = Prefab(hugeNull);
        hugePrefab.nodes[0].components.Add(new ComponentData
        {
            id = "renderer", type = "RimUnity.ParticleSystemRenderer",
            members = [Member("trailMaterial", new ValueData { kind = "asset", text = "trail-mat" })]
        });
        var hugeAsset = new RimUnityPrefabTranslator().Translate("trails.hugeNull", hugePrefab,
            id => id == "trail-mat" ? material : null, _ => shader, out _);
        ParticleTrailSpec hugeTrails = hugeAsset.Nodes[0].Emitter!.Trails!;
        Check.Equal(Vector4.One, hugeTrails.ColorOverTrail.Stops.Single().Rgba, "null-kind colorOverTrail defaults to opaque white");
        Check.Equal(Vector4.One, hugeTrails.ColorOverLifetime.Stops.Single().Rgba, "null-kind colorOverLifetime defaults to opaque white");
        Check.Equal(256, hugeTrails.MaxPoints, "huge finite lifetime saturates MaxPoints at 256 without int overflow");
    }

    internal static void TrailsExternalFallbackReasons()
    {
        var trailMaterial = new ComponentData
        {
            id = "renderer", type = "RimUnity.ParticleSystemRenderer",
            members = [Member("trailMaterial", new ValueData { kind = "asset", text = "mat" })]
        };
        var okMat = new ResourceData { type = "Material", members = [Member("_Color", Vector(1, 1, 1, 1))] };

        void RejectTrail(ValueData trails, string expectedReason, ComponentData? rendererOverride = null)
        {
            var particles = Particles();
            particles.members.Add(Member("trails", trails));
            var prefab = Prefab(particles);
            if (rendererOverride is not null) prefab.nodes[0].components.Add(rendererOverride);
            var asset = new RimUnityPrefabTranslator().Translate("trails.reject", prefab, _ => okMat, _ => null, out var report);
            Check.Equal(LumenParticleTier.External, report.Nodes.Single().Tier, "unsupported trail must route External: " + expectedReason);
            Check.True(report.Nodes.Single().Reasons.Contains(expectedReason),
                $"Expected reason '{expectedReason}', got [{string.Join(", ", report.Nodes.Single().Reasons)}]");
            Check.True(asset.Nodes[0].Emitter is null, "External node must not emit native emitter");
        }

        RejectTrail(Data(("enabled", Number(1))), "trails.trailMaterial.null");
        RejectTrail(Data(("enabled", Number(1)), ("mode", Number(1))), "trails.mode.Ribbon", trailMaterial);
        RejectTrail(Data(("enabled", Number(1)), ("ratio", Number(1.2f))), "trails.ratio.domain", trailMaterial);
        RejectTrail(Data(("enabled", Number(1)), ("minVertexDistance", Number(-0.1f))), "trails.minVertexDistance.domain", trailMaterial);
        RejectTrail(Data(("enabled", Number(1)), ("lifetime", Data(("mode", Number(2))))), "trails.lifetime.dualCurve", trailMaterial);
        RejectTrail(Data(("enabled", Number(1)), ("lifetime", Data(("mode", Number(3))))), "trails.lifetime.variable", trailMaterial);
        RejectTrail(Data(("enabled", Number(1)), ("lifetime", Curve(1, 2))), "trails.lifetime.curve", trailMaterial);
        RejectTrail(Data(("enabled", Number(1)), ("lifetime", Constant(0))), "trails.lifetime.domain", trailMaterial);
        RejectTrail(Data(("enabled", Number(1)), ("widthOverTrail", Data(("mode", Number(2))))), "trails.widthOverTrail.dualCurve", trailMaterial);
        RejectTrail(Data(("enabled", Number(1)), ("widthOverTrail", Data(("mode", Number(3))))), "trails.widthOverTrail.variable", trailMaterial);
        var weightedKey = Data(("time", Number(0)), ("value", Number(1)), ("weightedMode", Number(1)));
        var weightedCurve = Data(("mode", Number(1)), ("curveMultiplier", Number(1)), ("curve", Data(("keys", Array(weightedKey)))));
        RejectTrail(Data(("enabled", Number(1)), ("widthOverTrail", weightedCurve)),
            "trails.widthOverTrail.weighted", trailMaterial);
        RejectTrail(Data(("enabled", Number(1)), ("colorOverLifetime", Data(("mode", Number(3))))), "trails.colorOverLifetime.random", trailMaterial);
        RejectTrail(Data(("enabled", Number(1)), ("colorOverTrail", Data(("mode", Number(1)), ("gradient", Data(("mode", Number(2))))))),
            "trails.colorOverTrail.perceptualBlend", trailMaterial);
        RejectTrail(Data(("enabled", Number(1)), ("textureMode", Number(2))), "trails.textureMode.2", trailMaterial);
        RejectTrail(Data(("enabled", Number(1)), ("generateLightingData", Number(1))), "trails.generateLightingData", trailMaterial);

        // Disabled trails with unsupported fields and no trailMaterial still stay native!
        var disabledParticles = Particles();
        disabledParticles.members.Add(Member("trails", Data(("enabled", Number(0)), ("mode", Number(1)), ("ratio", Number(9)))));
        new RimUnityPrefabTranslator().Translate("trails.disabled", Prefab(disabledParticles), _ => okMat, _ => null, out var disabledReport);
        Check.Equal(LumenParticleTier.L1_NativeCpu, disabledReport.Nodes.Single().Tier, "disabled trails module does not reject native");

        // Invalid trailMaterial._Color domain falls back External with material[id]._Color.domain even over L2 threshold.
        var bigP = Particles(max: 256);
        bigP.members.Add(Member("trails", Data(("enabled", Number(1)))));
        var badPrefab = Prefab(bigP);
        badPrefab.nodes[0].components.Add(new ComponentData
        {
            id = "renderer", type = "RimUnity.ParticleSystemRenderer",
            members = [Member("trailMaterial", new ValueData { kind = "asset", text = "bad-trail-mat" })]
        });
        var badMat = new ResourceData { type = "Material", members = [Member("_Color", Vector(1, 1, 1, 2))] };
        new RimUnityPrefabTranslator().Translate("trails.badmat", badPrefab, _ => badMat, _ => null, out var badReport);
        Check.Equal(LumenParticleTier.External, badReport.Nodes.Single().Tier, "bad trailMaterial tint forces External");
        Check.Reasons(["material[bad-trail-mat]._Color.domain"], badReport.Nodes.Single().Reasons, "bad trailMaterial removes L2 threshold reason");
    }
}
