using System;
using System.Numerics;
using System.IO;
using System.Linq;
using RimLumen.Core.Contracts;
using RimLumen.Core.ParticleWorld;
using RimLumen.Core.ParticlesGpu;
using RimLumen.Core.Rendering;
using RimLumen.Core.Scene;
using RimLumen.Core.Tests.SceneAndLighting;

namespace RimLumen.Core.Tests.ParticleWorldPhysics;

[LumenTestSuite]
public static class WorldPhysicsSuite
{
    [LumenFact]
    public static void RingWaveHasTriangularSphericalShellProfile()
    {
        var spec = new EmitterSpec
        {
            Name = "ring",
            ForceFields = new[] { new ForceFieldSpec(ForceFieldKind.RingWave, Vector3.Zero, new Vector3(4, 1, 0), 8, 6) },
        };
        Close(new Vector3(3, 0, 0), ParticleForceMath.Acceleration(spec, Matrix4x4.Identity, new Vector3(0.5f, 0, 0), out _));
        Close(Vector3.Zero, ParticleForceMath.Acceleration(spec, Matrix4x4.Identity, Vector3.Zero, out _));
        Close(Vector3.Zero, ParticleForceMath.Acceleration(spec, Matrix4x4.Identity, Vector3.UnitX, out _));
        Close(new Vector3(0, 6, 0), ParticleForceMath.Acceleration(spec, Matrix4x4.Identity, new Vector3(0, 4, 0), out _, 1));
        Close(Vector3.Zero, ParticleForceMath.Acceleration(spec, Matrix4x4.Identity, new Vector3(0, 12, 0), out _, 3));
    }

    [LumenFact]
    public static void EnvironmentFiltersChannelsSumsForcesAndConvertsLocalVectors()
    {
        ParticleEnvironmentSource[] sources =
        {
            new(LumenEnvironmentChannel.Explosion, Vector3.Zero, 8, 6, 3, 1),
            new(LumenEnvironmentChannel.Cursor, Vector3.Zero, 6, 4, 0, 0)
        };
        var spec = new EmitterSpec { Name = "world", WorldSpaceSimulation = true, EnvironmentForceScale = 0.5f };
        Close(new(4, 0, 0), ParticleEnvironmentMath.Acceleration(sources, spec, Matrix4x4.Identity, Matrix4x4.Identity, new(3, 0, 0)));
        var cursor = new EmitterSpec { Name = "cursor", EnvironmentChannels = LumenEnvironmentChannel.Cursor, WorldSpaceSimulation = true };
        Close(new(2, 0, 0), ParticleEnvironmentMath.Acceleration(sources, cursor, Matrix4x4.Identity, Matrix4x4.Identity, new(3, 100, 0)));
        var none = new EmitterSpec { Name = "none", EnvironmentChannels = LumenEnvironmentChannel.None };
        Close(Vector3.Zero, ParticleEnvironmentMath.Acceleration(sources, none, Matrix4x4.Identity, Matrix4x4.Identity, new(3, 0, 0)));

        Matrix4x4 world = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(10, 0, 20);
        Matrix4x4.Invert(world, out Matrix4x4 inverse);
        ParticleEnvironmentSource[] localSources = { new(LumenEnvironmentChannel.Cursor, new(10, 0, 20), 8, 8, 0, 0) };
        var local = new EmitterSpec { Name = "local", WorldSpaceSimulation = false };
        Close(new(0, 0, 1), ParticleEnvironmentMath.Acceleration(localSources, local, world, inverse, new(0, 10, 1)));
        ParticleEnvironmentSource pull = localSources[0] with { Strength = -8 };
        Close(new(-4, 0, 0), pull.Acceleration(new(14, 30, 20)));
    }

    [LumenFact]
    public static void EnvironmentSnapshotIsSharedByEmittersAndBothCapturesUntilNextFrame()
    {
        const int map = 711;
        var spec = new EmitterSpec { Name = "cursor", MaxParticles = 1, EnvironmentChannels = LumenEnvironmentChannel.Cursor };
        var cpu = new CpuParticleSimulator();
        using var gpu = new LumenGpuParticleEngine(3);
        int first = cpu.AcquireEmitter(spec, Transform, 1, mapIndex: map);
        int second = cpu.AcquireEmitter(spec, Transform, 2, mapIndex: map);
        gpu.CreateInstance(spec, Transform, map, false, 1);
        gpu.CreateInstance(spec, Transform, map, false, 2);
        gpu.CreateInstance(spec, Transform, map + 1, false, 3);
        cpu.EmitBurst(first, 1);
        cpu.EmitBurst(second, 1);
        cpu.CaptureMainThread(Frame(0f, 710));
        float radius = ParticleEnvironment.CursorRadius, strength = ParticleEnvironment.CursorStrength;
        try
        {
            // Host-input boundary: no game or mouse. The cursor is two cells to the left of both particles.
            ParticleEnvironment.CursorRadius = 4;
            ParticleEnvironment.CursorStrength = 8;
            var frame = Frame(0.25f, 711);
            ParticleEnvironment.CaptureSources(frame.FrameIndex, map, new Vector3(-2, 0, 0), 0);
            var shared = ParticleEnvironment.ForMap(map, spec.EnvironmentChannels);
            Check(!shared.IsEmpty && shared.Equals(ParticleEnvironment.ForMap(map, spec.EnvironmentChannels)),
                "Two emitters on the same map must borrow the same source storage.");
            cpu.CaptureMainThread(frame);
            Close(Vector3.UnitX, cpu.LiveParticles[0].Velocity);
            Close(Vector3.UnitX, cpu.LiveParticles[1].Velocity);
            ParticleEnvironment.CursorStrength = 80; // A live setting change must not leak into the second capture.
            gpu.CaptureMainThread(frame);
            var captured = gpu.CapturedFrame!;
            Check(captured.Fields.Length == 2 && captured.Fields.All(f => f.StrengthAndFrequency.X == 8f)
                && captured.Emitters[2].Parameters.Tables.Z == 0f, "L2 must see the same frozen cursor, only on its map.");
            gpu.CaptureMainThread(Frame(0.25f, 712)); // No cursor on the new frame.
            Check(gpu.CapturedFrame!.Fields.Length == 0, "The next frame must replace the old map snapshot.");
        }
        finally
        {
            ParticleEnvironment.ForgetMap(map);
            ParticleEnvironment.CursorRadius = radius;
            ParticleEnvironment.CursorStrength = strength;
        }
    }

    [LumenFact]
    public static void ExplosionShellHasRadiusDerivedUpwardForceAndExpires()
    {
        ParticleEnvironmentSource pulse = ParticleEnvironmentMath.Explosion(Vector3.Zero, 4f, 0.25f, 1f);
        Vector3 acceleration = pulse.Acceleration(new(MathF.Sqrt(3), 0, 0));
        Near(96f, acceleration.Length());
        Near(48f, acceleration.Y);
        Close(Vector3.Zero, pulse.Acceleration(new(MathF.Sqrt(3), 4, 0)));
        Close(Vector3.Zero, ParticleEnvironmentMath.Explosion(Vector3.Zero, 4f, 0.6f, 1f).Acceleration(new(4, 0, 0)));
    }

    [LumenFact]
    public static void SweptWallsBounceWithUpstreamDampenAndLifetimeLoss()
    {
        var grid = new ParticleMapGrid(7, 5, 1, new uint[] { 0, 0, 1, 0, 0 });
        var spec = new CollisionSpec { Mode = LumenCollisionMode.Bounce, Bounce = 0.5f, Dampen = 0.25f, LifetimeLoss = 0.2f };
        ParticleMapHit hit = ParticleCollisionMath.Trace(grid, new(0.5f, 1, 0.5f), new(4.5f, 1, 0.5f), spec);
        Check(hit.Hit && !hit.Roof, "A multi-cell step must hit the wall.");
        Close(new(-1, 0, 0), hit.Normal);
        Close(new(1.9999f, 1, 0.5f), hit.Position);
        Vector3 position = new(4.5f, 1, 0.5f), velocity = new(4, 1, 0);
        float age = 2;
        Check(ParticleCollisionMath.Respond(spec, hit, ref position, ref velocity, ref age, 10), "Bounce emits Collision.");
        Close(new(-1.5f, 0.75f, 0), velocity);
        Near(4, age);
        Check(!ParticleCollisionMath.Trace(grid, new(0.5f, 3, 0.5f), new(4.5f, 3, 0.5f), spec).Hit, "Above-wall flight stays unobstructed.");
    }

    [LumenFact]
    public static void KillAndRoofBothExpireButOnlyWallsEmitCollision()
    {
        var spec = new CollisionSpec { Mode = LumenCollisionMode.Kill, KillUnderRoof = true };
        var wall = new ParticleMapGrid(7, 3, 1, new uint[] { 0, 1, 0 });
        ParticleMapHit hit = ParticleCollisionMath.Trace(wall, new(0.5f, 1, 0.5f), new(2.5f, 1, 0.5f), spec);
        Vector3 position = default, velocity = Vector3.UnitX;
        float age = 0;
        Check(ParticleCollisionMath.Respond(spec, hit, ref position, ref velocity, ref age, 10), "Wall kill emits Collision before Death.");
        Near(10, age);
        var roof = wall with { Cells = new uint[] { 0, 2, 0 } };
        hit = ParticleCollisionMath.Trace(roof, new(0.5f, 8, 0.5f), new(2.5f, 8, 0.5f), spec);
        age = 0;
        Check(!ParticleCollisionMath.Respond(spec, hit, ref position, ref velocity, ref age, 10), "Roof death never emits Collision.");
        Near(10, age);
    }

    [LumenFact]
    public static void LifecycleDrainBeforeFirstEmissionIgnoresNodeOrder()
    {
        static (bool Alive, int Particles, int Emitters, bool CpuWork) Drain(bool childFirst)
        {
            var source = new EffectNodeSpec { Name = "source", Emitter = Parent(new(LumenSubEmitterTrigger.Death, childFirst ? 0 : 1, 1)) };
            var child = new EffectNodeSpec { Name = "child", Emitter = new EmitterSpec
                { Name = "child", PreferredTier = LumenParticleTier.L1_NativeCpu, RateOverTime = 0 } };
            var asset = new EffectAsset { EffectId = $"drain.before-birth.{childFirst}", Nodes = childFirst ? [child, source] : [source, child] };
            var service = VfxTestData.Service(asset);
            using var handle = service.Play(new LumenPlayRequest { EffectId = asset.EffectId, MapIndex = 7 });
            Check(handle.IsAlive, "Both node orders must be valid.");
            handle.Stop(true);
            service.CaptureMainThread(Frame(1f / 60)); // No CPU capture: the contributor has no work.
            var cpu = service.Simulator;
            return (handle.IsAlive, cpu.LiveParticleCount, cpu.ActiveEmitterCount, cpu.HasActiveWork);
        }
        var sourceFirst = Drain(false);
        var childFirst = Drain(true);
        Check(sourceFirst == (false, 0, 0, false) && childFirst == sourceFirst,
            $"Expected (alive, particles, emitters, cpuWork) = (False, 0, 0, False); source-first={sourceFirst}, target-first={childFirst}.");
    }

    [LumenFact]
    public static void LifecycleThreeNodeDeathChainDrainsInEitherOrderOnBothTiers()
    {
        static int Drain(bool reversed)
        {
            static EffectNodeSpec Node(string name, int target) => new() { Name = name, Emitter = new EmitterSpec {
                Name = name, MaxParticles = 1, StartLifetimeMin = 0.25f, StartLifetimeMax = 0.25f,
                Bursts = name == "A" ? [new(0, 1)] : [], SubEmitters = target < 0 ? [] : [new(LumenSubEmitterTrigger.Death, target, 1)] } };
            var asset = new EffectAsset { EffectId = $"drain.chain.{reversed}", Nodes = reversed
                ? [Node("C", -1), Node("B", 0), Node("A", 1)] : [Node("A", 1), Node("B", 2), Node("C", -1)] };
            var service = VfxTestData.Service(asset);
            var request = new LumenPlayRequest { EffectId = asset.EffectId, MapIndex = 7 };
            using var l1 = service.Play(request);
            using var gpu = new LumenGpuParticleEngine(3);
            service.GpuEngineProvider = () => gpu;
            using var l2 = service.Play(request);
            Check(l1.IsAlive && l2.IsAlive && service.Simulator.ActiveEmitterCount == 3 && gpu.HasActiveWork, "Both chains must bind.");
            l1.Stop(true);
            l2.Stop(true);
            Check(!l1.IsAlive && service.Simulator.ActiveEmitterCount == 0 && !service.Simulator.HasActiveWork,
                "All three empty L1 emitters must be reclaimed without a simulation capture.");
            int steps = 0;
            while (gpu.HasActiveWork && steps < 5)
            {
                var frame = Frame(0.25f, (ulong)++steps);
                service.CaptureMainThread(frame);
                gpu.CaptureMainThread(frame);
                Check(gpu.CapturedFrame!.Emitters.All(e => e.Parameters.Slots.Z == 0f), "Stopping before capture admits no births.");
            }
            service.CaptureMainThread(Frame(0f, 6));
            Check(!gpu.HasActiveWork && !l2.IsAlive, "Each L2 source gets a final integration and its child a final lifetime; the chain must end.");
            return steps;
        }
        int forward = Drain(false), reverse = Drain(true);
        Console.WriteLine($"[DRAIN] A→B→C, forward/reverse: L1=0/0 remaining; L2={forward}/{reverse} captures to idle.");
        Check(forward == reverse, "L2 drain completion must not depend on the node array order.");
    }

    [LumenFact]
    public static void DeathEventsBindPerPlayInheritEndpointAppearanceAndDrain()
    {
        EmitterSpec parent = Parent(new(LumenSubEmitterTrigger.Death, 1, 2, Inherit: LumenSubEmitterInherit.Color | LumenSubEmitterInherit.Size));
        var child = new EmitterSpec { Name = "child", PreferredTier = LumenParticleTier.L1_NativeCpu,
            RateOverTime = 0, StartSpeedMin = 0, StartSpeedMax = 0, StartLifetimeMin = 1, StartLifetimeMax = 1,
            StartColor = new(0.5f, 1, 1, 0.5f), StartSizeMin = 0.5f, StartSizeMax = 0.5f, WorldSpaceSimulation = false };
        var asset = new EffectAsset { EffectId = "world.sub.play", DefaultDuration = 20,
            Nodes = new[] { new EffectNodeSpec { Name = "parent", Emitter = parent }, new EffectNodeSpec { Name = "child", Emitter = child } } };
        LumenVfxService service = VfxTestData.Service(asset);
        using ILumenEffectHandle first = service.Play(new LumenPlayRequest { EffectId = asset.EffectId, MapIndex = 7, OriginWorld = new(10, 0, 0) });
        using ILumenEffectHandle second = service.Play(new LumenPlayRequest { EffectId = asset.EffectId, MapIndex = 7, OriginWorld = new(20, 0, 0) });
        service.Simulator.CaptureMainThread(Frame(0.01f));
        first.Stop(allowFadeOut: true);
        second.Stop(allowFadeOut: true);
        service.Simulator.CaptureMainThread(Frame(0.2f, 2));
        CpuParticle[] children = service.Simulator.LiveParticles.ToArray();
        Check(children.Length == 4 && service.Simulator.GetLiveParticleCount(1) == 2 && service.Simulator.GetLiveParticleCount(3) == 2,
            "Two Plays must bind distinct targets and keep them alive through source drain.");
        Close(Vector3.Zero, children[0].Position); // world event converted to each child's local origin
        Near(1.5f, children[0].Size); // parent size 2 * endpoint 1.5, child .5
        Check(children[0].Color == new Vector4(1, 1.5f, 1, 0.125f), "Child birth colour multiplies the parent's endpoint RGBA.");
        service.Simulator.CaptureMainThread(Frame(1.1f, 3));
        Check(service.Simulator.ActiveEmitterCount == 0, "The whole dependency DAG drains after the children die.");
    }

    [LumenFact]
    public static void FloorDeathInheritsCurrentAppearanceBeforeForcedExpiry()
    {
        var cpu = new CpuParticleSimulator();
        var sourceSpec = new EmitterSpec
        {
            Name = "falling", RateOverTime = 0, KillBelowFloor = true, StartSpeedMin = 0, StartSpeedMax = 0,
            StartLifetimeMin = 1, StartLifetimeMax = 1, Bursts = new[] { new ParticleBurstSpec(0, 1) },
            ForceFields = new[] { new ForceFieldSpec(ForceFieldKind.GravityDrag, -Vector3.UnitY, Vector3.Zero, 0, 10) },
            SizeOverLifetime = new ScalarCurve(new[] { new CurveKey(0, 1, 1, 1), new CurveKey(1, 2, 1, 1) }),
            ColorOverLifetime = new ColorGradient(new[] { new ColorStop(0, Vector4.One), new ColorStop(1, Vector4.Zero) }),
            SubEmitters = new[] { new SubEmitterSpec(LumenSubEmitterTrigger.Death, 1, 1,
                Inherit: LumenSubEmitterInherit.Color | LumenSubEmitterInherit.Size) }
        };
        int source = cpu.AcquireEmitter(sourceSpec, new(Matrix4x4.CreateTranslation(0, 0.1f, 0), Quaternion.Identity), 1u);
        int target = cpu.AcquireEmitter(new EmitterSpec { Name = "child", RateOverTime = 0 }, Transform, 2u);
        cpu.BindNodeInstances(new[] { source, target }, null, Array.Empty<int>());
        cpu.CaptureMainThread(Frame(0.01f));
        cpu.CaptureMainThread(Frame(0.2f, 2));
        Check(cpu.GetLiveParticleCount(source) == 0 && cpu.GetLiveParticleCount(target) == 1, "Floor death must create exactly one child.");
        CpuParticle child = cpu.LiveParticles[0];
        Near(1.2f, child.Size);
        Near(0.8f, child.Color.X);
        Near(0.8f, child.Color.W);
        Close(new(0, -0.3f, 0), child.Position);
    }

    [LumenFact]
    public static void EventAndExplicitBurstsShareCpuCarryAndOverflowDoesNotReplay()
    {
        float density = RimLumenRenderHost.ParticleDensityScale;
        try
        {
            RimLumenRenderHost.ParticleDensityScale = 1f;
            var cpu = new CpuParticleSimulator();
            int source = cpu.AcquireEmitter(Parent(new(LumenSubEmitterTrigger.Death, 1, 3)), Transform, 1u);
            int target = cpu.AcquireEmitter(new EmitterSpec { Name = "target", RateOverTime = 0, MaxParticles = 2,
                StartLifetimeMin = 1, StartLifetimeMax = 1 }, Transform, 2u);
            cpu.BindNodeInstances(new[] { source, target }, null, Array.Empty<int>());
            cpu.CaptureMainThread(Frame(0.01f));
            RimLumenRenderHost.ParticleDensityScale = 0.25f;
            cpu.EmitBurst(target, 1); // ceil(.25) = 1, carry=-.75
            cpu.CaptureMainThread(Frame(0.2f, 2)); // death count3 consumes carry and requests zero
            Check(cpu.GetLiveParticleCount(target) == 1, "Event burst must consume the same carry as explicit burst.");
            cpu.EmitBurst(target, 12); // requests 3, only 1 fits
            cpu.CaptureMainThread(Frame(0, 3));
            Check(cpu.GetLiveParticleCount(target) == 2, "Capacity drops excess requests.");
            cpu.CaptureMainThread(Frame(2, 4));
            Check(cpu.GetLiveParticleCount(target) == 0, "Dropped child requests are never replayed.");
        }
        finally { RimLumenRenderHost.ParticleDensityScale = density; }
    }

    [LumenFact]
    public static void RejectedSubEmitterProbabilityDoesNotSpawn()
    {
        var cpu = new CpuParticleSimulator();
        int source = cpu.AcquireEmitter(Parent(new(LumenSubEmitterTrigger.Death, 1, 4, Probability: 0)), Transform, 1u);
        int target = cpu.AcquireEmitter(new EmitterSpec { Name = "target", RateOverTime = 0 }, Transform, 2u);
        cpu.BindNodeInstances(new[] { source, target }, null, Array.Empty<int>());
        cpu.CaptureMainThread(Frame(0.01f));
        cpu.CaptureMainThread(Frame(0.2f, 2));
        Check(cpu.GetLiveParticleCount(target) == 0, "Probability zero rejects the whole event.");
    }

    [LumenFact]
    public static void GpuCaptureBindsPerPlayAndKeepsSourcesThroughTheirLastDeathStep()
    {
        using var gpu = new LumenGpuParticleEngine(32);
        var child = new EmitterSpec { Name = "child", RateOverTime = 0, MaxParticles = 4, StartLifetimeMin = 1, StartLifetimeMax = 1 };
        int a = gpu.CreateInstance(Parent(new(LumenSubEmitterTrigger.Death, 1, 2)), Transform, -1, false, 1u);
        int b = gpu.CreateInstance(child, Transform, -1, false, 2u);
        gpu.BindNodeInstances(new[] { a, b });
        int c = gpu.CreateInstance(Parent(new(LumenSubEmitterTrigger.Death, 1, 2)), Transform, -1, false, 3u);
        int d = gpu.CreateInstance(child, Transform, -1, false, 4u);
        gpu.BindNodeInstances(new[] { c, d });
        gpu.EmitBurst(b, 1);
        gpu.RestartInstance(b);
        gpu.EmitBurst(b, 1);
        gpu.CaptureMainThread(Frame(0.01f));
        ParticleFrame captured = gpu.CapturedFrame!;
        Check(captured.SubLinks.Select(l => l.Source).SequenceEqual(new uint[] { 0, 2 }), "Each Play binds its own source rows.");
        Check(captured.ExplicitBursts.Select(x => x.ResetVersion).SequenceEqual(new uint[] { 0, 1 }), "Pending bursts keep enqueue order across restart.");
        Check(captured.Emitters[1].Parameters.Events.Z == 1f && captured.Emitters[0].Parameters.Events.Z == 0f,
            "Only targets move burst carry to GPU.");
        gpu.DestroyInstance(a); gpu.DestroyInstance(b); gpu.DestroyInstance(c); gpu.DestroyInstance(d);
        gpu.CaptureMainThread(Frame(0.2f, 2));
        Check(gpu.CapturedFrame!.Emitters.Length == 4, "Source last death step must run before its range is cleared.");
        gpu.CaptureMainThread(Frame(0.01f, 3));
        Check(gpu.CapturedFrame!.Emitters.Length == 2, "Sources may release after the final step; targets retain their last lifetime.");
        gpu.CaptureMainThread(Frame(1.1f, 4));
        Check(!gpu.HasActiveWork, "GPU targets finish draining without readback.");
    }

    // Formula pinning follows ParticleShaderTests. GPU arithmetic is checked against the fixed-point worked
    // sequence below, not represented as GPU execution. Slang compiles the actual function in the existing suite.
    [LumenFact]
    public static void GpuBurstIntegerFormulaAndCpuSchedulerKeepTheSamePrefixBudget()
    {
        var scheduler = new EmissionScheduler(new EmitterSpec { Name = "carry", RateOverTime = 0 }, 1u);
        int[] actual = { scheduler.AcceptBurst(1, 0.25f), scheduler.AcceptBurst(3, 0.25f),
            scheduler.AcceptBurst(5, 0.25f), scheduler.AcceptBurst(3, 0.25f), scheduler.AcceptBurst(100000, 0.5f) };
        Check(actual.SequenceEqual(new[] { 1, 0, 2, 0, 50000 }), "CPU Q=65536 prefix budgets differ.");
        string shader = Shader("ParticleBurstBudget.glsl");
        string[] limbs = { "(count & 0xffffu) * q", "(count >> 16u) * q",
            "(lowProduct >> 16u) + (highProduct & 0xffffu)", "(highProduct >> 16u) + (whole >> 16u)",
            "(whole << 16u) | (lowProduct & 0xffffu)", "if (high == 0u && low <= debt) { debt -= low; return 0u; }",
            "if (low < debt) high--;", "low -= debt;", "(high << 16u) | (low >> 16u)",
            "if (fraction != 0u) requested++;", "debt = (0u - fraction) & 0xffffu;" };
        Check(limbs.All(shader.Contains), "GPU limb product, ceiling or negative carry differs from the pinned integer rule.");
        string sim = Shader("ParticleSim.comp.glsl");
        Check(sim.Contains("acceptBurst(link.Count, emitter.DensityQ, debt)", StringComparison.Ordinal)
            && sim.Contains("acceptBurst(burst.Count, burst.DensityQ, debt)", StringComparison.Ordinal), "All GPU target bursts must use one carry.");
        Console.WriteLine("[BURST] CPU prefix requests = " + string.Join(",", actual) + "; GPU integer formula pinned (not GPU execution).");
    }

    [LumenFact]
    public static void WorldPhysicsShadersPinTheProductionCpuEquations()
    {
        string sim = Shader("ParticleSim.comp.glsl"), collision = Shader("ParticleCollision.glsl");
        Check(sim.Contains("abs(length(delta) - waveRadius) / halfThickness", StringComparison.Ordinal)
            && sim.Contains("waveRadius > endRadius", StringComparison.Ordinal)
            && sim.Contains("parameters.x * emitter.WorldPhysics.x", StringComparison.Ordinal)
            && sim.Contains("delta.y = 0.0", StringComparison.Ordinal)
            && sim.Contains("emitter.WorldInverse * vec4(force, 0.0)", StringComparison.Ordinal), "Force space, clock or distance formula changed.");
        Check(sim.Contains("(velocity - normal * ((1.0 + emitter.Collision.y) * approach)) * (1.0 - emitter.Collision.z)", StringComparison.Ordinal)
            && sim.Contains("particle.PositionAndAge.w += particle.VelocityAndLifetime.w * emitter.Collision.w", StringComparison.Ordinal)
            && sim.Contains("if (collision) appendEvent(emitter, particle, 1u)", StringComparison.Ordinal)
            && sim.Contains("if (particle.ActiveEmitter == 0u) appendEvent(emitter, particle, 0u)", StringComparison.Ordinal), "Collision/Death distinction or upstream response changed.");
        Check(collision.Contains("slab(from.y, delta.y, LUMEN_GROUND_Y, 2.0", StringComparison.Ordinal)
            && collision.Contains("position += normal * 0.0001", StringComparison.Ordinal), "Wall height or contact offset changed.");
    }

    private static EmitterSpec Parent(SubEmitterSpec sub) => new()
    {
        Name = "parent", PreferredTier = LumenParticleTier.L1_NativeCpu, MaxParticles = 4, RateOverTime = 0,
        StartLifetimeMin = 0.1f, StartLifetimeMax = 0.1f, StartSpeedMin = 0, StartSpeedMax = 0,
        StartSizeMin = 2, StartSizeMax = 2, StartColor = new(4, 3, 2, 0.5f),
        SizeOverLifetime = ScalarCurve.Constant(1.5f), ColorOverLifetime = ColorGradient.Constant(new Vector4(0.5f)),
        Bursts = new[] { new ParticleBurstSpec(0, 1) }, SubEmitters = new[] { sub }
    };
    private static LumenEmitterTransform Transform => new(Matrix4x4.Identity, Quaternion.Identity);
    private static LumenCaptureContext Frame(float dt, ulong frame = 1) => new()
    {
        FrameIndex = frame, DeltaTime = dt, ViewportWidth = 640, ViewportHeight = 480,
        ViewMatrix = Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity, LumenRenderPassId = 0
    };
    private static string Shader(string name) => File.ReadAllText(Path.Combine(Repo(), "1.7", "Shaders", "Particles", name));
    private static string Repo()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Source"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("RimLumen source root not found.");
    }
    private static void Near(float expected, float actual) => Check(float.IsFinite(actual) && MathF.Abs(expected - actual) < 0.0001f, $"Expected {expected}, actual {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static void Close(Vector3 expected, Vector3 actual)
    {
        if (!float.IsFinite(actual.LengthSquared()) || Vector3.Distance(expected, actual) > 0.0001f)
            throw new InvalidOperationException($"Expected {expected}, actual {actual}.");
    }
}
