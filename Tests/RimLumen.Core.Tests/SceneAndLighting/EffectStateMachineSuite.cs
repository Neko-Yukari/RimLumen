using System;
using System.Collections.Generic;
using System.IO;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.ParticlesGpu;
using RimLumen.Core.Scene;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class EffectStateMachineSuite
{
    [LumenFact]
    public static void StatelessOneShotStopsAtDefaultDurationAndFinishesAfterItsParticles()
    {
        var asset = new EffectAsset
        {
            EffectId = "fsm.one-shot", DefaultDuration = 0.5f,
            Nodes = new[] { new EffectNodeSpec { Name = "puff", Emitter = Burst("puff", 5, lifetime: 1f) } },
        };
        var service = VfxTestData.Service(asset);
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        var log = Record(handle);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(5, service.Simulator.LiveParticleCount);
        VfxAssert.Check(handle.IsEmitting, "the implicit state plays until defaultDuration");
        VfxTestData.Step(service, 0.25f);
        VfxAssert.Check(handle.IsAlive && !handle.IsEmitting, "defaultDuration ends emission, live particles keep the handle alive");
        VfxTestData.Step(service, 1f);
        VfxTestData.Step(service, 0f);
        VfxAssert.Check(!handle.IsAlive && !service.HasActiveWork, "the handle dies once its particles are gone");
        GlowAssert.Equal("finished", string.Join(",", log));
    }

    [LumenFact]
    public static void StatelessLoopingAssetPlaysUntilStopped()
    {
        var asset = new EffectAsset
        {
            EffectId = "fsm.stateless-loop", DefaultDuration = 0.5f,
            Nodes = new[] { new EffectNodeSpec { Name = "aura", Emitter = VfxTestData.Emitter(looping: true, tier: LumenParticleTier.L1_NativeCpu) } },
        };
        var service = VfxTestData.Service(asset);
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        for (int i = 0; i < 12; i++) VfxTestData.Step(service, 0.25f);
        VfxAssert.Check(handle.IsEmitting, "a looping emitter keeps a stateless asset playing past defaultDuration");
        handle.Stop(true);
        VfxAssert.Check(handle.IsAlive && !handle.IsEmitting, "Stop(true) without stopState drains at once");
    }

    [LumenFact]
    public static void NextChainsStatesAndLifecycleEventsArriveInOrder()
    {
        var asset = new EffectAsset
        {
            EffectId = "fsm.chain",
            Nodes = new[]
            {
                new EffectNodeSpec { Name = "charge", Emitter = Burst("charge", 2) },
                new EffectNodeSpec { Name = "release", Emitter = Burst("release", 3) },
            },
            States = new[]
            {
                new EffectStateSpec { Name = "charge", Nodes = new[] { "charge" }, Duration = 0.5f, Next = "release" },
                new EffectStateSpec { Name = "release", Nodes = new[] { "release" }, Duration = 0.5f },
            },
            TimelineEvents = new[]
            {
                new TimelineEventSpec(0.25f, "impact", -1, "release", Notify: true),
                new TimelineEventSpec(0.25f, "silent", -1, "release"),
            },
        };
        var service = VfxTestData.Service(asset);
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        var log = Record(handle);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(2, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(5, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal("state:charge,state:release,notify:impact", string.Join(",", log));
        VfxTestData.Step(service, 0.25f);
        VfxAssert.Check(handle.IsAlive && !handle.IsEmitting, "the last state without next ends the effect");
        GlowAssert.Equal(5, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 10f);
        VfxTestData.Step(service, 0f);
        GlowAssert.Equal("state:charge,state:release,notify:impact,finished", string.Join(",", log));
    }

    [LumenFact]
    public static void OneLargeFrameWalksEveryStateItCrosses()
    {
        var asset = new EffectAsset
        {
            EffectId = "fsm.large-step",
            Nodes = new[] { new EffectNodeSpec { Name = "n" } },
            States = new[]
            {
                new EffectStateSpec { Name = "a", Duration = 0.1f, Next = "b" },
                new EffectStateSpec { Name = "b", Duration = 0f, Next = "c" },
                new EffectStateSpec { Name = "c", Duration = 0.1f },
            },
            TimelineEvents = new[] { new TimelineEventSpec(0f, "c0", -1, "c", Notify: true) },
        };
        var service = VfxTestData.Service(asset);
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        var log = Record(handle);
        VfxTestData.Step(service, 1f);
        GlowAssert.Equal("state:a,state:b,state:c,notify:c0,finished", string.Join(",", log));
        VfxAssert.Check(!handle.IsAlive, "an effect without particles or lights dies on the frame it ends");
    }

    [LumenFact]
    public static void LoopStateRefiresItsEventsEachCycleAndOtherStatesEventsStayQuiet()
    {
        var asset = new EffectAsset
        {
            EffectId = "fsm.loop-events",
            Nodes = new[] { new EffectNodeSpec { Name = "n" } },
            States = new[]
            {
                new EffectStateSpec { Name = "idle", Duration = 1f, Loop = true },
                new EffectStateSpec { Name = "other", Duration = 1f, Loop = true },
            },
            TimelineEvents = new[]
            {
                new TimelineEventSpec(0.5f, "tick", -1, "idle", Notify: true),
                new TimelineEventSpec(1.5f, "beyond-cycle", -1, "idle", Notify: true),
                new TimelineEventSpec(0.5f, "elsewhere", -1, "other", Notify: true),
            },
        };
        var service = VfxTestData.Service(asset);
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        var log = Record(handle);
        for (int i = 0; i < 6; i++) VfxTestData.Step(service, 0.5f);
        GlowAssert.Equal("state:idle,notify:tick,notify:tick,notify:tick", string.Join(",", log));
        VfxAssert.Check(handle.IsEmitting, "a loop state never ends on its own");
    }

    [LumenFact]
    public static void StopTrueEntersStopStateIgnoresGoToAndThenDies()
    {
        var asset = new EffectAsset
        {
            EffectId = "fsm.stop-state",
            Nodes = new[]
            {
                new EffectNodeSpec { Name = "loop", Emitter = VfxTestData.Emitter(looping: true, lifetime: 0.5f, tier: LumenParticleTier.L1_NativeCpu) },
                new EffectNodeSpec { Name = "outro", Emitter = Burst("outro", 4, lifetime: 0.5f) },
            },
            States = new[]
            {
                new EffectStateSpec { Name = "idle", Nodes = new[] { "loop" }, Loop = true },
                new EffectStateSpec { Name = "outro", Nodes = new[] { "outro" }, Duration = 0.5f },
            },
            StopState = "outro",
        };
        var service = VfxTestData.Service(asset);
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        var log = Record(handle);
        GlowAssert.Throws<ArgumentException>(() => handle.GoTo("missing"));
        VfxTestData.Step(service, 0.25f);
        handle.Stop(true);
        handle.Stop(true);
        handle.GoTo("idle");
        VfxAssert.Check(handle.IsEmitting, "the stop state is still playing");
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal("state:idle,state:outro", string.Join(",", log));
        VfxTestData.Step(service, 0.25f);
        VfxAssert.Check(handle.IsAlive && !handle.IsEmitting, "the stop state ended and the effect drains");
        VfxTestData.Step(service, 1f);
        VfxTestData.Step(service, 0f);
        VfxAssert.Check(!handle.IsAlive, "drained after the stop state");
        GlowAssert.Equal("state:idle,state:outro,finished", string.Join(",", log));
    }

    [LumenFact]
    public static void GoToReentryRestartsL1AndL2SchedulesAlike()
    {
        var asset = new EffectAsset
        {
            EffectId = "fsm.reenter",
            Nodes = new[]
            {
                new EffectNodeSpec { Name = "cpu", Emitter = Burst("cpu", 3) },
                new EffectNodeSpec { Name = "gpu", Emitter = Burst("gpu", 3, tier: LumenParticleTier.L2_GpuCompute) },
            },
            States = new[] { new EffectStateSpec { Name = "hit", Nodes = new[] { "cpu", "gpu" }, Loop = true } },
        };
        var service = VfxTestData.Service(asset);
        var engine = new SpyGpuEngine();
        service.GpuEngineProvider = () => engine;
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        var log = Record(handle);
        GlowAssert.Equal(1, engine.Restarts.Count);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(3, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(3, service.Simulator.LiveParticleCount);
        handle.GoTo("hit");
        GlowAssert.Equal(2, engine.Restarts.Count);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(6, service.Simulator.LiveParticleCount);
        GlowAssert.Equal("state:hit,state:hit", string.Join(",", log));
    }

    [LumenFact]
    public static void GpuOnlyEffectFinishesWhenTheEngineReclaimsItsDrain()
    {
        var asset = new EffectAsset
        {
            EffectId = "fsm.gpu-drain", DefaultDuration = 0.5f,
            Nodes = new[] { new EffectNodeSpec { Name = "gpu", Emitter = Burst("gpu", 3, tier: LumenParticleTier.L2_GpuCompute) } },
        };
        var service = VfxTestData.Service(asset);
        var engine = new SpyGpuEngine();
        service.GpuEngineProvider = () => engine;
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        var log = Record(handle);
        VfxTestData.Step(service, 0.5f);
        GlowAssert.Equal((0, false), engine.Destroys[^1]);
        VfxTestData.Step(service, 5f);
        VfxAssert.Check(handle.IsAlive && !handle.IsEmitting && log.Count == 0, "GPU particles still drain");
        engine.Drained.Add(0);
        VfxTestData.Step(service, 0f);
        VfxAssert.Check(!handle.IsAlive, "the handle dies once the engine reclaimed the instance");
        GlowAssert.Equal("finished", string.Join(",", log));
        GlowAssert.Equal(1, engine.Destroys.Count);
    }

    [LumenFact]
    public static void HandlerSwitchStopsTheOldStateBeforeThisFramesCapture()
    {
        var asset = new EffectAsset
        {
            EffectId = "fsm.handler-switch",
            Nodes = new[]
            {
                new EffectNodeSpec { Name = "a", Emitter = VfxTestData.Emitter(looping: true, tier: LumenParticleTier.L1_NativeCpu) },
                new EffectNodeSpec { Name = "b" },
            },
            States = new[]
            {
                new EffectStateSpec { Name = "A", Nodes = new[] { "a" }, Duration = 1f, Loop = true },
                new EffectStateSpec { Name = "B", Nodes = new[] { "b" }, Loop = true },
            },
            TimelineEvents = new[] { new TimelineEventSpec(0.25f, "switch", -1, "A", Notify: true) },
        };
        var service = VfxTestData.Service(asset);
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        var log = Record(handle);
        handle.Notify += _ => handle.GoTo("B");
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(0, service.Simulator.LiveParticleCount);
        GlowAssert.Equal("state:A,notify:switch", string.Join(",", log));
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal("state:A,notify:switch,state:B", string.Join(",", log));
        GlowAssert.Equal(0, service.Simulator.LiveParticleCount);
    }

    [LumenFact]
    public static void EventsQueuedByHandlersWaitForTheNextFrame()
    {
        var asset = new EffectAsset
        {
            EffectId = "fsm.handler-queue",
            Nodes = new[] { new EffectNodeSpec { Name = "n" } },
            States = new[]
            {
                new EffectStateSpec { Name = "A", Loop = true },
                new EffectStateSpec { Name = "B", Loop = true },
            },
        };
        var service = VfxTestData.Service(asset);
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        var log = Record(handle);
        handle.StateChanged += state =>
        {
            if (log.Count < 3) handle.GoTo(state == "A" ? "B" : "A");
            else handle.Stop(false);
        };
        for (int i = 0; i < 3; i++) VfxTestData.Step(service, 0f);
        GlowAssert.Equal("state:A,state:B,state:A", string.Join(",", log));
        VfxAssert.Check(!handle.IsAlive && service.HasActiveWork, "Finished after a handler's Stop(false) is still owed");
        VfxTestData.Step(service, 0f);
        GlowAssert.Equal("state:A,state:B,state:A,finished", string.Join(",", log));
        VfxAssert.Check(!service.HasActiveWork, "the dead handle is dropped once Finished is raised");
    }

    [LumenFact]
    public static void EventsAHandlerCausesOnAnotherHandleWaitForTheNextFrameInEitherOrder()
    {
        var asset = new EffectAsset
        {
            EffectId = "fsm.cross-handle",
            Nodes = new[] { new EffectNodeSpec { Name = "n" } },
            States = new[] { new EffectStateSpec { Name = "A", Duration = 1f, Loop = true } },
            TimelineEvents = new[] { new TimelineEventSpec(0.25f, "ping", -1, Notify: true) },
        };
        var service = VfxTestData.Service(asset);
        using var first = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        using var second = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        int firstFinished = 0, secondFinished = 0;
        first.Finished += () => firstFinished++;
        second.Finished += () => secondFinished++;
        first.Notify += _ => second.Stop(false);
        second.Notify += _ => first.Stop(false);
        VfxTestData.Step(service, 0.25f);
        VfxAssert.Check(!first.IsAlive && !second.IsAlive, "both handlers ran and stopped the other handle");
        VfxAssert.Check(firstFinished == 0 && secondFinished == 0, "Finished caused by a handler waits for the next frame");
        VfxTestData.Step(service, 0f);
        VfxAssert.Check(firstFinished == 1 && secondFinished == 1 && !service.HasActiveWork, "both Finished arrive on the next frame");
    }

    [LumenFact]
    public static void RestartedSchedulerMatchesAFreshOne()
    {
        var spec = new EmitterSpec
        {
            Name = "schedule", MaxParticles = 64, Duration = 1f, RateOverTime = 7f,
            Bursts = new[] { (0f, 5), (0.4f, 3) },
        };
        var fresh = new EmissionScheduler(spec);
        var reused = new EmissionScheduler(spec);
        for (int i = 0; i < 9; i++) reused.Advance(0.15f, emitting: true);
        reused.Restart();
        for (int i = 0; i < 9; i++)
            GlowAssert.Equal(fresh.Advance(0.15f, emitting: true), reused.Advance(0.15f, emitting: true));
    }

    [LumenFact]
    public static void JsonAndXmlParseTheSameStates()
    {
        EffectAsset json = LumenJsonAssetLoader.Parse(StatesJson, "fsm.parse");
        var def = new LumenEffectDef
        {
            defName = "fsm.parse",
            nodes = new List<LumenNodeXml> { new() { name = "charge" }, new() { name = "release" } },
            states = new List<LumenStateXml>
            {
                new() { name = "charge", nodes = new List<string> { "charge" }, duration = 0.5f, next = "release" },
                new() { name = "release", nodes = new List<string> { "release" }, loop = true },
                new() { name = "end", nodes = new List<string> { "release" } },
            },
            initialState = "charge",
            stopState = "end",
            timelineEvents = new List<LumenTimelineEventXml> { new() { time = 0.25f, eventName = "impact", state = "release", notify = true } },
        };
        EffectAsset xml = def.ToEffectAsset();
        foreach (EffectAsset asset in new[] { json, xml })
        {
            GlowAssert.Equal(3, asset.States.Length);
            GlowAssert.Equal("release", asset.States[0].Next);
            GlowAssert.Equal(0.5f, asset.States[0].Duration!.Value);
            VfxAssert.Check(asset.States[1].Loop && asset.States[2].Duration is null, "loop flag and inherited duration");
            GlowAssert.Equal("charge", asset.InitialState);
            GlowAssert.Equal("end", asset.StopState);
            GlowAssert.Equal(new TimelineEventSpec(0.25f, "impact", -1, "release", true), asset.TimelineEvents[0]);
        }
    }

    [LumenFact]
    public static void InvalidStateGraphsFailAtTheParser()
    {
        string[] invalid =
        {
            """{ "nodes": [ { "name": "n" } ], "initialState": "a" }""",
            """{ "nodes": [ { "name": "n" } ], "timelineEvents": [ { "time": 0, "eventName": "e", "state": "a" } ] }""",
            """{ "nodes": [ { "name": "n" } ], "states": [ { "name": "a" }, { "name": "a" } ] }""",
            """{ "nodes": [ { "name": "n" } ], "states": [ { "name": "" } ] }""",
            """{ "nodes": [ { "name": "n" } ], "states": [ { "name": "a", "nodes": [ "missing" ] } ] }""",
            """{ "nodes": [ { "name": "n" } ], "states": [ { "name": "a", "next": "missing" } ] }""",
            """{ "nodes": [ { "name": "n" } ], "states": [ { "name": "a", "loop": true, "next": "a" } ] }""",
            """{ "nodes": [ { "name": "n" } ], "states": [ { "name": "a", "duration": -1 } ] }""",
            """{ "nodes": [ { "name": "n" } ], "states": [ { "name": "a" } ], "initialState": "missing" }""",
            """{ "nodes": [ { "name": "n" } ], "states": [ { "name": "a", "loop": true } ], "stopState": "a" }""",
            """{ "nodes": [ { "name": "n" } ], "states": [ { "name": "a", "next": "b" }, { "name": "b" } ], "stopState": "a" }""",
            """{ "nodes": [ { "name": "n" } ], "states": [ { "name": "a", "duration": 0, "next": "b" }, { "name": "b", "duration": 0, "next": "a" } ] }""",
            """{ "nodes": [ { "name": "n" } ], "states": [ { "name": "a" } ], "timelineEvents": [ { "time": 0, "eventName": "e", "state": "missing" } ] }""",
            """{ "nodes": [ { "name": "n" } ], "states": [ { "name": "a", "durration": 1 } ] }""",
            """{ "nodes": [ { "name": "n" } ], "states": [ { "name": "a" } ], "initialState": "" }""",
        };
        foreach (string json in invalid)
            GlowAssert.Throws<InvalidDataException>(() => LumenJsonAssetLoader.Parse(json, "fsm.invalid"));
        var registered = new EffectAsset
        {
            EffectId = "fsm.unvalidated", Nodes = new[] { new EffectNodeSpec { Name = "n" } },
            States = new[] { new EffectStateSpec { Name = "a", Next = "missing" } },
        };
        var emptyXmlState = new LumenEffectDef
        {
            defName = "fsm.xml-empty", nodes = new List<LumenNodeXml> { new() { name = "n" } },
            states = new List<LumenStateXml> { new() { name = "a" } }, initialState = "",
        };
        GlowAssert.Throws<InvalidDataException>(() => emptyXmlState.ToEffectAsset());
        var service = VfxTestData.Service(registered);
        using var rejectedState = service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = registered.EffectId });
        VfxAssert.Check(!rejectedState.IsAlive, "Invalid provider state graph is rejected before Play.");
        GlowAssert.Equal(0, service.LiveHandleCount);
        var nanDefault = new EffectAsset
        {
            EffectId = "fsm.nan-default", DefaultDuration = float.NaN, Nodes = registered.Nodes,
            States = new[] { new EffectStateSpec { Name = "a", Duration = 1f } },
        };
        service = VfxTestData.Service(nanDefault);
        using var rejectedDuration = service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = nanDefault.EffectId });
        VfxAssert.Check(!rejectedDuration.IsAlive && service.LiveHandleCount == 0, "Invalid provider duration cannot create a live handle.");
    }

    // The tornado of docs/api.md (state machine section): spawn -> sustain (loop) -> collapse (stopState).
    [LumenFact]
    public static void TornadoLifecycleFromTheManualRunsSpawnSustainCollapse()
    {
        EffectAsset asset = LumenJsonAssetLoader.Parse(TornadoJson, "Author.MyMod");
        var service = VfxTestData.Service(asset);
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        var log = Record(handle);
        for (int i = 0; i < 8; i++) VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal("state:spawn,notify:touchdown,state:sustain", string.Join(",", log));
        for (int i = 0; i < 40; i++) VfxTestData.Step(service, 0.25f);
        VfxAssert.Check(handle.IsEmitting && log.Count == 3, "sustain loops until the caller stops it");
        handle.Stop();
        handle.GoTo("sustain");
        for (int i = 0; i < 2; i++) VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal("state:spawn,notify:touchdown,state:sustain,state:collapse,notify:impact", string.Join(",", log));
        VfxAssert.Check(handle.IsEmitting, "collapse is still playing");
        for (int i = 0; i < 3; i++) VfxTestData.Step(service, 0.25f);
        VfxAssert.Check(handle.IsAlive && !handle.IsEmitting, "collapse ended, the last particles drain");
        VfxTestData.Step(service, 5f);
        VfxTestData.Step(service, 0f);
        VfxAssert.Check(!handle.IsAlive, "the tornado finished");
        GlowAssert.Equal("state:spawn,notify:touchdown,state:sustain,state:collapse,notify:impact,finished", string.Join(",", log));
    }

    private static EmitterSpec Burst(string name, int count, float lifetime = 10f,
        LumenParticleTier tier = LumenParticleTier.L1_NativeCpu) => new()
    {
        Name = name, MaxParticles = 16, Duration = 1f, RateOverTime = 0f, Bursts = new[] { (0f, count) },
        StartLifetimeMin = lifetime, StartLifetimeMax = lifetime, PreferredTier = tier,
    };

    private static List<string> Record(ILumenEffectHandle handle)
    {
        var log = new List<string>();
        handle.StateChanged += state => log.Add("state:" + state);
        handle.Notify += name => log.Add("notify:" + name);
        handle.Finished += () => log.Add("finished");
        return log;
    }

    private const string TornadoJson = """
    {
      "effectId": "Tornado",
      "nodes": [
        { "name": "funnel", "spacePlane": "Vertical3D", "emitter": {
            "maxParticles": 2048, "duration": 2, "looping": true, "rateOverTime": 600,
            "shape": "Cone", "coneAngleDegrees": 8, "startLifetime": 1.6, "startSpeed": 4,
            "forceFields": [ { "kind": "Vortex", "centerOrAxis": [0, 1, 0], "parameters": [1.5, 2, 0], "radius": 3, "strength": 6 } ] } },
        { "name": "debris", "spacePlane": "Vertical3D", "emitter": {
            "maxParticles": 256, "duration": 2, "looping": true, "rateOverTime": 40,
            "shape": "CircleXZ", "shapeExtents": 2, "startLifetime": 1.2, "startSpeed": 2 } },
        { "name": "dust", "emitter": {
            "maxParticles": 512, "duration": 0.5, "bursts": [[0, 300]],
            "shape": "CircleXZ", "shapeExtents": 2.5, "startLifetime": 1.0, "startSpeed": 1 } },
        { "name": "collapse", "spacePlane": "Vertical3D", "emitter": {
            "maxParticles": 512, "duration": 0.5, "bursts": [[0, 400]],
            "shape": "Sphere", "startLifetime": 1.0, "startSpeed": 6 } }
      ],
      "states": [
        { "name": "spawn", "nodes": ["dust", "funnel"], "duration": 1.5, "next": "sustain" },
        { "name": "sustain", "nodes": ["funnel", "debris"], "duration": 2, "loop": true },
        { "name": "collapse", "nodes": ["collapse"], "duration": 1.2 }
      ],
      "initialState": "spawn",
      "stopState": "collapse",
      "timelineEvents": [
        { "time": 1.0, "eventName": "touchdown", "state": "spawn", "notify": true },
        { "time": 0.5, "eventName": "gust", "state": "sustain", "targetNodeIndex": 1 },
        { "time": 0.3, "eventName": "impact", "state": "collapse", "notify": true }
      ]
    }
    """;

    private const string StatesJson = """
    {
      "nodes": [ { "name": "charge" }, { "name": "release" } ],
      "states": [
        { "name": "charge", "nodes": [ "charge" ], "duration": 0.5, "next": "release" },
        { "name": "release", "nodes": [ "release" ], "loop": true },
        { "name": "end", "nodes": [ "release" ] }
      ],
      "initialState": "charge",
      "stopState": "end",
      "timelineEvents": [ { "time": 0.25, "eventName": "impact", "state": "release", "notify": true } ]
    }
    """;
}
