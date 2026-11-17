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
        VfxAssert.Throws<NotSupportedException>(() => service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId }));
        VfxAssert.Equal(0, service.Simulator.ActiveEmitterCount);
        VfxAssert.Equal(0, service.LiveHandleCount);
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
        VfxAssert.Equal(0.25f, handle.PlaybackTime);
        VfxAssert.Equal(0, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0.25f);
        VfxAssert.Equal(4, service.Simulator.LiveParticleCount);
        for (int i = 0; i < 3; i++) VfxTestData.Step(service, 0.25f);
        VfxAssert.Equal(4, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0.25f);
        VfxAssert.Equal(1.5f, handle.PlaybackTime);
        VfxAssert.Equal(8, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 1f);
        VfxAssert.Equal(8, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0f);
        VfxAssert.Equal(8, service.Simulator.LiveParticleCount);
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
        VfxAssert.Equal(4, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 1.5f);
        VfxAssert.Equal(4, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0.5f);
        VfxAssert.Equal(2.5f, handle.PlaybackTime);
        VfxAssert.Equal(8, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 4f);
        VfxAssert.Equal(6.5f, handle.PlaybackTime);
        VfxAssert.Equal(16, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0f);
        VfxAssert.Equal(16, service.Simulator.LiveParticleCount);
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
                        Name = "paused", Duration = 10f, RateOverTime = 4f, Bursts = new[] { new ParticleBurstSpec(10f, 3) },
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
        VfxAssert.Throws<ArgumentException>(() => service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId, InitialState = "missing" }));
        VfxAssert.Equal(0, service.LiveHandleCount);
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId, InitialState = "aura" });
        VfxTestData.Step(service, 0.25f);
        VfxAssert.Equal(1, service.Simulator.LiveParticleCount);
        handle.Trigger("flash");
        VfxAssert.Equal(1, service.Simulator.LiveParticleCount); // D7 queues the pulse; this call does not simulate.
        VfxTestData.Step(service, 0f);
        VfxAssert.Equal(4, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0.25f);
        VfxAssert.Equal(5, service.Simulator.LiveParticleCount);
        // Trigger restarts the idle node's own schedule (rate 4/s) without leaving the aura state.
        handle.Trigger("idle");
        VfxAssert.Equal(5, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0.25f);
        VfxAssert.Equal(7, service.Simulator.LiveParticleCount);
        handle.Trigger("unknown-state");
        VfxAssert.Equal(7, service.Simulator.LiveParticleCount);
        handle.Dispose();
        handle.Trigger("flash");
        handle.Trigger("idle");
        VfxAssert.Equal(0, service.Simulator.LiveParticleCount);
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
                    PointLight = new PointLightSpec(4f, new Vector3(0.5f, 0.25f, 0.125f)),
                },
            },
        };
        var service = VfxTestData.Service(asset);
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest
        {
            MapIndex = 111, EffectId = asset.EffectId, OriginWorld = new Vector3(10f, 2f, 20f), Scale = 2f,
        });
        VfxTestData.Step(service, 0.25f, currentMapIndex: 111);
        VfxAssert.Equal(1, service.Simulator.LiveParticleCount);
        VfxAssert.Equal(Vector4.One, service.Simulator.Vertices[0].Color);
        VfxAssert.Equal(new VisualPointLight(10f, 20f, 4f, new Vector3(0.5f, 0.25f, 0.125f)), service.Lights[0]);
        handle.SetParam("intensity", 2f);
        handle.SetParam("rate", 2f);
        handle.SetParam("scale", 3f);
        handle.SetParam("custom", 7f);
        VfxAssert.Check(handle.TryGetParam("custom", out float custom) && custom == 7f, "Unknown params remain retrievable");
        VfxAssert.Check(handle.TryGetParam("intensity", out float intensity) && intensity == 2f, "Built-in params remain retrievable");
        VfxAssert.Check(handle.TryGetParam("rate", out float rate) && rate == 2f, "Rate is stored");
        VfxAssert.Check(handle.TryGetParam("scale", out float scale) && scale == 3f, "Scale is stored");
        VfxAssert.Check(!handle.TryGetParam("missing", out _), "Missing param is not invented");
        VfxTestData.Step(service, 0.5f, currentMapIndex: 111);
        VfxAssert.Equal(5, service.Simulator.LiveParticleCount);
        VfxAssert.Equal(Vector4.One, service.Simulator.Vertices[0].Color);
        VfxAssert.Equal(2f, service.Simulator.Vertices[0].HdrGain);
        // Unclamped: the light pool radius is not scaled with the effect, and intensity 2 doubles the colour.
        VfxAssert.Equal(new VisualPointLight(10f, 20f, 4f, new Vector3(1f, 0.5f, 0.25f)), service.Lights[0]);
        VfxAssert.Equal(new Vector3(10f, 2f, 20f), handle.RootWorld.Translation);
        VfxAssert.Equal(new Vector3(6f, 0f, 0f), Vector3.TransformNormal(Vector3.UnitX, handle.RootWorld));
        handle.Dispose();
        handle.SetParam("custom", 100f);
        VfxAssert.Check(handle.TryGetParam("custom", out custom) && custom == 7f, "Dead handles ignore params");
        VfxAssert.Equal(0, handle.LightCount);
    }

    [LumenFact]
    public static void PhaseAIntensityRejectsInvalidWithoutMutation()
    {
        var asset = new EffectAsset { EffectId = "intensity", Nodes = new[] { new EffectNodeSpec { Name = "root" } } };
        var service = VfxTestData.Service(asset);
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        handle.SetParam("intensity", 2f);
        VfxAssert.Throws<ArgumentOutOfRangeException>(() => handle.SetParam("intensity", -1f));
        VfxAssert.Throws<ArgumentOutOfRangeException>(() => handle.SetParam("intensity", float.NaN));
        VfxAssert.Throws<ArgumentOutOfRangeException>(() => handle.SetParam("intensity", float.PositiveInfinity));
        VfxAssert.Throws<ArgumentOutOfRangeException>(() => handle.SetParam("intensity", float.NegativeInfinity));
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
        VfxAssert.Equal(position, handle.RootWorld.Translation);
        VfxAssert.Close(Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitZ, handle.RootWorld));
        position = new Vector3(30f, 0f, 40f);
        facing = 0f;
        VfxTestData.Step(service, 0.25f);
        VfxAssert.Equal(position, handle.RootWorld.Translation);
        VfxAssert.Equal(new Vector3(32f, 0f, 40f), handle.NodeWorldMatrices[1].Translation);
        var frozen = handle.RootWorld;
        var frozenChild = handle.NodeWorldMatrices[1];
        attached = false;
        position = new Vector3(100f);
        facing = 270f;
        VfxTestData.Step(service, 0.25f);
        VfxAssert.Equal(frozen, handle.RootWorld);
        VfxAssert.Equal(frozenChild, handle.NodeWorldMatrices[1]);
        using var targeted = (EffectInstanceHandle)service.Play(new LumenPlayRequest
        {
            MapIndex = 111, EffectId = asset.EffectId, OriginWorld = new Vector3(10f, 0f, 20f), TargetWorld = new Vector3(20f, 0f, 20f),
            RotationDegrees = 180f,
        });
        VfxAssert.Close(Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitZ, targeted.RootWorld));
    }

    [LumenFact]
    public static void LightFollowsNodeGroundPositionEveryFrameAndPlaysItsCurve()
    {
        var asset = new EffectAsset
        {
            EffectId = "b3.light-follow", Nodes = new[]
            {
                new EffectNodeSpec { Name = "root" },
                new EffectNodeSpec
                {
                    Name = "light", ParentIndex = 0, LocalPosition = new Vector3(2f, 3f, 0f),
                    PointLight = new PointLightSpec(5f, Vector3.One,
                        new ScalarCurve(new[] { new RimLumen.Core.Contracts.CurveKey(0f, 0f, 1f, 1f),
                            new RimLumen.Core.Contracts.CurveKey(1f, 1f, 1f, 1f) })),
                },
            },
        };
        var service = VfxTestData.Service(asset);
        Vector3 position = new(10f, 0f, 20f);
        bool Follow(out Vector3 world, out float degrees) { world = position; degrees = 0f; return true; }
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest
        {
            EffectId = asset.EffectId, MapIndex = 42, OriginWorld = position, Follow = Follow,
        });
        VfxTestData.Step(service, 0.25f, currentMapIndex: 42);
        // The node's height (Y = 3) is dropped: the pool lies on the ground under the node.
        VfxAssert.Equal(1, service.Lights.Count);
        VfxAssert.Check(service.Lights[0] is { X: 12f, Z: 20f, Radius: 5f }, $"Pool centre/radius, got {service.Lights[0]}");
        VfxAssert.Close(new Vector3(0.25f), service.Lights[0].Light);
        // A 0.01-cell move shows up in the very next frame: no cell or re-registration quantization.
        position = new Vector3(10.01f, 0f, 20f);
        VfxTestData.Step(service, 0.25f, currentMapIndex: 42);
        VfxAssert.Check(MathF.Abs(service.Lights[0].X - 12.01f) < 0.0001f, $"Pool follows the node, got X={service.Lights[0].X}");
        VfxAssert.Close(new Vector3(0.5f), service.Lights[0].Light);
        handle.Dispose();
        VfxAssert.Equal(0, handle.LightCount);
    }

    [LumenFact]
    public static void LightPoolsOnlyReachTheMapTheirEffectWasPlayedOn()
    {
        var asset = LitAsset("b3.light-map");
        var service = VfxTestData.Service(asset);
        using var onFirst = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        using var onSecond = (EffectInstanceHandle)service.Play(new LumenPlayRequest
        {
            MapIndex = 222, EffectId = asset.EffectId, OriginWorld = new Vector3(5f, 0f, 6f),
        });
        VfxTestData.Step(service, 0.25f, currentMapIndex: 222);
        VfxAssert.Check(onFirst.LightCount == 1 && onSecond.LightCount == 1, "Both lights stay on while their maps are not shown");
        VfxAssert.Equal(1, service.Lights.Count);
        VfxAssert.Check(service.Lights[0] is { X: 5f, Z: 6f }, $"Only the map 222 pool is drawn, got {service.Lights[0]}");
        VfxTestData.Step(service, 0f, currentMapIndex: 333);
        VfxAssert.Equal(0, service.Lights.Count);
    }

    [LumenFact]
    public static void HandlerImmediateStopRemovesTheLightFromTheSameFrame()
    {
        var asset = LitAsset("b3.light-handler-stop", new[] { new TimelineEventSpec(0.25f, "ping", -1, Notify: true) });
        var service = VfxTestData.Service(asset);
        using var stopped = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        using var kept = (EffectInstanceHandle)service.Play(new LumenPlayRequest
        {
            MapIndex = 111, EffectId = asset.EffectId, OriginWorld = new Vector3(5f, 0f, 6f),
        });
        stopped.Notify += _ => stopped.Stop(false);
        VfxTestData.Step(service, 0.25f, currentMapIndex: 111);
        VfxAssert.Check(!stopped.IsAlive, "The handler stopped its handle");
        VfxAssert.Equal(1, service.Lights.Count);
        VfxAssert.Check(service.Lights[0] is { X: 5f, Z: 6f }, $"Only the surviving pool is drawn, got {service.Lights[0]}");
    }

    [LumenFact]
    public static void FadeStopDrainsParticlesFadesLightsAndCannotBeRestarted()
    {
        var asset = LitAsset("b3.fade");
        var service = VfxTestData.Service(asset);
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        VfxTestData.Step(service, 0.25f, currentMapIndex: 111);
        VfxAssert.Equal(1, service.Simulator.LiveParticleCount);
        VfxAssert.Close(Vector3.One, service.Lights[0].Light);
        handle.Stop(true);
        VfxAssert.Check(handle.IsAlive && !handle.IsEmitting, "Fade stops new emission, not existing particles");
        handle.Trigger("glow");
        handle.Trigger("flash");
        VfxAssert.Equal(1, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0.125f, currentMapIndex: 111);
        VfxAssert.Close(new Vector3(0.5f), service.Lights[0].Light);
        VfxAssert.Equal(1, service.Simulator.LiveParticleCount);
        handle.Stop(true);
        VfxTestData.Step(service, 0.125f, currentMapIndex: 111);
        VfxAssert.Equal(0, handle.LightCount);
        VfxAssert.Equal(0, service.Lights.Count);
        VfxAssert.Check(handle.IsAlive, "L1 particles gate death after lights finish");
        VfxTestData.Step(service, 0.75f);
        VfxAssert.Equal(0, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0f);
        VfxAssert.Check(!handle.IsAlive && !service.HasActiveWork, "Next pre-simulation capture sees the drained L1 emitter");
        VfxAssert.Equal(0, handle.CpuEmitterCount);
        VfxAssert.Equal(0, service.LiveHandleCount);
    }

    [LumenFact]
    public static void ImmediateStopAndDisposeRemoveParticlesAndLightsWithoutAnotherFrame()
    {
        var asset = LitAsset("b3.immediate");
        var service = VfxTestData.Service(asset);
        using var first = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        using var second = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        int finished = 0;
        first.Finished += () => finished++;
        second.Finished += () => finished++;
        VfxTestData.Step(service, 0.25f, currentMapIndex: 111);
        VfxAssert.Equal(2, service.Lights.Count);
        VfxAssert.Equal(2, service.Simulator.LiveParticleCount);
        first.Stop(false);
        VfxAssert.Check(!first.IsAlive, "Immediate stop is immediate");
        VfxAssert.Equal(1, service.Simulator.LiveParticleCount);
        VfxAssert.Equal(0, first.LightCount);
        VfxAssert.Equal(1, second.LightCount);
        second.Dispose();
        second.Dispose();
        VfxAssert.Equal(0, service.Simulator.LiveParticleCount);
        VfxAssert.Equal(0, service.Simulator.ActiveEmitterCount);
        VfxAssert.Equal(0, second.LightCount);
        VfxAssert.Equal(0, first.CpuEmitterCount);
        VfxAssert.Equal(0, finished);
        VfxAssert.Check(service.HasActiveWork, "Finished is still owed, so the service needs one more frame");
        VfxTestData.Step(service, 0f, currentMapIndex: 111);
        VfxAssert.Equal(0, service.Lights.Count);
        VfxAssert.Equal(2, finished);
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
        VfxAssert.Equal(false, engine.Updates[^1].Emitting);
        handle.Trigger("flash");
        VfxAssert.Equal((0, 4), engine.Bursts[^1]);
        VfxAssert.Equal(0, engine.Restarts.Count);
        handle.GoTo("lit");
        VfxAssert.Equal(0, engine.Restarts[^1]);
        VfxTestData.Step(service, 0.25f);
        VfxAssert.Equal(true, engine.Updates[^1].Emitting);
        handle.Stop(true);
        VfxAssert.Equal((0, false), engine.Destroys[0]);
        VfxTestData.Step(service, 2f);
        VfxAssert.Check(handle.IsAlive && handle.GpuEmitterCount == 1, "the handle owns the GPU drain until the engine reclaims it");
        handle.Dispose();
        VfxAssert.Equal((0, true), engine.Destroys[^1]);
        VfxAssert.Check(!handle.IsAlive && handle.GpuEmitterCount == 0, "Dispose during the GPU drain tears it down");
        VfxTestData.Step(service, 0f);
        VfxAssert.Equal(0, service.LiveHandleCount);
    }

    private static EffectAsset LitAsset(string id, TimelineEventSpec[]? timelineEvents = null) => new()
    {
        EffectId = id, Nodes = new[]
        {
            new EffectNodeSpec
            {
                Name = "glow", Emitter = VfxTestData.Emitter(lifetime: 1f),
                PointLight = new PointLightSpec(4f, Vector3.One),
            },
        },
        TimelineEvents = timelineEvents ?? new[] { new TimelineEventSpec(1f, "flash", 0) },
    };
}
