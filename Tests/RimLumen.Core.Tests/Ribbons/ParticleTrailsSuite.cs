using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Api;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Demo;
using RimLumen.Core.ParticlesGpu;
using RimLumen.Core.Rendering;
using RimLumen.Core.Ribbons;
using RimLumen.Core.Scene;
using RimLumen.Core.Tests.ParticlesGpu;
using RimLumen.Core.Tests.SceneAndLighting;

namespace RimLumen.Core.Tests.Ribbons;

[LumenTestSuite]
public static class ParticleTrailsSuite
{
    private const string PureJsonFirework = """
    {
      "effectId": "test.firework.trails",
      "defaultDuration": 3.2,
      "nodes": [
        {
          "name": "rocket",
          "spacePlane": "Vertical3D",
          "localPosition": [0, 0.35, 0],
          "emitter": {
            "preferredTier": "L1_NativeCpu",
            "maxParticles": 4,
            "duration": 1.0,
            "looping": false,
            "worldSpaceSimulation": true,
            "rateOverTime": 0,
            "bursts": [[0, 1]],
            "shape": "Point",
            "startLifetime": 1.0,
            "startSpeed": 19.3,
            "startSize": 0.16,
            "startColor": [1.0, 0.96, 0.82, 1.0],
            "hdrIntensity": 5.0,
            "forceFields": [
              { "kind": "GravityDrag", "centerOrAxis": [0, -1, 0], "parameters": [0, 0, 0], "radius": 0, "strength": 19.3 }
            ],
            "trails": {
              "ratio": 1.0,
              "lifetime": 0.65,
              "minVertexDistance": 0.06,
              "maxPoints": 64,
              "dieWithParticles": false,
              "worldSpace": true,
              "sizeAffectsWidth": true,
              "inheritParticleColor": true,
              "width": 0.9,
              "widthOverTrail": [[0, 1.0], [1, 0.0]],
              "colorOverTrail": [
                [0.0, [1.0, 0.96, 0.82, 1.0]],
                [0.35, [1.0, 0.70, 0.28, 0.85]],
                [1.0, [0.70, 0.16, 0.03, 0.0]]
              ],
              "hdrIntensity": 5.0,
              "blendMode": "Additive",
              "textureMode": "Stretch",
              "alignment": "View",
              "softParticleFadeDistance": 0.4
            },
            "subEmitters": [
              { "trigger": "Death", "targetNodeIndex": 1, "count": 16, "probability": 1.0 }
            ]
          }
        },
        {
          "name": "apex_burst",
          "spacePlane": "Vertical3D",
          "emitter": {
            "preferredTier": "L1_NativeCpu",
            "maxParticles": 64,
            "duration": 1.3,
            "looping": false,
            "worldSpaceSimulation": true,
            "rateOverTime": 0,
            "shape": "Sphere",
            "shapeExtents": [0.18, 0.18, 0.18],
            "startLifetime": 0.8,
            "startSpeedMin": 4.0,
            "startSpeedMax": 8.0,
            "startSize": 0.14,
            "startColor": [1, 0.7, 0.2, 1],
            "hdrIntensity": 8.0
          }
        }
      ]
    }
    """;

    [LumenFact]
    public static void PureJsonFireworkAssetParsesAndSimulatesRocketTrailAndDeathBurst()
    {
        EffectAsset asset = LumenJsonAssetLoader.Parse(PureJsonFirework);
        Check(asset.Nodes.Length == 2 && asset.Nodes[0].Emitter?.Trails is not null, "Parsed firework trail emitter.");
        var service = VfxTestData.Service(asset);
        using ILumenEffectHandle handle = service.Play(new LumenPlayRequest { EffectId = asset.EffectId, MapIndex = 1 });
        VfxTestData.Step(service, 1f / 60f, currentMapIndex: 1);
        float apexY = service.Simulator.LiveParticles[0].Position.Y;
        bool sawTrailVertices = false;
        for (int step = 0; step < 59; step++)
        {
            VfxTestData.Step(service, 1f / 60f, currentMapIndex: 1);
            Check(service.Simulator.LiveParticleCount == 1, $"Expected single rocket particle in flight (step={step}).");
            apexY = MathF.Max(apexY, service.Simulator.LiveParticles[0].Position.Y);
            if (service.Simulator.Vertices.Length > 4 && service.Simulator.SubBatches.Count >= 2)
                sawTrailVertices = true;
        }
        Check(apexY > 9.5f && apexY <= 10.1f, $"Rocket reaches ~10-cell apex parabolically (actual={apexY:F3}).");
        Check(sawTrailVertices, "Rocket emits particle trail vertices and trail sub-batch while rising.");

        // Step across 1.0 s: rocket dies, Death sub-emitter spawns apex burst near apexY, detached trail stays alive.
        VfxTestData.Step(service, 2f / 60f, currentMapIndex: 1);
        Check(service.Simulator.LiveParticleCount == 16, $"Death sub-emitter spawned 16 burst particles (actual={service.Simulator.LiveParticleCount}).");
        for (int p = 0; p < service.Simulator.LiveParticleCount; p++)
            Check(service.Simulator.LiveParticles[p].Position.Y > 9.0f, "Death burst children spawn near apex.");

        // Advance well past burst & detached trail lifetimes: handle drains cleanly and frees everything.
        for (int i = 0; i < 60; i++) VfxTestData.Step(service, 0.05f, currentMapIndex: 1);
        Check(!handle.IsAlive && !service.HasActiveWork && service.Simulator.ActiveEmitterCount == 0,
            "Firework rocket, burst, and detached trail drain and reclaim completely.");
    }

    [LumenFact]
    public static void UnsetTrailsAllocatesZeroExtraTrailState()
    {
        var spec = new EmitterSpec
        {
            Name = "no-trails", MaxParticles = 8, Looping = false, Duration = 2f,
            Bursts = new[] { new ParticleBurstSpec(0f, 3) }, StartLifetimeMin = 1f, StartLifetimeMax = 1f,
        };
        var cpu = new CpuParticleSimulator();
        int id = cpu.AcquireEmitter(spec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u, emitting: true);
        cpu.CaptureMainThread(CaptureFrame(0.1f));
        Check(cpu.LiveParticleCount == 3 && cpu.Vertices.Length == 12 && cpu.Indices.Length == 18 && cpu.SubBatches.Count == 1,
            "L1 without Trails emits strictly quad geometry with one sub-batch.");
        cpu.ReleaseEmitter(id, immediate: true);

        using var gpu = new LumenGpuParticleEngine(16);
        gpu.CreateInstance(spec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1, false, 1u);
        gpu.CaptureMainThread(CaptureFrame(0.1f));
        ParticleFrame frame = gpu.CapturedFrame!;
        Check(frame.TrailEmitters.Length == 0 && frame.TrailHeaderClears.Length == 0 && frame.TrailMapClears.Length == 0,
            "L2 without Trails emits zero trail frames or clear ranges.");
    }

    [LumenFact]
    public static void RibbonStripGeometryMiterAndFourTimesCapAndVertical3DAnchor()
    {
        // 1. Straight vertical direction: before/after = (0, 1) -> side = (1, 0), offset = halfWidth.
        Vector2 straight = RibbonStripGeometry.ComputeMiterOffset(new Vector2(0f, 1f), new Vector2(0f, 1f), 2f);
        Near(new Vector2(2f, 0f), straight, "Straight miter");

        // 2. 90-degree turn from +X to +Y: sideBefore=(0,-1), sideAfter=(1,0), miter=normalize(1,-1), dot=1/sqrt(2), scale=sqrt(2).
        Vector2 corner = RibbonStripGeometry.ComputeMiterOffset(new Vector2(1f, 0f), new Vector2(0f, 1f), 1f);
        Near(new Vector2(1f, -1f), corner, "90-degree bevel-less miter");

        // 3. Sharp ~180-degree hairpin reversal hits the exact 4x cap (dot < 0.25 -> clamped at 0.25).
        Vector2 hairpin = RibbonStripGeometry.ComputeMiterOffset(
            new Vector2(1f, 0.01f), new Vector2(-1f, 0.01f), 1.5f);
        Check(MathF.Abs(hairpin.Length() - 6f) < 1e-3f, $"Hairpin miter must cap strictly at 4x halfWidth (6.0), got {hairpin.Length():F4}.");

        // 4. Shared anchor vs per-point anchor in Vertical3D under vanilla top-down 2D camera.
        var topDown = new Matrix4x4(
            1f, 0f, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 0f, 1f);
        CameraBillboardBasis.Extract(topDown, out _, out Vector3 camUp, out float yScale);
        bool is2D = CameraBillboardBasis.IsStraightTopDown2D(Vector3.UnitX, camUp, yScale);
        Vector3 groundUp = CameraBillboardBasis.GroundUp(camUp);
        float moteY = Verse.Altitudes.AltitudeFor(Verse.AltitudeLayer.MoteOverhead);
        Vector3[] path = { new Vector3(5f, 4f, 10f), new Vector3(1f, 0f, 10f) };
        Vector3[] shared = new Vector3[2], perPoint = new Vector3[2];
        RibbonStripGeometry.ProjectCentres(path, shared, true, is2D, yScale, groundUp, moteY, new Vector2(1f, 10f));
        RibbonStripGeometry.ProjectCentres(path, perPoint, true, is2D, yScale, groundUp, moteY, null);
        // With shared anchor (1, 10): world delta X=4, Y=4 projects X=1+4=5, Z=10+2=12; with per-point anchor (5,10) delta X=0 so X stays 5, Z=12.
        // Now pitch XZ along groundUp=(0,1): with non-zero ground Z offset, shared anchor retains continuous ground offset.
        Vector3[] zPath = { new Vector3(1f, 4f, 14f), new Vector3(1f, 0f, 10f) };
        RibbonStripGeometry.ProjectCentres(zPath, shared, true, is2D, yScale, groundUp, moteY, new Vector2(1f, 10f));
        RibbonStripGeometry.ProjectCentres(zPath, perPoint, true, is2D, yScale, groundUp, moteY, null);
        Check(MathF.Abs(shared[0].Z - perPoint[0].Z) > 0.4f,
            "Shared particle anchor applies 60-degree pitch cosine compression consistently across particle trail.");
    }

    [LumenFact]
    public static void DieWithParticlesAndWorldSpaceAndRatioAndSizeAndColorInheritance()
    {
        // Case A: DieWithParticles=true drops trail same frame particle dies.
        var dieFast = new EmitterSpec
        {
            Name = "die-fast", PreferredTier = LumenParticleTier.L1_NativeCpu, MaxParticles = 4,
            Duration = 0.2f, Looping = false, Bursts = new[] { new ParticleBurstSpec(0f, 1) },
            StartSpeedMin = 5f, StartSpeedMax = 5f, StartLifetimeMin = 0.2f, StartLifetimeMax = 0.2f,
            Trails = new ParticleTrailSpec { DieWithParticles = true, Lifetime = 2f, MinVertexDistance = 0.05f },
        };
        var sim = new CpuParticleSimulator();
        int idFast = sim.AcquireEmitter(dieFast, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u);
        sim.CaptureMainThread(CaptureFrame(0.05f));
        sim.CaptureMainThread(CaptureFrame(0.05f));
        Check(sim.Vertices.Length > 4, "Living particle with DieWithParticles=true builds trail strip.");
        sim.CaptureMainThread(CaptureFrame(0.20f));
        Check(sim.LiveParticleCount == 0 && sim.Vertices.Length == 0 && !sim.HasActiveWork,
            "DieWithParticles=true clears trail immediately on particle death.");
        sim.ReleaseEmitter(idFast, immediate: true);

        // Case B: Ratio = 0 emits zero trails.
        var zeroRatio = new EmitterSpec
        {
            Name = "ratio0", PreferredTier = LumenParticleTier.L1_NativeCpu, MaxParticles = 8,
            Duration = 1f, Looping = false, Bursts = new[] { new ParticleBurstSpec(0f, 8) },
            StartSpeedMin = 4f, StartSpeedMax = 4f, StartLifetimeMin = 1f, StartLifetimeMax = 1f,
            Trails = new ParticleTrailSpec { Ratio = 0f, MinVertexDistance = 0.05f },
        };
        int idZero = sim.AcquireEmitter(zeroRatio, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u);
        sim.CaptureMainThread(CaptureFrame(0.1f));
        sim.CaptureMainThread(CaptureFrame(0.1f));
        Check(sim.Vertices.Length == 8 * 4 && sim.SubBatches.Count == 1, "Ratio=0 emits no trail vertices.");
        sim.ReleaseEmitter(idZero, immediate: true);

        // Case C: Local-space emitter with trails.WorldSpace=false translates tail with node vs true stays behind.
        var localTrailSpec = new EmitterSpec
        {
            Name = "local-ws-false", PreferredTier = LumenParticleTier.L1_NativeCpu, MaxParticles = 2,
            WorldSpaceSimulation = false, Duration = 2f, Looping = false,
            Bursts = new[] { new ParticleBurstSpec(0f, 1) },
            StartSpeedMin = 4f, StartSpeedMax = 4f, StartLifetimeMin = 1f, StartLifetimeMax = 1f,
            Trails = new ParticleTrailSpec { WorldSpace = false, MinVertexDistance = 0.05f, Width = 0.4f },
        };
        int idLocal = sim.AcquireEmitter(localTrailSpec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u);
        sim.CaptureMainThread(CaptureFrame(0.1f));
        sim.CaptureMainThread(CaptureFrame(0.1f));
        sim.SetEmitterTransform(idLocal, new LumenEmitterTransform(Matrix4x4.CreateTranslation(50f, 0f, 0f), Quaternion.Identity));
        sim.CaptureMainThread(CaptureFrame(0.05f));
        Check(sim.Vertices.Length > 4 && sim.Vertices[4].Position.X > 45f && sim.Vertices[^1].Position.X > 45f,
            "WorldSpace=false transforms the whole trail strip with node matrix.");
        sim.ReleaseEmitter(idLocal, immediate: true);

        // Case D: SizeAffectsWidth, SizeAffectsLifetime, and InheritParticleColor false vs true.
        var scaledSpec = new EmitterSpec
        {
            Name = "size-inherit", PreferredTier = LumenParticleTier.L1_NativeCpu, MaxParticles = 1,
            Duration = 1f, Looping = false, Bursts = new[] { new ParticleBurstSpec(0f, 1) },
            StartSpeedMin = 4f, StartSpeedMax = 4f, StartLifetimeMin = 0.5f, StartLifetimeMax = 0.5f,
            StartSizeMin = 3f, StartSizeMax = 3f, StartColor = new Vector4(0.2f, 0.4f, 0.6f, 0.5f),
            Trails = new ParticleTrailSpec
            {
                Lifetime = 0.4f, MinVertexDistance = 0.05f, Width = 0.5f,
                SizeAffectsWidth = true, SizeAffectsLifetime = true, InheritParticleColor = false,
                DieWithParticles = false,
            },
        };
        int idScaled = sim.AcquireEmitter(scaledSpec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u);
        sim.CaptureMainThread(CaptureFrame(0.1f));
        sim.CaptureMainThread(CaptureFrame(0.1f));
        float trailWidth = Vector3.Distance(sim.Vertices[4].Position, sim.Vertices[5].Position);
        Check(MathF.Abs(trailWidth - 1.5f) < 0.05f, $"SizeAffectsWidth=true scales 0.5*3=1.5 (got {trailWidth:F3}).");
        Check(Vector4.Distance(sim.Vertices[4].Color, Vector4.One) < 1e-4f, "InheritParticleColor=false keeps white trail vertex colour.");
        sim.CaptureMainThread(CaptureFrame(0.35f)); // t=0.55 > particle lifetime 0.5: detached point lifetime = 0.5 * 0.4 * 3 = 0.6s -> still alive!
        Check(sim.Vertices.Length > 0, "SizeAffectsLifetime=true multiplied trail lifetime by particle size 3 (0.6s) so detached trail survives past particle death.");
        sim.ReleaseEmitter(idScaled, immediate: true);
    }

    [LumenFact]
    public static void RendersMapFalseAndMapDisposeReleaseTrailsCleanly()
    {
        EffectAsset asset = LumenJsonAssetLoader.Parse(PureJsonFirework);
        var service = VfxTestData.Service(asset);
        ILumenEffectHandle map1 = service.Play(new LumenPlayRequest { EffectId = asset.EffectId, MapIndex = 1 });
        ILumenEffectHandle map2 = service.Play(new LumenPlayRequest { EffectId = asset.EffectId, MapIndex = 2 });
        VfxTestData.Step(service, 0.2f, currentMapIndex: 1);
        VfxTestData.Step(service, 0.2f, currentMapIndex: 1);
        Check(service.Simulator.Vertices.Length > 0 && service.Simulator.SubBatches.Count > 0, "Rendered map shows rocket trails.");

        // Non-map frame builds zero vertices/batches while detached/active timers keep advancing.
        service.CaptureMainThread(CaptureFrame(0.7f, frameIndex: 3, map: 1, rendersMap: false));
        service.Simulator.CaptureMainThread(CaptureFrame(0.7f, frameIndex: 3, map: 1, rendersMap: false));
        Check(service.Simulator.Vertices.IsEmpty && service.Simulator.Indices.IsEmpty && service.Simulator.SubBatches.Count == 0,
            "RendersMap=false produces zero L1 vertices or sub-batches.");
        Check(service.Simulator.LiveParticleCount > 0, "Simulation still stepped across RendersMap=false (rocket spawned Death burst).");

        service.ReleaseMap(1);
        Check(!map1.IsAlive && map2.IsAlive, "ReleaseMap disposes map 1 handle immediately while keeping map 2 alive.");
        VfxTestData.Step(service, 0.05f, currentMapIndex: 2);
        Check(service.Simulator.ActiveEmitterCount > 0 && service.Simulator.Vertices.Length > 0,
            "Map 2 firework and its trail continue rendering after map 1 release.");
        map2.Dispose();
    }

    [LumenFact]
    public static void ClaimTrailSlotUsesBank0OverflowProbeAndDeterministicSaturationRecycle()
    {
        bool[] active = new bool[12]; // P=4, BankCount=3 -> Bank 0 [0..3], Overflow [4..11]
        int slot0 = ParticleTrailMath.ClaimTrailSlot(active, 4, 3, localSlot: 1, out bool recycled);
        Check(slot0 == 1 && !recycled, "First claim takes private Bank 0 slot.");
        active[1] = true;

        int slot1 = ParticleTrailMath.ClaimTrailSlot(active, 4, 3, localSlot: 1, out recycled);
        Check(slot1 == 5 && !recycled, "Second claim takes own overflow Bank 1 slot.");
        active[5] = true;

        int slot2 = ParticleTrailMath.ClaimTrailSlot(active, 4, 3, localSlot: 1, out recycled);
        Check(slot2 == 9 && !recycled, "Third claim takes own overflow Bank 2 slot.");
        active[9] = true;

        // Fourth claim linearly probes overflow starting at 4 + ((1 + 1) % 8) = 6, NEVER touching Bank 0 slots (0, 2, 3)!
        int slot3 = ParticleTrailMath.ClaimTrailSlot(active, 4, 3, localSlot: 1, out recycled);
        Check(slot3 == 6 && !recycled, "Overflow probe borrows free overflow slot without touching peer Bank 0 slots.");

        // Saturate entire overflow [4..11] while leaving peer Bank 0 slots [0, 2, 3] false: must deterministically recycle localSlot=1.
        for (int i = 4; i < 12; i++) active[i] = true;
        int sat = ParticleTrailMath.ClaimTrailSlot(active, 4, 3, localSlot: 1, out recycled);
        Check(sat == 1 && recycled, "Overflow saturation deterministically recycles private Bank 0 slot.");
        Check(ParticleTrailMath.ComputeBankCount(new ParticleTrailSpec { DieWithParticles = false, Lifetime = 1e12f }) == ParticleTrailMath.MaxDetachedBanks,
            "Huge trail lifetime saturates cleanly at MaxDetachedBanks without int cast overflow.");
    }

    [LumenFact]
    public static void L2TrailPoolExhaustionRollsBackAndDrainWaitsForDetachedTrails()
    {
        using var engine = new LumenGpuParticleEngine(capacity: 16, trailHeaderCapacity: 8, trailPointCapacity: 64, trailMapCapacity: 16);
        var tooLarge = new EmitterSpec
        {
            Name = "too-large", PreferredTier = LumenParticleTier.L2_GpuCompute, MaxParticles = 8,
            Trails = new ParticleTrailSpec { DieWithParticles = false, Lifetime = 1f, MaxPoints = 16 }, // requires 16 headers > 8
        };
        bool threw = false;
        try { engine.CreateInstance(tooLarge, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1, false, 1u); }
        catch (ParticlePoolExhaustedException) { threw = true; }
        Check(threw, "Exhausting trail header pool throws ParticlePoolExhaustedException.");

        // Rollback check: the failed CreateInstance must have freed its particle Slots (8) so a full 16-particle non-trail succeeds!
        var nonTrailFull = new EmitterSpec { Name = "full-non-trail", PreferredTier = LumenParticleTier.L2_GpuCompute, MaxParticles = 16 };
        int fullId = engine.CreateInstance(nonTrailFull, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1, false, 1u);
        engine.DestroyInstance(fullId, immediate: true);
        engine.CaptureMainThread(CaptureFrame(0.016f, frameIndex: 1));

        // Drain extension & RendersMap=false L2 trail clear preservation:
        var detachedSpec = new EmitterSpec
        {
            Name = "l2-detach", PreferredTier = LumenParticleTier.L2_GpuCompute, MaxParticles = 2,
            StartLifetimeMin = 0.5f, StartLifetimeMax = 0.5f,
            Trails = new ParticleTrailSpec { DieWithParticles = false, Lifetime = 1.0f, MaxPoints = 16 },
        };
        int dId = engine.CreateInstance(detachedSpec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1, false, 2u);
        engine.CaptureMainThread(CaptureFrame(0.05f, frameIndex: 2));
        Check(engine.CapturedFrame!.TrailEmitters.Length == 1, "L2 frame carries TrailEmitters when enabled.");
        engine.DestroyInstance(dId, immediate: false);

        // Particle lifetime (0.5) + detached trail lifetime (0.5 * 1.0) = 1.0s: after 0.6s across RendersMap=false, instance stays alive;
        // after another 0.6s, instance drains and next rendered frame carries both particle clear and trail header/map clears.
        engine.CaptureMainThread(CaptureFrame(0.6f, frameIndex: 3, rendersMap: false));
        Check(engine.IsInstanceAlive(dId) && engine.CapturedFrame is null, "L2 draining trail stays alive past particle lifetime across RendersMap=false.");
        engine.CaptureMainThread(CaptureFrame(0.6f, frameIndex: 4, rendersMap: false));
        Check(!engine.IsInstanceAlive(dId), "L2 draining trail completes across RendersMap=false once particle + trail time elapses.");
        engine.CaptureMainThread(CaptureFrame(0.016f, frameIndex: 5, rendersMap: true));
        ParticleFrame finalFrame = engine.CapturedFrame!;
        Check(finalFrame.Clears.Length == 1 && finalFrame.TrailHeaderClears.Length == 1 && finalFrame.TrailMapClears.Length == 1,
            "Next rendered frame delivers particle and trail clear ranges released during RendersMap=false.");

        // Scaled WorldSpaceSimulation=true + SizeAffectsLifetime=true holds drain past unscaled duration (0.5 + 0.5*1*4 = 2.5s).
        var scaledWsSpec = new EmitterSpec
        {
            Name = "l2-ws-scaled", PreferredTier = LumenParticleTier.L2_GpuCompute, MaxParticles = 1,
            StartLifetimeMin = 0.5f, StartLifetimeMax = 0.5f,
            WorldSpaceSimulation = true,
            Trails = new ParticleTrailSpec { DieWithParticles = false, Lifetime = 1.0f, MaxPoints = 16, SizeAffectsLifetime = true },
        };
        int wsId = engine.CreateInstance(scaledWsSpec,
            new LumenEmitterTransform(Matrix4x4.CreateScale(4f), Quaternion.Identity), 1, false, 3u);
        engine.CaptureMainThread(CaptureFrame(0.05f, frameIndex: 6));
        engine.DestroyInstance(wsId, immediate: false);
        engine.CaptureMainThread(CaptureFrame(1.2f, frameIndex: 7));
        Check(engine.IsInstanceAlive(wsId), "WorldSpace scaled emitter keeps detached SizeAffectsLifetime trail alive past unscaled 1.0s.");
        engine.CaptureMainThread(CaptureFrame(1.5f, frameIndex: 8));
        Check(!engine.IsInstanceAlive(wsId), "WorldSpace scaled emitter finishes draining once full scaled lifetime elapses.");

        // SubEmitterInherit.Size multiplies child SizeAffectsLifetime drain past child's own unscaled duration.
        var childSpec = new EmitterSpec
        {
            Name = "l2-inherit-child", PreferredTier = LumenParticleTier.L2_GpuCompute, MaxParticles = 1,
            StartLifetimeMin = 0.5f, StartLifetimeMax = 0.5f, RateOverTime = 0f,
            WorldSpaceSimulation = true,
            Trails = new ParticleTrailSpec { DieWithParticles = false, Lifetime = 1.0f, MaxPoints = 16, SizeAffectsLifetime = true },
        };
        var parentSpec = new EmitterSpec
        {
            Name = "l2-inherit-parent", PreferredTier = LumenParticleTier.L2_GpuCompute, MaxParticles = 1,
            StartLifetimeMin = 0.2f, StartLifetimeMax = 0.2f, StartSizeMin = 4f, StartSizeMax = 4f,
            SubEmitters = new[] { new SubEmitterSpec(LumenSubEmitterTrigger.Death, 1, 1, 1f, LumenSubEmitterInherit.Size) },
        };
        int parentId = engine.CreateInstance(parentSpec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1, false, 4u);
        int childId = engine.CreateInstance(childSpec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1, false, 5u);
        engine.BindNodeInstances(new[] { parentId, childId });
        engine.DestroyInstance(parentId, immediate: false);
        engine.DestroyInstance(childId, immediate: false);
        engine.CaptureMainThread(CaptureFrame(0.3f, frameIndex: 9)); // Parent finishes drain timing; next frame removes it.
        engine.CaptureMainThread(CaptureFrame(0.016f, frameIndex: 10));
        Check(!engine.IsInstanceAlive(parentId) && engine.IsInstanceAlive(childId), "Parent removed while child stays alive.");
        engine.CaptureMainThread(CaptureFrame(1.2f, frameIndex: 11));
        Check(engine.IsInstanceAlive(childId), "SubEmitterInherit.Size keeps child trail alive past unscaled 1.0s.");
        engine.CaptureMainThread(CaptureFrame(1.5f, frameIndex: 12));
        Check(!engine.IsInstanceAlive(childId), "Child with SubEmitterInherit.Size drains cleanly once inherited trail expires.");
    }

    private static LumenCaptureContext CaptureFrame(float dt, ulong frameIndex = 1, int map = 1, bool rendersMap = true) => new()
    {
        CurrentMapIndex = map,
        RendersMap = rendersMap,
        FrameIndex = frameIndex,
        DeltaTime = dt,
        ViewportWidth = 640,
        ViewportHeight = 480,
        ViewMatrix = Matrix4x4.Identity,
        ProjectionMatrix = Matrix4x4.Identity,
        LumenRenderPassId = 0,
    };

    private static void Near(Vector2 expected, Vector2 actual, string label)
        => Check(Vector2.Distance(expected, actual) < 1e-4f, $"{label}: expected {expected}, got {actual}.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
