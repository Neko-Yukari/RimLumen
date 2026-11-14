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

    [LumenFact]
    public static void InitialStateCanWaitPastDurationBeforeTrigger()
    {
        using var engine = new LumenGpuParticleEngine(8);
        var asset = new EffectAsset
        {
            EffectId = "gpu.wait-trigger", Nodes = new[]
            {
                new EffectNodeSpec { Name = "waiting", Emitter = new EmitterSpec
                {
                    Name = "waiting", MaxParticles = 8, Duration = 0.25f,
                    Bursts = new[] { (0f, 8) }, StartLifetimeMin = 1f, StartLifetimeMax = 1f,
                } },
            },
        };
        var service = SceneAndLighting.VfxTestData.Service(asset);
        service.GpuEngineProvider = () => engine;
        using var handle = service.Play(new LumenPlayRequest { EffectId = asset.EffectId, InitialState = "other" });
        engine.CaptureMainThread(Frame(2f));
        engine.CaptureMainThread(Frame(2f, 1));
        Require(engine.HasActiveWork, "An unactivated InitialState node must not expire before Trigger.");
        handle.Trigger("waiting");
        engine.CaptureMainThread(Frame(0.25f, 2));
        Require(engine.HasActiveWork, "The triggered node retains its spawned particles.");
    }

    [LumenFact]
    public static void PausedCapturePreservesTheInitialBurstUntilTimeAdvances()
    {
        using var engine = new LumenGpuParticleEngine(16);
        engine.CreateInstance(new EmitterSpec
        {
            Name = "paused-initial-burst", MaxParticles = 16, Duration = 1f,
            Bursts = new[] { (0f, 12) }, StartLifetimeMin = 1f, StartLifetimeMax = 1f,
        }, Matrix4x4.Identity, mapIndex: 7, realTime: false);
        engine.CaptureMainThread(PausedFrame(0.016f));
        float pausedBudget = engine.CapturedFrame!.Emitters[0].Parameters.Slots.Z;
        engine.CaptureMainThread(Frame(0.016f, 1));
        float resumedBudget = engine.CapturedFrame!.Emitters[0].Parameters.Slots.Z;
        Require(pausedBudget == 0f && resumedBudget == 12f,
            $"Expected paused/resumed budgets 0/12, actual {pausedBudget}/{resumedBudget}.");
    }

    [LumenFact]
    public static void EmitterDrawsAreScopedToTheCapturingMap()
    {
        var onSeven = new ParticleEmitterFrame(1, default, Array.Empty<GpuCurveSample>(), null, LumenBlendMode.Additive, 7);
        var unscoped = new ParticleEmitterFrame(2, default, Array.Empty<GpuCurveSample>(), null, LumenBlendMode.Additive);
        Require(!onSeven.IsVisibleOnMap(9), "A map 7 instance must not draw while map 9 is captured.");
        Require(onSeven.IsVisibleOnMap(7), "A map 7 instance draws on its own map.");
        Require(onSeven.IsVisibleOnMap(-1), "An unscoped capture draws every map.");
        Require(unscoped.IsVisibleOnMap(9), "A map-less instance draws everywhere.");
    }

    [LumenFact]
    public static void RealTimeInstanceDrainsOnUnscaledTimeWhilePaused()
    {
        using var realTime = new LumenGpuParticleEngine(8);
        using var gameTime = new LumenGpuParticleEngine(8);
        EmitterSpec spec = new()
        {
            Name = "drain", MaxParticles = 8, Looping = true, Duration = 1,
            StartLifetimeMin = 0.5f, StartLifetimeMax = 0.5f,
        };
        int fast = realTime.CreateInstance(spec, Matrix4x4.Identity, 3, realTime: true);
        int slow = gameTime.CreateInstance(spec, Matrix4x4.Identity, 3, realTime: false);
        realTime.DestroyInstance(fast);
        gameTime.DestroyInstance(slow);
        realTime.CaptureMainThread(PausedFrame(1f));
        gameTime.CaptureMainThread(PausedFrame(1f));
        Require(!realTime.HasActiveWork, "A real-time instance drains on unscaled time while paused.");
        Require(gameTime.HasActiveWork, "A game-time instance must not advance through a paused frame.");
    }

    [LumenFact]
    public static void CameraBillboardBasisCancelsIsoRimSquashWithoutStrippingZoom()
    {
        const float squash = 0.003f;
        float pitch = 35.3f * MathF.PI / 180f;
        float yaw = 45f * MathF.PI / 180f;
        Vector3 forward = Vector3.Normalize(new Vector3(
            MathF.Cos(pitch) * MathF.Sin(yaw),
            -MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Cos(yaw)));
        Vector3 camRight = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        Vector3 camUp = Vector3.Cross(forward, camRight);
        var isoView = new Matrix4x4(
            camRight.X, camRight.Y * squash, camRight.Z, 0f,
            camUp.X, camUp.Y * squash, camUp.Z, 0f,
            -forward.X, -forward.Y * squash, -forward.Z, 0f,
            0f, 0f, 0f, 1f);
        RimLumen.Core.Rendering.CameraBillboardBasis.Extract(isoView, out Vector3 right, out Vector3 up, out float yScale);
        Require(MathF.Abs(yScale - squash) < 1e-5f, $"Expected yScale={squash}, got {yScale}.");
        Vector3 viewRight = Vector3.TransformNormal(right, Matrix4x4.Transpose(isoView));
        Vector3 viewUp = Vector3.TransformNormal(up, Matrix4x4.Transpose(isoView));
        Require((viewRight - Vector3.UnitX).Length() < 1e-3f && (viewUp - Vector3.UnitY).Length() < 1e-3f,
            $"Expected camera XY axes in view space, got right={viewRight}, up={viewUp}.");

        RimLumen.Core.Rendering.CameraBillboardBasis.ComputeStretchedAxes(right, up, yScale, new Vector3(3f, 0f, 1f),
            out Vector3 sRight, out Vector3 sUp);
        Vector3 viewSRight = Vector3.TransformNormal(sRight, Matrix4x4.Transpose(isoView));
        Vector3 viewSUp = Vector3.TransformNormal(sUp, Matrix4x4.Transpose(isoView));
        Require(MathF.Abs(viewSRight.Length() - 1f) < 1e-3f && MathF.Abs(viewSUp.Length() - 1f) < 1e-3f
            && MathF.Abs(Vector3.Dot(viewSRight, viewSUp)) < 1e-3f,
            $"Stretched axes lost unit orthogonality in view space: right={viewSRight}, up={viewSUp}.");
    }

    private static LumenCaptureContext PausedFrame(float unscaled) => new()
    {
        FrameIndex = 0, DeltaTime = 0f, UnscaledDeltaTime = unscaled, ViewportWidth = 64, ViewportHeight = 64,
        ViewMatrix = Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity, LumenRenderPassId = 0,
    };

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
