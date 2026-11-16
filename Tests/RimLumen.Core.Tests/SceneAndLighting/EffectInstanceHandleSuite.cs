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
    public static void FailedSecondNodeRollsBackTheFirstCpuEmitter()
    {
        var asset = new EffectAsset
        {
            EffectId = "rollback.nodes", Nodes = new[]
            {
                new EffectNodeSpec { Name = "cpu", Emitter = VfxTestData.Emitter(tier: LumenParticleTier.L1_NativeCpu) },
                new EffectNodeSpec
                {
                    Name = "gpu",
                    Emitter = new EmitterSpec
                    {
                        Name = "unsupported-mesh",
                        MaxParticles = 4,
                        Duration = 1f,
                        BillboardMode = LumenBillboardMode.Mesh,
                        PreferredTier = LumenParticleTier.L2_GpuCompute,
                    },
                },
            },
        };
        var service = VfxTestData.Service(asset);
        using var engine = new RimLumen.Core.ParticlesGpu.LumenGpuParticleEngine(8);
        service.GpuEngineProvider = () => engine;
        GlowAssert.Throws<NotSupportedException>(() => service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId }));
        GlowAssert.Equal(0, service.Simulator.ActiveEmitterCount);
        GlowAssert.Equal(0, service.LiveHandleCount);
    }

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
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        handle.SetParam("rate", 0f);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(0.25f, handle.PlaybackTime);
        GlowAssert.Equal(0, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(4, service.Simulator.LiveParticleCount);
        for (int i = 0; i < 3; i++) VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(4, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(1.5f, handle.PlaybackTime);
        GlowAssert.Equal(8, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 1f);
        GlowAssert.Equal(8, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0f);
        GlowAssert.Equal(8, service.Simulator.LiveParticleCount);
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
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        handle.SetParam("rate", 0f);
        VfxTestData.Step(service, 0.5f);
        GlowAssert.Equal(4, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 1.5f);
        GlowAssert.Equal(4, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0.5f);
        GlowAssert.Equal(2.5f, handle.PlaybackTime);
        GlowAssert.Equal(8, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 4f);
        GlowAssert.Equal(6.5f, handle.PlaybackTime);
        GlowAssert.Equal(16, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0f);
        GlowAssert.Equal(16, service.Simulator.LiveParticleCount);
    }

    [LumenFact]
    public static void TriggerFiresNamedEventsAndRestartsNodesOutsideTheCurrentState()
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
            TimelineEvents = new[] { new TimelineEventSpec(20f, "flash", 0, "aura") },
            States = new[]
            {
                new EffectStateSpec { Name = "idle", Nodes = new[] { "idle" }, Loop = true },
                new EffectStateSpec { Name = "aura", Nodes = new[] { "aura" }, Loop = true },
            },
        };
        var service = VfxTestData.Service(asset);
        GlowAssert.Throws<ArgumentException>(() => service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId, InitialState = "missing" }));
        GlowAssert.Equal(0, service.LiveHandleCount);
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId, InitialState = "aura" });
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(1, service.Simulator.LiveParticleCount);
        handle.Trigger("flash");
        GlowAssert.Equal(1, service.Simulator.LiveParticleCount); // D7 queues the pulse; this call does not simulate.
        VfxTestData.Step(service, 0f);
        GlowAssert.Equal(4, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(5, service.Simulator.LiveParticleCount);
        // Trigger restarts the idle node's own schedule (rate 4/s) without leaving the aura state.
        handle.Trigger("idle");
        GlowAssert.Equal(5, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(7, service.Simulator.LiveParticleCount);
        handle.Trigger("unknown-state");
        GlowAssert.Equal(7, service.Simulator.LiveParticleCount);
        handle.Dispose();
        handle.Trigger("flash");
        handle.Trigger("idle");
        GlowAssert.Equal(0, service.Simulator.LiveParticleCount);
    }

    [LumenFact]
    public static void ParamsScaleRealLightColourRgbVerticesEmissionRateAndRootScale()
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
            MapIndex = 111, EffectId = asset.EffectId, OriginWorld = new Vector3(10f, 2f, 20f), Scale = 2f,
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
        GlowAssert.Equal(new Vector4(2f, 2f, 2f, 1f), service.Simulator.Vertices[0].Color);
        GlowAssert.Equal(new ColorInt(255, 128, 64, 255), sink.Registrations[^1].Color);
        GlowAssert.Equal(new Vector3(10f, 2f, 20f), handle.RootWorld.Translation);
        GlowAssert.Equal(new Vector3(6f, 0f, 0f), Vector3.TransformNormal(Vector3.UnitX, handle.RootWorld));
        handle.Dispose();
        handle.SetParam("custom", 100f);
        VfxAssert.Check(handle.TryGetParam("custom", out custom) && custom == 7f, "Dead handles ignore params");
        GlowAssert.Equal(sink.Registrations.Count, sink.DeregisterCount);
    }

    [LumenFact]
    public static void PhaseAIntensityRejectsInvalidWithoutMutation()
    {
        var asset = new EffectAsset { EffectId = "intensity", Nodes = new[] { new EffectNodeSpec { Name = "root" } } };
        var service = VfxTestData.Service(asset);
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        handle.SetParam("intensity", 2f);
        GlowAssert.Throws<ArgumentOutOfRangeException>(() => handle.SetParam("intensity", -1f));
        GlowAssert.Throws<ArgumentOutOfRangeException>(() => handle.SetParam("intensity", float.NaN));
        GlowAssert.Throws<ArgumentOutOfRangeException>(() => handle.SetParam("intensity", float.PositiveInfinity));
        GlowAssert.Throws<ArgumentOutOfRangeException>(() => handle.SetParam("intensity", float.NegativeInfinity));
        VfxAssert.Check(handle.TryGetParam("intensity", out float intensity) && intensity == 2f, "Invalid intensity must not mutate state");
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
            MapIndex = 111, EffectId = asset.EffectId, OriginWorld = new Vector3(-20f), RotationDegrees = 180f,
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
            MapIndex = 111, EffectId = asset.EffectId, OriginWorld = new Vector3(10f, 0f, 20f), TargetWorld = new Vector3(20f, 0f, 20f),
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
    public static void DisposedMapStopsItsHandleWithoutMigratingLightsToAnotherMap()
    {
        var asset = LitAsset("b3.map-dispose");
        var service = VfxTestData.Service(asset);
        service.GlowerDriverProvider = mapIndex =>
            LumenMapLightingRegistry.TryResolve(mapIndex, out var driver) ? driver : null;
        var firstSink = new RecordingGlowGridSink();
        var secondSink = new RecordingGlowGridSink();
        using var first = new MapGlowerDriver(firstSink);
        using var second = new MapGlowerDriver(secondSink);
        LumenMapLightingRegistry.Register(111, first);
        LumenMapLightingRegistry.Register(222, second);
        try
        {
            using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest
            {
                EffectId = asset.EffectId, MapIndex = 111,
            });
            VfxTestData.Step(service, 0.25f);
            GlowAssert.Equal(true, handle.IsAlive);
            GlowAssert.Equal(1, firstSink.Registrations.Count);
            GlowAssert.Equal(1, firstSink.LiveHandles.Count);
            GlowAssert.Equal(1, service.Simulator.LiveParticleCount);
            first.Dispose();
            VfxTestData.Step(service, 0.25f);

            VfxAssert.Check(!handle.IsAlive && secondSink.Registrations.Count == 0,
                $"Map 111 teardown expected IsAlive=False and map 222 registrations=0; got IsAlive={handle.IsAlive}, map 222 registrations={secondSink.Registrations.Count}.");
            GlowAssert.Equal(false, handle.IsEmitting);
            GlowAssert.Equal(0, handle.LightCount);
            GlowAssert.Equal(0, handle.CpuEmitterCount);
            GlowAssert.Equal(0, service.Simulator.ActiveEmitterCount);
            GlowAssert.Equal(0, service.Simulator.LiveParticleCount);
            GlowAssert.Equal(0, service.LiveHandleCount);
            GlowAssert.Equal(0, firstSink.LiveHandles.Count);
            GlowAssert.Equal(1, firstSink.DeregisterCount);
            GlowAssert.Equal(firstSink.Registrations.Count, firstSink.DeregisterCount);
            GlowAssert.Equal(0, secondSink.LiveHandles.Count);
            GlowAssert.Equal(0, second.TrackedLightCount);
        }
        finally
        {
            LumenMapLightingRegistry.Unregister(111, first);
            LumenMapLightingRegistry.Unregister(222, second);
        }
    }

    [LumenFact]
    public static void ExplicitMapKeepsBoundDriverWhenSecondMapRegistersAndStopsWhenBoundDriverDisposes()
    {
        var asset = LitAsset("b3.sticky-default-map");
        var service = VfxTestData.Service(asset);
        service.GlowerDriverProvider = mapIndex =>
            LumenMapLightingRegistry.TryResolve(mapIndex, out var driver) ? driver : null;
        var sinkA = new RecordingGlowGridSink();
        var sinkB = new RecordingGlowGridSink();
        using var driverA = new MapGlowerDriver(sinkA);
        using var driverB = new MapGlowerDriver(sinkB);
        LumenMapLightingRegistry.Register(111, driverA);
        try
        {
            using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest
            {
                MapIndex = 111, EffectId = asset.EffectId,
            });
            VfxTestData.Step(service, 0.25f);
            GlowAssert.Equal(1, sinkA.Registrations.Count);
            GlowAssert.Equal(0, sinkA.DeregisterCount);
            GlowAssert.Equal(1, handle.LightCount);

            LumenMapLightingRegistry.Register(222, driverB);
            VfxTestData.Step(service, 0.25f);
            VfxAssert.Check(sinkA.Registrations.Count == 1 && sinkA.DeregisterCount == 0 && sinkB.Registrations.Count == 0,
                $"Expected map A to keep 1 registration (0 deregistrations) and map B to have 0; got A=({sinkA.Registrations.Count}, dereg={sinkA.DeregisterCount}), B={sinkB.Registrations.Count}.");
            GlowAssert.Equal(true, handle.IsAlive);
            GlowAssert.Equal(1, handle.LightCount);
            GlowAssert.Equal(0, driverB.TrackedLightCount);

            driverA.Dispose();
            VfxTestData.Step(service, 0.25f);
            GlowAssert.Equal(false, handle.IsAlive);
            GlowAssert.Equal(false, handle.IsEmitting);
            GlowAssert.Equal(0, handle.LightCount);
            GlowAssert.Equal(0, sinkB.Registrations.Count);
            GlowAssert.Equal(0, driverB.TrackedLightCount);
            GlowAssert.Equal(sinkA.Registrations.Count, sinkA.DeregisterCount);
        }
        finally
        {
            LumenMapLightingRegistry.Unregister(111, driverA);
            LumenMapLightingRegistry.Unregister(222, driverB);
        }
    }

    [LumenFact]
    public static void FadeStopDrainsParticlesFadesLightsAndCannotBeRestarted()
    {
        var asset = LitAsset("b3.fade");
        var service = VfxTestData.Service(asset);
        var sink = new RecordingGlowGridSink();
        using var driver = new MapGlowerDriver(sink);
        service.GlowerDriverProvider = _ => driver;
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(1, service.Simulator.LiveParticleCount);
        GlowAssert.Equal(new ColorInt(255, 255, 255, 255), sink.Registrations[^1].Color);
        handle.Stop(true);
        VfxAssert.Check(handle.IsAlive && !handle.IsEmitting, "Fade stops new emission, not existing particles");
        handle.Trigger("glow");
        handle.Trigger("flash");
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
        using var first = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        using var second = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        int finished = 0;
        first.Finished += () => finished++;
        second.Finished += () => finished++;
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
        GlowAssert.Equal(0, finished);
        VfxAssert.Check(service.HasActiveWork, "Finished is still owed, so the service needs one more frame");
        VfxTestData.Step(service, 0f);
        GlowAssert.Equal(2, finished);
        VfxAssert.Check(!service.HasActiveWork, "Immediate teardown leaves no service work once Finished is raised");
    }

    [LumenFact]
    public static void GpuTriggerActivatesAndFadeStopKeepsDrainOwnershipUntilTheEngineReclaims()
    {
        var asset = new EffectAsset
        {
            EffectId = "b3.gpu-stop", Nodes = new[]
            {
                new EffectNodeSpec { Name = "gpu", Emitter = VfxTestData.Emitter() },
            },
            TimelineEvents = new[] { new TimelineEventSpec(1f, "flash", 0, "other") },
            States = new[]
            {
                new EffectStateSpec { Name = "other", Loop = true },
                new EffectStateSpec { Name = "lit", Nodes = new[] { "gpu" }, Loop = true },
            },
        };
        var service = VfxTestData.Service(asset);
        var engine = new SpyGpuEngine();
        service.GpuEngineProvider = () => engine;
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        GlowAssert.Equal(false, engine.Updates[^1].Emitting);
        handle.Trigger("flash");
        GlowAssert.Equal((0, 4), engine.Bursts[^1]);
        GlowAssert.Equal(0, engine.Restarts.Count);
        handle.GoTo("lit");
        GlowAssert.Equal(0, engine.Restarts[^1]);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(true, engine.Updates[^1].Emitting);
        handle.Stop(true);
        GlowAssert.Equal((0, false), engine.Destroys[0]);
        VfxTestData.Step(service, 2f);
        VfxAssert.Check(handle.IsAlive && handle.GpuEmitterCount == 1, "the handle owns the GPU drain until the engine reclaims it");
        handle.Dispose();
        GlowAssert.Equal((0, true), engine.Destroys[^1]);
        VfxAssert.Check(!handle.IsAlive && handle.GpuEmitterCount == 0, "Dispose during the GPU drain tears it down");
        VfxTestData.Step(service, 0f);
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
