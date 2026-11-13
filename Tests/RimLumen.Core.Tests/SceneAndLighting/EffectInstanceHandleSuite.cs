using System;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Lighting;
using RimLumen.Core.Scene;
using Verse;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class EffectInstanceHandleSuite
{
    [LumenFact]
    public static void TimelineCrossingsFireOnceAndProduceVisibleBursts()
    {
        var asset = new EffectAsset
        {
            EffectId = "b3.timeline", DefaultDuration = 2f,
            Nodes = new[] { new EffectNodeSpec { Name = "spark", Emitter = VfxTestData.Emitter() } },
            TimelineEvents = new[] { new TimelineEventSpec(0.5f, "first", 0), new TimelineEventSpec(1.5f, "second", 0) },
        };
        var service = VfxTestData.Service(asset);
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { EffectId = asset.EffectId });
        handle.SetParam("rate", 0f);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(0.25f, handle.PlaybackTime);
        GlowAssert.Equal(0, handle.TimelineEventFireCount);
        GlowAssert.Equal(0, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(1, handle.TimelineEventFireCount);
        GlowAssert.Equal(4, service.Simulator.LiveParticleCount);
        for (int i = 0; i < 3; i++) VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(1, handle.TimelineEventFireCount);
        GlowAssert.Equal(4, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(1.5f, handle.PlaybackTime);
        GlowAssert.Equal(2, handle.TimelineEventFireCount);
        GlowAssert.Equal(8, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 1f);
        GlowAssert.Equal(2, handle.TimelineEventFireCount);
        GlowAssert.Equal(8, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0f);
        GlowAssert.Equal(2, handle.TimelineEventFireCount);
    }

    [LumenFact]
    public static void LoopingTimelineRefiresAcrossWrapsWithoutWrappingPlaybackTime()
    {
        var asset = new EffectAsset
        {
            EffectId = "b3.timeline-loop", DefaultDuration = 2f,
            Nodes = new[] { new EffectNodeSpec { Name = "spark", Emitter = VfxTestData.Emitter(looping: true) } },
            TimelineEvents = new[] { new TimelineEventSpec(0.5f, "pulse", 0) },
        };
        var service = VfxTestData.Service(asset);
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { EffectId = asset.EffectId });
        handle.SetParam("rate", 0f);
        VfxTestData.Step(service, 0.5f);
        GlowAssert.Equal(1, handle.TimelineEventFireCount);
        GlowAssert.Equal(4, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 1.5f);
        GlowAssert.Equal(1, handle.TimelineEventFireCount);
        VfxTestData.Step(service, 0.5f);
        GlowAssert.Equal(2.5f, handle.PlaybackTime);
        GlowAssert.Equal(2, handle.TimelineEventFireCount);
        GlowAssert.Equal(8, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 4f);
        GlowAssert.Equal(6.5f, handle.PlaybackTime);
        GlowAssert.Equal(4, handle.TimelineEventFireCount);
        GlowAssert.Equal(16, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0f);
        GlowAssert.Equal(4, handle.TimelineEventFireCount);
    }

    [LumenFact]
    public static void TriggerFiresNamedEventsAndActivatesInitiallyPausedNodes()
    {
        var asset = new EffectAsset
        {
            EffectId = "b3.trigger", Nodes = new[]
            {
                new EffectNodeSpec
                {
                    Name = "idle", Emitter = new EmitterSpec
                    {
                        Name = "paused", RateOverTime = 4f, Bursts = new[] { (10f, 3) },
                        StartLifetimeMin = 20f, StartLifetimeMax = 20f,
                    },
                },
                new EffectNodeSpec { Name = "aura", Emitter = VfxTestData.Emitter() },
            },
            TimelineEvents = new[] { new TimelineEventSpec(20f, "flash", 0) },
        };
        var service = VfxTestData.Service(asset);
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { EffectId = asset.EffectId, InitialState = "aura" });
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(1, service.Simulator.LiveParticleCount);
        handle.Trigger("flash");
        GlowAssert.Equal(1, handle.TimelineEventFireCount);
        GlowAssert.Equal(4, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(5, service.Simulator.LiveParticleCount);
        handle.Trigger("idle");
        GlowAssert.Equal(8, service.Simulator.LiveParticleCount);
        GlowAssert.Equal(1, handle.TimelineEventFireCount);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(10, service.Simulator.LiveParticleCount);
        handle.Trigger("unknown-state");
        GlowAssert.Equal(10, service.Simulator.LiveParticleCount);
        GlowAssert.Equal(1, handle.TimelineEventFireCount);
        handle.Dispose();
        handle.Trigger("flash");
        handle.Trigger("idle");
        GlowAssert.Equal(0, service.Simulator.LiveParticleCount);
        GlowAssert.Equal(1, handle.TimelineEventFireCount);
    }

    [LumenFact]
    public static void ParamsScaleRealLightColourVerticesEmissionRateAndRootScale()
    {
        var asset = new EffectAsset
        {
            EffectId = "b3.params", Nodes = new[]
            {
                new EffectNodeSpec
                {
                    Name = "glow", Emitter = VfxTestData.Emitter(),
                    PointLight = new PointLightSpec(4f, 1f, new Vector3(0.5f, 0.25f, 0.125f)),
                },
            },
        };
        var service = VfxTestData.Service(asset);
        var sink = new RecordingGlowGridSink();
        using var driver = new MapGlowerDriver(sink);
        service.GlowerDriverProvider = _ => driver;
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest
        {
            EffectId = asset.EffectId, OriginWorld = new Vector3(10f, 2f, 20f), Scale = 2f,
        });
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(1, service.Simulator.LiveParticleCount);
        GlowAssert.Equal(Vector4.One, service.Simulator.Vertices[0].Color);
        GlowAssert.Equal(new ColorInt(128, 64, 32, 255), sink.Registrations[^1].Color);
        handle.SetParam("intensity", 2f);
        handle.SetParam("rate", 2f);
        handle.SetParam("scale", 3f);
        handle.SetParam("custom", 7f);
        VfxAssert.Check(handle.TryGetParam("custom", out float custom) && custom == 7f, "Unknown params remain retrievable");
        VfxAssert.Check(handle.TryGetParam("intensity", out float intensity) && intensity == 2f, "Built-in params remain retrievable");
        VfxAssert.Check(handle.TryGetParam("rate", out float rate) && rate == 2f, "Rate is stored");
        VfxAssert.Check(handle.TryGetParam("scale", out float scale) && scale == 3f, "Scale is stored");
        VfxAssert.Check(!handle.TryGetParam("missing", out _), "Missing param is not invented");
        VfxTestData.Step(service, 0.5f);
        GlowAssert.Equal(5, service.Simulator.LiveParticleCount);
        GlowAssert.Equal(new Vector4(2f), service.Simulator.Vertices[0].Color);
        GlowAssert.Equal(new ColorInt(255, 128, 64, 255), sink.Registrations[^1].Color);
        GlowAssert.Equal(new Vector3(10f, 2f, 20f), handle.RootWorld.Translation);
        GlowAssert.Equal(new Vector3(6f, 0f, 0f), Vector3.TransformNormal(Vector3.UnitX, handle.RootWorld));
        handle.Dispose();
        handle.SetParam("custom", 100f);
        VfxAssert.Check(handle.TryGetParam("custom", out custom) && custom == 7f, "Dead handles ignore params");
        GlowAssert.Equal(sink.Registrations.Count, sink.DeregisterCount);
    }

    [LumenFact]
    public static void FollowOverridesOriginAndFacingThenFreezesOnDetach()
    {
        var asset = new EffectAsset
        {
            EffectId = "b3.follow", Nodes = new[]
            {
                new EffectNodeSpec { Name = "root" },
                new EffectNodeSpec { Name = "child", ParentIndex = 0, LocalPosition = new Vector3(2f, 0f, 0f) },
            },
        };
        var service = VfxTestData.Service(asset);
        Vector3 position = new(10f, 0f, 20f);
        float facing = 90f;
        bool attached = true;
        bool Follow(out Vector3 world, out float degrees) { world = position; degrees = facing; return attached; }
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest
        {
            EffectId = asset.EffectId, OriginWorld = new Vector3(-20f), RotationDegrees = 180f,
            TargetWorld = new Vector3(-20f, -20f, 10f), Follow = Follow,
        });
        GlowAssert.Equal(position, handle.RootWorld.Translation);
        VfxAssert.Close(Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitZ, handle.RootWorld));
        position = new Vector3(30f, 0f, 40f);
        facing = 0f;
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(position, handle.RootWorld.Translation);
        GlowAssert.Equal(new Vector3(32f, 0f, 40f), handle.NodeWorldMatrices[1].Translation);
        var frozen = handle.RootWorld;
        var frozenChild = handle.NodeWorldMatrices[1];
        attached = false;
        position = new Vector3(100f);
        facing = 270f;
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(frozen, handle.RootWorld);
        GlowAssert.Equal(frozenChild, handle.NodeWorldMatrices[1]);
        using var targeted = (EffectInstanceHandle)service.Play(new LumenPlayRequest
        {
            EffectId = asset.EffectId, OriginWorld = new Vector3(10f, 0f, 20f), TargetWorld = new Vector3(20f, 0f, 20f),
            RotationDegrees = 180f,
        });
        VfxAssert.Close(Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitZ, targeted.RootWorld));
    }

    [LumenFact]
    public static void LightsResolveLazilyAndUseCurrentNodeCellAndPlaybackCurve()
    {
        var asset = new EffectAsset
        {
            EffectId = "b3.light-lazy", Nodes = new[]
            {
                new EffectNodeSpec
                {
                    Name = "light", LocalPosition = new Vector3(2f, 0f, 0f),
                    PointLight = new PointLightSpec(5f, 1f, Vector3.One,
                        new ScalarCurve(new[] { new RimLumen.Core.Contracts.CurveKey(0f, 0f, 1f, 1f),
                            new RimLumen.Core.Contracts.CurveKey(1f, 1f, 1f, 1f) })),
                },
            },
        };
        var service = VfxTestData.Service(asset);
        var sink = new RecordingGlowGridSink();
        using var driver = new MapGlowerDriver(sink);
        MapGlowerDriver? available = null;
        int observedMap = -1;
        service.GlowerDriverProvider = map => { observedMap = map; return available; };
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest
        {
            EffectId = asset.EffectId, MapIndex = 42, OriginWorld = new Vector3(10f, 0f, 20f),
        });
        GlowAssert.Equal(42, observedMap);
        GlowAssert.Equal(0, handle.LightCount);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(0, handle.LightCount);
        available = driver;
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(1, handle.LightCount);
        GlowAssert.Equal(new IntVec3(12, 0, 20), sink.Registrations[^1].Cell);
        GlowAssert.Equal(new ColorInt(128, 128, 128, 255), sink.Registrations[^1].Color);
        handle.Dispose();
        GlowAssert.Equal(0, driver.TrackedLightCount);
        GlowAssert.Equal(sink.Registrations.Count, sink.DeregisterCount);
    }

    [LumenFact]
    public static void FadeStopDrainsParticlesFadesLightsAndCannotBeRestarted()
    {
        var asset = LitAsset("b3.fade");
        var service = VfxTestData.Service(asset);
        var sink = new RecordingGlowGridSink();
        using var driver = new MapGlowerDriver(sink);
        service.GlowerDriverProvider = _ => driver;
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { EffectId = asset.EffectId });
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(1, service.Simulator.LiveParticleCount);
        GlowAssert.Equal(new ColorInt(255, 255, 255, 255), sink.Registrations[^1].Color);
        handle.Stop(true);
        VfxAssert.Check(handle.IsAlive && !handle.IsEmitting, "Fade stops new emission, not existing particles");
        handle.Trigger("glow");
        handle.Trigger("flash");
        GlowAssert.Equal(0, handle.TimelineEventFireCount);
        GlowAssert.Equal(1, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0.125f);
        GlowAssert.Equal(new ColorInt(128, 128, 128, 255), sink.Registrations[^1].Color);
        GlowAssert.Equal(1, service.Simulator.LiveParticleCount);
        handle.Stop(true);
        VfxTestData.Step(service, 0.125f);
        GlowAssert.Equal(0, handle.LightCount);
        GlowAssert.Equal(0, driver.TrackedLightCount);
        VfxAssert.Check(handle.IsAlive, "L1 particles gate death after lights finish");
        GlowAssert.Equal(sink.Registrations.Count, sink.DeregisterCount);
        VfxTestData.Step(service, 0.75f);
        GlowAssert.Equal(0, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0f);
        VfxAssert.Check(!handle.IsAlive && !service.HasActiveWork, "Next pre-simulation capture sees the drained L1 emitter");
        GlowAssert.Equal(0, handle.CpuEmitterCount);
        GlowAssert.Equal(0, service.LiveHandleCount);
    }

    [LumenFact]
    public static void ImmediateStopAndDisposeRemoveParticlesAndLightsWithoutAnotherFrame()
    {
        var asset = LitAsset("b3.immediate");
        var service = VfxTestData.Service(asset);
        var sink = new RecordingGlowGridSink();
        using var driver = new MapGlowerDriver(sink);
        service.GlowerDriverProvider = _ => driver;
        using var first = (EffectInstanceHandle)service.Play(new LumenPlayRequest { EffectId = asset.EffectId });
        using var second = (EffectInstanceHandle)service.Play(new LumenPlayRequest { EffectId = asset.EffectId });
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(2, driver.TrackedLightCount);
        GlowAssert.Equal(2, service.Simulator.LiveParticleCount);
        first.Stop(false);
        VfxAssert.Check(!first.IsAlive, "Immediate stop is immediate");
        GlowAssert.Equal(1, service.Simulator.LiveParticleCount);
        GlowAssert.Equal(1, driver.TrackedLightCount);
        second.Dispose();
        second.Dispose();
        GlowAssert.Equal(0, service.Simulator.LiveParticleCount);
        GlowAssert.Equal(0, service.Simulator.ActiveEmitterCount);
        GlowAssert.Equal(0, driver.TrackedLightCount);
        GlowAssert.Equal(0, first.LightCount);
        GlowAssert.Equal(0, first.CpuEmitterCount);
        GlowAssert.Equal(sink.Registrations.Count, sink.DeregisterCount);
        VfxAssert.Check(!service.HasActiveWork, "Immediate teardown leaves no service work");
    }

    [LumenFact]
    public static void GpuTriggerActivatesAndFadeStopTransfersDrainOwnership()
    {
        var asset = new EffectAsset
        {
            EffectId = "b3.gpu-stop", Nodes = new[]
            {
                new EffectNodeSpec { Name = "gpu", Emitter = VfxTestData.Emitter() },
            },
            TimelineEvents = new[] { new TimelineEventSpec(1f, "flash", 0) },
        };
        var service = VfxTestData.Service(asset);
        var engine = new SpyGpuEngine();
        service.GpuEngineProvider = () => engine;
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { EffectId = asset.EffectId, InitialState = "other" });
        GlowAssert.Equal(false, engine.Updates[^1].Emitting);
        handle.Trigger("flash");
        GlowAssert.Equal(true, engine.Updates[^1].Emitting);
        GlowAssert.Equal(1, handle.TimelineEventFireCount);
        handle.Stop(true);
        GlowAssert.Equal((0, false), engine.Destroys[0]);
        GlowAssert.Equal(0, handle.GpuEmitterCount);
        VfxAssert.Check(!handle.IsAlive && engine.HasActiveWork, "GPU drain belongs to engine, not a liveness proxy");
        handle.Dispose();
        GlowAssert.Equal(1, engine.Destroys.Count);
        VfxTestData.Step(service, 2f);
        GlowAssert.Equal(0, service.LiveHandleCount);
    }

    private static EffectAsset LitAsset(string id) => new()
    {
        EffectId = id, Nodes = new[]
        {
            new EffectNodeSpec
            {
                Name = "glow", Emitter = VfxTestData.Emitter(lifetime: 1f),
                PointLight = new PointLightSpec(4f, 1f, Vector3.One),
            },
        },
        TimelineEvents = new[] { new TimelineEventSpec(1f, "flash", 0) },
    };
}
