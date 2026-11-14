using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.ParticlesGpu;
using RimLumen.Core.Scene;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class LumenVfxServiceSuite
{
    [LumenFact]
    public static void ResolveMissReturnsInertUntrackedHandle()
    {
        var service = new LumenVfxService(new CpuParticleSimulator());
        using var handle = service.Play(new LumenPlayRequest { EffectId = "b3.missing" });
        VfxAssert.Check(!handle.IsAlive, "A resolve miss is dead");
        handle.Trigger("unknown");
        handle.SetParam("intensity", 2f);
        handle.Stop();
        handle.Stop(false);
        handle.Dispose();
        VfxAssert.Check(service.LiveHandleCount == 0 && !service.HasActiveWork, "A miss never becomes live");
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
            EffectId = asset.EffectId, OriginWorld = new Vector3(10f, 0f, 20f),
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
        VfxAssert.Check(!service.HasActiveWork, "Dead handles cannot keep the render pipeline active");
        VfxTestData.Step(service, 0f);
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
        using var fallback = (EffectInstanceHandle)service.Play(new LumenPlayRequest { EffectId = asset.EffectId });
        GlowAssert.Equal(2, fallback.CpuEmitterCount);
        GlowAssert.Equal(0, fallback.GpuEmitterCount);
        var firstEngine = available = new SpyGpuEngine();
        using var routed = (EffectInstanceHandle)service.Play(new LumenPlayRequest
        {
            EffectId = asset.EffectId, OriginWorld = new Vector3(10f, 0f, 20f),
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
    public static void CapabilitiesReflectInstalledBackends()
    {
        var simulator = new CpuParticleSimulator();
        var service = new LumenVfxService(simulator) { GpuEngineProvider = () => null };
        GlowAssert.Equal("LumenVfxService", service.Name);
        GlowAssert.Equal(new LumenCapabilities(false, false, false, 0), service.Capabilities);
        simulator.BatchSink = new CountingBatchSink();
        service.GpuEngineProvider = () => new SpyGpuEngine();
        GlowAssert.Equal(new LumenCapabilities(true, true, true, 4096), service.Capabilities);
        service.EnqueuePreCompute(default);
        service.RecordBufferBarriers(default);
        service.RecordHdrDraws(default);
        GlowAssert.Equal(0, ((CountingBatchSink)simulator.BatchSink).Calls);
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
        var asset = new EffectAsset { EffectId = "b3.bootstrap" };
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

        var request = new LumenPlayRequest { EffectId = asset.EffectId, OriginWorld = Vector3.Zero };
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

    private sealed class CountingBatchSink : IL1ParticleBatchSink
    {
        public int Calls;
        public void Submit(ReadOnlySpan<ParticleVertex> vertices, ReadOnlySpan<uint> indices, int liveParticleCount) => Calls++;
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
    public readonly List<(EmitterSpec Spec, Matrix4x4 World)> Creates = new();
    public readonly List<(int Id, Matrix4x4 World, bool Emitting, float Intensity)> Updates = new();
    public readonly List<(int Id, bool Immediate)> Destroys = new();
    public int FrameCalls;
    public string Name => "SpyGpuEngine";
    public bool HasActiveWork => true;
    public int CreateInstance(EmitterSpec spec, Matrix4x4 initialWorldTransform)
    {
        int id = Creates.Count;
        Creates.Add((spec, initialWorldTransform));
        return id;
    }
    public void UpdateInstance(int instanceId, in Matrix4x4 worldTransform, bool emitting, float intensityMultiplier = 1f)
        => Updates.Add((instanceId, worldTransform, emitting, intensityMultiplier));
    public void DestroyInstance(int instanceId, bool immediate = false) => Destroys.Add((instanceId, immediate));
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
