using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Api;
using RimLumen.Core.Contracts;
using RimLumen.Core.Lighting;
using RimLumen.Core.ParticlesGpu;
using RimLumen.Core.Rendering;
using RimLumen.Core.Scene;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class LumenVfxServiceSuite
{
    [LumenFact]
    public static void ResolveMissReturnsInertUntrackedHandle()
    {
        var service = new LumenVfxService(new CpuParticleSimulator());
        using var handle = service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = "b3.missing" });
        VfxAssert.Check(!handle.IsAlive, "A resolve miss is dead");
        using var repeated = service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = "b3.missing" });
        using var another = service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = "b3.missing.other" });
        VfxAssert.Check(!repeated.IsAlive && !another.IsAlive, "Repeated and distinct unknown ids stay inert");
        handle.Trigger("unknown");
        handle.SetParam("intensity", 2f);
        handle.Stop();
        handle.Stop(false);
        handle.Dispose();
        VfxAssert.Check(service.LiveHandleCount == 0 && !service.HasActiveWork, "A miss never becomes live");
    }

    [LumenFact]
    public static void UnknownEffectWarningDecisionIsOncePerIdAndExcludesFoundOrRejected()
    {
        var service = new LumenVfxService(new CpuParticleSimulator());
        VfxAssert.Check(service.ShouldWarnUnknownEffect("f10.unknown", LumenAssetResolution.NotFound), "First unknown id warns");
        VfxAssert.Check(!service.ShouldWarnUnknownEffect("f10.unknown", LumenAssetResolution.NotFound), "Repeated unknown id does not warn");
        VfxAssert.Check(service.ShouldWarnUnknownEffect("f10.other", LumenAssetResolution.NotFound), "Another unknown id has its own warning");
        VfxAssert.Check(!service.ShouldWarnUnknownEffect("f10.found", LumenAssetResolution.Found), "Found assets do not warn");
        VfxAssert.Check(!service.ShouldWarnUnknownEffect("f10.rejected", LumenAssetResolution.Rejected), "Rejected assets do not warn");
        VfxAssert.Check(service.ShouldWarnUnknownEffect("f10.found", LumenAssetResolution.NotFound), "A found asset does not consume a future miss warning");
        VfxAssert.Check(service.ShouldWarnUnknownEffect("f10.rejected", LumenAssetResolution.NotFound), "A rejection does not consume a future miss warning");
    }

    [LumenFact]
    public static void ResolveHitBindsOnceBeforeTheFirstFrame()
    {
        var asset = new EffectAsset
        {
            EffectId = "b3.hit", Nodes = new[]
            {
                new EffectNodeSpec { Name = "root", Emitter = VfxTestData.Emitter() },
                new EffectNodeSpec { Name = "child", ParentIndex = 0, LocalPosition = new Vector3(2f, 0f, 0f) },
                new EffectNodeSpec { Name = "second", Emitter = VfxTestData.Emitter() },
            },
        };
        var service = VfxTestData.Service(asset);
        using var handle = (EffectInstanceHandle)service.Play(new LumenPlayRequest
        {
            MapIndex = 111, EffectId = asset.EffectId, OriginWorld = new Vector3(10f, 0f, 20f),
        });
        VfxAssert.Check(handle.IsAlive && handle.IsEmitting && service.HasActiveWork, "Hit is live immediately");
        GlowAssert.Equal(0f, handle.PlaybackTime);
        GlowAssert.Equal(asset.EffectId, handle.EffectId);
        GlowAssert.Equal(3, handle.NodeWorldMatrices.Length);
        GlowAssert.Equal(new Vector3(12f, 0f, 20f), handle.NodeWorldMatrices[1].Translation);
        GlowAssert.Equal(2, handle.CpuEmitterCount);
        GlowAssert.Equal(1, service.LiveHandleCount);
        GlowAssert.Equal(0, service.Simulator.LiveParticleCount);
        VfxTestData.Step(service, 0.25f);
        int emitterId = service.Simulator.LiveParticles[0].EmitterId;
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(2, service.Simulator.ActiveEmitterCount);
        GlowAssert.Equal(emitterId, service.Simulator.LiveParticles[0].EmitterId);
        GlowAssert.Equal(0.5f, handle.PlaybackTime);
        handle.Dispose();
        GlowAssert.Equal(0, service.LiveHandleCount);
        VfxAssert.Check(service.HasActiveWork, "A disposed handle still owes Finished for one frame");
        VfxTestData.Step(service, 0f);
        VfxAssert.Check(!service.HasActiveWork, "Dead handles cannot keep the render pipeline active");
        GlowAssert.Equal(0, service.Simulator.ActiveEmitterCount);
    }

    [LumenFact]
    public static void RoutingResolvesAtPlayAndStaysBoundToThatEngine()
    {
        var asset = new EffectAsset
        {
            EffectId = "b3.routing", Nodes = new[]
            {
                new EffectNodeSpec { Name = "gpu", LocalPosition = new Vector3(2f, 0f, 0f), Emitter = VfxTestData.Emitter() },
                new EffectNodeSpec { Name = "cpu", Emitter = VfxTestData.Emitter(tier: LumenParticleTier.L1_NativeCpu) },
            },
        };
        var service = VfxTestData.Service(asset);
        SpyGpuEngine? available = null;
        service.GpuEngineProvider = () => available;
        using var fallback = (EffectInstanceHandle)service.Play(new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId });
        GlowAssert.Equal(2, fallback.CpuEmitterCount);
        GlowAssert.Equal(0, fallback.GpuEmitterCount);
        var firstEngine = available = new SpyGpuEngine();
        using var routed = (EffectInstanceHandle)service.Play(new LumenPlayRequest
        {
            MapIndex = 111, EffectId = asset.EffectId, OriginWorld = new Vector3(10f, 0f, 20f),
        });
        GlowAssert.Equal(1, routed.CpuEmitterCount);
        GlowAssert.Equal(1, routed.GpuEmitterCount);
        GlowAssert.Equal(1, firstEngine.Creates.Count);
        GlowAssert.Equal(asset.Nodes[0].Emitter, firstEngine.Creates[0].Spec);
        GlowAssert.Equal(routed.NodeWorldMatrices[0], firstEngine.Creates[0].World);
        GlowAssert.Equal(new Vector3(12f, 0f, 20f), firstEngine.Creates[0].World.Translation);
        available = new SpyGpuEngine();
        routed.SetParam("intensity", 2f);
        VfxTestData.Step(service, 0.25f);
        GlowAssert.Equal(1, firstEngine.Updates.Count);
        GlowAssert.Equal(2f, firstEngine.Updates[0].Intensity);
        GlowAssert.Equal(0, available.Creates.Count);
        GlowAssert.Equal(0, available.Updates.Count);
        GlowAssert.Equal(2, fallback.CpuEmitterCount);
        service.EnqueuePreCompute(default);
        service.RecordBufferBarriers(default);
        service.RecordHdrDraws(default);
        GlowAssert.Equal(0, firstEngine.FrameCalls);
        routed.Stop(false);
        GlowAssert.Equal((0, true), firstEngine.Destroys[0]);
    }

    [LumenFact]
    public static void P2cD9MapLookupNeverSelectsAnotherRegisteredMap()
    {
        using var first = new MapGlowerDriver(new RecordingGlowGridSink());
        using var last = new MapGlowerDriver(new RecordingGlowGridSink());
        LumenMapLightingRegistry.Register(7101, first);
        LumenMapLightingRegistry.Register(7102, last);
        try
        {
            VfxAssert.Check(!LumenMapLightingRegistry.TryResolve(-1, out var unresolved) && unresolved is null,
                "An unresolved Play map must not select the last registered map.");
            VfxAssert.Check(LumenMapLightingRegistry.TryResolve(7101, out var exact) && ReferenceEquals(first, exact),
                "An explicit RimWorld map uniqueID must resolve its own light driver.");
        }
        finally
        {
            LumenMapLightingRegistry.Unregister(7101, first);
            LumenMapLightingRegistry.Unregister(7102, last);
        }
    }

    [LumenFact]
    public static void P2cD9PlayMapRuleRejectsMissingOrInvalidMapAndKeepsExplicitIdsHostFree()
    {
        GlowAssert.Equal(7101, LumenPlayMap.ResolveMapIndex(-1, 7101));
        GlowAssert.Equal(7102, LumenPlayMap.ResolveMapIndex(7102, null));
        GlowAssert.Throws<InvalidOperationException>(() => LumenPlayMap.ResolveMapIndex(-1, null));
        GlowAssert.Throws<InvalidOperationException>(() => LumenPlayMap.ResolveMapIndex(-1, -1));
        GlowAssert.Throws<ArgumentOutOfRangeException>(() => LumenPlayMap.ResolveMapIndex(-2, 7101));

    }

    [LumenFact]
    public static void P2cD9PlayBindsOneMapAcrossCpuGpuAndDeferredLights()
    {
        var asset = new EffectAsset
        {
            EffectId = "p2c.map-propagation", Nodes = new[]
            {
                new EffectNodeSpec { Name = "cpu", Emitter = VfxTestData.Emitter(tier: LumenParticleTier.L1_NativeCpu),
                    PointLight = new PointLightSpec { GlowRadius = 2f } },
                new EffectNodeSpec { Name = "gpu", Emitter = VfxTestData.Emitter() },
            },
        };
        var catalog = new InMemoryAssetCatalog();
        catalog.Register(asset);
        LumenHostRegistry.RegisterAssetProvider(catalog);
        var simulator = new CpuParticleSimulator();
        var service = new LumenVfxService(simulator);
        var gpu = new SpyGpuEngine();
        service.GpuEngineProvider = () => gpu;
        var sinkA = new RecordingGlowGridSink();
        var sinkB = new RecordingGlowGridSink();
        using var driverA = new MapGlowerDriver(sinkA);
        using var driverB = new MapGlowerDriver(sinkB);
        using var handle = service.Play(new LumenPlayRequest { EffectId = asset.EffectId, MapIndex = 7101 });
        GlowAssert.Equal(7101, gpu.Creates[0].MapIndex);
        LumenMapLightingRegistry.Register(7101, driverA);
        LumenMapLightingRegistry.Register(7102, driverB);
        try
        {
            var context = new LumenCaptureContext
            {
                CurrentMapIndex = 7102, FrameIndex = 1, DeltaTime = 0.25f, ViewportWidth = 640, ViewportHeight = 480,
                ViewMatrix = Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity, LumenRenderPassId = 0,
            };
            service.CaptureMainThread(context);
            simulator.CaptureMainThread(context);
            GlowAssert.Equal(1, simulator.LiveParticleCount);
            GlowAssert.Equal(0, simulator.Vertices.Length);
            GlowAssert.Equal(1, sinkA.Registrations.Count);
            GlowAssert.Equal(0, sinkB.Registrations.Count);
            simulator.CaptureMainThread(context with { CurrentMapIndex = 7101, FrameIndex = 2, DeltaTime = 0f });
            GlowAssert.Equal(4, simulator.Vertices.Length);
        }
        finally
        {
            LumenMapLightingRegistry.Unregister(7101, driverA);
            LumenMapLightingRegistry.Unregister(7102, driverB);
        }
    }

    [LumenFact]
    public static void R1CapacityReportsSelectedRealEnginePoolInsteadOfBuiltinDefault()
    {
        using var engine = new LumenGpuParticleEngine(64);
        var service = new LumenVfxService(new CpuParticleSimulator()) { GpuEngineProvider = () => engine };
        GlowAssert.Equal(64, service.Capabilities.MaxGpuParticlesPerFrame);
        service.GpuEngineProvider = () => null;
        GlowAssert.Equal(0, service.Capabilities.MaxGpuParticlesPerFrame);
    }

    [LumenFact]
    public static void CapabilitiesReflectInstalledBackends()
    {
        var simulator = new CpuParticleSimulator();
        var service = new LumenVfxService(simulator) { GpuEngineProvider = () => null };
        LumenCapabilities frame = RimLumenRenderHost.Capabilities;
        try
        {
            RimLumenRenderHost.SetFrameCapabilities(false, false);
            GlowAssert.Equal("LumenVfxService", service.Name);
            GlowAssert.Equal(new LumenCapabilities(false, false, false, 0), service.Capabilities);
            simulator.HdrRendererAttached = true;
            service.GpuEngineProvider = () => new SpyGpuEngine();
            GlowAssert.Equal(new LumenCapabilities(false, false, true, 1_048_576), service.Capabilities);
            RimLumenRenderHost.SetFrameCapabilities(true, true);
            GlowAssert.Equal(new LumenCapabilities(true, true, true, 1_048_576), service.Capabilities);
            service.EnqueuePreCompute(default);
            service.RecordBufferBarriers(default);
            service.RecordHdrDraws(default);
        }
        finally
        {
            RimLumenRenderHost.SetFrameCapabilities(frame.IsHdrActive, frame.IsSceneDepthAvailable);
        }
    }

    [LumenFact]
    public static void R1SelectedServiceOwnsBackendCapacityAndHostOnlyOwnsFrameFacts()
    {
        var host = new LumenCapabilities(true, false, true, 1_048_576);
        GlowAssert.Equal(new LumenCapabilities(true, false, true, 64),
            RimLumenApi.MergeCapabilities(host, new LumenCapabilities(false, true, true, 64)));
        GlowAssert.Equal(new LumenCapabilities(true, false, false, 0),
            RimLumenApi.MergeCapabilities(host, new LumenCapabilities(false, true, false, 0)));
        GlowAssert.Equal(host, RimLumenApi.MergeCapabilities(host, null));
    }

    [LumenFact]
    public static void P2cD10ApiKeepsHostFrameFactsSeparateFromBackendSupportAndSlots()
    {
        LumenSceneBootstrap.EnsureInitialized();
        ILumenVfxService original = LumenHostRegistry.VfxService!;
        LumenCapabilities frame = RimLumenRenderHost.Capabilities;
        try
        {
            LumenHostRegistry.RegisterVfxService(new CapabilityService(new LumenCapabilities(true, true, true, 1_048_576)));
            RimLumenRenderHost.SetFrameCapabilities(false, false);
            GlowAssert.Equal(new LumenCapabilities(false, false, true, 1_048_576), RimLumenApi.Capabilities);
            RimLumenRenderHost.SetFrameCapabilities(true, false);
            GlowAssert.Equal(new LumenCapabilities(true, false, true, 1_048_576), RimLumenApi.Capabilities);
        }
        finally
        {
            RimLumenRenderHost.SetFrameCapabilities(frame.IsHdrActive, frame.IsSceneDepthAvailable);
            LumenHostRegistry.RegisterVfxService(original);
        }
    }

    private sealed class CapabilityService(LumenCapabilities capabilities) : ILumenVfxService
    {
        public LumenCapabilities Capabilities => capabilities;
        public ILumenEffectHandle Play(in LumenPlayRequest request) => throw new NotSupportedException();
    }

    [LumenFact]
    public static void BootstrapRegistersStableInstancesOnceInServiceThenSimulatorOrder()
    {
        VfxAssert.Check(!LumenSceneBootstrap.IsInitialized, "Bootstrap is initially uninitialized in the offline runner");
        GlowAssert.Throws<InvalidOperationException>(() => _ = LumenSceneBootstrap.Catalog);
        GlowAssert.Throws<InvalidOperationException>(() => _ = LumenSceneBootstrap.Simulator);
        GlowAssert.Throws<InvalidOperationException>(() => _ = LumenSceneBootstrap.Service);
        LumenSceneBootstrap.EnsureInitialized();
        var catalog = LumenSceneBootstrap.Catalog;
        var simulator = LumenSceneBootstrap.Simulator;
        var service = LumenSceneBootstrap.Service;
        var before = LumenHostRegistry.GetContributorsSnapshot();
        LumenSceneBootstrap.EnsureInitialized();
        var after = LumenHostRegistry.GetContributorsSnapshot();
        VfxAssert.Check(LumenSceneBootstrap.IsInitialized, "Bootstrap publishes initialization");
        VfxAssert.Check(ReferenceEquals(catalog, LumenSceneBootstrap.Catalog) &&
            ReferenceEquals(simulator, LumenSceneBootstrap.Simulator) &&
            ReferenceEquals(service, LumenSceneBootstrap.Service), "Repeated bootstrap keeps stable instances");
        VfxAssert.Check(ReferenceEquals(service, LumenHostRegistry.VfxService), "Service is publicly registered");
        GlowAssert.Equal(before.Length, after.Length);
        GlowAssert.Equal(1, Array.FindAll(after, x => ReferenceEquals(x, service)).Length);
        GlowAssert.Equal(1, Array.FindAll(after, x => ReferenceEquals(x, simulator)).Length);
        VfxAssert.Check(Array.IndexOf(after, service) < Array.IndexOf(after, simulator), "Service updates land before simulation");
        var asset = new EffectAsset { EffectId = "b3.bootstrap", Nodes = new[] { new EffectNodeSpec { Name = "root" } } };
        catalog.Register(asset);
        VfxAssert.Check(LumenHostRegistry.TryResolveAsset(asset.EffectId, out var resolved) && ReferenceEquals(asset, resolved),
            "Bootstrap catalog is reachable through the host registry");
        catalog.Unregister(asset.EffectId);
    }

    [LumenFact]
    public static void PlayReturnsInertHandleAndThrottlesPoolExhaustedWarningEveryTenSeconds()
    {
        GlowAssert.Equal(1_048_576, EmitterSlotAllocator.DefaultCapacity);

        var asset = new EffectAsset
        {
            EffectId = "b3.pool_exhausted",
            Nodes = new[]
            {
                new EffectNodeSpec
                {
                    Name = "lead_l1",
                    Emitter = new EmitterSpec
                    {
                        Name = "l1", RateOverTime = 2f, MaxParticles = 16, Duration = 2f,
                        StartLifetimeMin = 10f, StartLifetimeMax = 10f, PreferredTier = LumenParticleTier.L1_NativeCpu,
                    },
                },
                new EffectNodeSpec
                {
                    Name = "gpu_burst",
                    Emitter = new EmitterSpec
                    {
                        Name = "gpu", RateOverTime = 0f, MaxParticles = 150, Duration = 2f,
                        StartLifetimeMin = 10f, StartLifetimeMax = 10f, PreferredTier = LumenParticleTier.L2_GpuCompute,
                    },
                },
            },
        };
        LumenVfxService service = VfxTestData.Service(asset);
        using var engine = new LumenGpuParticleEngine(capacity: 200);
        service.GpuEngineProvider = () => engine;

        long nowMs = 1_000;
        var warnings = new List<string>();
        service.ClockMsProvider = () => nowMs;
        service.PoolExhaustedWarningSink = warnings.Add;

        var request = new LumenPlayRequest { MapIndex = 111, EffectId = asset.EffectId, OriginWorld = Vector3.Zero };
        using ILumenEffectHandle first = service.Play(request);
        VfxAssert.Check(first.IsAlive, "First allocation fits within 200 GPU slots");
        GlowAssert.Equal(1, service.Simulator.ActiveEmitterCount);
        GlowAssert.Equal(0, warnings.Count);

        using ILumenEffectHandle second = service.Play(request);
        VfxAssert.Check(!second.IsAlive, "Second play drops cleanly to an inert handle instead of throwing");
        GlowAssert.Equal(1, service.Simulator.ActiveEmitterCount);
        GlowAssert.Equal(1, service.LiveHandleCount);
        GlowAssert.Equal(1, warnings.Count);

        nowMs = 10_999;
        using ILumenEffectHandle third = service.Play(request);
        VfxAssert.Check(!third.IsAlive, "Still returns inert handle inside 10s cooldown");
        GlowAssert.Equal(1, warnings.Count);

        nowMs = 11_000;
        using ILumenEffectHandle fourth = service.Play(request);
        VfxAssert.Check(!fourth.IsAlive, "Still returns inert handle after 10s cooldown");
        GlowAssert.Equal(2, warnings.Count);
    }

}

internal static class VfxTestData
{
    public static EmitterSpec Emitter(float rate = 4f, bool looping = false, float lifetime = 10f,
        LumenParticleTier tier = LumenParticleTier.L2_GpuCompute) => new()
    {
        Name = "particles", RateOverTime = rate, MaxParticles = 128, Duration = 2f,
        Looping = looping, StartLifetimeMin = lifetime, StartLifetimeMax = lifetime, PreferredTier = tier,
    };

    public static LumenVfxService Service(EffectAsset asset)
    {
        var catalog = new InMemoryAssetCatalog();
        catalog.Register(asset);
        LumenHostRegistry.RegisterAssetProvider(catalog);
        return new LumenVfxService(new CpuParticleSimulator())
        {
            GpuEngineProvider = () => null, GlowerDriverProvider = _ => null,
        };
    }

    public static void Step(LumenVfxService service, float deltaTime)
    {
        var context = new LumenCaptureContext
        {
            FrameIndex = 1, DeltaTime = deltaTime, ViewportWidth = 640, ViewportHeight = 480,
            ViewMatrix = Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity, LumenRenderPassId = 0,
        };
        service.CaptureMainThread(context);
        service.Simulator.CaptureMainThread(context);
    }
}

internal sealed class SpyGpuEngine : ILumenGpuParticleEngine
{
    public readonly List<(EmitterSpec Spec, Matrix4x4 World, int MapIndex)> Creates = new();
    public readonly List<(int Id, Matrix4x4 World, bool Emitting, float Intensity)> Updates = new();
    public readonly List<(int Id, bool Immediate)> Destroys = new();
    public int FrameCalls;
    public int Capacity => 1_048_576;
    public string Name => "SpyGpuEngine";
    public bool HasActiveWork => true;
    public int CreateInstance(EmitterSpec spec, LumenEmitterTransform initialTransform, int mapIndex, bool realTime, uint emissionSeed)
    {
        int id = Creates.Count;
        Creates.Add((spec, initialTransform.World, mapIndex));
        return id;
    }
    public void UpdateInstance(int instanceId, in LumenEmitterTransform transform, bool emitting, float intensityMultiplier, float rateMultiplier)
        => Updates.Add((instanceId, transform.World, emitting, intensityMultiplier));
    public void DestroyInstance(int instanceId, bool immediate = false) => Destroys.Add((instanceId, immediate));
    public readonly List<int> Restarts = new();
    public readonly List<(int Id, int Count)> Bursts = new();
    public void RestartInstance(int instanceId) => Restarts.Add(instanceId);
    public void EmitBurst(int instanceId, int count) => Bursts.Add((instanceId, count));
    // Ids the test has declared drained; every other created, not immediately destroyed id is alive.
    public readonly HashSet<int> Drained = new();
    public bool IsInstanceAlive(int instanceId) => instanceId < Creates.Count && !Drained.Contains(instanceId)
        && !Destroys.Contains((instanceId, true));
    public void CaptureMainThread(in LumenCaptureContext context) => FrameCalls++;
    public void EnqueuePreCompute(in LumenComputeContext context) => FrameCalls++;
    public void RecordBufferBarriers(in LumenBarrierContext context) => FrameCalls++;
    public void RecordHdrDraws(in LumenHdrDrawContext context) => FrameCalls++;
}

internal static class VfxAssert
{
    public static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Close(Vector3 expected, Vector3 actual)
        => Check(Vector3.Distance(expected, actual) < 0.0001f, $"Expected {expected}, got {actual}");
}
