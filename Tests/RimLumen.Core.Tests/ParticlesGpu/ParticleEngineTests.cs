using System;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.ParticlesGpu;

namespace RimLumen.Core.Tests.ParticlesGpu;

[LumenTestSuite]
public static class ParticleEngineTests
{
    [LumenFact]
    public static void IdleConstructorAndCaptureNeedNoGpuContext()
    {
        using var engine = new LumenGpuParticleEngine(8);
        Require(!engine.HasActiveWork, "A fresh engine must be idle.");
        engine.CaptureMainThread(Frame(0));
        Require(!engine.HasActiveWork, "An empty capture must remain idle.");
    }

    [LumenFact]
    public static void DrainStopsLoopingEmitterAndReleasesCapacityWithoutGpu()
    {
        using var engine = new LumenGpuParticleEngine(8);
        int id = engine.CreateInstance(Looping(), Matrix4x4.Identity);
        Require(id >= 0, "Runtime IDs must be non-negative.");
        engine.CaptureMainThread(Frame(0.1f));
        engine.DestroyInstance(id);
        engine.CaptureMainThread(Frame(0.5f, 1));
        Require(engine.HasActiveWork, "Particles must survive before maximum lifetime elapses.");
        engine.DestroyInstance(id);
        engine.UpdateInstance(id, Matrix4x4.Identity, true);
        engine.CaptureMainThread(Frame(0.5f, 2));
        Require(!engine.HasActiveWork, "Repeated destroy/update must not restart a draining emitter.");
        int replacement = engine.CreateInstance(Looping(), Matrix4x4.Identity);
        Require(replacement > id, "Reclaimed ranges must not resurrect stale runtime IDs.");
    }

    [LumenFact]
    public static void ImmediateDestroyReclaimsOnlyAtNextCapture()
    {
        using var engine = new LumenGpuParticleEngine(8);
        int id = engine.CreateInstance(Looping(), Matrix4x4.Identity);
        engine.DestroyInstance(id, immediate: true);
        Require(engine.HasActiveWork, "Removal remains pending until capture.");
        engine.CaptureMainThread(Frame(0));
        Require(!engine.HasActiveWork, "Immediate removal must not wait for lifetime.");
        engine.CreateInstance(Looping(), Matrix4x4.Identity);
    }

    [LumenFact]
    public static void NonLoopingEmitterDrainsAfterFinalSpawn()
    {
        using var engine = new LumenGpuParticleEngine(8);
        engine.CreateInstance(new EmitterSpec
        {
            Name = "one-shot", MaxParticles = 8, Duration = 0.25f,
            Bursts = new[] { (0f, 8) }, StartLifetimeMin = 1, StartLifetimeMax = 1
        }, Matrix4x4.Identity);
        engine.CaptureMainThread(Frame(2));
        Require(engine.HasActiveWork, "A large final step must retain newly spawned particles.");
        engine.CaptureMainThread(Frame(0.5f, 1));
        Require(engine.HasActiveWork, "Final particles must remain during drain.");
        engine.CaptureMainThread(Frame(0.5f, 2));
        Require(!engine.HasActiveWork, "A finished emitter must return to idle.");
    }

    [LumenFact]
    public static void DestroyingUnknownOrAlreadyRemovedIdsIsHarmless()
    {
        using var engine = new LumenGpuParticleEngine(8);
        engine.DestroyInstance(123);
        int id = engine.CreateInstance(Looping(), Matrix4x4.Identity);
        engine.DestroyInstance(id, true);
        engine.CaptureMainThread(Frame(0));
        engine.DestroyInstance(id);
        engine.UpdateInstance(id, Matrix4x4.Identity, true);
        Require(!engine.HasActiveWork, "An expired handle must not recreate work.");
    }

    [LumenFact]
    public static void RetiringDroppedLastFrameUnblocksIdleWithoutDiscardingNewerFrames()
    {
        var queue = new ParticleFrameQueue();
        var dropped = new ParticleQueuedFrame(new ParticleFrame(Frame(0, 41),
            Array.Empty<ParticleEmitterFrame>(), Array.Empty<GpuForceField>(), Array.Empty<EmitterSlotRange>()),
            Array.Empty<Waylith.Rendering.Texture>(), Array.Empty<bool>());
        var newer = dropped with { Snapshot = dropped.Snapshot with { Context = Frame(0, 42) } };
        queue.Enqueue(dropped);
        queue.Enqueue(newer);
        queue.Retire(41);
        Require(!queue.IsEmpty, "Retiring a dropped frame must preserve the newer captured frame.");
        Require(!queue.TryTake(41, out _), "A dropped frame must not be simulated later.");
        Require(queue.TryTake(42, out var actual) && ReferenceEquals(actual, newer), "The exact newer snapshot must remain available.");
        queue.Retire(42);
        Require(queue.IsEmpty, "Retiring an already consumed frame must be harmless.");
        queue.Enqueue(dropped);
        queue.Retire(41);
        Require(queue.IsEmpty, "The last dropped active frame must not prevent idle GPU release.");
    }

    private static EmitterSpec Looping() => new()
    {
        Name = "loop", MaxParticles = 8, Looping = true, Duration = 1,
        RateOverTime = 8, StartLifetimeMin = 1, StartLifetimeMax = 1
    };

    private static LumenCaptureContext Frame(float dt, ulong index = 0) => new()
    {
        FrameIndex = index, DeltaTime = dt, ViewportWidth = 64, ViewportHeight = 64,
        ViewMatrix = Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity, LumenRenderPassId = 0
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
