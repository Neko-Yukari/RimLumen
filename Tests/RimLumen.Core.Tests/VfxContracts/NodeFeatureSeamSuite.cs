using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;
using RimLumen.Core.Tests.SceneAndLighting;

namespace RimLumen.Core.Tests.VfxContracts;

[LumenTestSuite]
public static class NodeFeatureSeamSuite
{
    [LumenFact]
    public static void BackendInstanceFollowsCreateRestartDeactivateDrainLifecycle()
    {
        var backend = new SpyNodeBackend(LumenNodeFeature.Distortion);
        LumenHostRegistry.RegisterNodeFeatureBackend(backend);
        try
        {
            var service = VfxTestData.Service(StatefulRingAsset("seam.lifecycle"));
            var handle = service.Play(new LumenPlayRequest { EffectId = "seam.lifecycle", MapIndex = 7, RealTime = true });
            VfxAssert.Equal("create ring active=False map=7 realTime=True | restart", backend.Log());

            VfxTestData.Step(service, 0.5f);
            handle.Trigger("ring");
            VfxTestData.Step(service, 0.6f);
            VfxAssert.Equal("create ring active=False map=7 realTime=True | restart | update active=True dt=0.5 | restart"
                + " | update active=False dt=0 | update active=False dt=0.6", backend.Log());

            backend.Calls.Clear();
            handle.Stop();
            VfxTestData.Step(service, 0.1f);
            VfxAssert.Equal("destroy immediate=False | update active=False dt=0.1", backend.Log());
            VfxAssert.Check(handle.IsAlive, "The handle stays alive while the backend instance drains");

            backend.Drained = true;
            VfxTestData.Step(service, 0.1f);
            VfxAssert.Check(!handle.IsAlive, "The handle dies once the backend instance reports dead");
            VfxAssert.Equal("destroy immediate=False | update active=False dt=0.1", backend.Log());
        }
        finally
        {
            LumenHostRegistry.UnregisterNodeFeatureBackend(backend);
        }
    }

    [LumenFact]
    public static void ImmediateStopTearsTheBackendInstanceDownAtOnce()
    {
        var backend = new SpyNodeBackend(LumenNodeFeature.Distortion);
        LumenHostRegistry.RegisterNodeFeatureBackend(backend);
        try
        {
            var service = VfxTestData.Service(StatefulRingAsset("seam.teardown"));
            var handle = service.Play(new LumenPlayRequest { EffectId = "seam.teardown", MapIndex = 7 });
            handle.Stop(false);
            VfxAssert.Equal("create ring active=False map=7 realTime=False | restart | destroy immediate=True", backend.Log());
            VfxAssert.Check(!handle.IsAlive, "Stop(false) kills the handle without waiting for the backend");
        }
        finally
        {
            LumenHostRegistry.UnregisterNodeFeatureBackend(backend);
        }
    }

    [LumenFact]
    public static void PlayWithoutRegisteredBackendThrowsNamingEffectNodeAndFeature()
    {
        var asset = new EffectAsset
        {
            EffectId = "seam.unregistered",
            Nodes = new[] { new EffectNodeSpec { Name = "streak", Ribbon = TrailRibbon() } },
        };
        var service = VfxTestData.Service(asset);
        var error = Capture<InvalidOperationException>(() => service.Play(new LumenPlayRequest { EffectId = "seam.unregistered", MapIndex = 7 }));
        VfxAssert.Check(error.Message.Contains("seam.unregistered") && error.Message.Contains("streak") && error.Message.Contains("Ribbon"), error.Message);
        VfxAssert.Equal(0, service.LiveHandleCount);
    }

    [LumenFact]
    public static void BeamAtTargetWithoutTargetWorldThrows()
    {
        var asset = new EffectAsset
        {
            EffectId = "seam.beam",
            Nodes = new[] { new EffectNodeSpec { Name = "zap", Ribbon = new RibbonSpec { Mode = LumenRibbonMode.Beam, EndAtTarget = true, Segments = 1 } } },
        };
        var service = VfxTestData.Service(asset);
        var error = Capture<ArgumentException>(() => service.Play(new LumenPlayRequest { EffectId = "seam.beam", MapIndex = 7 }));
        VfxAssert.Check(error.Message.Contains("seam.beam") && error.Message.Contains("zap") && error.Message.Contains("TargetWorld"), error.Message);
    }

    [LumenFact]
    public static void SecondBackendForTheSameFeatureIsRejected()
    {
        var first = new SpyNodeBackend(LumenNodeFeature.Distortion);
        LumenHostRegistry.RegisterNodeFeatureBackend(first);
        try
        {
            LumenHostRegistry.RegisterNodeFeatureBackend(first);
            Capture<InvalidOperationException>(() => LumenHostRegistry.RegisterNodeFeatureBackend(new SpyNodeBackend(LumenNodeFeature.Distortion)));
        }
        finally
        {
            LumenHostRegistry.UnregisterNodeFeatureBackend(first);
        }
    }

    // States: "intro" (ring, 1 s) -> "idle" (rest, looping); no stop state, so Stop(true) drains at once.
    private static EffectAsset StatefulRingAsset(string effectId) => new()
    {
        EffectId = effectId,
        Nodes = new[]
        {
            new EffectNodeSpec
            {
                Name = "ring",
                Distortion = new DistortionSpec { Kind = LumenDistortionKind.RingWave, Radius = 4f, Strength = 0.3f, Thickness = 0.5f, Speed = 6f, Duration = 0.8f },
            },
            new EffectNodeSpec { Name = "rest" },
        },
        States = new[]
        {
            new EffectStateSpec { Name = "intro", Nodes = new[] { "ring" }, Duration = 1f, Next = "idle" },
            new EffectStateSpec { Name = "idle", Nodes = new[] { "rest" }, Duration = 1f, Loop = true },
        },
    };

    private static RibbonSpec TrailRibbon() => new() { Mode = LumenRibbonMode.Trail, Lifetime = 1f, MinVertexDistance = 0.1f, MaxPoints = 8 };

    private static T Capture<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T error)
        {
            return error;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

// Pure in-process backend: records every call; instances drain once the test sets Drained.
internal sealed class SpyNodeBackend : ILumenNodeFeatureBackend
{
    private readonly HashSet<int> destroyedNow = new();
    private int nextId;

    public SpyNodeBackend(LumenNodeFeature feature) => Feature = feature;

    public LumenNodeFeature Feature { get; }
    public List<string> Calls { get; } = new();
    public bool Drained { get; set; }

    public string Log() => string.Join(" | ", Calls);

    public int CreateInstance(EffectNodeSpec node, in LumenNodeFeatureFrame initial, bool realTime, uint seed)
    {
        Calls.Add($"create {node.Name} active={initial.Active} map={initial.MapIndex} realTime={realTime}");
        return nextId++;
    }

    public void UpdateInstance(int instanceId, in LumenNodeFeatureFrame frame) => Calls.Add($"update active={frame.Active} dt={frame.DeltaTime.ToString(CultureInfo.InvariantCulture)}");

    public void RestartInstance(int instanceId) => Calls.Add("restart");

    public void DestroyInstance(int instanceId, bool immediate)
    {
        Calls.Add($"destroy immediate={immediate}");
        if (immediate) destroyedNow.Add(instanceId);
    }

    public bool IsInstanceAlive(int instanceId) => !Drained && !destroyedNow.Contains(instanceId);
}
