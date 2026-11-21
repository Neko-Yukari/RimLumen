using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Xml;
using RimLumen.Core.Api;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.ParticleWorld;
using RimLumen.Core.ParticlesGpu;
using RimLumen.Core.Scene;
using RimLumen.Core.Tests.SceneAndLighting;

namespace RimLumen.Core.Tests.ParticleWorldPhysics;

[LumenTestSuite]
public static class EnvironmentInteractionSuite
{
    private static ulong nextFrame = 10_000;

    [LumenFact]
    public static void PublicExplosionMatchesVanillaHookAndLegacyMath()
    {
        const int mapId = 8101;
        var map = new Verse.Map { uniqueID = mapId };
        try
        {
            var spec = new EmitterSpec
            {
                Name = "probe",
                MaxParticles = 1,
                EnvironmentChannels = LumenEnvironmentChannel.Explosion,
                EnvironmentForceScale = 0.5f,
            };
            var cpu = new CpuParticleSimulator();
            using var gpu = new LumenGpuParticleEngine(4);
            var atSample = new LumenEmitterTransform(Matrix4x4.CreateTranslation(MathF.Sqrt(3f), 0f, 0f), Quaternion.Identity);
            int emitterId = cpu.AcquireEmitter(spec, atSample, 1u, mapIndex: mapId);
            gpu.CreateInstance(spec, atSample, mapId, realTime: false, emissionSeed: 1u);
            cpu.EmitBurst(emitterId, 1);
            cpu.CaptureMainThread(TopDownFrame(mapId, 0f));

            RimLumenApi.EmitExplosion(map, Vector3.Zero, radius: 4f);
            var step = TopDownFrame(mapId, 0.25f);
            cpu.CaptureMainThread(step);
            gpu.CaptureMainThread(step);

            ReadOnlySpan<LumenEnvironmentForce> frozen = LumenHostRegistry.GetEnvironmentForces(mapId, LumenEnvironmentChannel.Explosion);
            Check(frozen.Length == 1, $"Expected 1 frozen explosion force, got {frozen.Length}.");
            LumenEnvironmentForce legacy = ParticleEnvironmentMath.Explosion(Vector3.Zero, 4f, 0.25f);
            Check(frozen[0] == legacy, $"Public explosion {frozen[0]} differed from legacy {legacy}.");

            Vector3 expectedVel = legacy.Acceleration(new Vector3(MathF.Sqrt(3f), 0f, 0f)) * spec.EnvironmentForceScale * 0.25f;
            Close(expectedVel, cpu.LiveParticles[0].Velocity);

            GpuForceField gpuField = gpu.CapturedFrame!.Fields.Single();
            Close(legacy.CenterWorld, new Vector3(gpuField.CenterOrAxisAndKind.X, gpuField.CenterOrAxisAndKind.Y, gpuField.CenterOrAxisAndKind.Z));
            Near(5f, gpuField.CenterOrAxisAndKind.W);
            Near(legacy.WaveRadius, gpuField.ParametersAndRadius.X);
            Near(legacy.ShellHalfThickness, gpuField.ParametersAndRadius.Y);
            Near(legacy.Radius, gpuField.ParametersAndRadius.W);
            Near(legacy.Strength * spec.EnvironmentForceScale, gpuField.StrengthAndFrequency.X);

            string hooksSource = File.ReadAllText(Path.Combine(Repo(), "Source", "RimLumen.Core", "Environment", "ParticleEnvironmentHooks.cs"));
            Check(hooksSource.Contains("RimLumenApi.EmitExplosion(", StringComparison.Ordinal),
                "Vanilla ExplosionStarted hook must route through public RimLumenApi.EmitExplosion.");

            var afterExpiry = TopDownFrame(mapId, 0.35f);
            gpu.CaptureMainThread(afterExpiry);
            Check(LumenHostRegistry.GetEnvironmentForces(mapId, LumenEnvironmentChannel.Explosion).IsEmpty,
                "Explosion pulse must expire after 0.5 simulation seconds.");
        }
        finally
        {
            ParticleEnvironment.ForgetMap(mapId);
        }
    }

    [LumenFact]
    public static void CursorSourceRepelsPositiveAttractsNegativeAndMatchesAcrossL1AndL2()
    {
        const int mapId = 8102;
        var map = new Verse.Map { uniqueID = mapId };
        try
        {
            var spec = new EmitterSpec
            {
                Name = "smoke",
                MaxParticles = 2,
                StartLifetimeMin = 5f,
                StartLifetimeMax = 5f,
                EnvironmentChannels = LumenEnvironmentChannel.Cursor,
                EnvironmentForceScale = 1.5f,
            };
            var cpu = new CpuParticleSimulator();
            using var gpu = new LumenGpuParticleEngine(8);
            // Particle sits at (3, 12, 0): HorizontalRadial ignores Y=12; SphericalRadial includes Y.
            var elevated = new LumenEmitterTransform(Matrix4x4.CreateTranslation(3f, 12f, 0f), Quaternion.Identity);
            int repelEmitter = cpu.AcquireEmitter(spec, elevated, 1u, mapIndex: mapId);
            gpu.CreateInstance(spec, elevated, mapId, realTime: false, emissionSeed: 1u);
            cpu.EmitBurst(repelEmitter, 1);
            cpu.CaptureMainThread(TopDownFrame(mapId, 0f));

            using ILumenEnvironmentSource cursor = RimLumenApi.CreateCursor(map, Vector3.Zero, radius: 6f, strength: 8f);
            Check(cursor.IsAlive && cursor.MapIndex == mapId, "CreateCursor must return a live handle bound to mapId.");

            var repelFrame = TopDownFrame(mapId, 0.25f);
            cpu.CaptureMainThread(repelFrame);
            gpu.CaptureMainThread(repelFrame);

            // d_xz = 3, R = 6 -> w = 0.5 -> world accel = (+4, 0, 0) * scale 1.5 = (+6, 0, 0); dv = (+1.5, 0, 0).
            Close(new Vector3(1.5f, 0f, 0f), cpu.LiveParticles[0].Velocity);
            GpuForceField horizontalGpu = gpu.CapturedFrame!.Fields.Single();
            Near(6f, horizontalGpu.CenterOrAxisAndKind.W);
            Near(6f, horizontalGpu.ParametersAndRadius.W);
            Near(12f, horizontalGpu.StrengthAndFrequency.X);

            // Negative strength attracts inward: pull from (+1.5, 0, 0) with strength -8 at updated position.
            cursor.SetStrength(-8f);
            cursor.SetPosition(new Vector3(cpu.LiveParticles[0].Position.X + 3f, 0f, 0f));
            // Cursor is now 3 cells to the RIGHT (+X) of particle; negative strength pulls particle toward cursor (+X),
            // Wait: delta = particle - cursor = (-3, *, 0), delta/d = (-1, 0, 0), strength = -8 -> (-1)*(-8)*0.5*1.5 = +6!
            // Place cursor at particle.X - 3 instead so attraction pulls toward -X!
            cursor.SetPosition(new Vector3(cpu.LiveParticles[0].Position.X - 3f, 0f, 0f));
            var attractFrame = TopDownFrame(mapId, 0.5f);
            cpu.CaptureMainThread(attractFrame);
            gpu.CaptureMainThread(attractFrame);
            // Previous v = +1.5; accel = (-6, 0, 0) * 0.5s = -3.0 -> new v = (-1.5, 0, 0).
            Close(new Vector3(-1.5f, 0f, 0f), cpu.LiveParticles[0].Velocity);
            Near(-12f, gpu.CapturedFrame!.Fields.Single().StrengthAndFrequency.X);
            cursor.Dispose();
            Check(!cursor.IsAlive, "Disposed cursor source must report IsAlive == false.");

            // SphericalRadial 3D force parity (GPU kind 7u)
            using ILumenEnvironmentSource spherical = RimLumenApi.CreateEnvironmentSource(new LumenEnvironmentSourceRequest
            {
                MapIndex = mapId,
                Channel = LumenEnvironmentChannel.Cursor,
                Shape = LumenEnvironmentForceShape.SphericalRadial,
                InitialPositionWorld = new Vector3(0f, 0f, 0f),
                Radius = 10f,
                Strength = 10f,
                TriggersCursorEvents = false,
            });
            var sphericalCpu = new CpuParticleSimulator();
            int sphEmitter = sphericalCpu.AcquireEmitter(
                new EmitterSpec
                {
                    Name = "sph",
                    MaxParticles = 1,
                    StartLifetimeMin = 5f,
                    StartLifetimeMax = 5f,
                    EnvironmentChannels = LumenEnvironmentChannel.Cursor,
                },
                new LumenEmitterTransform(Matrix4x4.CreateTranslation(0f, 3f, 4f), Quaternion.Identity),
                2u,
                mapIndex: mapId);
            sphericalCpu.EmitBurst(sphEmitter, 1);
            sphericalCpu.CaptureMainThread(TopDownFrame(mapId, 0f));
            var sphStep = TopDownFrame(mapId, 0.5f);
            sphericalCpu.CaptureMainThread(sphStep);
            gpu.CaptureMainThread(sphStep);
            // d_xyz = 5, R = 10 -> w = 0.5 -> a = (0, 3/5, 4/5) * 5 = (0, 3, 4); dv over 0.5s = (0, 1.5, 2).
            Close(new Vector3(0f, 1.5f, 2f), sphericalCpu.LiveParticles[0].Velocity);
            GpuForceField sphGpu = gpu.CapturedFrame!.Fields.Single();
            Near(7f, sphGpu.CenterOrAxisAndKind.W);

            string shader = File.ReadAllText(Path.Combine(Repo(), "1.7", "Shaders", "Particles", "ParticleSim.comp.glsl"));
            Check(shader.Contains("if (kind == 6u) delta.y = 0.0;", StringComparison.Ordinal),
                "ParticleSim.comp.glsl must zero delta.y only for HorizontalRadial (kind 6u), keeping 3D delta for SphericalRadial (kind 7u).");
        }
        finally
        {
            ParticleEnvironment.ForgetMap(mapId);
        }
    }

    [LumenFact]
    public static void CursorInteractionEventsRespectThresholdsRisingEdgeCooldownAndStateFilter()
    {
        const int mapId = 8103;
        var map = new Verse.Map { uniqueID = mapId };
        const string json = """
        {
          "effectId": "env.spore",
          "defaultDuration": 2.0,
          "nodes": [
            { "name": "idle", "emitter": { "preferredTier": "L1_NativeCpu", "looping": true, "rateOverTime": 0 } },
            { "name": "agitated", "emitter": { "preferredTier": "L1_NativeCpu", "looping": false, "rateOverTime": 0 } },
            { "name": "puff", "emitter": { "preferredTier": "L1_NativeCpu", "rateOverTime": 0, "bursts": [[0, 5]], "startLifetime": 5 } }
          ],
          "states": [
            { "name": "calm", "nodes": ["idle"], "duration": 2.0, "loop": true },
            { "name": "agitated", "nodes": ["agitated"], "duration": 0.4, "next": "calm" }
          ],
          "initialState": "calm",
          "interactionEvents": [
            { "trigger": "CursorEnter", "eventName": "enter", "radius": 3.0, "targetNodeIndex": 2, "cooldownSeconds": 0.3, "notify": true },
            { "trigger": "CursorExit", "eventName": "exit", "radius": 3.0, "notify": true },
            { "trigger": "CursorStay", "eventName": "stay", "radius": 2.0, "maxCursorSpeed": 0.5, "holdSeconds": 0.2, "cooldownSeconds": 0.2, "state": "calm", "notify": true },
            { "trigger": "CursorSwipe", "eventName": "swipe", "radius": 2.5, "minCursorSpeed": 2.0, "state": "calm", "goToState": "agitated", "cooldownSeconds": 0.3, "notify": true }
          ]
        }
        """;

        try
        {
            EffectAsset asset = LumenJsonAssetLoader.Parse(json);
            EffectAsset xmlAsset = ParseXmlEffect("env.spore",
                nodesXml: new[]
                {
                    "<li><name>idle</name><emitter><preferredTier>L1_NativeCpu</preferredTier><looping>true</looping><rateOverTime>0</rateOverTime></emitter></li>",
                    "<li><name>agitated</name><emitter><preferredTier>L1_NativeCpu</preferredTier><looping>false</looping><rateOverTime>0</rateOverTime></emitter></li>",
                    "<li><name>puff</name><emitter><preferredTier>L1_NativeCpu</preferredTier><rateOverTime>0</rateOverTime><bursts><li><time>0</time><count>5</count></li></bursts><startLifetimeMin>5</startLifetimeMin><startLifetimeMax>5</startLifetimeMax></emitter></li>",
                },
                statesXml: new[]
                {
                    "<li><name>calm</name><nodes><li>idle</li></nodes><duration>2</duration><loop>true</loop></li>",
                    "<li><name>agitated</name><nodes><li>agitated</li></nodes><duration>0.4</duration><next>calm</next></li>",
                },
                initialState: "calm",
                interactionsXml: new[]
                {
                    "<li><trigger>CursorEnter</trigger><eventName>enter</eventName><radius>3</radius><targetNodeIndex>2</targetNodeIndex><cooldownSeconds>0.3</cooldownSeconds><notify>true</notify></li>",
                    "<li><trigger>CursorExit</trigger><eventName>exit</eventName><radius>3</radius><notify>true</notify></li>",
                    "<li><trigger>CursorStay</trigger><eventName>stay</eventName><radius>2</radius><maxCursorSpeed>0.5</maxCursorSpeed><holdSeconds>0.2</holdSeconds><cooldownSeconds>0.2</cooldownSeconds><state>calm</state><notify>true</notify></li>",
                    "<li><trigger>CursorSwipe</trigger><eventName>swipe</eventName><radius>2.5</radius><minCursorSpeed>2</minCursorSpeed><state>calm</state><goToState>agitated</goToState><cooldownSeconds>0.3</cooldownSeconds><notify>true</notify></li>",
                });
            Check(asset.InteractionEvents.Length == 4 && xmlAsset.InteractionEvents.Length == 4, "JSON and XML must both parse 4 interaction events.");

            var service = VfxTestData.Service(asset);
            using var handle = service.Play(new LumenPlayRequest
            {
                EffectId = asset.EffectId,
                MapIndex = mapId,
                OriginWorld = new Vector3(20f, 0f, 20f),
                InteractionEvents = new[]
                {
                    new InteractionEventSpec { Trigger = LumenInteractionTrigger.CursorEnter, EventName = "request_enter", Radius = 3f, Notify = true },
                },
            });
            var log = new List<string>();
            handle.StateChanged += s => log.Add("state:" + s);
            handle.Notify += e => log.Add("notify:" + e);

            // orthoSize = 10 -> full height = 20 cells.
            using ILumenEnvironmentSource cursor = RimLumenApi.CreateCursor(map, new Vector3(16f, 0f, 20f));
            StepService(service, mapId, 0.05f, orthoSize: 10f);
            Check(string.Join(",", log) == "state:calm", $"Unexpected initial events: {string.Join(",", log)}");
            log.Clear();

            // Move slowly inside radius 3 (16 -> 18.5: 2.5 cells / 20 = 0.125 vh in 0.05s -> inst 2.5, EMA 1.58 < 2.0):
            // Fires CursorEnter (+ puff 5 particles + request_enter), does NOT fire CursorSwipe.
            cursor.SetPosition(new Vector3(18.5f, 0f, 20f));
            StepService(service, mapId, 0.05f, orthoSize: 10f);
            Check(string.Join(",", log) == "notify:enter,notify:request_enter", $"Expected enter+request_enter, got: {string.Join(",", log)}");
            Check(service.Simulator.LiveParticleCount == 5, $"Expected 5 puff particles, got {service.Simulator.LiveParticleCount}.");
            log.Clear();

            // Hold motionless at (19, 0, 20) (d = 1 <= 2) until speed decays <= 0.5 and accumulates 0.2s hold:
            cursor.SetPosition(new Vector3(19f, 0f, 20f));
            int stayCount = 0;
            for (int i = 0; i < 8; i++)
            {
                StepService(service, mapId, 0.05f, orthoSize: 10f);
                if (log.Remove("notify:stay")) stayCount++;
            }
            Check(stayCount == 1 && log.Count == 0, $"Expected exactly 1 stay trigger during 0.4s dwell (with 0.2s cooldown), got {stayCount}, extra={string.Join(",", log)}.");

            // Exit then immediately re-enter within enter's 0.3s cooldown? Note 8 * 0.05s = 0.40s > 0.30s so enter cooldown expired.
            // Test exit + cooldown explicitly: exit to 16 -> fires exit; re-enter to 18.5 -> fires enter (sets 0.3s cooldown);
            // immediately exit to 16 and re-enter within 0.10s (< 0.3s) -> enter is suppressed by cooldown while request_enter (0s cooldown) fires!
            cursor.SetPosition(new Vector3(16f, 0f, 20f));
            StepService(service, mapId, 0.05f, orthoSize: 10f);
            Check(string.Join(",", log) == "notify:exit", $"Expected exit, got {string.Join(",", log)}");
            log.Clear();

            cursor.SetPosition(new Vector3(18.5f, 0f, 20f));
            StepService(service, mapId, 0.05f, orthoSize: 10f);
            Check(string.Join(",", log) == "notify:enter,notify:request_enter", $"Expected re-enter after cooldown, got {string.Join(",", log)}");
            log.Clear();

            cursor.SetPosition(new Vector3(16f, 0f, 20f));
            StepService(service, mapId, 0.05f, orthoSize: 10f);
            log.Clear();
            cursor.SetPosition(new Vector3(18.5f, 0f, 20f));
            StepService(service, mapId, 0.05f, orthoSize: 10f);
            Check(string.Join(",", log) == "notify:request_enter", $"Cooldown should block 'enter' but allow 'request_enter', got {string.Join(",", log)}");
            log.Clear();

            // Fast swipe across circle: 18.5 -> 21.5 (3 cells = 0.15 vh in 0.02s -> inst 7.5 vh/s -> EMA > 2.0 vh/s):
            // Fires StateChanged("agitated") THEN Notify("swipe") once!
            cursor.SetPosition(new Vector3(21.5f, 0f, 20f));
            StepService(service, mapId, 0.02f, orthoSize: 10f);
            Check(string.Join(",", log) == "state:agitated,notify:swipe", $"Swipe must switch state before notify, got {string.Join(",", log)}");
            log.Clear();

            // Continue fast swipe inside radius on next frame: blocked by rising-edge latch AND state filter ("calm")!
            cursor.SetPosition(new Vector3(19.5f, 0f, 20f));
            StepService(service, mapId, 0.02f, orthoSize: 10f);
            Check(log.Count == 0, $"Swipe must not re-fire during continuous pass or wrong state, got {string.Join(",", log)}");
        }
        finally
        {
            ParticleEnvironment.ForgetMap(mapId);
        }
    }

    [LumenFact]
    public static void ScreenCursorSpeedIsInvariantAcrossCameraZoomAndResolutions()
    {
        // 1. Pure math: 1/4 screen height in 0.05s (5.0 vh/s instantaneous) at 1080p vs 4K:
        Vector2 p0_1080 = CursorVelocityMath.ScreenPixelsToHeightUnits(new Vector2(400f, 200f), 1080);
        Vector2 p1_1080 = CursorVelocityMath.ScreenPixelsToHeightUnits(new Vector2(670f, 200f), 1080); // +270px = +0.25H
        Vector2 p0_4k = CursorVelocityMath.ScreenPixelsToHeightUnits(new Vector2(800f, 400f), 2160);
        Vector2 p1_4k = CursorVelocityMath.ScreenPixelsToHeightUnits(new Vector2(1340f, 400f), 2160); // +540px = +0.25H

        Vector2 vFirst = CursorVelocityMath.StepScreenVelocity(default, p0_1080, hasPrevious: false, default, 0.05f);
        Check(vFirst == Vector2.Zero, $"First sample must yield zero velocity, got {vFirst}.");

        Vector2 v1080 = CursorVelocityMath.StepScreenVelocity(p0_1080, p1_1080, hasPrevious: true, Vector2.Zero, 0.05f);
        Vector2 v4k = CursorVelocityMath.StepScreenVelocity(p0_4k, p1_4k, hasPrevious: true, Vector2.Zero, 0.05f);
        Close2(v1080, v4k);
        Near(5f * (1f - MathF.Exp(-1f)), v1080.X);

        // Long hitch (> 0.25s) resets to zero; zero step preserves velocity.
        Check(CursorVelocityMath.StepScreenVelocity(p0_1080, p1_1080, hasPrevious: true, v1080, 0f) == v1080,
            "Zero dt must preserve smoothed velocity.");
        Check(CursorVelocityMath.StepScreenVelocity(p0_1080, p1_1080, hasPrevious: true, v1080, 0.3f) == Vector2.Zero,
            "Hitch dt > 0.25s must reset velocity to zero.");

        // 2. Camera zoom invariance: moving 5 cells at orthoSize=10 (20 cells tall) vs 15 cells at orthoSize=30 (60 cells tall)
        Matrix4x4 vpZoomIn = TopDownViewProjection(centerX: 0f, centerZ: 0f, orthoSize: 10f, aspect: 16f / 9f);
        Matrix4x4 vpZoomOut = TopDownViewProjection(centerX: 0f, centerZ: 0f, orthoSize: 30f, aspect: 16f / 9f);

        Vector2 vZoomIn = CursorVelocityMath.StepWorldCursorVelocity(
            new Vector3(0f, 0f, 0f), new Vector3(5f, 0f, 0f), hasPrevious: true, Vector2.Zero,
            in vpZoomIn, 1920, 1080, 0.05f, out bool projIn);
        Vector2 vZoomOut = CursorVelocityMath.StepWorldCursorVelocity(
            new Vector3(0f, 0f, 0f), new Vector3(15f, 0f, 0f), hasPrevious: true, Vector2.Zero,
            in vpZoomOut, 3840, 2160, 0.05f, out bool projOut);
        Check(projIn && projOut, "Top-down orthographic points must project cleanly.");
        Close2(v1080, vZoomIn);
        Close2(vZoomIn, vZoomOut);

        // Stationary world cursor under camera WASD pan must have zero screen speed:
        Matrix4x4 vpPanned = TopDownViewProjection(centerX: 50f, centerZ: -30f, orthoSize: 10f, aspect: 16f / 9f);
        Vector2 vPan = CursorVelocityMath.StepWorldCursorVelocity(
            new Vector3(10f, 0f, 10f), new Vector3(10f, 0f, 10f), hasPrevious: true, Vector2.Zero,
            in vpPanned, 1920, 1080, 0.05f, out _);
        Check(vPan == Vector2.Zero, $"Camera pan alone must not induce world-cursor velocity, got {vPan}.");

        // 3. Off-map cursor (MapIndex != CurrentMapIndex) reports Speed = 0 while retaining force:
        const int renderedMapId = 8104, backgroundMapId = 8105;
        try
        {
            using ILumenEnvironmentSource bgCursor = RimLumenApi.CreateCursor(
                new Verse.Map { uniqueID = backgroundMapId }, new Vector3(0f, 0f, 0f), radius: 5f, strength: 6f);
            ParticleEnvironment.BeginFrame(TopDownFrame(renderedMapId, 0.05f));
            bgCursor.SetPosition(new Vector3(10f, 0f, 0f));
            ParticleEnvironment.BeginFrame(TopDownFrame(renderedMapId, 0.05f));

            ReadOnlySpan<LumenCursorSnapshot> bgCursors = LumenHostRegistry.GetCursors(backgroundMapId);
            Check(bgCursors.Length == 1 && bgCursors[0].Speed == 0f, "Cursor on a non-rendered map must report Speed == 0.");
            Check(LumenHostRegistry.GetEnvironmentForces(backgroundMapId, LumenEnvironmentChannel.Cursor).Length == 1,
                "Cursor force on a background map must remain active for background simulation.");
        }
        finally
        {
            ParticleEnvironment.ForgetMap(renderedMapId);
            ParticleEnvironment.ForgetMap(backgroundMapId);
        }
    }

    [LumenFact]
    public static void ExplosionInteractionEventFiresOnShockwaveArrivalAndFirstFrameWhenNear()
    {
        const int mapId = 8106;
        var map = new Verse.Map { uniqueID = mapId };
        try
        {
            var asset = new EffectAsset
            {
                EffectId = "env.blast-sensor",
                DefaultDuration = 5f,
                Nodes = new[] { new EffectNodeSpec { Name = "root", Emitter = VfxTestData.Emitter(looping: true, rate: 0f, tier: LumenParticleTier.L1_NativeCpu) } },
                InteractionEvents = new[]
                {
                    new InteractionEventSpec
                    {
                        Trigger = LumenInteractionTrigger.Explosion,
                        EventName = "hit",
                        Radius = 1f,
                        MinExplosionStrength = 40f,
                        Notify = true,
                    },
                },
            };
            var service = VfxTestData.Service(asset);
            using var near = service.Play(new LumenPlayRequest { EffectId = asset.EffectId, MapIndex = mapId, OriginWorld = new Vector3(20f, 0f, 20f) });
            using var far = service.Play(new LumenPlayRequest { EffectId = asset.EffectId, MapIndex = mapId, OriginWorld = new Vector3(26f, 0f, 20f) });
            int nearHits = 0, farHits = 0;
            near.Notify += _ => nearHits++;
            far.Notify += _ => farHits++;

            // Weak explosion (radius 1 -> strength 24 < 40) at (20, 0, 20): filtered out by MinExplosionStrength!
            RimLumenApi.EmitExplosion(map, new Vector3(20f, 0f, 20f), radius: 1f);
            StepService(service, mapId, 0.05f, orthoSize: 10f);
            Check(nearHits == 0 && farHits == 0, "Weak explosion below MinExplosionStrength must not trigger.");

            // Radius 6 explosion at (20, 0, 20): strength = 144 >= 40, wave speed = 12 cells/s, halfThickness = 0.75.
            // Near is at d_xz = 0 -> triggers on Frame 1!
            // Far is at d_xz = 6.0 (d_xz - rule.Radius = 5.0):
            //   Frames 1..7 (t = 0.05..0.35s): r_w + 0.75 <= 4.20 + 0.75 = 4.95 < 5.0 -> not yet!
            //   Frame 8 (t = 0.40s): r_w + 0.75 = 4.80 + 0.75 = 5.55 >= 5.0 -> triggers on Frame 8!
            RimLumenApi.EmitExplosion(map, new Vector3(20f, 0f, 20f), radius: 6f);
            StepService(service, mapId, 0.05f, orthoSize: 10f);
            Check(nearHits == 1 && farHits == 0, $"Expected near=1, far=0 on frame 1; got near={nearHits}, far={farHits}.");

            for (int f = 2; f <= 7; f++)
            {
                StepService(service, mapId, 0.05f, orthoSize: 10f);
                Check(nearHits == 1 && farHits == 0, $"Shockwave arrived early on frame {f}: near={nearHits}, far={farHits}.");
            }

            // Also emit a second nearby explosion at (26, 0, 20) BEFORE Frame 7? Wait: test out-of-order on a separate pair below;
            // here verify Frame 8 triggers farHits == 1 and subsequent frames do not re-trigger!
            StepService(service, mapId, 0.05f, orthoSize: 10f);
            Check(nearHits == 1 && farHits == 1, $"Shockwave must trigger far effect on frame 8: near={nearHits}, far={farHits}.");
            StepService(service, mapId, 0.05f, orthoSize: 10f);
            StepService(service, mapId, 0.05f, orthoSize: 10f);
            Check(nearHits == 1 && farHits == 1, "Same explosion pulse must never trigger twice.");

            // Out-of-order arrival: distant explosion emitted FIRST, close explosion emitted SECOND -> both must fire!
            using var target = service.Play(new LumenPlayRequest { EffectId = asset.EffectId, MapIndex = mapId, OriginWorld = new Vector3(50f, 0f, 50f) });
            int targetHits = 0;
            target.Notify += _ => targetHits++;
            RimLumenApi.EmitExplosion(map, new Vector3(56f, 0f, 50f), radius: 6f); // arrives at t = 0.40s
            StepService(service, mapId, 0.05f, orthoSize: 10f);
            RimLumenApi.EmitExplosion(map, new Vector3(50f, 0f, 50f), radius: 2f); // strength 48 >= 40, arrives immediately!
            StepService(service, mapId, 0.05f, orthoSize: 10f);
            Check(targetHits == 1, $"Close second explosion must trigger immediately; got {targetHits}.");
            for (int i = 0; i < 7; i++)
                StepService(service, mapId, 0.05f, orthoSize: 10f);
            Check(targetHits == 2, $"Earlier distant explosion must still trigger when its wave arrives later; got {targetHits}.");
        }
        finally
        {
            ParticleEnvironment.ForgetMap(mapId);
        }
    }

    [LumenFact]
    public static void ForgetMapReleasesOnlyTargetMapPulsesSustainedSourcesAndCursors()
    {
        const int mapA = 8107, mapB = 8108;
        var objA = new Verse.Map { uniqueID = mapA };
        var objB = new Verse.Map { uniqueID = mapB };
        try
        {
            RimLumenApi.EmitExplosion(objA, Vector3.Zero, radius: 4f);
            RimLumenApi.EmitExplosion(objB, Vector3.Zero, radius: 5f);
            using ILumenEnvironmentSource cursorA = RimLumenApi.CreateCursor(objA, new Vector3(1f, 0f, 1f), radius: 4f, strength: 6f);
            using ILumenEnvironmentSource cursorB = RimLumenApi.CreateCursor(objB, new Vector3(2f, 0f, 2f), radius: 4f, strength: 6f);

            ParticleEnvironment.BeginFrame(TopDownFrame(mapA, 0.05f));
            Check(cursorA.IsAlive && cursorB.IsAlive, "Both sources must start alive.");
            Check(LumenHostRegistry.GetEnvironmentForces(mapA, LumenEnvironmentChannel.Explosion | LumenEnvironmentChannel.Cursor).Length == 2, "Map A must have 2 forces.");
            Check(LumenHostRegistry.GetEnvironmentForces(mapB, LumenEnvironmentChannel.Explosion | LumenEnvironmentChannel.Cursor).Length == 2, "Map B must have 2 forces.");

            ParticleCollisionFields.Forget(mapA);

            Check(!cursorA.IsAlive && cursorB.IsAlive, "Forget(mapA) must kill mapA's source and keep mapB's source alive.");
            Check(LumenHostRegistry.GetEnvironmentForces(mapA, LumenEnvironmentChannel.Explosion | LumenEnvironmentChannel.Cursor).IsEmpty, "Map A forces must be cleared immediately.");
            Check(LumenHostRegistry.GetCursors(mapA).IsEmpty, "Map A cursors must be cleared immediately.");

            ParticleEnvironment.BeginFrame(TopDownFrame(mapB, 0.05f));
            Check(LumenHostRegistry.GetEnvironmentForces(mapB, LumenEnvironmentChannel.Explosion | LumenEnvironmentChannel.Cursor).Length == 2, "Map B forces must survive mapA disposal.");
            Check(LumenHostRegistry.GetCursors(mapB).Length == 1, "Map B cursor must survive mapA disposal.");
        }
        finally
        {
            ParticleEnvironment.ForgetMap(mapA);
            ParticleEnvironment.ForgetMap(mapB);
        }
    }

    [LumenFact]
    public static void BoundaryValidationAndZeroIdleOverhead()
    {
        const int mapId = 8109;
        var map = new Verse.Map { uniqueID = mapId };
        try
        {
            // Idle gate: no mouse force leases, no cursor-event handles -> NeedsHostMouse is false!
            Check(!ParticleEnvironment.NeedsHostMouse(mapId), "Idle map must not read host mouse.");
            RimLumenApi.EmitExplosion(map, Vector3.Zero, radius: 4f);
            Check(!ParticleEnvironment.NeedsHostMouse(mapId), "Explosion alone without cursor subscribers must not read host mouse.");

            using (ILumenEnvironmentSource mouseLease = RimLumenApi.CreateMouseForce(map, radius: 5f, strength: 4f))
            {
                Check(mouseLease.IsAlive && ParticleEnvironment.NeedsHostMouse(mapId),
                    "FollowHostMouse lease must enable host mouse sampling on its map.");
                Check(!ParticleEnvironment.NeedsHostMouse(mapId + 1),
                    "FollowHostMouse lease on mapId must not enable host mouse sampling on another map.");
                Throws<InvalidOperationException>(() => mouseLease.SetPosition(Vector3.One));
            }
            Check(!ParticleEnvironment.NeedsHostMouse(mapId), "Disposing mouse lease must disable host mouse sampling immediately.");

            // Pure JSON cursor-event asset enables host mouse only while playing, and disables on Stop/Dispose:
            EffectAsset hoverAsset = LumenJsonAssetLoader.Parse("""
                {
                  "effectId": "env.hover-mouse-gate",
                  "nodes": [{ "name": "n", "emitter": { "preferredTier": "L1_NativeCpu", "looping": true, "rateOverTime": 0 } }],
                  "interactionEvents": [{ "trigger": "CursorEnter", "eventName": "in", "radius": 2.0, "notify": true }]
                }
                """);
            var service = VfxTestData.Service(hoverAsset);
            using (ILumenEffectHandle h = service.Play(new LumenPlayRequest { EffectId = hoverAsset.EffectId, MapIndex = mapId }))
            {
                Check(ParticleEnvironment.NeedsHostMouse(mapId), "Playing handle with Cursor* event must enable host mouse sampling.");
                // Feed host mouse via inner CaptureFrame seam: pure JSON asset receives CursorEnter without C# CreateCursor!
                int entered = 0;
                h.Notify += _ => entered++;
                ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.05f), null, null,
                    new HostMouseSample(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f)));
                StepServiceAlreadyCaptured(service, mapId, 0.05f);
                Check(entered == 1, $"Host mouse sample must drive pure-JSON CursorEnter without C# CreateCursor; got {entered}.");
            }
            Check(!ParticleEnvironment.NeedsHostMouse(mapId), "Disposed handle must release its cursor-event subscription.");

            // Capacity cap: MaxSustainedSourcesPerMap = 8, 9th returns inert source!
            var leases = new List<ILumenEnvironmentSource>();
            for (int i = 0; i < ParticleEnvironment.MaxSustainedSourcesPerMap; i++)
            {
                ILumenEnvironmentSource s = RimLumenApi.CreateCursor(map, Vector3.Zero, radius: 2f, strength: 1f);
                Check(s.IsAlive, $"Source {i} within cap must be alive.");
                leases.Add(s);
            }
            ILumenEnvironmentSource ninth = RimLumenApi.CreateCursor(map, Vector3.Zero, radius: 2f, strength: 1f);
            Check(!ninth.IsAlive, "9th sustained source on the same map must return an inert handle.");
            foreach (var s in leases) s.Dispose();

            // Validation rejections across C# API and JSON/XML authoring boundaries:
            Throws<ArgumentOutOfRangeException>(() => RimLumenApi.EmitExplosion(map, Vector3.Zero, radius: 0f));
            Throws<ArgumentOutOfRangeException>(() => RimLumenApi.EmitExplosion(map, new Vector3(float.NaN, 0f, 0f), radius: 2f));
            Throws<ArgumentException>(() => RimLumenApi.EmitEnvironmentPulse(new LumenEnvironmentPulseRequest
            {
                MapIndex = mapId, Channel = LumenEnvironmentChannel.None, CenterWorld = Vector3.Zero,
                Radius = 2f, Strength = 4f, Duration = 0.5f, ShellHalfThickness = 0.2f,
            }));
            Throws<ArgumentException>(() => RimLumenApi.EmitEnvironmentPulse(new LumenEnvironmentPulseRequest
            {
                MapIndex = mapId, Channel = LumenEnvironmentChannel.Cursor, Shape = LumenEnvironmentForceShape.HorizontalRadial,
                CenterWorld = Vector3.Zero, Radius = 2f, Strength = 4f, Duration = 0.5f, ShellHalfThickness = 0.2f,
            }));
            Throws<ArgumentException>(() => RimLumenApi.CreateEnvironmentSource(new LumenEnvironmentSourceRequest
            {
                MapIndex = mapId, Shape = LumenEnvironmentForceShape.SphericalWave, Radius = 2f, Strength = 4f,
            }));

            string[] badJsons =
            {
                """{ "effectId": "bad", "nodes": [{ "name": "n" }], "interactionEvents": [{ "trigger": "CursorEnter", "eventName": "e", "unknownKey": 1, "radius": 2 }] }""",
                """{ "effectId": "bad", "nodes": [{ "name": "n" }], "interactionEvents": [{ "trigger": "CursorEnter", "eventName": "e", "radius": 0 }] }""",
                """{ "effectId": "bad", "nodes": [{ "name": "n" }], "interactionEvents": [{ "trigger": "CursorSwipe", "eventName": "e", "radius": 2, "minCursorSpeed": 0 }] }""",
                """{ "effectId": "bad", "nodes": [{ "name": "n" }], "interactionEvents": [{ "trigger": "CursorEnter", "eventName": "e", "radius": 2, "holdSeconds": 0.5 }] }""",
                """{ "effectId": "bad", "nodes": [{ "name": "n" }], "interactionEvents": [{ "trigger": "Explosion", "eventName": "e", "minCursorSpeed": 1 }] }""",
                """{ "effectId": "bad", "nodes": [{ "name": "n" }], "interactionEvents": [{ "trigger": "CursorEnter", "eventName": "e", "radius": 2, "goToState": "missing" }] }""",
            };
            foreach (string bad in badJsons)
                Throws<InvalidDataException>(() => LumenJsonAssetLoader.Parse(bad));

            Throws<ArgumentException>(() => service.Play(new LumenPlayRequest
            {
                EffectId = hoverAsset.EffectId,
                MapIndex = mapId,
                InteractionEvents = new[] { new InteractionEventSpec { Trigger = LumenInteractionTrigger.CursorSwipe, EventName = "bad", Radius = 2f, MinCursorSpeed = 0f } },
            }));

            // Package-qualified scanner alias preserves InteractionEvents:
            string tempDir = Path.Combine(Path.GetTempPath(), "rimlumen-env-alias-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                File.WriteAllText(Path.Combine(tempDir, "Pod.lumen.json"), """
                    {
                      "effectId": "Pod",
                      "nodes": [{ "name": "n" }],
                      "interactionEvents": [{ "trigger": "CursorEnter", "eventName": "hi", "radius": 2, "notify": true }]
                    }
                    """);
                var scanCatalog = new InMemoryAssetCatalog();
                LumenModAssetScanner.ScanDirectory(scanCatalog, "Author.TestMod", tempDir);
                Check(scanCatalog.TryResolveEffect("Author.TestMod:Pod", out EffectAsset? qualified)
                    && qualified is not null && qualified.InteractionEvents.Length == 1,
                    "Scanner package-qualified alias must retain InteractionEvents.");
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
        }
        finally
        {
            ParticleEnvironment.ForgetMap(mapId);
        }
    }

    [LumenFact]
    public static void InteractionNotifyDistinguishesHostMouseModCursorHandlesAndExplosions()
    {
        const int mapId = 8110;
        var map = new Verse.Map { uniqueID = mapId };
        try
        {
            var asset = new EffectAsset
            {
                EffectId = "env.cursor-identity",
                DefaultDuration = 5f,
                Nodes = new[]
                {
                    new EffectNodeSpec
                    {
                        Name = "root",
                        Emitter = VfxTestData.Emitter(looping: true, rate: 0f, tier: LumenParticleTier.L1_NativeCpu),
                    },
                },
                TimelineEvents = new[]
                {
                    new TimelineEventSpec(10f, "manual_ping", -1, Notify: true),
                },
                InteractionEvents = new[]
                {
                    new InteractionEventSpec { Trigger = LumenInteractionTrigger.CursorEnter, EventName = "enter", Radius = 3f, Notify = true },
                    new InteractionEventSpec { Trigger = LumenInteractionTrigger.CursorExit, EventName = "exit", Radius = 3f, Notify = true },
                    new InteractionEventSpec { Trigger = LumenInteractionTrigger.CursorStay, EventName = "stay", Radius = 3f, HoldSeconds = 0.1f, CooldownSeconds = 1f, Notify = true },
                    new InteractionEventSpec { Trigger = LumenInteractionTrigger.CursorSwipe, EventName = "swipe", Radius = 3f, MinCursorSpeed = 2f, Notify = true },
                    new InteractionEventSpec { Trigger = LumenInteractionTrigger.Explosion, EventName = "blast", Radius = 1f, Notify = true },
                },
            };

            var service = VfxTestData.Service(asset);
            using ILumenEffectHandle handle = service.Play(new LumenPlayRequest
            {
                EffectId = asset.EffectId,
                MapIndex = mapId,
                OriginWorld = new Vector3(20f, 0f, 20f),
            });

            var legacyNames = new List<string>();
            var notifications = new List<LumenInteractionNotification>();
            handle.Notify += legacyNames.Add;
            handle.InteractionNotify += notifications.Add;

            // Timeline events raise Notify only, never InteractionNotify.
            handle.Trigger("manual_ping");
            StepService(service, mapId, 0.05f, orthoSize: 10f);
            Check(string.Join(",", legacyNames) == "manual_ping", $"Expected manual_ping on Notify, got {string.Join(",", legacyNames)}");
            Check(notifications.Count == 0, $"Timeline Trigger must not raise InteractionNotify, got {notifications.Count}.");
            legacyNames.Clear();

            // 1. Host mouse enters, dwells, swipes, and exits (while a mod cursor sits far away at (0, 0)):
            using ILumenEnvironmentSource farCursor = RimLumenApi.CreateCursor(map, new Vector3(0f, 0f, 0f));
            ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.05f, orthoSize: 10f), null, null,
                new HostMouseSample(new Vector2(20f, 20f), new Vector2(0.5f, 0.5f)));
            StepServiceAlreadyCaptured(service, mapId, 0.05f);

            ReadOnlySpan<LumenCursorSnapshot> snapshots = LumenHostRegistry.GetCursors(mapId);
            Check(snapshots.Length == 2, $"Expected 2 cursor snapshots (host + mod), got {snapshots.Length}.");
            Check(snapshots[0].Source.Kind == LumenCursorSourceKind.HostMouse && snapshots[0].Source.Source is null,
                $"Host mouse snapshot must carry HostMouse kind, got {snapshots[0].Source}.");
            Check(snapshots[1].Source.Kind == LumenCursorSourceKind.ModCursor && ReferenceEquals(snapshots[1].Source.Source, farCursor),
                $"Mod cursor snapshot must reference farCursor, got {snapshots[1].Source}.");

            Check(notifications.Count == 1
                && notifications[0].EventName == "enter"
                && notifications[0].Trigger == LumenInteractionTrigger.CursorEnter
                && notifications[0].Cursor.Kind == LumenCursorSourceKind.HostMouse
                && notifications[0].Cursor.Source is null,
                $"Host mouse enter must report HostMouse source, got {string.Join(";", notifications)}");
            Check(string.Join(",", legacyNames) == "enter", $"Legacy Notify must remain unchanged, got {string.Join(",", legacyNames)}");
            notifications.Clear();
            legacyNames.Clear();

            // Dwell one more 0.05s step (total 0.10s >= HoldSeconds 0.10s) -> CursorStay from HostMouse:
            ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.05f, orthoSize: 10f), null, null,
                new HostMouseSample(new Vector2(20f, 20f), new Vector2(0.5f, 0.5f)));
            StepServiceAlreadyCaptured(service, mapId, 0.05f);
            Check(notifications.Count == 1
                && notifications[0].EventName == "stay"
                && notifications[0].Trigger == LumenInteractionTrigger.CursorStay
                && notifications[0].Cursor.Kind == LumenCursorSourceKind.HostMouse,
                $"Host mouse stay must report HostMouse source, got {string.Join(";", notifications)}");
            notifications.Clear();
            legacyNames.Clear();

            // Fast host mouse swipe inside circle (+0.30 vh in 0.02s -> inst 15 vh/s -> EMA > 2 vh/s):
            ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.02f, orthoSize: 10f), null, null,
                new HostMouseSample(new Vector2(21f, 20f), new Vector2(0.8f, 0.5f)));
            StepServiceAlreadyCaptured(service, mapId, 0.02f);
            Check(notifications.Count == 1
                && notifications[0].EventName == "swipe"
                && notifications[0].Trigger == LumenInteractionTrigger.CursorSwipe
                && notifications[0].Cursor.Kind == LumenCursorSourceKind.HostMouse,
                $"Host mouse swipe must report HostMouse source, got {string.Join(";", notifications)}");
            notifications.Clear();
            legacyNames.Clear();

            // Host mouse obscured by UI (hostMouse = null) while farCursor remains at (0, 0) -> CursorExit still reports HostMouse!
            ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.05f, orthoSize: 10f), null, null, hostMouse: null);
            StepServiceAlreadyCaptured(service, mapId, 0.05f);
            Check(notifications.Count == 1
                && notifications[0].EventName == "exit"
                && notifications[0].Trigger == LumenInteractionTrigger.CursorExit
                && notifications[0].Cursor.Kind == LumenCursorSourceKind.HostMouse,
                $"Host mouse UI-obscured exit must report HostMouse (not farCursor), got {string.Join(";", notifications)}");
            notifications.Clear();
            legacyNames.Clear();

            // 2. Mod cursor enters, swipes, and exits while host mouse sits outside at (5, 5):
            using ILumenEnvironmentSource aimCursor = RimLumenApi.CreateCursor(map, new Vector3(18.5f, 0f, 20f));
            ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.05f, orthoSize: 10f), null, null,
                new HostMouseSample(new Vector2(5f, 5f), new Vector2(0.1f, 0.1f)));
            StepServiceAlreadyCaptured(service, mapId, 0.05f);
            Check(notifications.Count == 1
                && notifications[0].EventName == "enter"
                && notifications[0].Trigger == LumenInteractionTrigger.CursorEnter
                && notifications[0].Cursor.Kind == LumenCursorSourceKind.ModCursor
                && ReferenceEquals(notifications[0].Cursor.Source, aimCursor)
                && !ReferenceEquals(notifications[0].Cursor.Source, farCursor),
                $"Mod cursor enter must reference aimCursor specifically, got {string.Join(";", notifications)}");
            notifications.Clear();
            legacyNames.Clear();

            // Fast mod cursor swipe inside circle (18.5 -> 21.5 in 0.02s at orthoSize 10 -> EMA > 2 vh/s):
            aimCursor.SetPosition(new Vector3(21.5f, 0f, 20f));
            ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.02f, orthoSize: 10f), null, null,
                new HostMouseSample(new Vector2(5f, 5f), new Vector2(0.1f, 0.1f)));
            StepServiceAlreadyCaptured(service, mapId, 0.02f);
            Check(notifications.Count == 1
                && notifications[0].EventName == "swipe"
                && notifications[0].Cursor.Kind == LumenCursorSourceKind.ModCursor
                && ReferenceEquals(notifications[0].Cursor.Source, aimCursor),
                $"Mod cursor swipe must reference aimCursor, got {string.Join(";", notifications)}");
            notifications.Clear();
            legacyNames.Clear();

            // Mod cursor moves outside to (10, 0, 20) while host mouse sits at (5, 5) -> CursorExit must report aimCursor, NOT HostMouse!
            aimCursor.SetPosition(new Vector3(10f, 0f, 20f));
            ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.05f, orthoSize: 10f), null, null,
                new HostMouseSample(new Vector2(5f, 5f), new Vector2(0.1f, 0.1f)));
            StepServiceAlreadyCaptured(service, mapId, 0.05f);
            Check(notifications.Count == 1
                && notifications[0].EventName == "exit"
                && notifications[0].Trigger == LumenInteractionTrigger.CursorExit
                && notifications[0].Cursor.Kind == LumenCursorSourceKind.ModCursor
                && ReferenceEquals(notifications[0].Cursor.Source, aimCursor),
                $"Mod cursor exit must reference aimCursor (not stationary HostMouse), got {string.Join(";", notifications)}");
            notifications.Clear();
            legacyNames.Clear();

            // Mod cursor disposed while inside circle and host mouse sits outside at (5, 5) -> CursorExit must still report disposedCursor, NOT HostMouse!
            using (ILumenEnvironmentSource disposedCursor = RimLumenApi.CreateCursor(map, new Vector3(19.5f, 0f, 20f)))
            {
                ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.05f, orthoSize: 10f), null, null,
                    new HostMouseSample(new Vector2(5f, 5f), new Vector2(0.1f, 0.1f)));
                StepServiceAlreadyCaptured(service, mapId, 0.05f);
                Check(notifications.Count == 1
                    && notifications[0].EventName == "enter"
                    && ReferenceEquals(notifications[0].Cursor.Source, disposedCursor),
                    $"Disposed-cursor setup enter must reference disposedCursor, got {string.Join(";", notifications)}");
                notifications.Clear();
                legacyNames.Clear();

                disposedCursor.Dispose();
                ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.05f, orthoSize: 10f), null, null,
                    new HostMouseSample(new Vector2(5f, 5f), new Vector2(0.1f, 0.1f)));
                StepServiceAlreadyCaptured(service, mapId, 0.05f);
                Check(notifications.Count == 1
                    && notifications[0].EventName == "exit"
                    && notifications[0].Trigger == LumenInteractionTrigger.CursorExit
                    && notifications[0].Cursor.Kind == LumenCursorSourceKind.ModCursor
                    && ReferenceEquals(notifications[0].Cursor.Source, disposedCursor),
                    $"Disposed mod cursor exit must still report disposedCursor, got {string.Join(";", notifications)}");
                notifications.Clear();
                legacyNames.Clear();
            }

            // CreateMouseForce (FollowHostMouse = true) must still emit Cursor* notifications with HostMouse (Source == null), not ModCursor:
            using (ILumenEnvironmentSource mouseForce = RimLumenApi.CreateMouseForce(map, radius: 3f, strength: -4f))
            {
                ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.05f, orthoSize: 10f), null, null,
                    new HostMouseSample(new Vector2(20f, 20f), new Vector2(0.1f, 0.1f)));
                StepServiceAlreadyCaptured(service, mapId, 0.05f);
                Check(notifications.Count == 1
                    && notifications[0].EventName == "enter"
                    && notifications[0].Cursor.Kind == LumenCursorSourceKind.HostMouse
                    && notifications[0].Cursor.Source is null,
                    $"Active CreateMouseForce must keep host mouse source as HostMouse, got {string.Join(";", notifications)}");
                notifications.Clear();
                legacyNames.Clear();

                ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.05f, orthoSize: 10f), null, null, hostMouse: null);
                StepServiceAlreadyCaptured(service, mapId, 0.05f);
                Check(notifications.Count == 1 && notifications[0].EventName == "exit" && notifications[0].Cursor.Kind == LumenCursorSourceKind.HostMouse,
                    $"Active CreateMouseForce exit must keep HostMouse source, got {string.Join(";", notifications)}");
                notifications.Clear();
                legacyNames.Clear();
                Check(mouseForce.IsAlive, "Mouse force remains alive until disposed.");
            }

            // 3. Explosion interaction event reports Trigger == Explosion and Kind == None / Source == null:
            RimLumenApi.EmitExplosion(map, new Vector3(20f, 0f, 20f), radius: 3f);
            StepService(service, mapId, 0.05f, orthoSize: 10f);
            Check(notifications.Count == 1
                && notifications[0].EventName == "blast"
                && notifications[0].Trigger == LumenInteractionTrigger.Explosion
                && notifications[0].Cursor.Kind == LumenCursorSourceKind.None
                && notifications[0].Cursor.Source is null,
                $"Explosion interaction must report Kind == None and Source == null, got {string.Join(";", notifications)}");
            Check(string.Join(",", legacyNames) == "blast", $"Legacy Notify must receive 'blast', got {string.Join(",", legacyNames)}");

            // 4. Speed-gated CursorExit keeps AnySpeedMatches condition while always attributing to rs.LastInsideCursor:
            farCursor.Dispose();
            aimCursor.Dispose();
            using ILumenEffectHandle speedGatedHandle = service.Play(new LumenPlayRequest
            {
                EffectId = asset.EffectId,
                MapIndex = mapId,
                OriginWorld = new Vector3(40f, 0f, 20f),
                InteractionEvents = new[]
                {
                    new InteractionEventSpec { Trigger = LumenInteractionTrigger.CursorExit, EventName = "fast_exit", Radius = 3f, MinCursorSpeed = 2f, Notify = true },
                },
            });
            var fastExitLog = new List<LumenInteractionNotification>();
            speedGatedHandle.InteractionNotify += n => { if (n.EventName == "fast_exit") fastExitLog.Add(n); };

            // (a) Disposing the sole inside cursor when no cursor remains (cursors.IsEmpty) does not fire when MinCursorSpeed > 0:
            using (ILumenEnvironmentSource disappearing = RimLumenApi.CreateCursor(map, new Vector3(38f, 0f, 20f)))
            {
                ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.05f, orthoSize: 10f), null, null, hostMouse: null);
                StepServiceAlreadyCaptured(service, mapId, 0.05f);
                disappearing.Dispose();
                ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.05f, orthoSize: 10f), null, null, hostMouse: null);
                StepServiceAlreadyCaptured(service, mapId, 0.05f);
                Check(fastExitLog.Count == 0, $"Empty cursor list must not fire speed-gated CursorExit, got {string.Join(";", fastExitLog)}");
            }

            // (b) Slow leaving cursor with stationary outside host mouse -> AnySpeedMatches is false, does not fire:
            using ILumenEnvironmentSource leavingCursor = RimLumenApi.CreateCursor(map, new Vector3(37.5f, 0f, 20f));
            ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.05f, orthoSize: 10f), null, null,
                new HostMouseSample(new Vector2(5f, 5f), new Vector2(0.1f, 0.1f)));
            StepServiceAlreadyCaptured(service, mapId, 0.05f);

            leavingCursor.SetPosition(new Vector3(36.9f, 0f, 20f));
            ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.05f, orthoSize: 10f), null, null,
                new HostMouseSample(new Vector2(5f, 5f), new Vector2(0.1f, 0.1f)));
            StepServiceAlreadyCaptured(service, mapId, 0.05f);
            Check(fastExitLog.Count == 0, $"Slow exit with no speed-matching cursor must not fire, got {string.Join(";", fastExitLog)}");

            // (c) Slow leaving cursor while an outside host mouse matches speed -> fires and attributes to leavingCursor (not HostMouse):
            leavingCursor.SetPosition(new Vector3(37.5f, 0f, 20f));
            ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.05f, orthoSize: 10f), null, null,
                new HostMouseSample(new Vector2(5f, 5f), new Vector2(0.1f, 0.1f)));
            StepServiceAlreadyCaptured(service, mapId, 0.05f);
            fastExitLog.Clear();

            leavingCursor.SetPosition(new Vector3(36.9f, 0f, 20f));
            ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.05f, orthoSize: 10f), null, null,
                new HostMouseSample(new Vector2(5f, 5f), new Vector2(0.4f, 0.1f)));
            StepServiceAlreadyCaptured(service, mapId, 0.05f);
            Check(fastExitLog.Count == 1
                && fastExitLog[0].Trigger == LumenInteractionTrigger.CursorExit
                && fastExitLog[0].Cursor.Kind == LumenCursorSourceKind.ModCursor
                && ReferenceEquals(fastExitLog[0].Cursor.Source, leavingCursor),
                $"Speed-gated CursorExit must still attribute to leavingCursor, got {string.Join(";", fastExitLog)}");
            fastExitLog.Clear();

            // (d) Disposing leavingCursor while an outside host mouse matches speed -> fires and still attributes to leavingCursor:
            leavingCursor.SetPosition(new Vector3(37.5f, 0f, 20f));
            ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.05f, orthoSize: 10f), null, null,
                new HostMouseSample(new Vector2(5f, 5f), new Vector2(0.4f, 0.1f)));
            StepServiceAlreadyCaptured(service, mapId, 0.05f);
            fastExitLog.Clear();

            leavingCursor.Dispose();
            ParticleEnvironment.CaptureFrame(TopDownFrame(mapId, 0.05f, orthoSize: 10f), null, null,
                new HostMouseSample(new Vector2(5f, 5f), new Vector2(0.1f, 0.1f)));
            StepServiceAlreadyCaptured(service, mapId, 0.05f);
            Check(fastExitLog.Count == 1
                && fastExitLog[0].Trigger == LumenInteractionTrigger.CursorExit
                && fastExitLog[0].Cursor.Kind == LumenCursorSourceKind.ModCursor
                && ReferenceEquals(fastExitLog[0].Cursor.Source, leavingCursor),
                $"Disposed leavingCursor speed-gated exit must still attribute to leavingCursor, got {string.Join(";", fastExitLog)}");
        }
        finally
        {
            ParticleEnvironment.ForgetMap(mapId);
        }
    }

    private static void StepService(LumenVfxService service, int mapId, float dt, float orthoSize)
    {
        var context = TopDownFrame(mapId, dt, orthoSize);
        service.CaptureMainThread(context);
        service.Simulator.CaptureMainThread(context);
    }

    private static void StepServiceAlreadyCaptured(LumenVfxService service, int mapId, float dt)
    {
        var context = new LumenCaptureContext
        {
            CurrentMapIndex = mapId,
            FrameIndex = nextFrame, // matches last TopDownFrame
            DeltaTime = dt,
            UnscaledDeltaTime = dt,
            ViewportWidth = 1920,
            ViewportHeight = 1080,
            ViewMatrix = TopDownView(0f, 0f),
            ProjectionMatrix = TopDownProjection(10f, 16f / 9f),
            LumenRenderPassId = 0,
        };
        service.CaptureMainThread(context);
        service.Simulator.CaptureMainThread(context);
    }

    private static LumenCaptureContext TopDownFrame(int mapId, float dt, float orthoSize = 10f)
    {
        ulong frame = ++nextFrame;
        return new LumenCaptureContext
        {
            CurrentMapIndex = mapId,
            FrameIndex = frame,
            DeltaTime = dt,
            UnscaledDeltaTime = dt,
            ViewportWidth = 1920,
            ViewportHeight = 1080,
            ViewMatrix = TopDownView(20f, 20f),
            ProjectionMatrix = TopDownProjection(orthoSize, 16f / 9f),
            LumenRenderPassId = 0,
        };
    }

    // Column-vector camera looking straight down -Y with screen +X = world +X, screen +Y = world +Z:
    // camX = worldX - cx; camY = worldZ - cz; camZ = -(worldY - 15).
    private static Matrix4x4 TopDownView(float centerX, float centerZ) => new(
        1f, 0f, 0f, -centerX,
        0f, 0f, 1f, -centerZ,
        0f, 1f, 0f, -15f,
        0f, 0f, 0f, 1f);

    // Vulkan column-vector orthographic projection matching UnityStubs Matrix4x4.Ortho(-S*aspect, S*aspect, -S, S, 0.3, 100):
    private static Matrix4x4 TopDownProjection(float orthoSize, float aspect) => new(
        1f / (orthoSize * aspect), 0f, 0f, 0f,
        0f, -1f / orthoSize, 0f, 0f,
        0f, 0f, 1f / (100f - 0.3f), 100f / (100f - 0.3f),
        0f, 0f, 0f, 1f);

    private static Matrix4x4 TopDownViewProjection(float centerX, float centerZ, float orthoSize, float aspect)
        => TopDownProjection(orthoSize, aspect) * TopDownView(centerX, centerZ);

    private static EffectAsset ParseXmlEffect(
        string defName,
        string[] nodesXml,
        string[] statesXml,
        string initialState,
        string[] interactionsXml)
    {
        bool profiler = Verse.DeepProfiler.enabled;
        Verse.DeepProfiler.enabled = false;
        try
        {
            var nodes = new List<LumenNodeXml>();
            foreach (string xml in nodesXml)
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                nodes.Add(Verse.DirectXmlToObject.ObjectFromXml<LumenNodeXml>(doc.DocumentElement!, false));
            }
            var states = new List<LumenStateXml>();
            foreach (string xml in statesXml)
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                states.Add(Verse.DirectXmlToObject.ObjectFromXml<LumenStateXml>(doc.DocumentElement!, false));
            }
            var interactions = new List<LumenInteractionEventXml>();
            foreach (string xml in interactionsXml)
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                interactions.Add(Verse.DirectXmlToObject.ObjectFromXml<LumenInteractionEventXml>(doc.DocumentElement!, false));
            }
            return new LumenEffectDef
            {
                defName = defName,
                nodes = nodes,
                states = states,
                initialState = initialState,
                interactionEvents = interactions,
            }.ToEffectAsset();
        }
        finally
        {
            Verse.DeepProfiler.enabled = profiler;
        }
    }

    private static string Repo()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Source")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("RimLumen source root not found.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Near(float expected, float actual)
        => Check(float.IsFinite(actual) && MathF.Abs(expected - actual) < 0.0001f, $"Expected {expected}, actual {actual}.");

    private static void Close(Vector3 expected, Vector3 actual)
        => Check(float.IsFinite(actual.LengthSquared()) && Vector3.Distance(expected, actual) < 0.0001f, $"Expected {expected}, actual {actual}.");

    private static void Close2(Vector2 expected, Vector2 actual)
        => Check(float.IsFinite(actual.LengthSquared()) && Vector2.Distance(expected, actual) < 0.0001f, $"Expected {expected}, actual {actual}.");

    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
