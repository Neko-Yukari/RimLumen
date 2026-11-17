using System;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Demo;
using RimLumen.Core.Mod;
using RimLumen.Core.ParticlesGpu;
using RimLumen.Core.Rendering;
using RimLumen.Core.Scene;
using RimLumen.Core.Tests.SceneAndLighting;

namespace RimLumen.Core.Tests.Demo;

[LumenTestSuite]
public static class DemoBuildingsSuite
{
    // Ensures: every firework variant passes LumenAssetRules, keeps the launch/burst/sparkle/light node
    //          structure, and shoots along true +Y (Vertical3D) with (0, -1, 0) gravity and floor expiry.
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
            NativeTestData.Check(asset.Nodes.Length == 4, $"{id}: 4 nodes (trail, burst, sparkle, light)");
            NativeTestData.Check(asset.TimelineEvents.Length == 0, $"{id}: bursts are time-driven, not Trigger-driven");

            EffectNodeSpec trailNode = asset.Nodes[0];
            EmitterSpec trail = trailNode.Emitter!;
            NativeTestData.Check(trailNode.SpacePlane == LumenSpacePlane.Vertical3D, $"{id}: trail uses Vertical3D");
            NativeTestData.Close(0.35f, trailNode.LocalPosition.Y, $"{id}: trail leaves the launcher 0.35 above the floor");
            NativeTestData.Check(trail.KillBelowFloor, $"{id}: trail particles expire on floor contact");
            NativeTestData.Check(HasGravityDown(trail), $"{id}: trail gravity pulls toward -Y");
            NativeTestData.Check(trail.PreferredTier == LumenParticleTier.L1_NativeCpu, $"{id}: trail stays on L1");
            NativeTestData.Check(trail.Looping == false, $"{id}: trail is a one-shot");
            NativeTestData.Close(RimLumenDemoEffects.LaunchSeconds, trail.Duration, $"{id}: trail duration");
            NativeTestData.Check(trail.Bursts.Length == 0, $"{id}: trail emits by rate, not by burst");

            EffectNodeSpec burstNode = asset.Nodes[1];
            EmitterSpec burst = burstNode.Emitter!;
            NativeTestData.Check(burstNode.SpacePlane == LumenSpacePlane.Vertical3D, $"{id}: burst uses Vertical3D");
            NativeTestData.Close(4.8f, burstNode.LocalPosition.Y, $"{id}: apex altitude");
            NativeTestData.Close(0f, burstNode.LocalPosition.Z, $"{id}: apex sits over the launcher, not up-screen");
            NativeTestData.Check(burst.KillBelowFloor, $"{id}: burst sparks expire on floor contact");
            NativeTestData.Check(burst.PreferredTier == LumenParticleTier.L2_GpuCompute, $"{id}: burst runs on L2");
            NativeTestData.Check(burst.Looping == false, $"{id}: burst is a one-shot");
            NativeTestData.Check(burst.Bursts.Length == 1, $"{id}: one apex burst");
            NativeTestData.Close(RimLumenDemoEffects.LaunchSeconds, burst.Bursts[0].Time, $"{id}: burst time at apex");
            NativeTestData.Check(burst.Bursts[0].Count == 1800, $"{id}: 1800 sparks leave together");
            NativeTestData.Check(burst.Bursts[0].Time <= burst.Duration, $"{id}: burst time lies inside the emission window");
            NativeTestData.Close(0f, burst.RateOverTime, $"{id}: burst node does not trickle");
            NativeTestData.Check(HasGravityDown(burst), $"{id}: L2 burst gravity pulls toward -Y");
            NativeTestData.Check(HasCurl(burst), $"{id}: L2 burst has curl turbulence");
            NativeTestData.Close(8f, burst.HdrIntensity, $"{id}: HDR burst intensity");
            NativeTestData.Check(burst.ColorOverLifetime.Stops[^1].Rgba.W == 0f, $"{id}: sparks fade to alpha 0");

            EffectNodeSpec sparkleNode = asset.Nodes[2];
            EmitterSpec sparkle = sparkleNode.Emitter!;
            NativeTestData.Check(sparkleNode.SpacePlane == LumenSpacePlane.Vertical3D, $"{id}: sparkle core uses Vertical3D");
            NativeTestData.Close(4.8f, sparkleNode.LocalPosition.Y, $"{id}: sparkle core altitude");
            NativeTestData.Check(sparkle.PreferredTier == LumenParticleTier.L1_NativeCpu, $"{id}: sparkle core stays on L1");
            NativeTestData.Check(sparkle.Bursts.Length == 1, $"{id}: sparkle core fires once");
            NativeTestData.Check(sparkle.Bursts[0].Time >= RimLumenDemoEffects.LaunchSeconds, $"{id}: sparkle core fires with the shell, not before");

            NativeTestData.Close(4.8f, asset.Nodes[3].LocalPosition.Y, $"{id}: flash altitude");
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
        NativeTestData.Close(Verse.Altitudes.AltitudeFor(Verse.AltitudeLayer.MoteOverhead), mistNode.LocalPosition.Y, "mist draws at the mote layer");
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

    // Ensures: the gold firework binds its burst to L2 and its trail/sparkle to L1, and a paused frame
    //          (DeltaTime = 0) advances the clock by nothing and spawns nothing.
    [LumenFact]
    public static void FireworkRoutesToGpuAndFreezesWhilePaused()
    {
        EffectAsset asset = RimLumenDemoEffects.CreateFireworkAsset(RimLumenDemoEffects.FireworkGoldId, 0);
        LumenVfxService service = VfxTestData.Service(asset);
        var engine = new SpyGpuEngine();
        service.GpuEngineProvider = () => engine;

        using ILumenEffectHandle handle = service.Play(new LumenPlayRequest
        {
            MapIndex = 111, EffectId = asset.EffectId,
            OriginWorld = new Vector3(10f, 0f, 20f),
            RealTime = false,
        });
        var typed = (EffectInstanceHandle)handle;
        VfxAssert.Check(typed.IsAlive && typed.IsEmitting, "firework starts playing");
        VfxAssert.Equal(1, typed.GpuEmitterCount);
        VfxAssert.Equal(2, typed.CpuEmitterCount);
        VfxAssert.Check(service.HasActiveWork, "a playing firework keeps the pipeline active");

        // Vertical3D keeps the node's identity rotation, so the Cone axis (local +Y) stays world +Y, and the
        // burst sits 4.8 above the play origin (y = 0 here) directly over the launcher.
        Vector3 upAxis = Vector3.TransformNormal(Vector3.UnitY, typed.NodeWorldMatrices[0]);
        VfxAssert.Close(Vector3.UnitY, upAxis);
        NativeTestData.Close(4.8f, typed.NodeWorldMatrices[1].Translation.Y, "apex altitude above the origin");
        NativeTestData.Close(0f, typed.NodeWorldMatrices[1].Translation.Z - typed.NodeWorldMatrices[0].Translation.Z, "apex has no up-screen offset");
        NativeTestData.Close(10f, typed.NodeWorldMatrices[0].Translation.X, "effect origin x");
        NativeTestData.Close(20f, typed.NodeWorldMatrices[0].Translation.Z, "effect origin z");

        for (int i = 0; i < 10; i++) VfxTestData.Step(service, 0f);
        VfxAssert.Equal(0f, typed.PlaybackTime);
        VfxAssert.Equal(0, service.Simulator.LiveParticleCount);
        VfxAssert.Check(engine.Updates.TrueForAll(update => update.Emitting), "paused frames never stop the emitters");

        for (int i = 0; i < 14; i++) VfxTestData.Step(service, 0.05f);
        VfxAssert.Check(typed.PlaybackTime > RimLumenDemoEffects.LaunchSeconds, "playback passes the apex");
        VfxAssert.Check(service.Simulator.LiveParticleCount > 0, "the L1 trail emitted particles");
    }

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

    // Ensures: Stop(allowFadeOut: false) tears the GPU instance down immediately, releases the L1 emitters
    //          and drops the handle, so nothing keeps the pipeline or the simulator busy.
    [LumenFact]
    public static void ImmediateStopReleasesEveryBinding()
    {
        EffectAsset asset = RimLumenDemoEffects.CreateFireworkAsset(RimLumenDemoEffects.FireworkCyanId, 1);
        LumenVfxService service = VfxTestData.Service(asset);
        var engine = new SpyGpuEngine();
        service.GpuEngineProvider = () => engine;

        ILumenEffectHandle handle = service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        for (int i = 0; i < 10; i++) VfxTestData.Step(service, 0.05f);
        VfxAssert.Check(service.Simulator.ActiveEmitterCount == 2, "both L1 emitters are alive before the stop");

        handle.Stop(allowFadeOut: false);
        handle.Dispose();
        VfxAssert.Check(!handle.IsAlive, "an immediate stop kills the handle");
        VfxAssert.Equal(1, engine.Destroys.Count);
        VfxAssert.Check(engine.Destroys[0].Immediate, "the L2 instance is destroyed immediately");

        VfxTestData.Step(service, 0.05f);
        VfxAssert.Equal(0, service.Simulator.ActiveEmitterCount);
        VfxAssert.Equal(0, service.Simulator.LiveParticleCount);
        VfxAssert.Equal(0, service.LiveHandleCount);
        VfxAssert.Check(!service.HasActiveWork, "no handle keeps the pipeline active after the stop");
    }

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

    // Ensures: the apex burst really is a single delayed pulse of EmissionScheduler, not a trickle: 0
    //          particles for the whole climb, exactly 1800 in the frame that crosses 0.65 s, then 0 again.
    [LumenFact]
    public static void ApexBurstFiresOnceAtTheScheduledFrame()
    {
        EffectAsset asset = RimLumenDemoEffects.CreateFireworkAsset(RimLumenDemoEffects.FireworkGoldId, 0);
        EmitterSpec burst = asset.Nodes[1].Emitter!;
        var scheduler = new EmissionScheduler(burst, 1u);

        double beforeApex = 0;
        for (int i = 0; i < 12; i++) beforeApex += scheduler.Advance(0.05f, emitting: true);
        VfxAssert.Equal(0, beforeApex);

        double atApex = scheduler.Advance(0.05f, emitting: true);
        VfxAssert.Equal(1800, atApex);

        double afterApex = 0;
        for (int i = 0; i < 10; i++) afterApex += scheduler.Advance(0.05f, emitting: true);
        VfxAssert.Equal(0, afterApex);

        var trail = new EmissionScheduler(asset.Nodes[0].Emitter!, 1u);
        VfxAssert.Check(trail.Advance(0.1f, emitting: true) > 0, "the launch trail emits by rate from the first frame");
    }

    [LumenFact]
    public static void LaunchTrailActuallyTravelsAlongPositiveY()
    {
        var asset = RimLumenDemoEffects.CreateFireworkAsset(RimLumenDemoEffects.FireworkGoldId, 0);
        var service = VfxTestData.Service(asset);
        using var handle = service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        VfxTestData.Step(service, 0.016f);
        EmitterSpec spec = asset.Nodes[0].Emitter!;
        VfxAssert.Equal("launch_trail", spec.Name);
        VfxAssert.Check(service.Simulator.LiveParticleCount > 0, "The first frame spawns the launch trail");
        Vector3 velocity = service.Simulator.LiveParticles[0].Velocity;
        Vector3 direction = Vector3.Normalize(velocity);
        VfxAssert.Check(direction.Y > 0.9f, $"Launch must travel +Y, actual direction={direction}");
        VfxAssert.Check(velocity.Y > 0.9f * spec.StartSpeedMin,
            $"Launch must retain its upward speed, actual velocity={velocity}");
        VfxAssert.Check(MathF.Abs(velocity.Z) < 0.15f * spec.StartSpeedMax,
            $"Launch must not drift along the ground +Z, actual velocity={velocity}");
    }

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
