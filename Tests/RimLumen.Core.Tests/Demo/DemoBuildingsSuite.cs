using System;
using System.Linq;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Demo;
using RimLumen.Core.Mod;
using RimLumen.Core.ParticlesGpu;
using RimLumen.Core.Rendering;
using RimLumen.Core.Ribbons;
using RimLumen.Core.Scene;
using RimLumen.Core.ScreenDim;
using RimLumen.Core.Tests.SceneAndLighting;

namespace RimLumen.Core.Tests.Demo;

[LumenTestSuite]
public static class DemoBuildingsSuite
{
    // Ensures: every firework variant passes LumenAssetRules, keeps the pure-asset rocket/burst/sparkle/light/crackle
    //          structure without C# Follow, shoots along true +Y (Vertical3D) with parabolic deceleration to ~10 cells
    //          at 1.0 s, and uses Death sub-emitters plus floor-expiry bounce physics.
    [LumenFact]
    public static void FireworkVariantsValidateAndShareTheApexStructure()
    {
        var hues = new Vector4[RimLumenDemoEffects.FireworkEffectIds.Length];
        for (int i = 0; i < RimLumenDemoEffects.FireworkEffectIds.Length; i++)
        {
            string id = RimLumenDemoEffects.FireworkEffectIds[i];
            EffectAsset asset = RimLumenDemoEffects.CreateFireworkAsset(id, i);
            LumenAssetRules.Validate(asset, id);

            NativeTestData.Check(asset.EffectId == id, $"{id}: keeps the requested effect id");
            NativeTestData.Check(asset.Nodes.Length == 5, $"{id}: 5 nodes (rocket, burst, sparkle, light, crackle)");
            NativeTestData.Check(asset.TimelineEvents.Length == 0, $"{id}: bursts are Death-driven, not Trigger-driven");

            EffectNodeSpec rocketNode = asset.Nodes[0];
            EmitterSpec rocket = rocketNode.Emitter!;
            NativeTestData.Check(rocketNode.Name == "rocket", $"{id}: node 0 is rocket");
            NativeTestData.Check(rocketNode.SpacePlane == LumenSpacePlane.Vertical3D, $"{id}: rocket uses Vertical3D");
            NativeTestData.Check(rocketNode.Ribbon is null, $"{id}: rocket uses emitter.Trails rather than node Ribbon");
            NativeTestData.Close(RimLumenDemoEffects.LaunchHeight, rocketNode.LocalPosition.Y, $"{id}: rocket launches at mouth height");
            NativeTestData.Check(rocket.PreferredTier == LumenParticleTier.L2_GpuCompute, $"{id}: rocket runs on L2");
            NativeTestData.Check(rocket.Looping == false && rocket.Shape == LumenShapeKind.Point, $"{id}: one-shot point rocket");
            NativeTestData.Check(rocket.Bursts.Length == 1 && rocket.Bursts[0].Time == 0f && rocket.Bursts[0].Count == 1,
                $"{id}: single launch burst at t=0");
            NativeTestData.Close(RimLumenDemoEffects.LaunchSeconds, rocket.StartLifetimeMin, $"{id}: rocket climb duration");
            NativeTestData.Close(19.3f, rocket.StartSpeedMin, $"{id}: derived rocket initial speed");
            NativeTestData.Check(HasGravityDown(rocket) && MathF.Abs(rocket.ForceFields[0].Strength - 19.3f) < 1e-4f,
                $"{id}: derived rocket gravity pulls down to zero velocity at apex");
            NativeTestData.Check(rocket.Trails is { DieWithParticles: false, Ratio: 1f },
                $"{id}: rocket carries per-particle trail that fades out after death");
            NativeTestData.Check(rocket.SubEmitters.Length == 2
                && rocket.SubEmitters[0] == new SubEmitterSpec(LumenSubEmitterTrigger.Death, 1, 1800)
                && rocket.SubEmitters[1] == new SubEmitterSpec(LumenSubEmitterTrigger.Death, 2, 360),
                $"{id}: rocket triggers burst & sparkle on Death");

            EffectNodeSpec burstNode = asset.Nodes[1];
            EmitterSpec burst = burstNode.Emitter!;
            NativeTestData.Check(burstNode.SpacePlane == LumenSpacePlane.Vertical3D, $"{id}: burst uses Vertical3D");
            NativeTestData.Close(0f, burstNode.LocalPosition.Y, $"{id}: burst inherits apex world position from rocket Death event");
            NativeTestData.Check(burst.KillBelowFloor, $"{id}: burst sparks expire on floor contact");
            NativeTestData.Check(burst.PreferredTier == LumenParticleTier.L2_GpuCompute, $"{id}: burst runs on L2");
            NativeTestData.Check(burst.Looping == false && burst.Bursts.Length == 0 && burst.RateOverTime == 0f,
                $"{id}: burst emits solely via rocket Death sub-emitter");
            NativeTestData.Check(HasGravityDown(burst), $"{id}: L2 burst gravity pulls toward -Y");
            NativeTestData.Check(HasCurl(burst), $"{id}: L2 burst has curl turbulence");
            NativeTestData.Close(8f, burst.HdrIntensity, $"{id}: HDR burst intensity");
            NativeTestData.Check(burst.ColorOverLifetime.Stops[^1].Rgba.W == 0f, $"{id}: sparks fade to alpha 0");

            EffectNodeSpec sparkleNode = asset.Nodes[2];
            EmitterSpec sparkle = sparkleNode.Emitter!;
            NativeTestData.Check(sparkleNode.SpacePlane == LumenSpacePlane.Vertical3D, $"{id}: sparkle core uses Vertical3D");
            NativeTestData.Check(sparkle.PreferredTier == LumenParticleTier.L2_GpuCompute, $"{id}: sparkle core matches rocket L2 tier");
            NativeTestData.Check(sparkle.Bursts.Length == 0 && sparkle.RateOverTime == 0f,
                $"{id}: sparkle core emits solely via rocket Death sub-emitter");

            NativeTestData.Close(RimLumenDemoEffects.ApexHeight, asset.Nodes[3].LocalPosition.Y, $"{id}: flash sits at 10-cell apex");
            ScalarCurve dim = asset.Nodes[3].ScreenDim!.StrengthOverTime!;
            for (float t = 0f; t <= RimLumenDemoEffects.LaunchSeconds; t += 0.01f)
                NativeTestData.Close(0f, CurveEvaluator.Evaluate(dim, t), $"{id}: no dim before the apex (t = {t:F2} s)");
            PointLightSpec light = asset.Nodes[3].PointLight!.Value;
            NativeTestData.Check(light.IntensityOverTime is not null, $"{id}: flash is driven by IntensityOverTime");
            ScalarCurve curve = light.IntensityOverTime!;
            NativeTestData.Close(0f, curve.Keys[0].Value, $"{id}: flash is dark at launch");
            NativeTestData.Check(curve.Keys[2].Value > 1f, $"{id}: flash peaks after the apex");
            NativeTestData.Close(0f, curve.Keys[^1].Value, $"{id}: flash is gone before the handle is retired");
            NativeTestData.Check(curve.Keys[^1].Time <= RimLumenDemoEffects.FireworkVisibleSeconds, $"{id}: flash ends inside the visible window");

            hues[i] = burst.ColorOverLifetime.Stops[1].Rgba;
        }

        NativeTestData.Check(hues[0].X > 0.9f && hues[0].Z < 0.3f, "gold variant is warm");
        NativeTestData.Check(hues[1].X < 0.3f && hues[1].Z > 0.9f, "cyan variant is cool");
        NativeTestData.Check(hues[2].X > 0.9f && hues[2].Z > 0.8f, "magenta variant is warm and blue");
        NativeTestData.Check(hues[0] != hues[1] && hues[1] != hues[2] && hues[0] != hues[2], "the three variants differ");
    }

    // Ensures: the fountain validates, loops on L2 in both emitters, and keeps the mist on the ground.
    [LumenFact]
    public static void FountainAssetValidatesAndLoops()
    {
        EffectAsset asset = RimLumenDemoEffects.CreateParticleFountainAsset();
        LumenAssetRules.Validate(asset, RimLumenDemoEffects.ParticleFountainId);

        NativeTestData.Check(asset.Nodes.Length == 3, "fountain has jet, mist and light nodes");

        EffectNodeSpec jetNode = asset.Nodes[0];
        EmitterSpec jet = jetNode.Emitter!;
        NativeTestData.Check(jetNode.SpacePlane == LumenSpacePlane.Vertical3D, "jet shoots along true +Y");
        NativeTestData.Close(0.55f, jetNode.LocalPosition.Y, "jet throat altitude");
        NativeTestData.Check(jet.KillBelowFloor, "jet droplets expire on floor contact");
        NativeTestData.Check(jet.PreferredTier == LumenParticleTier.L2_GpuCompute, "jet runs on L2");
        NativeTestData.Check(jet.Looping, "jet never stops on its own");
        NativeTestData.Check(jet.Shape == LumenShapeKind.Cone && jet.ConeAngleDegrees > 0f, "jet is a cone");
        NativeTestData.Check(HasGravityDown(jet), "jet falls back down");
        NativeTestData.Check(HasCurl(jet), "jet wobbles");
        NativeTestData.Check(jet.SoftParticleFadeDistance > 0f, "jet fades against geometry");
        NativeTestData.Check(jet.ColorOverLifetime.Stops[^1].Rgba.W == 0f, "jet droplets fade to alpha 0");

        EffectNodeSpec mistNode = asset.Nodes[1];
        EmitterSpec mist = mistNode.Emitter!;
        NativeTestData.Check(mistNode.SpacePlane == LumenSpacePlane.GroundXZ, "mist lies on the ground plane");
        NativeTestData.Close(0f, mistNode.LocalPosition.Y, "mist lies on the ground");
        NativeTestData.Check(mist.Shape == LumenShapeKind.CircleXZ, "mist spawns on a ground disc");
        NativeTestData.Check(mist.BlendMode == LumenBlendMode.AlphaBlend, "mist is alpha blended, not additive");
        NativeTestData.Check(mist.Looping, "mist loops");

        NativeTestData.Close(0.6f, asset.Nodes[2].LocalPosition.Y, "fountain light altitude");
        PointLightSpec light = asset.Nodes[2].PointLight!.Value;
        NativeTestData.Check(light.IntensityOverTime is null, "fountain light is steady");
        NativeTestData.Check(light.GlowRadius > 0f && light.ColorRgb.Z > light.ColorRgb.X, "fountain light is cool cyan");
    }

    // Ensures: the street lamp is one steady warm light at the shared lamp radius, held alive by a loop state
    //          with no particles.
    [LumenFact]
    public static void StreetLampIsOneSteadyWarmLightThatNeverEnds()
    {
        EffectAsset asset = RimLumenDemoEffects.CreateStreetLampAsset();
        LumenAssetRules.Validate(asset, RimLumenDemoEffects.StreetLampId);
        NativeTestData.Check(asset.Nodes.Length == 1 && asset.Nodes[0].Emitter is null, "lamp is a single light-only node");
        PointLightSpec light = asset.Nodes[0].PointLight!.Value;
        NativeTestData.Close(RimLumenDemoEffects.StreetLampRadius, light.GlowRadius, "lamp pool radius");
        NativeTestData.Check(light.IntensityOverTime is null, "lamp light is steady");
        NativeTestData.Check(light.ColorRgb.X > light.ColorRgb.Y && light.ColorRgb.Y > light.ColorRgb.Z, "lamp light is warm");

        LumenVfxService service = VfxTestData.Service(asset);
        using ILumenEffectHandle handle = service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        for (int i = 0; i < 200; i++) VfxTestData.Step(service, 0.05f);
        VfxAssert.Check(handle.IsAlive, "the loop state keeps the lamp lit after 10 s");
        VfxAssert.Equal(0, service.Simulator.LiveParticleCount);
    }

    // Ensures: the gold firework binds all 4 emitters to L2 (rocket + burst + sparkle + crackle), and a paused
    //          frame (DeltaTime = 0) advances the clock by nothing and spawns nothing.
    [LumenFact]
    public static void FireworkRoutesToGpuAndFreezesWhilePaused() => WithFeatureBackends(() =>
    {
        EffectAsset asset = RimLumenDemoEffects.CreateFireworkAsset(RimLumenDemoEffects.FireworkGoldId, 0);
        LumenVfxService service = VfxTestData.Service(asset);
        var engine = new SpyGpuEngine();
        service.GpuEngineProvider = () => engine;

        using EffectInstanceHandle typed = PlayRocket(service, asset, new Vector3(10f, 0f, 20f));
        VfxAssert.Check(typed.IsAlive && typed.IsEmitting, "firework starts playing");
        VfxAssert.Equal(4, typed.GpuEmitterCount);
        VfxAssert.Equal(0, typed.CpuEmitterCount);
        VfxAssert.Check(service.HasActiveWork, "a playing firework keeps the pipeline active");

        // Vertical3D keeps the node's identity rotation, so local +Y stays world +Y, and the rocket launches at LaunchHeight
        // directly over the launcher while apex_flash sits statically at ApexHeight.
        Vector3 upAxis = Vector3.TransformNormal(Vector3.UnitY, typed.NodeWorldMatrices[0]);
        VfxAssert.Close(Vector3.UnitY, upAxis);
        NativeTestData.Close(RimLumenDemoEffects.LaunchHeight, typed.NodeWorldMatrices[0].Translation.Y, "the rocket node starts at launcher mouth");
        NativeTestData.Close(RimLumenDemoEffects.ApexHeight, typed.NodeWorldMatrices[3].Translation.Y, "the flash node sits at 10-cell apex");
        NativeTestData.Close(0f, typed.NodeWorldMatrices[3].Translation.Z - typed.NodeWorldMatrices[0].Translation.Z, "apex has no up-screen offset");
        NativeTestData.Close(10f, typed.NodeWorldMatrices[0].Translation.X, "effect origin x");
        NativeTestData.Close(20f, typed.NodeWorldMatrices[0].Translation.Z, "effect origin z");

        for (int i = 0; i < 10; i++) VfxTestData.Step(service, 0f);
        VfxAssert.Equal(0f, typed.PlaybackTime);
        VfxAssert.Equal(0, service.Simulator.LiveParticleCount);
        VfxAssert.Check(engine.Updates.TrueForAll(update => update.Emitting), "paused frames never stop the emitters");

        for (int i = 0; i < 22; i++) VfxTestData.Step(service, 0.05f);
        VfxAssert.Check(typed.PlaybackTime > RimLumenDemoEffects.LaunchSeconds, "playback passes the apex");
    });

    // Ensures: the fountain plays entirely on L2 and survives a second of playback.
    [LumenFact]
    public static void FountainRoutesBothEmittersToGpu()
    {
        EffectAsset asset = RimLumenDemoEffects.CreateParticleFountainAsset();
        LumenVfxService service = VfxTestData.Service(asset);
        var engine = new SpyGpuEngine();
        service.GpuEngineProvider = () => engine;

        using ILumenEffectHandle handle = service.Play(new LumenPlayRequest
        {
            MapIndex = 111, EffectId = asset.EffectId,
            OriginWorld = new Vector3(4f, 0f, 6f),
        });
        var typed = (EffectInstanceHandle)handle;
        VfxAssert.Equal(2, typed.GpuEmitterCount);
        VfxAssert.Equal(0, typed.CpuEmitterCount);
        VfxAssert.Equal(2, engine.Creates.Count);

        for (int i = 0; i < 20; i++) VfxTestData.Step(service, 0.05f);
        VfxAssert.Check(typed.IsAlive && typed.PlaybackTime > 0.9f, "the looping fountain keeps running");
        VfxAssert.Check(service.HasActiveWork, "the fountain keeps the pipeline active");
    }

    // Ensures: Stop(allowFadeOut: false) tears the GPU instance down immediately, releasing all 4 L2 emitters
    //          and dropping the handle so nothing keeps the pipeline active.
    [LumenFact]
    public static void ImmediateStopReleasesEveryBinding() => WithFeatureBackends(() =>
    {
        EffectAsset asset = RimLumenDemoEffects.CreateFireworkAsset(RimLumenDemoEffects.FireworkCyanId, 1);
        LumenVfxService service = VfxTestData.Service(asset);
        var engine = new SpyGpuEngine();
        service.GpuEngineProvider = () => engine;

        ILumenEffectHandle handle = service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        for (int i = 0; i < 10; i++) VfxTestData.Step(service, 0.05f);
        VfxAssert.Equal(4, engine.Creates.Count);

        handle.Stop(allowFadeOut: false);
        handle.Dispose();
        VfxAssert.Check(!handle.IsAlive, "an immediate stop kills the handle");
        VfxAssert.Equal(4, engine.Destroys.Count);
        VfxAssert.Check(engine.Destroys[0].Immediate, "the L2 instance is destroyed immediately");

        VfxTestData.Step(service, 0.05f);
        VfxAssert.Equal(0, service.Simulator.ActiveEmitterCount);
        VfxAssert.Equal(0, service.Simulator.LiveParticleCount);
        VfxAssert.Equal(0, service.LiveHandleCount);
        VfxAssert.Check(!service.HasActiveWork, "no handle keeps the pipeline active after the stop");
    });

    // Ensures: the demo buildings are off by default, and the settings->registry seam is a safe no-op
    //          while the def database is empty (offline runner / settings load before XML defs resolve).
    [LumenFact]
    public static void SettingsDefaultOffAndRegistrySyncIsSafeBeforeDefsLoad()
    {
        bool originalGlow = RimLumenRenderHost.EnableVanillaAdditiveBloom;
        float originalDensity = RimLumenRenderHost.ParticleDensityScale;
        LumenColorBlendSpace originalBlendSpace = RimLumenRenderHost.ColorBlendSpace;
        try
        {
            var settings = new RimLumenSettings();
            VfxAssert.Check(!settings.EnableDemoBuildings, "demo buildings default to off");

            settings.EnableDemoBuildings = true;
            settings.ColorBlendSpace = LumenColorBlendSpace.Linear;
            settings.ApplyScalarsToRuntime();
            VfxAssert.Check(RimLumenRenderHost.EnableVanillaAdditiveBloom, "ApplyScalarsToRuntime still applies the glow toggle");
            VfxAssert.Check(RimLumenRenderHost.ColorBlendSpace == LumenColorBlendSpace.Linear, "ApplyScalarsToRuntime requests linear blending");

            VfxAssert.Check(!DemoBuildingRegistry.SyncWithSettings(true), "Sync reports no Joy category before defs load");
            VfxAssert.Check(!DemoBuildingRegistry.SyncWithSettings(false), "Sync stays false before defs load");

            settings.EnableDemoBuildings = false;
            settings.ApplyScalarsToRuntime();
        }
        finally
        {
            RimLumenRenderHost.EnableVanillaAdditiveBloom = originalGlow;
            RimLumenRenderHost.ParticleDensityScale = originalDensity;
            RimLumenRenderHost.ColorBlendSpace = originalBlendSpace;
        }
    }

    // Ensures: the rocket node launches its single particle on frame 0 and the burst node has zero scheduled bursts.
    [LumenFact]
    public static void ApexBurstFiresOnceAtTheScheduledFrame()
    {
        EffectAsset asset = RimLumenDemoEffects.CreateFireworkAsset(RimLumenDemoEffects.FireworkGoldId, 0);
        var rocketScheduler = new EmissionScheduler(asset.Nodes[0].Emitter!, 1u);
        VfxAssert.Equal(1, rocketScheduler.Advance(0.05f, emitting: true));
        VfxAssert.Equal(0, rocketScheduler.Advance(0.05f, emitting: true));
        VfxAssert.Equal(0, asset.Nodes[1].Emitter!.Bursts.Length);
    }

    // Ensures: simulated purely by particle physics without C# Follow, the rocket particle climbs strictly along +Y
    //          to ~10 cells above ground with velocity decelerating to zero at 1.0 s, emits trail vertices, and dies
    //          to trigger apex_burst and sparkle_core.
    [LumenFact]
    public static void RocketParticleClimbsParabolicallyToApexAndTriggersDeathTrailAndBurst()
    {
        EffectAsset asset = RimLumenDemoEffects.CreateFireworkAsset(RimLumenDemoEffects.FireworkGoldId, 0);
        var simulator = new CpuParticleSimulator();
        var launcher = new Vector3(3f, 0f, 5f);
        int rocketId = simulator.AcquireEmitter(asset.Nodes[0].Emitter!,
            new LumenEmitterTransform(Matrix4x4.CreateTranslation(launcher + asset.Nodes[0].LocalPosition), Quaternion.Identity), 1u);
        int burstId = simulator.AcquireEmitter(asset.Nodes[1].Emitter!,
            new LumenEmitterTransform(Matrix4x4.CreateTranslation(launcher), Quaternion.Identity), 2u);
        int sparkleId = simulator.AcquireEmitter(asset.Nodes[2].Emitter!,
            new LumenEmitterTransform(Matrix4x4.CreateTranslation(launcher), Quaternion.Identity), 3u);
        int crackleId = simulator.AcquireEmitter(asset.Nodes[4].Emitter!,
            new LumenEmitterTransform(Matrix4x4.CreateTranslation(launcher), Quaternion.Identity), 4u);
        simulator.SetEmitterDisplayState(rocketId, true, null);
        simulator.SetEmitterDisplayState(burstId, true, null);
        simulator.SetEmitterDisplayState(sparkleId, true, null);
        simulator.SetEmitterDisplayState(crackleId, true, null);
        simulator.BindNodeInstances(new[] { rocketId, burstId, sparkleId, -1, crackleId }, null, Array.Empty<int>());

        static LumenCaptureContext StepFrame(float dt) => new()
        {
            CurrentMapIndex = -1, RendersMap = true, FrameIndex = 1, DeltaTime = dt,
            ViewportWidth = 640, ViewportHeight = 480,
            ViewMatrix = Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity, LumenRenderPassId = 0,
        };

        simulator.CaptureMainThread(StepFrame(1f / 60f));
        VfxAssert.Equal(1, simulator.LiveParticleCount);
        float previousY = simulator.LiveParticles[0].Position.Y;
        NativeTestData.Close(RimLumenDemoEffects.LaunchHeight, previousY, "rocket spawns at LaunchHeight");
        for (int step = 0; step < 59; step++)
        {
            simulator.CaptureMainThread(StepFrame(1f / 60f));
            VfxAssert.Equal(1, simulator.LiveParticleCount);
            CpuParticle p = simulator.LiveParticles[0];
            VfxAssert.Check(p.Position.Y > previousY, $"rocket still rises before 1.0s (Y={p.Position.Y:F3})");
            NativeTestData.Close(launcher.X, p.Position.X, "rocket stays over launcher X");
            NativeTestData.Close(launcher.Z, p.Position.Z, "rocket stays over launcher Z");
            previousY = p.Position.Y;
        }
        VfxAssert.Check(previousY > 9.5f && previousY <= RimLumenDemoEffects.ApexHeight + 0.05f,
            $"rocket climbs near 10-cell apex before 1.0 s (got Y={previousY:F3})");
        VfxAssert.Check(simulator.Vertices.Length > 4, "rising rocket leaves trail strip vertices");
        simulator.CaptureMainThread(StepFrame(2f / 60f));
        VfxAssert.Check(simulator.LiveParticleCount > 1000, "rocket death triggers apex_burst & sparkle_core");
        VfxAssert.Check(simulator.Vertices.Length > simulator.LiveParticleCount * 4,
            "detached rocket trail stays alive right after explosion when DieWithParticles=false");
    }

    private static EffectInstanceHandle PlayRocket(LumenVfxService service, EffectAsset asset, Vector3 launcher) =>
        (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId, OriginWorld = launcher });

    // The firework's ribbon and screen-dim nodes need their backends registered to play.
    private static void WithFeatureBackends(Action body)
    {
        var ribbons = new LumenRibbonBackend();
        var dims = new LumenScreenDimBackend();
        LumenHostRegistry.RegisterNodeFeatureBackend(ribbons);
        LumenHostRegistry.RegisterNodeFeatureBackend(dims);
        try { body(); }
        finally
        {
            LumenHostRegistry.UnregisterNodeFeatureBackend(dims);
            LumenHostRegistry.UnregisterNodeFeatureBackend(ribbons);
        }
    }

    [LumenFact]
    public static void DistortionPylonValidates() =>
        LumenAssetRules.Validate(RimLumenDemoEffects.CreateDistortionPylonAsset(), RimLumenDemoEffects.DistortionPylonId);

    private static bool HasGravityDown(EmitterSpec emitter)
    {
        foreach (ForceFieldSpec field in emitter.ForceFields)
        {
            if (field.Kind == ForceFieldKind.GravityDrag && field.CenterOrAxis == new Vector3(0f, -1f, 0f) && field.Strength > 0f)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCurl(EmitterSpec emitter)
    {
        foreach (ForceFieldSpec field in emitter.ForceFields)
        {
            if (field.Kind == ForceFieldKind.CurlTurbulence && field.Strength > 0f) return true;
        }

        return false;
    }
}
