using System;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class ParticleParitySuite
{
    [LumenFact]
    public static void ReviewR1ExplicitPulseIsAdmittedAfterFullPoolReclaim()
    {
        var sim = new CpuParticleSimulator();
        int id = sim.AcquireEmitter(new EmitterSpec
        {
            Name = "explicit-full-pool", MaxParticles = 1, StartLifetimeMin = 0.5f, StartLifetimeMax = 0.5f,
        }, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u, false);
        sim.EmitBurst(id, 1);
        Step(sim, 0f);
        Require(sim.GetLiveParticleCount(id) == 1, "First capture admits the explicit pulse.");
        sim.EmitBurst(id, 1);
        Step(sim, 0.5f);
        Require(sim.GetLiveParticleCount(id) == 1, "The next explicit pulse must use the slot reclaimed this capture.");
        Near(0f, sim.LiveParticles[0].Age);
        Require(sim.LiveParticles[0].EmitterId == id, "The replacement retains its emitter ownership.");
    }

    [LumenFact]
    public static void ReviewR1AcceptedPulsesSurvivePauseRestartAndDrainButNotImmediateRelease()
    {
        var sim = new CpuParticleSimulator();
        int id = sim.AcquireEmitter(new EmitterSpec { Name = "pending", StartLifetimeMin = 1f, StartLifetimeMax = 1f }, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u, false);
        sim.EmitBurst(id, 1);
        Require(sim.LiveParticleCount == 0 && sim.HasActiveWork, "A pending pulse is active work, not yet a live particle.");
        sim.RestartEmitter(id);
        sim.SetEmitterParams(id, false, 1f, 1f);
        sim.ReleaseEmitter(id, false);
        Step(sim, 0.5f);
        Require(sim.GetLiveParticleCount(id) == 1 && sim.ActiveEmitterCount == 1, "Accepted pulse survives restart, pause and drain.");
        Near(0f, sim.LiveParticles[0].Age);
        Step(sim, 1f);
        Require(sim.ActiveEmitterCount == 0 && !sim.HasActiveWork, "Draining emitter releases only after its new particle lifetime.");
        int cancelled = sim.AcquireEmitter(new EmitterSpec { Name = "cancelled" }, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u, false);
        sim.EmitBurst(cancelled, 1);
        sim.ReleaseEmitter(cancelled, true);
        Step(sim, 0f);
        Require(sim.LiveParticleCount == 0 && sim.ActiveEmitterCount == 0 && !sim.HasActiveWork, "Immediate release discards pending pulses.");
    }

    [LumenFact]
    public static void BurstCarryQuarterDensityPreservesPrefixBudgetAndFrameGrouping()
    {
        float original = RimLumen.Core.Rendering.RimLumenRenderHost.ParticleDensityScale;
        try
        {
            RimLumen.Core.Rendering.RimLumenRenderHost.ParticleDensityScale = 0.25f;
            var spec = new EmitterSpec { Name = "carry", MaxParticles = 16, StartLifetimeMin = 10f, StartLifetimeMax = 10f };
            var cpu = new CpuParticleSimulator();
            int cpuId = cpu.AcquireEmitter(spec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u, false);
            using var gpu = new RimLumen.Core.ParticlesGpu.LumenGpuParticleEngine(16);
            int gpuId = gpu.CreateInstance(spec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), -1, false, 1u);
            int cpuBefore = 0;
            var outputs = new int[8];
            for (int i = 0; i < outputs.Length; i++)
            {
                cpu.EmitBurst(cpuId, 1);
                gpu.EmitBurst(gpuId, 1);
                Step(cpu, 0f);
                gpu.CaptureMainThread(BurstFrame(0f, (ulong)i));
                int admitted = cpu.LiveParticleCount - cpuBefore;
                int gpuBudget = (int)gpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z;
                outputs[i] = admitted;
                Require(admitted == gpuBudget, $"Pulse {i}: CPU={admitted}, GPU={gpuBudget}.");
                cpuBefore = cpu.LiveParticleCount;
            }
            Console.WriteLine($"density=.25 count1 x8 admissions=[{string.Join(',', outputs)}], total={cpu.LiveParticleCount}.");
            Require(outputs.AsSpan().SequenceEqual(new[] { 1, 0, 0, 0, 1, 0, 0, 0 }) && cpu.LiveParticleCount == 2,
                "Burst carry must ceil the accumulated total, not enforce min1 for each pulse.");
            var groupedCpu = new CpuParticleSimulator();
            int groupedId = groupedCpu.AcquireEmitter(spec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u, false);
            using var groupedGpu = new RimLumen.Core.ParticlesGpu.LumenGpuParticleEngine(16);
            int groupedGpuId = groupedGpu.CreateInstance(spec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), -1, false, 1u);
            for (int i = 0; i < 8; i++)
            {
                groupedCpu.EmitBurst(groupedId, 1); groupedGpu.EmitBurst(groupedGpuId, 1);
            }
            Step(groupedCpu, 0f); groupedGpu.CaptureMainThread(BurstFrame(0f));
            Require(groupedCpu.LiveParticleCount == 2 && groupedGpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z == 2f,
                "Eight pulses grouped into one capture request the same total as eight separate captures.");
            cpu.EmitBurst(cpuId, 7); gpu.EmitBurst(gpuId, 7);
            Step(cpu, 0f); gpu.CaptureMainThread(BurstFrame(0f, 8));
            Require(cpu.LiveParticleCount == 4 && gpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z == 2f, "Fresh count7 at quarter density requests2.");
            cpu.EmitBurst(cpuId, 7); gpu.EmitBurst(gpuId, 7);
            Step(cpu, 0f); gpu.CaptureMainThread(BurstFrame(0f, 9));
            Require(cpu.LiveParticleCount == 6 && gpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z == 2f, "Second count7 with carry also requests2.");
            CheckPulseDensity(density: 0.25f, first: 4, second: 4, expected: 2);
            CheckPulseDensity(density: 1f, first: 6, second: 6, expected: 8, capacity: 8);
            CheckPulseDensity(density: 0.999f, first: 1000, second: 0, expected: 999, capacity: 1024);
        }
        finally { RimLumen.Core.Rendering.RimLumenRenderHost.ParticleDensityScale = original; }
    }

    [LumenFact]
    public static void BurstCarryTenthDensityFloorsQuantumBeforeIntegerCeiling()
    {
        float original = RimLumen.Core.Rendering.RimLumenRenderHost.ParticleDensityScale;
        try
        {
            RimLumen.Core.Rendering.RimLumenRenderHost.ParticleDensityScale = 0.1f;
            var spec = new EmitterSpec { Name = "tenths", MaxParticles = 16, StartLifetimeMin = 10f, StartLifetimeMax = 10f };
            var cpu = new CpuParticleSimulator();
            int cpuId = cpu.AcquireEmitter(spec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u, false);
            using var gpu = new RimLumen.Core.ParticlesGpu.LumenGpuParticleEngine(16);
            int gpuId = gpu.CreateInstance(spec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), -1, false, 1u);
            for (int i = 0; i < 10; i++)
            {
                cpu.EmitBurst(cpuId, 1); gpu.EmitBurst(gpuId, 1);
            }
            Step(cpu, 0f); gpu.CaptureMainThread(BurstFrame(0f));
            int gpuBudget = (int)gpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z;
            Console.WriteLine($"density=.1, count1x10: CPU={cpu.LiveParticleCount}, GPU={gpuBudget}, expected=1 (floor quantum6553, cumulative65530).");
            Require(cpu.LiveParticleCount == 1 && gpuBudget == 1,
                "floor(.1*65536)=6553 makes ten unit pulses request ceil(65530/65536)=1, never the rounded quantum's2.");
        }
        finally { RimLumen.Core.Rendering.RimLumenRenderHost.ParticleDensityScale = original; }
    }

    [LumenFact]
    public static void BurstCarryFixedPointPreventsFifthDensityDriftAndDoesNotResetAtLoopWrap()
    {
        float original = RimLumen.Core.Rendering.RimLumenRenderHost.ParticleDensityScale;
        try
        {
            RimLumen.Core.Rendering.RimLumenRenderHost.ParticleDensityScale = 0.2f;
            var spec = new EmitterSpec { Name = "fifths", MaxParticles = 16, StartLifetimeMin = 20f, StartLifetimeMax = 20f };
            var cpu = new CpuParticleSimulator();
            int cpuId = cpu.AcquireEmitter(spec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u, false);
            using var gpu = new RimLumen.Core.ParticlesGpu.LumenGpuParticleEngine(16);
            int gpuId = gpu.CreateInstance(spec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), -1, false, 1u);
            for (int i = 0; i < 5; i++)
            {
                cpu.EmitBurst(cpuId, 1); gpu.EmitBurst(gpuId, 1);
            }
            Step(cpu, 0f); gpu.CaptureMainThread(BurstFrame(0f));
            Console.WriteLine($"density=.2, count1x5: CPU={cpu.LiveParticleCount}, GPU={gpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z}, requested=1.");
            Require(cpu.LiveParticleCount == 1 && gpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z == 1f,
                "Q65536 round(.2*Q)=13107; five unit pulses total65535/Q request exactly1, not a floating-point overshoot2.");
            RimLumen.Core.Rendering.RimLumenRenderHost.ParticleDensityScale = 0.25f;
            var looping = new EmitterSpec
            {
                Name = "carry-loop", MaxParticles = 16, Looping = true, Duration = 1f, Bursts = new[] { new ParticleBurstSpec(0.5f, 1) },
                StartLifetimeMin = 20f, StartLifetimeMax = 20f,
            };
            var loopCpu = new CpuParticleSimulator();
            loopCpu.AcquireEmitter(looping, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u);
            using var loopGpu = new RimLumen.Core.ParticlesGpu.LumenGpuParticleEngine(16);
            loopGpu.CreateInstance(looping, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), -1, false, 1u);
            int totalGpu = 0;
            for (int i = 0; i < 8; i++)
            {
                Step(loopCpu, 1f); loopGpu.CaptureMainThread(BurstFrame(1f, (ulong)i));
                totalGpu += (int)loopGpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z;
            }
            Require(loopCpu.LiveParticleCount == 2 && totalGpu == 2,
                "Eight loops with one scheduled unit burst each at quarter density must request2; loop wrap does not reset the carry.");
        }
        finally { RimLumen.Core.Rendering.RimLumenRenderHost.ParticleDensityScale = original; }
    }

    [LumenFact]
    public static void BurstCarryScheduledAndExplicitShareOneRemainderAndRestartKeepsPending()
    {
        float original = RimLumen.Core.Rendering.RimLumenRenderHost.ParticleDensityScale;
        try
        {
            RimLumen.Core.Rendering.RimLumenRenderHost.ParticleDensityScale = 0.25f;
            var spec = new EmitterSpec
            {
                Name = "shared-carry", MaxParticles = 8, Duration = 1f, Bursts = new[] { new ParticleBurstSpec(0.25f, 1) },
                StartLifetimeMin = 10f, StartLifetimeMax = 10f,
            };
            var cpu = new CpuParticleSimulator();
            int cpuId = cpu.AcquireEmitter(spec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u);
            using var gpu = new RimLumen.Core.ParticlesGpu.LumenGpuParticleEngine(8);
            int gpuId = gpu.CreateInstance(spec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), -1, false, 1u);
            cpu.EmitBurst(cpuId, 1); gpu.EmitBurst(gpuId, 1);
            Step(cpu, 0.25f); gpu.CaptureMainThread(BurstFrame(0.25f));
            Require(cpu.LiveParticleCount == 1 && gpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z == 1f,
                "Explicit pulse consumes the fresh ceil; crossed scheduled burst shares its negative remainder and admits zero.");
            cpu.RestartEmitter(cpuId); gpu.RestartInstance(gpuId);
            cpu.EmitBurst(cpuId, 1); gpu.EmitBurst(gpuId, 1);
            cpu.RestartEmitter(cpuId); gpu.RestartInstance(gpuId);
            cpu.EmitBurst(cpuId, 1); gpu.EmitBurst(gpuId, 1);
            int before = cpu.LiveParticleCount;
            Step(cpu, 0f); gpu.CaptureMainThread(BurstFrame(0f, 1));
            Require(cpu.LiveParticleCount - before == 2 && gpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z == 2f,
                "Restart resets the fractional burst carry but must retain already accepted pending particles.");
        }
        finally { RimLumen.Core.Rendering.RimLumenRenderHost.ParticleDensityScale = original; }
    }

    [LumenFact]
    public static void BurstCarryGoToResetsTheEmitterRemainder()
    {
        float original = RimLumen.Core.Rendering.RimLumenRenderHost.ParticleDensityScale;
        try
        {
            RimLumen.Core.Rendering.RimLumenRenderHost.ParticleDensityScale = 0.25f;
            var asset = new EffectAsset
            {
                EffectId = "carry-goto", DefaultDuration = 10f,
                Nodes = new[] { new EffectNodeSpec { Name = "node", Emitter = new EmitterSpec
                {
                    Name = "goto", MaxParticles = 8, Duration = 10f, Bursts = new[] { new ParticleBurstSpec(9f, 1) },
                    StartLifetimeMin = 10f, StartLifetimeMax = 10f,
                } } },
                States = new[] { new EffectStateSpec { Name = "active", Nodes = new[] { "node" }, Loop = true } },
                TimelineEvents = new[] { new TimelineEventSpec(9f, "pulse", 0, "active") },
            };
            var service = VfxTestData.Service(asset);
            using var handle = service.Play(new LumenPlayRequest { EffectId = asset.EffectId, MapIndex = 7 });
            handle.SetParam("rate", 0f);
            handle.Trigger("pulse"); VfxTestData.Step(service, 0f);
            handle.Trigger("pulse"); VfxTestData.Step(service, 0f);
            Require(service.Simulator.LiveParticleCount == 1, "Before GoTo the second unit pulse must use the same carry.");
            handle.GoTo("active");
            handle.Trigger("pulse"); VfxTestData.Step(service, 0f);
            Require(service.Simulator.LiveParticleCount == 2, "State reentry restarts the emitter and grants a fresh first nonzero burst.");
        }
        finally { RimLumen.Core.Rendering.RimLumenRenderHost.ParticleDensityScale = original; }
    }

    [LumenFact]
    public static void Phase3PauseSkipsCrossedBurstAfterActivation()
    {
        var spec = new EmitterSpec
        {
            Name = "pause-crossed", Duration = 1f, MaxParticles = 8,
            Bursts = new[] { new ParticleBurstSpec(0.5f, 2) },
        };
        var transform = new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity);
        var cpu = new CpuParticleSimulator();
        int cpuId = cpu.AcquireEmitter(spec, transform, 1u);
        using var gpu = new RimLumen.Core.ParticlesGpu.LumenGpuParticleEngine(8);
        int gpuId = gpu.CreateInstance(spec, transform, -1, false, 1u);
        gpu.UpdateInstance(gpuId, transform, true, 1f, 1f);
        Step(cpu, 0.25f);
        gpu.CaptureMainThread(BurstFrame(0.25f));
        Require(cpu.LiveParticleCount == 0 && gpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z == 0f, "No burst before pause.");
        cpu.SetEmitterParams(cpuId, false, 1f, 1f);
        gpu.UpdateInstance(gpuId, transform, false, 1f, 1f);
        Require(cpu.HasActiveWork, "An activated paused clock with no particles must remain eligible for production capture.");
        Step(cpu, 0.75f);
        gpu.CaptureMainThread(BurstFrame(0.75f, 1));
        Require(!cpu.HasActiveWork, "A completed nonloop clock without live or pending particles becomes idle.");
        Require(cpu.LiveParticleCount == 0 && gpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z == 0f, "Paused burst is skipped.");
        cpu.SetEmitterParams(cpuId, true, 1f, 1f);
        gpu.UpdateInstance(gpuId, transform, true, 1f, 1f);
        Step(cpu, 0.25f);
        gpu.CaptureMainThread(BurstFrame(0.25f, 2));
        Console.WriteLine($"pause-resume expected=0 CPU={cpu.LiveParticleCount} GPU={gpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z}");
        Require(cpu.LiveParticleCount == 0 && gpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z == 0f, "Resume must not replay the crossed burst.");
    }

    [LumenFact]
    public static void Phase3PauseNeverActivatedLoopKeepsItsFullWindow()
    {
        var spec = new EmitterSpec
        {
            Name = "dormant-loop", Duration = 1f, Looping = true, MaxParticles = 8,
            StartLifetimeMin = 10f, StartLifetimeMax = 10f,
            Bursts = new[] { new ParticleBurstSpec(0.5f, 2) },
        };
        var transform = new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity);
        var cpu = new CpuParticleSimulator();
        int cpuId = cpu.AcquireEmitter(spec, transform, 1u, emitting: false);
        using var gpu = new RimLumen.Core.ParticlesGpu.LumenGpuParticleEngine(8);
        int gpuId = gpu.CreateInstance(spec, transform, -1, false, 1u);
        gpu.UpdateInstance(gpuId, transform, false, 1f, 1f);
        Require(!cpu.HasActiveWork, "A dormant loop without live or pending particles stays inactive.");
        Step(cpu, 0.75f); gpu.CaptureMainThread(BurstFrame(0.75f));
        cpu.SetEmitterParams(cpuId, true, 1f, 1f);
        cpu.SetEmitterParams(cpuId, false, 1f, 1f);
        Require(cpu.HasActiveWork, "A loop activated then paused before its first capture still needs its clock advanced.");
        cpu.SetEmitterParams(cpuId, true, 1f, 1f);
        gpu.UpdateInstance(gpuId, transform, true, 1f, 1f);
        Step(cpu, 0.5f); gpu.CaptureMainThread(BurstFrame(0.5f, 1));
        Require(cpu.LiveParticleCount == 2 && gpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z == 2f,
            "Never-activated looping emitters start at time zero on both backends.");
        cpu.RestartEmitter(cpuId); gpu.RestartInstance(gpuId);
        cpu.SetEmitterParams(cpuId, false, 1f, 1f);
        gpu.UpdateInstance(gpuId, transform, false, 1f, 1f);
        Step(cpu, 0.75f); gpu.CaptureMainThread(BurstFrame(0.75f, 2));
        cpu.SetEmitterParams(cpuId, true, 1f, 1f);
        gpu.UpdateInstance(gpuId, transform, true, 1f, 1f);
        Step(cpu, 0.25f); gpu.CaptureMainThread(BurstFrame(0.25f, 3));
        Require(cpu.LiveParticleCount == 2 && gpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z == 0f,
            "Restart activates immediately, so a pause before the next update still skips crossed bursts.");
    }

    [LumenFact]
    public static void Phase3BurstCapacityDropDoesNotReplayAfterPoolExpires()
    {
        var spec = new EmitterSpec
        {
            Name = "capacity-drop", Duration = 1f, MaxParticles = 3,
            StartLifetimeMin = 10f, StartLifetimeMax = 10f,
            Bursts = new[] { new ParticleBurstSpec(0.25f, 5), new ParticleBurstSpec(0.5f, 7) },
        };
        var transform = new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity);
        var cpu = new CpuParticleSimulator();
        int cpuId = cpu.AcquireEmitter(spec, transform, 1u);
        using var gpu = new RimLumen.Core.ParticlesGpu.LumenGpuParticleEngine(3);
        int gpuId = gpu.CreateInstance(spec, transform, -1, false, 1u);
        cpu.EmitBurst(cpuId, 2); gpu.EmitBurst(gpuId, 2);
        Step(cpu, 1f); gpu.CaptureMainThread(BurstFrame(1f));
        Console.WriteLine($"capacity-drop expected=3 CPU-admitted={cpu.LiveParticleCount} GPU-budget={gpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z}");
        Require(cpu.LiveParticleCount == 3 && gpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z == 3f,
            "Authored bursts and a pending pulse are admitted only up to capacity.");
        Step(cpu, 10f); gpu.CaptureMainThread(BurstFrame(10f, 1));
        Require(cpu.LiveParticleCount == 0 && gpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z == 0f,
            "Dropped authored and explicit requests are not replenished after the full pool expires.");
    }

    private static LumenCaptureContext BurstFrame(float dt, ulong index = 0) => new()
    {
        FrameIndex = index, ViewportWidth = 100, ViewportHeight = 100, LumenRenderPassId = 0,
        DeltaTime = dt, ViewMatrix = Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity,
    };

    private static void CheckPulseDensity(float density, int first, int second, int expected, int capacity = 16)
    {
        RimLumen.Core.Rendering.RimLumenRenderHost.ParticleDensityScale = density;
        var spec = new EmitterSpec
        {
            Name = "pulse-density", MaxParticles = capacity, StartLifetimeMin = 10f, StartLifetimeMax = 10f,
        };
        var cpu = new CpuParticleSimulator();
        int cpuId = cpu.AcquireEmitter(spec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u, false);
        cpu.EmitBurst(cpuId, first);
        cpu.EmitBurst(cpuId, second);
        Step(cpu, 0f);
        using var gpu = new RimLumen.Core.ParticlesGpu.LumenGpuParticleEngine(capacity);
        int gpuId = gpu.CreateInstance(spec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), -1, false, 1u);
        gpu.EmitBurst(gpuId, first);
        gpu.EmitBurst(gpuId, second);
        gpu.CaptureMainThread(new LumenCaptureContext
        {
            FrameIndex = 0, ViewportWidth = 100, ViewportHeight = 100, LumenRenderPassId = 0,
            DeltaTime = 0f, ViewMatrix = Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity,
        });
        int gpuBudget = (int)gpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z;
        Console.WriteLine($"density={density}, pulses={first}+{second}: CPU={cpu.LiveParticleCount}, GPU={gpuBudget}, expected={expected}.");
        Require(cpu.LiveParticleCount == expected && gpuBudget == expected,
            $"density={density}, pulses={first}+{second}: expected {expected} on both backends, CPU={cpu.LiveParticleCount}, GPU={gpuBudget}.");
    }

    [LumenFact]
    public static void ReviewR1TimelineEndKeepsPulseOwnershipAndImmediateStopCancelsIt()
    {
        var asset = new EffectAsset
        {
            EffectId = "review-end-pulse", DefaultDuration = 0.25f,
            Nodes = new[] { new EffectNodeSpec { Name = "pulse", Emitter = VfxTestData.Emitter(rate: 1f, lifetime: 0.5f, tier: LumenParticleTier.L1_NativeCpu) } },
            TimelineEvents = new[] { new TimelineEventSpec(0.25f, "pulse", 0) },
        };
        var service = VfxTestData.Service(asset);
        using var handle = service.Play(new LumenPlayRequest { EffectId = asset.EffectId, MapIndex = 7 });
        handle.SetParam("rate", 0f);
        VfxTestData.Step(service, 0.25f);
        Require(handle.IsAlive && service.Simulator.LiveParticleCount == 1, "Timeline's final-frame pulse keeps the draining handle alive.");
        Near(0f, service.Simulator.LiveParticles[0].Age);
        VfxTestData.Step(service, 0.5f);
        VfxTestData.Step(service, 0f);
        Require(!handle.IsAlive && service.Simulator.ActiveEmitterCount == 0, "Ownership releases after the full post-admission particle lifetime.");
        using var cancelled = service.Play(new LumenPlayRequest { EffectId = asset.EffectId, MapIndex = 7 });
        cancelled.Trigger("pulse");
        cancelled.Stop(false);
        VfxTestData.Step(service, 0f);
        Require(!cancelled.IsAlive && service.Simulator.LiveParticleCount == 0 && service.Simulator.ActiveEmitterCount == 0,
            "Immediate public stop discards a queued Trigger pulse and its emitter ownership.");
    }

    [LumenFact]
    public static void ReviewR2RejectsInvalidRateAndZeroRetainsAuthoredBurst()
    {
        using var gpu = new RimLumen.Core.ParticlesGpu.LumenGpuParticleEngine(8);
        var asset = new EffectAsset
        {
            EffectId = "review-negative-rate", DefaultDuration = 10f,
            Nodes = new[] { new EffectNodeSpec { Name = "node", Emitter = new EmitterSpec
            {
                Name = "rate-burst", MaxParticles = 8, RateOverTime = 4f, Bursts = new[] { new ParticleBurstSpec(0.25f, 1) },
                StartLifetimeMin = 10f, StartLifetimeMax = 10f,
            } } },
        };
        var cpuService = VfxTestData.Service(asset);
        var gpuService = VfxTestData.Service(asset);
        gpuService.GpuEngineProvider = () => gpu;
        using var cpuHandle = cpuService.Play(new LumenPlayRequest { EffectId = asset.EffectId, MapIndex = 7 });
        using var gpuHandle = gpuService.Play(new LumenPlayRequest { EffectId = asset.EffectId, MapIndex = 7 });
        GlowAssert.Throws<ArgumentOutOfRangeException>(() => cpuHandle.SetParam("rate", -1f));
        GlowAssert.Throws<ArgumentOutOfRangeException>(() => gpuHandle.SetParam("rate", -1f));
        GlowAssert.Throws<ArgumentOutOfRangeException>(() => cpuHandle.SetParam("rate", float.NaN));
        GlowAssert.Throws<ArgumentOutOfRangeException>(() => cpuHandle.SetParam("rate", float.PositiveInfinity));
        GlowAssert.Throws<ArgumentOutOfRangeException>(() => cpuHandle.SetParam("rate", float.NegativeInfinity));
        cpuHandle.SetParam("rate", 0f);
        gpuHandle.SetParam("rate", 0f);
        VfxTestData.Step(cpuService, 0.25f);
        VfxTestData.Step(gpuService, 0.25f);
        gpu.CaptureMainThread(new LumenCaptureContext
        {
            FrameIndex = 0, ViewportWidth = 100, ViewportHeight = 100, LumenRenderPassId = 0,
            DeltaTime = 0.25f, ViewMatrix = Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity,
        });
        int cpuCount = cpuService.Simulator.LiveParticleCount;
        float gpuBudget = gpu.CapturedFrame!.Emitters[0].Parameters.Slots.Z;
        Console.WriteLine($"rate=0, authored rate4 + burst(.25,1), dt=.25: CPU count={cpuCount}, GPU admission={gpuBudget}.");
        Require(cpuCount == 1 && gpuBudget == 1f, "Rate zero stops continuous emission without cancelling the authored burst.");
    }

    [LumenFact]
    public static void P2A1aPointAndBoxEmitAlongPositiveY()
    {
        Vector(Vector3.UnitY * 2f, Spawn(Spec(LumenShapeKind.Point), Matrix4x4.Identity).Velocity);
        Vector(Vector3.UnitY * 2f, Spawn(Spec(LumenShapeKind.Box), Matrix4x4.Identity).Velocity);
    }

    [LumenFact]
    public static void P2A1bConeSpawnsOnEllipticDisc()
    {
        CpuParticle p = Spawn(Spec(LumenShapeKind.Cone), Matrix4x4.Identity);
        Require(p.Position.LengthSquared() > 0.01f && p.Position.Y == 0f, "Cone base is an XZ disc, not its apex.");
        Near(p.Position.X / 2f * p.Velocity.Z, p.Position.Z / 4f * p.Velocity.X);
    }

    [LumenFact]
    public static void P2A1cSphereUsesAllThreeExtents()
    {
        CpuParticle p = Spawn(Spec(LumenShapeKind.Sphere), Matrix4x4.Identity);
        Vector3 unitPosition = p.Position / new Vector3(2f, 3f, 4f);
        Vector(Vector3.Normalize(unitPosition) * 2f, p.Velocity);
    }

    [LumenFact]
    public static void P2A1dCircleUsesBothGroundExtents()
    {
        CpuParticle p = Spawn(Spec(LumenShapeKind.CircleXZ), Matrix4x4.Identity);
        Vector3 unitPosition = p.Position / new Vector3(2f, 3f, 4f);
        Vector(Vector3.Normalize(unitPosition) * 2f, p.Velocity);
    }

    [LumenFact]
    public static void P2A1eWorldTransformPreservesVelocityScale()
    {
        CpuParticle unscaled = Spawn(Spec(LumenShapeKind.Cone), Matrix4x4.Identity);
        Matrix4x4 world = Matrix4x4.CreateScale(2f, 3f, 4f) * Matrix4x4.CreateRotationZ(0.4f);
        CpuParticle scaled = Spawn(Spec(LumenShapeKind.Cone), world);
        Vector(Vector3.TransformNormal(unscaled.Velocity, world), scaled.Velocity);
    }

    [LumenFact]
    public static void P2A2aZeroAxisStillUsesExponentialDrag()
    {
        var sim = new CpuParticleSimulator();
        int id = sim.AcquireEmitter(new EmitterSpec
        {
            Name = "drag", StartSpeedMin = 2f, StartSpeedMax = 2f, StartLifetimeMin = 20f, StartLifetimeMax = 20f,
            ForceFields = new[] { new ForceFieldSpec(ForceFieldKind.GravityDrag, Vector3.Zero, new Vector3(2f, 99f, 0f), 0f, 0f) },
        }, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u, false);
        sim.EmitBurst(id, 1);
        Step(sim, 0f);
        Step(sim, 0.5f);
        Vector(new Vector3(0f, 0.7357589f, 0f), sim.LiveParticles[0].Velocity);
    }

    [LumenFact]
    public static void P2A2bVortexWeightsTangentialRadialAndLiftSeparately()
    {
        Vector(new Vector3(-0.5f, 1.5f, -1f), ForceStep(new ForceFieldSpec(
            ForceFieldKind.Vortex, Vector3.UnitY, new Vector3(2f, 6f, 0f), 2f, 4f),
            new Vector3(1f, 0f, 0f), Vector3.Zero));
    }

    [LumenFact]
    public static void P2A2cZeroRadiusAttractorHasInfiniteReach()
    {
        Vector(new Vector3(-2f, 0f, 0f), ForceStep(new ForceFieldSpec(
            ForceFieldKind.PointAttractor, Vector3.Zero, Vector3.Zero, 0f, 4f),
            new Vector3(3f, 0f, 0f), Vector3.Zero));
    }

    [LumenFact]
    public static void F4AttractorUsesTheSharedCanonicalDirectionThreshold()
    {
        var field = new ForceFieldSpec(ForceFieldKind.PointAttractor, Vector3.Zero, Vector3.Zero, 0f, 4f);
        Vector(new Vector3(-2f, 0f, 0f), ForceStep(field, new Vector3(0.000005f, 0f, 0f), Vector3.Zero));
        Vector(new Vector3(-2f, 0f, 0f), ForceStep(field, new Vector3(0.00002f, 0f, 0f), Vector3.Zero));
    }

    [LumenFact]
    public static void PausedKillBelowFloorFreezesGameTimeButStillRechecksMovedFloor()
    {
        var spec = new EmitterSpec
        {
            Name = "paused-floor", MaxParticles = 8, WorldSpaceSimulation = false, KillBelowFloor = true, Shape = LumenShapeKind.Cone,
            ConeAngleDegrees = 0f, StartSpeedMin = 2f, StartSpeedMax = 2f, StartLifetimeMin = 10f, StartLifetimeMax = 10f,
            ForceFields = new[] { new ForceFieldSpec(ForceFieldKind.GravityDrag, Vector3.UnitY, Vector3.Zero, 0f, -10f) },
        };
        var cpu = new CpuParticleSimulator();
        int id = cpu.AcquireEmitter(spec, new LumenEmitterTransform(Matrix4x4.CreateTranslation(0f, 2f, 0f), Quaternion.Identity), 1u, false);
        cpu.EmitBurst(id, 1); Step(cpu, 0f); Step(cpu, 0.5f);
        CpuParticle before = cpu.LiveParticles[0];
        var paused = BurstFrame(0f) with { UnscaledDeltaTime = 0.5f };
        cpu.CaptureMainThread(paused);
        Vector(before.Position, cpu.LiveParticles[0].Position);
        Vector(before.Velocity, cpu.LiveParticles[0].Velocity);
        Near(before.Age, cpu.LiveParticles[0].Age);
        cpu.SetEmitterVerticalState(id, false, 1f);
        cpu.CaptureMainThread(paused);
        Require(cpu.LiveParticleCount == 0, "A zero-dt capture still expires a descending particle below a moved floor.");
        using var gpu = new RimLumen.Core.ParticlesGpu.LumenGpuParticleEngine(8);
        gpu.CreateInstance(spec, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), mapIndex: 7, realTime: false, emissionSeed: 1u);
        gpu.CaptureMainThread(paused);
        Require(gpu.CapturedFrame!.Emitters[0].Parameters.State.X == 0f && gpu.CapturedFrame.Emitters[0].KillBelowFloor,
            "KillBelowFloor retains its compute work while the uploaded integration dt remains zero.");
    }

    [LumenFact]
    public static void P2A2dCurlIsSmoothThreeDimensionalAndUsesOffset()
    {
        var field = new ForceFieldSpec(ForceFieldKind.CurlTurbulence, Vector3.Zero, new Vector3(0.1f, 0.2f, 0.3f), 0f, 2f, 0.7f);
        Vector3 a = ForceStep(field, new Vector3(0.4f, 0.6f, 0.8f), Vector3.Zero);
        Vector3 b = ForceStep(field, new Vector3(0.4001f, 0.6f, 0.8f), Vector3.Zero);
        Vector3 shifted = ForceStep(field with { Parameters = Vector3.Zero }, new Vector3(0.3f, 0.4f, 0.5f), Vector3.Zero);
        Require(MathF.Abs(a.Y) > 0.01f && Vector3.Distance(a, b) > 0.000001f && Vector3.Distance(a, b) < 0.001f,
            "Curl must vary smoothly within a lattice cell and accelerate all three axes.");
        Vector(a, shifted);
    }

    [LumenFact]
    public static void P2A2eMovingWorldFieldsFollowNodeButDirectionsStayAuthored()
    {
        Vector(new Vector3(0f, 0f, 2f), ForceStep(new ForceFieldSpec(
            ForceFieldKind.Vortex, Vector3.UnitY, Vector3.Zero, 0f, 4f),
            new Vector3(10f, 0f, 0f), new Vector3(11f, 0f, 0f)));
        Vector(new Vector3(2f, 0f, 0f), ForceStep(new ForceFieldSpec(
            ForceFieldKind.PointAttractor, new Vector3(2f, 0f, 0f), Vector3.Zero, 0f, 4f),
            new Vector3(10f, 0f, 0f), new Vector3(11f, 0f, 0f)));
    }

    [LumenFact]
    public static void ParityFieldDirectionsStayAuthoredAndLocalCentresStayLocal()
    {
        Matrix4x4 world = Matrix4x4.CreateRotationZ(MathF.PI / 2f) * Matrix4x4.CreateTranslation(10f, 20f, 30f);
        var vortex = new EmitterSpec
        {
            Name = "axis", ForceFields = new[] { new ForceFieldSpec(ForceFieldKind.Vortex, Vector3.UnitY, Vector3.Zero, 0f, 4f) },
        };
        Vector(new Vector3(0f, 0f, -4f), ParticleForceMath.Acceleration(vortex, world, new Vector3(11f, 20f, 30f), out _));
        var local = new EmitterSpec
        {
            Name = "local-centre", WorldSpaceSimulation = false,
            ForceFields = new[] { new ForceFieldSpec(ForceFieldKind.PointAttractor, new Vector3(2f, 0f, 0f), Vector3.Zero, 0f, 4f) },
        };
        Vector(new Vector3(4f, 0f, 0f), ParticleForceMath.Acceleration(local, world, Vector3.UnitX, out _));
        var rotatedCentre = new EmitterSpec { Name = "rotated-centre", ForceFields = local.ForceFields };
        Vector(new Vector3(0f, 4f, 0f), ParticleForceMath.Acceleration(rotatedCentre, world, world.Translation, out _));
    }

    private static Vector3 ForceStep(ForceFieldSpec field, Vector3 birthOrigin, Vector3 currentOrigin)
    {
        var sim = new CpuParticleSimulator();
        int id = sim.AcquireEmitter(new EmitterSpec
        {
            Name = "force", StartLifetimeMin = 20f, StartLifetimeMax = 20f, ForceFields = new[] { field },
        }, new LumenEmitterTransform(Matrix4x4.CreateTranslation(birthOrigin), Quaternion.Identity), 1u, false);
        sim.EmitBurst(id, 1);
        Step(sim, 0f);
        sim.SetEmitterTransform(id, new LumenEmitterTransform(Matrix4x4.CreateTranslation(currentOrigin), Quaternion.Identity));
        Step(sim, 0.5f);
        return sim.LiveParticles[0].Velocity;
    }

    private static void Step(CpuParticleSimulator sim, float dt, Matrix4x4? view = null)
        => sim.CaptureMainThread(new LumenCaptureContext
        {
            FrameIndex = 0, ViewportWidth = 100, ViewportHeight = 100, LumenRenderPassId = 0,
            DeltaTime = dt, ViewMatrix = view ?? Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity,
        });

    [LumenFact]
    public static void P2A3aLengthScaleDoesNotChangeWidth()
    {
        var sim = Quad(LumenBillboardMode.Camera, lengthScale: 3f);
        Near(2f, Vector3.Distance(sim.Vertices[0].Position, sim.Vertices[1].Position));
        Near(2f, sim.LiveParticles[0].Size);
    }

    [LumenFact]
    public static void P2A3bStretchAddsVelocityInWorldUnits()
    {
        var sim = Quad(LumenBillboardMode.StretchedVelocity, lengthScale: 3f);
        Near(2f, Vector3.Distance(sim.Vertices[0].Position, sim.Vertices[1].Position));
        Near(7f, Vector3.Distance(sim.Vertices[1].Position, sim.Vertices[2].Position));
    }

    [LumenFact]
    public static void P2A3cHorizontalUsesFixedWorldAxes()
    {
        var sim = Quad(LumenBillboardMode.Horizontal, lengthScale: 1f);
        Step(sim, 0f, Matrix4x4.CreateRotationY(0.7f));
        Vector(new Vector3(-1f, 0f, -1f), sim.Vertices[0].Position);
        Vector(new Vector3(1f, 0f, 1f), sim.Vertices[2].Position);
    }

    [LumenFact]
    public static void P2A3dWorldSizeBakesDeterminantAtBirth()
    {
        var sim = new CpuParticleSimulator();
        int id = sim.AcquireEmitter(new EmitterSpec
        {
            Name = "size-world", StartSizeMin = 2f, StartSizeMax = 2f,
        }, new LumenEmitterTransform(Matrix4x4.CreateScale(-2f, 4f, 8f), Quaternion.Identity), 1u, false);
        sim.EmitBurst(id, 1);
        Step(sim, 0f);
        sim.SetEmitterTransform(id, new LumenEmitterTransform(Matrix4x4.CreateScale(1f), Quaternion.Identity));
        Step(sim, 0f);
        Near(8f, sim.LiveParticles[0].StartSize);
        Near(8f, Vector3.Distance(sim.Vertices[0].Position, sim.Vertices[1].Position));
    }

    [LumenFact]
    public static void P2A3eLocalSizeUsesCurrentDeterminantAtDraw()
    {
        var sim = new CpuParticleSimulator();
        int id = sim.AcquireEmitter(new EmitterSpec
        {
            Name = "size-local", WorldSpaceSimulation = false, StartSizeMin = 2f, StartSizeMax = 2f,
        }, new LumenEmitterTransform(Matrix4x4.CreateScale(1f), Quaternion.Identity), 1u, false);
        sim.EmitBurst(id, 1);
        Step(sim, 0f);
        sim.SetEmitterTransform(id, new LumenEmitterTransform(Matrix4x4.CreateScale(-2f, 4f, 8f), Quaternion.Identity));
        Step(sim, 0f);
        Near(2f, sim.LiveParticles[0].StartSize);
        Near(8f, Vector3.Distance(sim.Vertices[0].Position, sim.Vertices[1].Position));
    }

    private static CpuParticleSimulator Quad(LumenBillboardMode mode, float lengthScale)
    {
        var sim = new CpuParticleSimulator();
        int id = sim.AcquireEmitter(new EmitterSpec
        {
            Name = "quad", BillboardMode = mode, LengthScale = lengthScale, VelocityStretchFactor = 0.5f,
            StartSpeedMin = 2f, StartSpeedMax = 2f, StartSizeMin = 2f, StartSizeMax = 2f,
        }, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u, false);
        sim.EmitBurst(id, 1);
        Step(sim, 0f);
        return sim;
    }

    [LumenFact]
    public static void P2A4cQuadRadialUvsAreIndependentOfAtlasFrame()
    {
        var sim = new CpuParticleSimulator();
        int id = sim.AcquireEmitter(new EmitterSpec
        {
            Name = "radial", FlipbookTilesX = 2, FlipbookTilesY = 2,
            StartLifetimeMin = 4f, StartLifetimeMax = 4f,
        }, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u, false);
        sim.EmitBurst(id, 1);
        Step(sim, 0f);
        Require(sim.Vertices[0].Uv == new Vector2(0f, 0.5f) && sim.Vertices[0].RadialUv == Vector2.Zero,
            "Top atlas row must not move the procedural disc origin.");
        Step(sim, 2f);
        Require(sim.Vertices[0].RadialUv == Vector2.Zero && sim.Vertices[1].RadialUv == Vector2.UnitX
            && sim.Vertices[2].RadialUv == Vector2.One && sim.Vertices[3].RadialUv == Vector2.UnitY,
            "All four local corners must remain unchanged when the atlas advances to its bottom row.");
        Require(System.Runtime.InteropServices.Marshal.SizeOf<ParticleVertex>() == 44
            && System.Runtime.InteropServices.Marshal.OffsetOf<ParticleVertex>(nameof(ParticleVertex.RadialUv)).ToInt32() == 36,
            "TexCoord1 must occupy offset36 in the 44-byte vertex.");
    }

    [LumenFact]
    public static void P2A5FullPoolReclaimsBeforeAdmittingNewParticles()
    {
        var sim = new CpuParticleSimulator();
        sim.AcquireEmitter(new EmitterSpec
        {
            Name = "pool", MaxParticles = 1, Looping = true, Duration = 1f, RateOverTime = 2f,
            StartLifetimeMin = 0.5f, StartLifetimeMax = 0.5f,
        }, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u);
        Step(sim, 0.5f);
        Step(sim, 0.5f);
        Require(sim.LiveParticleCount == 1, "The particle expiring this frame must free a slot for this frame's emission.");
        Near(0f, sim.LiveParticles[0].Age);
    }

    [LumenFact]
    public static void P2AShapeLocalTransformsSampleButNotNodeVelocityOrSize()
    {
        var spec = new EmitterSpec
        {
            Name = "shape-local", Shape = LumenShapeKind.Cone, ConeAngleDegrees = 0f,
            ShapeExtents = Vector3.Zero, ShapePosition = new Vector3(1f, 2f, 3f),
            ShapeRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2f),
            ShapeScale = new Vector3(5f, 6f, 7f), StartSpeedMin = 2f, StartSpeedMax = 2f,
            StartSizeMin = 3f, StartSizeMax = 3f,
        };
        CpuParticle p = Spawn(spec, Matrix4x4.CreateTranslation(10f, 20f, 30f));
        Vector(new Vector3(11f, 22f, 33f), p.Position);
        Vector(new Vector3(2f, 0f, 0f), p.Velocity);
        Near(3f, p.StartSize);
    }

    [LumenFact]
    public static void P2AShapeLocalScaleThenRotateThenTranslate()
    {
        var spec = new EmitterSpec
        {
            Name = "shape-order", Shape = LumenShapeKind.Box, WorldSpaceSimulation = false,
            ShapePosition = new Vector3(1f, 2f, 3f), ShapeScale = new Vector3(2f, 3f, 4f),
            ShapeRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
        };
        Vector3 p = new(1f, 2f, 3f), d = Vector3.UnitY;
        ParticleShapeTransform.Apply(spec, ref p, ref d);
        Vector(new Vector3(-5f, 4f, 15f), p);
        Vector(-Vector3.UnitX, d);
    }

    [LumenFact]
    public static void P2ACurvesUseTheSame64SampleInterpolationAsGpu()
    {
        // t^2 at age 1/2 falls between LUT samples 31/63 and 32/63:
        // interpolated scalar = 1985/7938, not the exact curve's 1/4.
        // A gradient kink at 1/2 is also interpolated across those two samples.
        var sim = new CpuParticleSimulator();
        int id = sim.AcquireEmitter(new EmitterSpec
        {
            Name = "lut", StartLifetimeMin = 2f, StartLifetimeMax = 2f, StartSizeMin = 1f, StartSizeMax = 1f,
            SizeOverLifetime = new ScalarCurve(new[] { new CurveKey(0f, 0f, 0f, 0f), new CurveKey(1f, 1f, 2f, 0f) }),
            ColorOverLifetime = new ColorGradient(new[]
            {
                new ColorStop(0f, Vector4.Zero), new ColorStop(0.5f, Vector4.One), new ColorStop(1f, Vector4.Zero),
            }),
        }, new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), 1u, false);
        sim.EmitBurst(id, 1);
        Step(sim, 0f);
        Step(sim, 1f);
        Require(MathF.Abs(sim.LiveParticles[0].Size - 0.250062988f) < 0.000001f,
            $"GPU LUT size at age0.5 is 0.250062988, CPU returned {sim.LiveParticles[0].Size}.");
        Require(MathF.Abs(sim.LiveParticles[0].Color.X - 0.984126984f) < 0.000001f,
            $"GPU LUT gradient at its kink is 62/63, CPU returned {sim.LiveParticles[0].Color.X}.");
    }

    private static EmitterSpec Spec(LumenShapeKind shape) => new()
    {
        Name = "parity", Shape = shape, ShapeExtents = new Vector3(2f, 3f, 4f),
        StartSpeedMin = 2f, StartSpeedMax = 2f, StartLifetimeMin = 20f, StartLifetimeMax = 20f,
    };

    private static CpuParticle Spawn(EmitterSpec spec, Matrix4x4 world)
    {
        var sim = new CpuParticleSimulator(123);
        int id = sim.AcquireEmitter(spec, new LumenEmitterTransform(world, Quaternion.Identity), 1u, false);
        sim.EmitBurst(id, 1);
        Step(sim, 0f);
        return sim.LiveParticles[0];
    }

    private static void Near(float expected, float actual)
        => Require(MathF.Abs(expected - actual) < 0.0001f, $"Expected {expected}, got {actual}.");
    private static void Vector(Vector3 expected, Vector3 actual)
        => Require(Vector3.Distance(expected, actual) < 0.0001f, $"Expected {expected}, got {actual}.");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
