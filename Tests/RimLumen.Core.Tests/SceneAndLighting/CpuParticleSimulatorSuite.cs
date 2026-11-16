using System;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class CpuParticleSimulatorSuite
{
    [LumenFact]
    public static void IdleActiveAndImmediateRelease()
    {
        var simulator = new CpuParticleSimulator();
        Check(simulator.Name == "L1_NativeCpu", "Contributor name");
        Check(!simulator.HasActiveWork, "New simulator is idle");
        int id = simulator.AcquireEmitter(new EmitterSpec { Name = "idle" }, Matrix4x4.Identity, false);
        Check(!simulator.HasActiveWork, "Non-emitting acquisition is idle");
        Check(simulator.ActiveEmitterCount == 1, "Acquired emitter count");
        simulator.SetEmitterParams(id, true, 1f, 1f);
        Check(simulator.HasActiveWork, "Emission makes active");
        simulator.EmitBurst(id, 2);
        simulator.ReleaseEmitter(id, true);
        Check(!simulator.HasActiveWork && simulator.LiveParticleCount == 0 && simulator.ActiveEmitterCount == 0,
            "Immediate release removes work");
        simulator.ReleaseEmitter(id, true);
        simulator.SetEmitterTransform(id, Matrix4x4.Identity);
        simulator.SetEmitterParams(id, true, 1f, 1f);
        simulator.EmitBurst(id, 2);
        Check(simulator.GetLiveParticleCount(id) == 0, "Unknown ids are ignored");
    }

    [LumenFact]
    public static void RateAccumulatorCapAndFreshParticleOrdering()
    {
        var simulator = new CpuParticleSimulator();
        int id = simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "rate", RateOverTime = 3f, MaxParticles = 5,
            StartLifetimeMin = 20f, StartLifetimeMax = 20f, StartSpeedMin = 2f, StartSpeedMax = 2f,
        }, Matrix4x4.Identity, rateMultiplier: 2f);
        Step(simulator, 0.25f);
        Check(simulator.LiveParticleCount == 1, "First frame emits one");
        Close(0f, simulator.LiveParticles[0].Age);
        Vector(Vector3.Zero, simulator.LiveParticles[0].Position);
        Step(simulator, 0.25f);
        Check(simulator.GetLiveParticleCount(id) == 3, "Remainder emits two next frame");
        Close(0.25f, simulator.LiveParticles[0].Age);
        Close(0.5f, simulator.LiveParticles[0].Position.Z);
        Close(0f, simulator.LiveParticles[1].Age);
        Step(simulator, 0.25f);
        Check(simulator.LiveParticleCount == 4, "Third frame emits one");
        Step(simulator, 0.25f);
        Check(simulator.LiveParticleCount == 5, "Fourth frame capped at five");
        simulator.EmitBurst(id, 100);
        Check(simulator.LiveParticleCount == 5, "Explicit burst observes cap");
        simulator.ReleaseEmitter(id, true);
        int second = simulator.AcquireEmitter(new EmitterSpec { Name = "reuse", MaxParticles = 8 }, Matrix4x4.Identity);
        simulator.EmitBurst(second, 8);
        Step(simulator, 0f);
        Check(simulator.Vertices.Length == 32, "Capacity grows safely after release");
    }

    [LumenFact]
    public static void BurstsFireOnlyOnCrossingAndRepeatAfterWrap()
    {
        var simulator = new CpuParticleSimulator();
        simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "bursts", Looping = true, Duration = 1f,
            StartLifetimeMin = 20f, StartLifetimeMax = 20f, Bursts = new[] { (0.5f, 2) },
        }, Matrix4x4.Identity);
        Step(simulator, 0.25f);
        Check(simulator.LiveParticleCount == 0, "No early burst");
        Step(simulator, 0.25f);
        Check(simulator.LiveParticleCount == 2, "Burst at half second");
        Step(simulator, 0.5f);
        Check(simulator.LiveParticleCount == 2, "Wrap alone does not refire half-second burst");
        Step(simulator, 0.5f);
        Check(simulator.LiveParticleCount == 4, "Next loop refires");
        Step(simulator, 0f);
        Check(simulator.LiveParticleCount == 4, "Zero dt does not repeat a crossing");
        Step(simulator, 2f);
        Check(simulator.LiveParticleCount == 8, "Two-loop step fires twice");
    }

    [LumenFact]
    public static void ZeroAndDurationTimeBurstsFireAtStartAndLoopBoundary()
    {
        var simulator = new CpuParticleSimulator();
        simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "start", Looping = true, Duration = 1f,
            StartLifetimeMin = 10f, StartLifetimeMax = 10f, Bursts = new[] { (0f, 1), (1f, 2) },
        }, Matrix4x4.Identity);
        Step(simulator, 0f);
        Check(simulator.LiveParticleCount == 0, "No time crossing at zero dt");
        Step(simulator, 0.5f);
        Check(simulator.LiveParticleCount == 1, "Start burst fires once");
        Step(simulator, 0.5f);
        Check(simulator.LiveParticleCount == 4, "Loop boundary fires duration burst and refires start burst");
        Step(simulator, 0.25f);
        Check(simulator.LiveParticleCount == 4, "No double firing after wrap");
    }

    [LumenFact]
    public static void NonLoopingDurationStopsRateAndDrainsParticles()
    {
        var spec = new EmitterSpec
        {
            Name = "one-shot", Looping = false, Duration = 0.5f, RateOverTime = 4f,
            StartLifetimeMin = 3f, StartLifetimeMax = 3f,
        };
        var simulator = new CpuParticleSimulator();
        simulator.AcquireEmitter(spec, Matrix4x4.Identity);
        Step(simulator, 0.5f);
        Check(simulator.LiveParticleCount == 2, "Half-second emits exactly two particles");
        Step(simulator, 0.5f);
        Check(simulator.LiveParticleCount == 2, "One-shot still has only two particles at one second");
        Step(simulator, 1f);
        Check(simulator.LiveParticleCount == 2 && simulator.HasActiveWork, "No more spawns at two seconds; live particles keep work active");
        Step(simulator, 1.5f);
        Check(simulator.LiveParticleCount == 0 && !simulator.HasActiveWork, "Particles expire at age three and one-shot drains");

        var crossing = new CpuParticleSimulator();
        crossing.AcquireEmitter(spec, Matrix4x4.Identity);
        Step(crossing, 1f);
        Check(crossing.LiveParticleCount == 2, "A step crossing Duration only accumulates half a second of emission");
    }

    [LumenFact]
    public static void LoopingDurationKeepsEmittingAcrossThreeCycles()
    {
        var simulator = new CpuParticleSimulator();
        simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "loop-rate", Looping = true, Duration = 0.5f, RateOverTime = 4f,
            StartLifetimeMin = 3f, StartLifetimeMax = 3f,
        }, Matrix4x4.Identity);
        Step(simulator, 0.5f);
        Check(simulator.LiveParticleCount == 2, "First cycle emits two");
        Step(simulator, 0.5f);
        Check(simulator.LiveParticleCount == 4, "Second cycle emits two more");
        Step(simulator, 0.5f);
        Check(simulator.LiveParticleCount == 6 && simulator.HasActiveWork, "Third cycle emits two more and remains active");
    }

    [LumenFact]
    public static void NonLoopingBurstBeyondDurationNeverFires()
    {
        var simulator = new CpuParticleSimulator();
        simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "late-burst", Looping = false, Duration = 0.5f,
            StartLifetimeMin = 3f, StartLifetimeMax = 3f, Bursts = new[] { (0.75f, 2) },
        }, Matrix4x4.Identity);
        Step(simulator, 1f);
        Check(simulator.LiveParticleCount == 0, "Burst at 0.75 is outside the half-second duration");
        Step(simulator, 1f);
        Check(simulator.LiveParticleCount == 0 && !simulator.HasActiveWork, "Expired empty emitter remains idle");
    }

    [LumenFact]
    public static void PausedNonLoopingEmitterGetsFullDurationWindowAndEmissionLocalBurstsWhenActivatedLate()
    {
        var rateSpec = new EmitterSpec
        {
            Name = "late-rate", Looping = false, Duration = 0.5f, RateOverTime = 4f,
            StartLifetimeMin = 3f, StartLifetimeMax = 3f,
        };
        var simulator = new CpuParticleSimulator();
        int id = simulator.AcquireEmitter(rateSpec, Matrix4x4.Identity, emitting: false);
        Step(simulator, 1.5f);
        Check(simulator.ActiveEmitterCount == 1 && simulator.LiveParticleCount == 0 && !simulator.HasActiveWork,
            "Paused non-looping emitter does not drain or die while paused");
        simulator.SetEmitterParams(id, true, 1f, 1f);
        Step(simulator, 0.25f);
        Check(simulator.LiveParticleCount == 1 && simulator.HasActiveWork,
            "Quarter-second after late activation spawns one particle at rate 4");
        Step(simulator, 0.25f);
        Check(simulator.LiveParticleCount == 2,
            "Full half-second activation window spawns two particles total");
        Step(simulator, 0.5f);
        Check(simulator.LiveParticleCount == 2 && simulator.HasActiveWork,
            "Emission stops once 0.5s of activation time elapses; live particles survive");
        Step(simulator, 2.5f);
        Check(simulator.LiveParticleCount == 0 && !simulator.HasActiveWork && simulator.ActiveEmitterCount == 1,
            "Live particles finish their lifetime; the spent emitter idles until released");
        simulator.RestartEmitter(id);
        Step(simulator, 0.5f);
        Check(simulator.LiveParticleCount == 2, "A restarted spent emitter runs a fresh 0.5s window");
        Step(simulator, 0.5f);
        Check(simulator.LiveParticleCount == 2, "The restarted window ends at its Duration");
        simulator.ReleaseEmitter(id);
        Step(simulator, 3f);
        Check(simulator.LiveParticleCount == 0 && simulator.ActiveEmitterCount == 0,
            "A released emitter drains and is removed");

        var burstSimulator = new CpuParticleSimulator();
        int burstId = burstSimulator.AcquireEmitter(new EmitterSpec
        {
            Name = "late-burst-window", Looping = false, Duration = 0.5f,
            StartLifetimeMin = 3f, StartLifetimeMax = 3f,
            Bursts = new[] { (0.25f, 2), (0.75f, 3) },
        }, Matrix4x4.Identity, emitting: false);
        Step(burstSimulator, 0.25f);
        Check(burstSimulator.LiveParticleCount == 0, "Burst does not fire at 0.25s of paused game time");
        Step(burstSimulator, 0.75f);
        Check(burstSimulator.LiveParticleCount == 0 && burstSimulator.ActiveEmitterCount == 1,
            "Paused burst emitter survives past 1s of paused game time");
        burstSimulator.SetEmitterParams(burstId, true, 1f, 1f);
        Step(burstSimulator, 0.1f);
        Check(burstSimulator.LiveParticleCount == 0, "Burst at 0.25s waits for 0.25s of emission time");
        Step(burstSimulator, 0.15f);
        Check(burstSimulator.LiveParticleCount == 2, "Burst at 0.25s fires 0.25s after activation");
        Step(burstSimulator, 1f);
        Check(burstSimulator.LiveParticleCount == 2, "Burst at 0.75s beyond 0.5s duration never fires");

        var guardSimulator = new CpuParticleSimulator();
        guardSimulator.AcquireEmitter(rateSpec, Matrix4x4.Identity, emitting: true);
        Step(guardSimulator, 1f);
        Check(guardSimulator.LiveParticleCount == 2, "Emitter acquired while emitting still spawns 2 across 1s");
    }

    [LumenFact]
    public static void VortexTurnsPositiveXIntoNegativeZ()
    {
        var simulator = new CpuParticleSimulator();
        int id = simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "vortex", StartLifetimeMin = 10f, StartLifetimeMax = 10f,
            ForceFields = new[] { new ForceFieldSpec(ForceFieldKind.Vortex, Vector3.UnitY, Vector3.Zero, 0f, 2f) },
        }, Matrix4x4.CreateTranslation(1f, 0f, 0f), false);
        simulator.EmitBurst(id, 1);
        Step(simulator, 0.5f);
        Vector(new Vector3(1f, 0f, -0.5f), simulator.LiveParticles[0].Position);
        Vector(new Vector3(0f, 0f, -1f), simulator.LiveParticles[0].Velocity);
    }

    [LumenFact]
    public static void AttractorPullsInsideAndIgnoresOutsideRadius()
    {
        var spec = new EmitterSpec
        {
            Name = "attractor", StartLifetimeMin = 10f, StartLifetimeMax = 10f,
            ForceFields = new[] { new ForceFieldSpec(ForceFieldKind.PointAttractor, Vector3.Zero, Vector3.Zero, 2f, 4f) },
        };
        var simulator = new CpuParticleSimulator();
        int inside = simulator.AcquireEmitter(spec, Matrix4x4.CreateTranslation(1f, 0f, 0f), false);
        int outside = simulator.AcquireEmitter(spec, Matrix4x4.CreateTranslation(3f, 0f, 0f), false);
        simulator.EmitBurst(inside, 1);
        simulator.EmitBurst(outside, 1);
        Step(simulator, 0.5f);
        Vector(new Vector3(0.5f, 0f, 0f), simulator.LiveParticles[0].Position);
        Vector(new Vector3(3f, 0f, 0f), simulator.LiveParticles[1].Position);
    }

    [LumenFact]
    public static void GravityDragIsAppliedBeforePositionAndInFieldOrder()
    {
        var simulator = new CpuParticleSimulator();
        int id = simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "gravity", StartLifetimeMin = 10f, StartLifetimeMax = 10f,
            StartSpeedMin = 4f, StartSpeedMax = 4f,
            ForceFields = new[]
            {
                new ForceFieldSpec(ForceFieldKind.GravityDrag, Vector3.Zero, new Vector3(8f, 1f, 0f), 0f, 0f),
                new ForceFieldSpec(ForceFieldKind.GravityDrag, Vector3.Zero, new Vector3(2f, 0f, 0f), 0f, 0f),
            },
        }, Matrix4x4.Identity, false);
        simulator.EmitBurst(id, 1);
        Step(simulator, 0.5f);
        Vector(new Vector3(0f, -3f, 2f), simulator.LiveParticles[0].Velocity);
        Vector(new Vector3(0f, -1.5f, 1f), simulator.LiveParticles[0].Position);
    }

    [LumenFact]
    public static void CanonicalThreeDimensionalGravityDragMatchesGpuFormula()
    {
        var simulator = new CpuParticleSimulator();
        int id = simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "canonical-gravity", StartLifetimeMin = 10f, StartLifetimeMax = 10f,
            StartSpeedMin = 0f, StartSpeedMax = 0f,
            ForceFields = new[]
            {
                new ForceFieldSpec(ForceFieldKind.GravityDrag, new Vector3(4f, -19.62f, -2f), new Vector3(0.25f, 0f, 0f), 0f, 1f),
            },
        }, Matrix4x4.Identity, false);
        simulator.EmitBurst(id, 1);
        float dt = 0.5f;
        Step(simulator, dt);
        Vector3 expectedVel = new Vector3(4f, -19.62f, -2f) * dt * MathF.Exp(-0.25f * dt);
        Vector(expectedVel, simulator.LiveParticles[0].Velocity);
        Vector(expectedVel * dt, simulator.LiveParticles[0].Position);
    }

    [LumenFact]
    public static void LifetimeBoundaryDrainAndImmediateStop()
    {
        var simulator = new CpuParticleSimulator();
        int id = simulator.AcquireEmitter(new EmitterSpec { Name = "drain", RateOverTime = 8f }, Matrix4x4.Identity);
        simulator.EmitBurst(id, 1);
        simulator.ReleaseEmitter(id, false);
        Check(simulator.HasActiveWork && simulator.ActiveEmitterCount == 1, "Drain remains active");
        Step(simulator, 0.5f);
        Check(simulator.LiveParticleCount == 1, "Drain does not emit");
        Step(simulator, 0.5f);
        Check(simulator.LiveParticleCount == 0 && !simulator.HasActiveWork && simulator.ActiveEmitterCount == 0,
            "Age equal to lifetime expires and releases emitter");
        int live = simulator.AcquireEmitter(new EmitterSpec { Name = "immediate" }, Matrix4x4.Identity);
        simulator.EmitBurst(live, 3);
        Step(simulator, 0f);
        simulator.ReleaseEmitter(live, true);
        Check(simulator.Vertices.Length == 0 && simulator.Indices.Length == 0 && !simulator.HasActiveWork,
            "Immediate removal clears existing batch");
    }

    [LumenFact]
    public static void SeedAndInputsDetermineParticleState()
    {
        var spec = new EmitterSpec
        {
            Name = "random", Shape = LumenShapeKind.Sphere, RateOverTime = 8f,
            StartLifetimeMin = 3f, StartLifetimeMax = 6f,
            StartSpeedMin = 1f, StartSpeedMax = 4f, StartSizeMin = 1f, StartSizeMax = 2f,
        };
        var a = new CpuParticleSimulator(42);
        var b = new CpuParticleSimulator(42);
        var c = new CpuParticleSimulator(43);
        a.AcquireEmitter(spec, Matrix4x4.Identity);
        b.AcquireEmitter(spec, Matrix4x4.Identity);
        c.AcquireEmitter(spec, Matrix4x4.Identity);
        for (int frame = 0; frame < 4; frame++)
        {
            Step(a, 0.25f); Step(b, 0.25f); Step(c, 0.25f);
        }
        Check(a.LiveParticleCount == 8 && b.LiveParticleCount == 8, "Deterministic emission count");
        for (int i = 0; i < 8; i++)
        {
            Check(a.LiveParticles[i].Position == b.LiveParticles[i].Position, "Identical seed position");
            Check(a.LiveParticles[i].Velocity == b.LiveParticles[i].Velocity, "Identical seed velocity");
            Check(a.LiveParticles[i].Lifetime == b.LiveParticles[i].Lifetime, "Identical seed lifetime");
            Check(a.LiveParticles[i].StartSize == b.LiveParticles[i].StartSize, "Identical seed size");
        }
        Check(a.LiveParticles[0].Position != c.LiveParticles[0].Position, "Different seed changes position");
    }

    [LumenFact]
    public static void ShapesStayInsideTheirDeclaredVolumes()
    {
        var simulator = new CpuParticleSimulator(123);
        int sphere = simulator.AcquireEmitter(new EmitterSpec { Name = "sphere", Shape = LumenShapeKind.Sphere, ShapeExtents = new Vector3(2f) }, Matrix4x4.Identity, false);
        int disc = simulator.AcquireEmitter(new EmitterSpec { Name = "disc", Shape = LumenShapeKind.CircleXZ, ShapeExtents = new Vector3(3f) }, Matrix4x4.Identity, false);
        int cone = simulator.AcquireEmitter(new EmitterSpec { Name = "cone", Shape = LumenShapeKind.Cone, ConeAngleDegrees = 60f, StartSpeedMin = 1f, StartSpeedMax = 1f }, Matrix4x4.Identity, false);
        int box = simulator.AcquireEmitter(new EmitterSpec { Name = "box", Shape = LumenShapeKind.Box, ShapeExtents = new Vector3(1f, 2f, 3f) }, Matrix4x4.Identity, false);
        simulator.EmitBurst(sphere, 256); simulator.EmitBurst(disc, 256); simulator.EmitBurst(cone, 256); simulator.EmitBurst(box, 256);
        for (int i = 0; i < 256; i++)
        {
            Check(simulator.LiveParticles[i].Position.Length() <= 2f, "Sphere radius");
            var circle = simulator.LiveParticles[i + 256];
            Check(circle.Position.Length() <= 3f && circle.Position.Y == 0f, "XZ disc radius/plane");
            var direction = simulator.LiveParticles[i + 512].Velocity;
            Close(1f, direction.Length());
            Check(direction.Y >= 0.5f, "Cone axis is +Y and half-angle <=60 degrees");
            var position = simulator.LiveParticles[i + 768].Position;
            Check(MathF.Abs(position.X) <= 1f && MathF.Abs(position.Y) <= 2f && MathF.Abs(position.Z) <= 3f, "Box bounds");
        }
    }

    [LumenFact]
    public static void CameraAndTopDownBillboardsUseCaptureViewBasis()
    {
        var simulator = new CpuParticleSimulator();
        int id = simulator.AcquireEmitter(new EmitterSpec { Name = "quad", StartSizeMin = 2f, StartSizeMax = 2f }, Matrix4x4.Identity, false);
        simulator.EmitBurst(id, 1);
        Step(simulator, 0f);
        Vector(new Vector3(-1f, -1f, 0f), simulator.Vertices[0].Position);
        Vector(new Vector3(1f, -1f, 0f), simulator.Vertices[1].Position);
        Vector(new Vector3(1f, 1f, 0f), simulator.Vertices[2].Position);
        Vector(new Vector3(-1f, 1f, 0f), simulator.Vertices[3].Position);
        Step(simulator, 0f, Matrix4x4.CreateRotationX(MathF.PI / 2f));
        Vector(new Vector3(-1f, 0f, -1f), simulator.Vertices[0].Position);
        Vector(new Vector3(1f, 0f, 1f), simulator.Vertices[2].Position);
    }

    [LumenFact]
    public static void HorizontalStretchedAndMeshBillboards()
    {
        var simulator = new CpuParticleSimulator();
        int horizontal = simulator.AcquireEmitter(new EmitterSpec { Name = "horizontal", BillboardMode = LumenBillboardMode.Horizontal, StartSizeMin = 2f, StartSizeMax = 2f }, Matrix4x4.CreateTranslation(0f, 5f, 0f), false);
        int stretched = simulator.AcquireEmitter(new EmitterSpec { Name = "stretched", BillboardMode = LumenBillboardMode.StretchedVelocity, StartSizeMin = 2f, StartSizeMax = 2f, StartSpeedMin = 2f, StartSpeedMax = 2f, VelocityStretchFactor = 2f }, Matrix4x4.CreateRotationX(-MathF.PI / 2f), false);
        int mesh = simulator.AcquireEmitter(new EmitterSpec { Name = "mesh", BillboardMode = LumenBillboardMode.Mesh, StartSizeMin = 2f, StartSizeMax = 2f }, Matrix4x4.Identity, false);
        simulator.EmitBurst(horizontal, 1); simulator.EmitBurst(stretched, 1); simulator.EmitBurst(mesh, 1);
        Step(simulator, 0f);
        Vector(new Vector3(-1f, 5f, 1f), simulator.Vertices[0].Position);
        Vector(new Vector3(1f, 5f, 1f), simulator.Vertices[1].Position);
        Vector(new Vector3(1f, 5f, -1f), simulator.Vertices[2].Position);
        Vector(new Vector3(-1f, 5f, -1f), simulator.Vertices[3].Position);
        Vector(new Vector3(0f, 4f, 0f), Vector3.Cross(
            simulator.Vertices[1].Position - simulator.Vertices[0].Position,
            simulator.Vertices[2].Position - simulator.Vertices[0].Position));
        Vector(new Vector3(-1f, -5f, 0f), simulator.Vertices[4].Position);
        Vector(new Vector3(1f, 5f, 0f), simulator.Vertices[6].Position);
        Vector(new Vector3(-1f, -1f, 0f), simulator.Vertices[8].Position);
        Step(simulator, 0f, Matrix4x4.CreateRotationY(-MathF.PI / 4f));
        Vector(new Vector3(-MathF.Sqrt(2f), 5f, 0f), simulator.Vertices[0].Position);
        Vector(new Vector3(0f, 5f, MathF.Sqrt(2f)), simulator.Vertices[1].Position);
        Vector(new Vector3(MathF.Sqrt(2f), 5f, 0f), simulator.Vertices[2].Position);
        Vector(new Vector3(0f, 5f, -MathF.Sqrt(2f)), simulator.Vertices[3].Position);
        Vector(new Vector3(0f, 4f, 0f), Vector3.Cross(
            simulator.Vertices[2].Position - simulator.Vertices[0].Position,
            simulator.Vertices[3].Position - simulator.Vertices[0].Position));
        Step(simulator, 0f, Matrix4x4.CreateRotationZ(MathF.PI / 2f));
        Vector(new Vector3(-1f, 5f, 1f), simulator.Vertices[0].Position);
    }

    [LumenFact]
    public static void LocalParticlesFollowTransformButWorldParticlesDoNot()
    {
        var simulator = new CpuParticleSimulator();
        int local = simulator.AcquireEmitter(new EmitterSpec { Name = "local", WorldSpaceSimulation = false, StartSpeedMin = 2f, StartSpeedMax = 2f }, Matrix4x4.CreateTranslation(10f, 0f, 0f), false);
        int world = simulator.AcquireEmitter(new EmitterSpec { Name = "world", StartSpeedMin = 2f, StartSpeedMax = 2f }, Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(10f, 0f, 0f), false);
        simulator.EmitBurst(local, 1); simulator.EmitBurst(world, 1);
        simulator.SetEmitterTransform(local, Matrix4x4.CreateTranslation(20f, 0f, 0f));
        simulator.SetEmitterTransform(world, Matrix4x4.CreateTranslation(20f, 0f, 0f));
        Step(simulator, 0.25f);
        Vector(new Vector3(0f, 0f, 0.5f), simulator.LiveParticles[0].Position);
        Vector(new Vector3(10.5f, 0f, 0f), simulator.LiveParticles[1].Position);
        Vector(new Vector3(19.5f, -0.5f, 0.5f), simulator.Vertices[0].Position);
        Vector(new Vector3(10f, -0.5f, 0f), simulator.Vertices[4].Position);
    }

    [LumenFact]
    public static void CurvesColorIntensityAndFlipbookHaveHandComputedValues()
    {
        var simulator = new CpuParticleSimulator();
        int id = simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "appearance", StartLifetimeMin = 4f, StartLifetimeMax = 4f,
            StartSizeMin = 2f, StartSizeMax = 2f, LengthScale = 3f,
            SizeOverLifetime = new ScalarCurve(new[] { new CurveKey(0f, 1f), new CurveKey(1f, 3f) }),
            StartColorLinear = new Vector4(2f, 4f, 6f, 1f), HdrIntensity = 2f,
            ColorOverLifetime = new ColorGradient(new[] { new ColorStop(0f, Vector4.Zero), new ColorStop(1f, Vector4.One) }),
            FlipbookTilesX = 2, FlipbookTilesY = 2,
        }, Matrix4x4.Identity, false);
        simulator.SetEmitterParams(id, false, 1f, 0.5f);
        simulator.EmitBurst(id, 1);
        Step(simulator, 0f);
        Check(simulator.LiveParticles[0].FlipbookFrame == 0, "Starts at frame zero");
        Uv(new Vector2(0f, 0.5f), simulator.Vertices[0].Uv);
        Step(simulator, 1f);
        Check(simulator.LiveParticles[0].FlipbookFrame == 1, "Quarter lifetime frame one");
        Uv(new Vector2(0.5f, 0.5f), simulator.Vertices[0].Uv);
        Uv(new Vector2(1f, 1f), simulator.Vertices[2].Uv);
        Step(simulator, 1f);
        Check(simulator.LiveParticles[0].FlipbookFrame == 2, "Half lifetime frame two");
        Close(12f, simulator.LiveParticles[0].Size);
        Check(simulator.LiveParticles[0].Color == new Vector4(1f, 2f, 3f, 0.5f), "Linear color * HDR * multiplier");
        Uv(new Vector2(0f, 0f), simulator.Vertices[0].Uv);
        Uv(new Vector2(0.5f, 0.5f), simulator.Vertices[2].Uv);
        Step(simulator, 1f);
        Check(simulator.LiveParticles[0].FlipbookFrame == 3, "Three-quarter lifetime frame three");
        Uv(new Vector2(0.5f, 0f), simulator.Vertices[0].Uv);
        Uv(new Vector2(1f, 0.5f), simulator.Vertices[2].Uv);
    }

    [LumenFact]
    public static void TwoFlipbookCyclesRepeatAndKeepUvsInsideEachTile()
    {
        var simulator = new CpuParticleSimulator();
        int id = simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "two-cycles", StartLifetimeMin = 4f, StartLifetimeMax = 4f,
            FlipbookTilesX = 2, FlipbookTilesY = 2, FlipbookCycles = 2f,
        }, Matrix4x4.Identity, false);
        simulator.EmitBurst(id, 1);
        var samples = new[]
        {
            (Dt: 0.5f, Frame: 1, Min: new Vector2(0.5f, 0.5f), Max: new Vector2(1f, 1f)),
            (Dt: 0.5f, Frame: 2, Min: new Vector2(0f, 0f), Max: new Vector2(0.5f, 0.5f)),
            (Dt: 1f, Frame: 0, Min: new Vector2(0f, 0.5f), Max: new Vector2(0.5f, 1f)),
            (Dt: 1.5f, Frame: 3, Min: new Vector2(0.5f, 0f), Max: new Vector2(1f, 0.5f)),
        };
        foreach (var sample in samples)
        {
            Step(simulator, sample.Dt);
            Check(simulator.LiveParticles[0].FlipbookFrame == sample.Frame,
                $"At age {simulator.LiveParticles[0].Age}, expected frame {sample.Frame}, got {simulator.LiveParticles[0].FlipbookFrame}");
            Uv(sample.Min, simulator.Vertices[0].Uv);
            Uv(sample.Max, simulator.Vertices[2].Uv);
            foreach (var vertex in simulator.Vertices)
                Check(vertex.Uv.X >= sample.Min.X && vertex.Uv.X <= sample.Max.X
                    && vertex.Uv.Y >= sample.Min.Y && vertex.Uv.Y <= sample.Max.Y, "UV stays inside the expected tile");
        }
    }

    [LumenFact]
    public static void BatchSinkReceivesQuadIndicesAndGpuCallbacksAreNoOps()
    {
        var simulator = new CpuParticleSimulator();
        int id = simulator.AcquireEmitter(new EmitterSpec { Name = "batch" }, Matrix4x4.Identity, false);
        simulator.EmitBurst(id, 2);
        Step(simulator, 0f);
        simulator.RecordHdrDraws(default);
        var sink = new BatchSink();
        simulator.BatchSink = sink;
        simulator.EnqueuePreCompute(default);
        simulator.RecordBufferBarriers(default);
        simulator.RecordHdrDraws(default);
        Check(sink.Calls == 1 && sink.Particles == 2 && sink.Vertices == 8 && sink.Indices == 12, "Batch submission sizes");
        ReadOnlySpan<uint> expected = stackalloc uint[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
        Check(simulator.Indices.SequenceEqual(expected), "Quad winding and offsets");
    }

    [LumenFact]
    public static void CurlIsPositionDeterministicAndSteadyStateFramesAllocateNothing()
    {
        var simulator = new CpuParticleSimulator();
        var spec = new EmitterSpec
        {
            Name = "curl", RateOverTime = 4f, Looping = true, Duration = 1f, MaxParticles = 32,
            ForceFields = new[] { new ForceFieldSpec(ForceFieldKind.CurlTurbulence, Vector3.Zero, Vector3.Zero, 0f, 2f, 0.25f) },
        };
        int id = simulator.AcquireEmitter(spec, Matrix4x4.Identity, false);
        simulator.EmitBurst(id, 1);
        Step(simulator, 0.25f);
        Vector3 firstVelocity = simulator.LiveParticles[0].Velocity;
        Check(firstVelocity.LengthSquared() > 0f && firstVelocity.Y == 0f, "Curl produces transverse acceleration");
        simulator.ReleaseEmitter(id, true);
        int later = simulator.AcquireEmitter(spec, Matrix4x4.Identity, false);
        simulator.EmitBurst(later, 1);
        Step(simulator, 0.25f);
        Vector(firstVelocity, simulator.LiveParticles[0].Velocity);
        simulator.SetEmitterParams(later, true, 1f, 1f);
        simulator.BatchSink = new BatchSink();
        for (int i = 0; i < 20; i++) { Step(simulator, 0.25f); simulator.RecordHdrDraws(default); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            Step(simulator, 0.25f);
            simulator.RecordHdrDraws(default);
            simulator.EnqueuePreCompute(default);
            simulator.RecordBufferBarriers(default);
            _ = simulator.HasActiveWork;
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(bytes == 0, $"Steady-state allocated {bytes} bytes");
    }

    [LumenFact]
    public static void HdrIntensityPreservesAlpha()
    {
        var sim = new CpuParticleSimulator();
        int id = sim.AcquireEmitter(new EmitterSpec
        {
            Name = "hdr-alpha", StartColorLinear = new Vector4(1f, 2f, 3f, 0.4f), HdrIntensity = 4f,
        }, Matrix4x4.Identity, false);
        sim.SetEmitterParams(id, false, 1f, 2f);
        sim.EmitBurst(id, 1);
        Step(sim, 0f);
        Check(sim.Vertices[0].Color == new Vector4(8f, 16f, 24f, 0.4f), "HDR scales RGB only, never coverage alpha");
    }

    [LumenFact]
    public static void EmittersOnAnotherMapProduceNoVerticesOrSubBatches()
    {
        var sim = new CpuParticleSimulator();
        var spec = new EmitterSpec { Name = "per-map", StartLifetimeMin = 10f, StartLifetimeMax = 10f };
        int mapA = sim.AcquireEmitter(spec, Matrix4x4.Identity, false, mapIndex: 7);
        int mapB = sim.AcquireEmitter(spec, Matrix4x4.Identity, false, mapIndex: 9);
        sim.EmitBurst(mapA, 2);
        sim.EmitBurst(mapB, 3);
        Step(sim, 0f, mapIndex: 7);
        Check(sim.LiveParticleCount == 5 && sim.Vertices.Length == 8, "Only map 7 reaches the vertex batch");
        Check(sim.SubBatches.Count == 1 && sim.SubBatches[0].IndexCount == 12, "One 2-particle sub-batch");
        Step(sim, 0f, mapIndex: 9);
        Check(sim.LiveParticleCount == 5 && sim.Vertices.Length == 12, "Only map 9 reaches the vertex batch");
        Step(sim, 0f, mapIndex: -1);
        Check(sim.Vertices.Length == 20, "An unscoped frame draws every emitter");
    }

    [LumenFact]
    public static void SquashedIsoRimCameraPreservesBillboardAndStretchedScreenRatio()
    {
        // IsoRim Waylith injects Scale(1, 0.003, 1) into worldToCameraMatrix at pitch=55, yaw=45.
        const float squash = 0.003f;
        float pitch = 55f * MathF.PI / 180f;
        float yaw = 45f * MathF.PI / 180f;
        Vector3 forward = Vector3.Normalize(new Vector3(
            MathF.Cos(pitch) * MathF.Sin(yaw),
            -MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Cos(yaw)));
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        Vector3 camUp = Vector3.Cross(forward, right);
        // Column-vector rigid view: rows are R, U, -F; then Scale(1, squash, 1) multiplies column 1 by squash.
        var rigidView = new Matrix4x4(
            right.X, right.Y, right.Z, 0f,
            camUp.X, camUp.Y, camUp.Z, 0f,
            -forward.X, -forward.Y, -forward.Z, 0f,
            0f, 0f, 0f, 1f);
        var isoView = new Matrix4x4(
            rigidView.M11, rigidView.M12 * squash, rigidView.M13, 0f,
            rigidView.M21, rigidView.M22 * squash, rigidView.M23, 0f,
            rigidView.M31, rigidView.M32 * squash, rigidView.M33, 0f,
            0f, 0f, 0f, 1f);

        var simulator = new CpuParticleSimulator();
        int quadId = simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "iso-quad", StartSizeMin = 2f, StartSizeMax = 2f, StartLifetimeMin = 10f, StartLifetimeMax = 10f
        }, Matrix4x4.Identity, false);
        int stretchedId = simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "iso-streak", BillboardMode = LumenBillboardMode.StretchedVelocity,
            StartSizeMin = 2f, StartSizeMax = 2f, StartSpeedMin = 4f, StartSpeedMax = 4f,
            VelocityStretchFactor = 0.25f, StartLifetimeMin = 10f, StartLifetimeMax = 10f
        }, Matrix4x4.CreateRotationZ(-MathF.PI / 2f), false);
        simulator.EmitBurst(quadId, 1);
        simulator.EmitBurst(stretchedId, 1);
        Step(simulator, 0f, isoView);

        // Helper: transform world point by column-vector isoView (M * v = Transform(v, Transpose(M))).
        Vector3 ToView(Vector3 w) => Vector3.Transform(w, Matrix4x4.Transpose(isoView));

        Vector3 q0 = ToView(simulator.Vertices[0].Position);
        Vector3 q1 = ToView(simulator.Vertices[1].Position);
        Vector3 q2 = ToView(simulator.Vertices[2].Position);
        float quadViewWidth = (q1 - q0).Length();
        float quadViewHeight = (q2 - q1).Length();
        Check(MathF.Abs(quadViewWidth - 2f) < 1e-3f && MathF.Abs(quadViewHeight - 2f) < 1e-3f
            && MathF.Abs(q1.Z - q0.Z) < 1e-3f && MathF.Abs(q2.Z - q1.Z) < 1e-3f,
            $"IsoRim quad collapsed or tilted in view space: width={quadViewWidth:F5}, height={quadViewHeight:F5}");

        Vector3 s0 = ToView(simulator.Vertices[4].Position);
        Vector3 s1 = ToView(simulator.Vertices[5].Position);
        Vector3 s2 = ToView(simulator.Vertices[6].Position);
        float streakViewWidth = (s1 - s0).Length();
        float streakViewLength = (s2 - s1).Length();
        Check(MathF.Abs(streakViewWidth - 2f) < 1e-3f && streakViewLength >= 3.9f
            && MathF.Abs(s1.Z - s0.Z) < 1e-3f && MathF.Abs(s2.Z - s1.Z) < 1e-3f,
            $"IsoRim stretched particle collapsed or tilted in view space: width={streakViewWidth:F5}, length={streakViewLength:F5}");
    }

    [LumenFact]
    public static void Vertical3DUnderSquashedIsoRimCameraKeepsTrueHeightAndStretchDirection()
    {
        (Matrix4x4 isoView, Vector3 camRight, Vector3 camUp) = IsoRimView();
        Vector3 ToView(Vector3 w) => Vector3.Transform(w, Matrix4x4.Transpose(isoView));
        Vector3 Center(CpuParticleSimulator simulator, int firstVertex)
            => (simulator.Vertices[firstVertex].Position + simulator.Vertices[firstVertex + 1].Position
                + simulator.Vertices[firstVertex + 2].Position + simulator.Vertices[firstVertex + 3].Position) * 0.25f;

        var simulator = new CpuParticleSimulator();
        var quad = new EmitterSpec
        {
            Name = "iso-height", StartSizeMin = 2f, StartSizeMax = 2f, StartLifetimeMin = 10f, StartLifetimeMax = 10f,
        };
        Matrix4x4 apex = Matrix4x4.CreateTranslation(0f, 4.8f, 0f);
        int free = simulator.AcquireEmitter(quad, apex, false);
        int ground = simulator.AcquireEmitter(quad, apex, false);
        simulator.SetEmitterVerticalState(free, true, 0f);
        simulator.EmitBurst(free, 1);
        simulator.EmitBurst(ground, 1);
        Step(simulator, 0f, isoView);
        // Vertical3D: 4.8 of true height reaches the screen unsquashed. The legacy path keeps the 0.003 squash.
        Check(MathF.Abs(ToView(Center(simulator, 0)).Y - 4.8f * camUp.Y) < 1e-3f,
            $"Vertical3D height collapsed in view space: y={ToView(Center(simulator, 0)).Y:F5}, expected {4.8f * camUp.Y:F5}");
        Check(MathF.Abs(ToView(Center(simulator, 4)).Y - 4.8f * 0.003f * camUp.Y) < 1e-3f,
            "A non-Vertical3D emitter must keep the legacy squashed height");

        // A 45-degree diagonal: the true screen direction must come from the unsquashed velocity.
        var streak = new CpuParticleSimulator();
        int streakId = streak.AcquireEmitter(new EmitterSpec
        {
            Name = "iso-diagonal", BillboardMode = LumenBillboardMode.StretchedVelocity, Shape = LumenShapeKind.Cone,
            ConeAngleDegrees = 0f, StartSizeMin = 2f, StartSizeMax = 2f, StartSpeedMin = 4f, StartSpeedMax = 4f,
            VelocityStretchFactor = 0.25f, StartLifetimeMin = 10f, StartLifetimeMax = 10f,
        }, Matrix4x4.CreateRotationZ(-MathF.PI / 4f) * apex, false);
        streak.SetEmitterVerticalState(streakId, true, 0f);
        streak.EmitBurst(streakId, 1);
        Step(streak, 0f, isoView);
        Vector3 velocity = Vector3.Normalize(new Vector3(1f, 1f, 0f)) * 4f;
        Vector2 expected = Vector2.Normalize(new Vector2(Vector3.Dot(velocity, camRight), Vector3.Dot(velocity, camUp)));
        Vector3 edge = ToView(streak.Vertices[2].Position) - ToView(streak.Vertices[1].Position);
        Vector2 actual = Vector2.Normalize(new Vector2(edge.X, edge.Y));
        Check(MathF.Abs(edge.Length() - 4f) < 1e-3f && MathF.Abs(edge.Z) < 1e-3f
            && Vector2.Distance(expected, actual) < 1e-3f,
            $"Vertical3D streak direction {actual} (length {edge.Length():F4}) differs from the true 3D projection {expected}");
    }

    [LumenFact]
    public static void Vertical3DUnderStraightDownCameraProjectsHeightOntoScreenUp()
    {
        // Vanilla top-down camera: right = +X, screen-up = +Z, looking down -Y, no squash.
        Matrix4x4 topDown = RigidView(Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitY);
        float moteY = Verse.Altitudes.AltitudeFor(Verse.AltitudeLayer.MoteOverhead);
        var simulator = new CpuParticleSimulator();
        var quad = new EmitterSpec
        {
            Name = "top-down-height", StartSizeMin = 2f, StartSizeMax = 2f, StartLifetimeMin = 10f, StartLifetimeMax = 10f,
        };
        Matrix4x4 apex = Matrix4x4.CreateTranslation(3f, 4.8f, 7f);
        int free = simulator.AcquireEmitter(quad, apex, false);
        int ground = simulator.AcquireEmitter(quad, apex, false);
        int streak = simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "top-down-streak", BillboardMode = LumenBillboardMode.StretchedVelocity, Shape = LumenShapeKind.Cone,
            ConeAngleDegrees = 0f, StartSizeMin = 2f, StartSizeMax = 2f, StartSpeedMin = 4f, StartSpeedMax = 4f,
            VelocityStretchFactor = 0.25f, StartLifetimeMin = 10f, StartLifetimeMax = 10f,
        }, apex, false);
        simulator.SetEmitterVerticalState(free, true, 0f);
        simulator.SetEmitterVerticalState(streak, true, 0f);
        simulator.EmitBurst(free, 1);
        simulator.EmitBurst(ground, 1);
        simulator.EmitBurst(streak, 1);
        Step(simulator, 0f, topDown);
        Vector3 Center(int firstVertex)
            => (simulator.Vertices[firstVertex].Position + simulator.Vertices[firstVertex + 1].Position
                + simulator.Vertices[firstVertex + 2].Position + simulator.Vertices[firstVertex + 3].Position) * 0.25f;

        // Group order is acquisition order: Vertical3D quad and legacy quad share a group (vertices 0..7), the streak follows.
        Vector3 freeCenter = Center(0);
        Close(3f, freeCenter.X);
        Close(11.8f, freeCenter.Z);
        Check(freeCenter.Y >= moteY - 1e-4f, $"Vertical3D must draw at or above the mote layer, y={freeCenter.Y}");
        Vector(new Vector3(3f, 4.8f, 7f), Center(4));
        Vector3 streakCenter = Center(8);
        Close(11.8f, streakCenter.Z);
        Vector(new Vector3(0f, 0f, 4f), simulator.Vertices[10].Position - simulator.Vertices[9].Position);
    }

    [LumenFact]
    public static void GroundEmittersStayOnTheLegacyPathUnderIsoRimAndTopDownCameras()
    {
        // RimUnity prefabs use GroundXZ with Unity-baked rotations and MoteOverhead-sized Y offsets: no display remap.
        Matrix4x4 isoView = IsoRimView().View;
        Matrix4x4 topDown = RigidView(Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitY);
        var simulator = new CpuParticleSimulator();
        int id = simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "ground-legacy", StartSizeMin = 2f, StartSizeMax = 2f, StartLifetimeMin = 10f, StartLifetimeMax = 10f,
        }, Matrix4x4.CreateTranslation(3f, 11f, 7f), false);
        simulator.EmitBurst(id, 1);
        Vector3 Center() => (simulator.Vertices[0].Position + simulator.Vertices[1].Position
            + simulator.Vertices[2].Position + simulator.Vertices[3].Position) * 0.25f;
        Step(simulator, 0f, topDown);
        Vector(new Vector3(3f, 11f, 7f), Center());
        Step(simulator, 0f, isoView);
        Vector(new Vector3(3f, 11f, 7f), Center());
    }

    [LumenFact]
    public static void KillBelowFloorExpiresOnlyDescendingParticlesBelowTheBaseHeight()
    {
        var falling = new EmitterSpec
        {
            Name = "falling", KillBelowFloor = true, StartLifetimeMin = 10f, StartLifetimeMax = 10f,
            ForceFields = new[] { new ForceFieldSpec(ForceFieldKind.GravityDrag, new Vector3(0f, -1f, 0f), Vector3.Zero, 0f, 10f) },
        };
        var simulator = new CpuParticleSimulator();
        // y = 0.5, no initial speed: after 0.5 s it is at 0.5 - 2.5 = -2 and descending.
        int expires = simulator.AcquireEmitter(falling, Matrix4x4.CreateTranslation(0f, 0.5f, 0f), false);
        int ignoresFlag = simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "no-flag", StartLifetimeMin = 10f, StartLifetimeMax = 10f, ForceFields = falling.ForceFields,
        }, Matrix4x4.CreateTranslation(0f, 0.5f, 0f), false);
        int local = simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "falling-local", KillBelowFloor = true, WorldSpaceSimulation = false,
            StartLifetimeMin = 10f, StartLifetimeMax = 10f, ForceFields = falling.ForceFields,
        }, Matrix4x4.CreateTranslation(0f, 0.5f, 0f), false);
        // Spawned exactly at the floor and rising, and below the floor but rising: both survive.
        var rising = new EmitterSpec
        {
            Name = "rising", KillBelowFloor = true, Shape = LumenShapeKind.Cone, ConeAngleDegrees = 0f,
            StartSpeedMin = 4f, StartSpeedMax = 4f, StartLifetimeMin = 10f, StartLifetimeMax = 10f,
        };
        int atFloor = simulator.AcquireEmitter(rising, Matrix4x4.CreateTranslation(0f, 0f, 0f), false);
        // Starts at -3 and rises to -1 within the step: still below the floor, but not descending.
        int belowFloor = simulator.AcquireEmitter(rising, Matrix4x4.CreateTranslation(0f, -3f, 0f), false);
        foreach (int id in new[] { expires, ignoresFlag, local, atFloor, belowFloor })
        {
            simulator.SetEmitterVerticalState(id, true, 0f);
            simulator.EmitBurst(id, 1);
        }
        Check(simulator.LiveParticleCount == 5, "Five particles before the step");
        Step(simulator, 0.5f);
        Check(simulator.GetLiveParticleCount(expires) == 0, "A descending world-space particle below the floor expires");
        Check(simulator.GetLiveParticleCount(local) == 0, "A descending local-space particle below the floor expires");
        Check(simulator.GetLiveParticleCount(ignoresFlag) == 1, "Emitters without KillBelowFloor keep falling");
        Check(simulator.GetLiveParticleCount(atFloor) == 1, "A rising particle spawned at the floor survives");
        Check(simulator.GetLiveParticleCount(belowFloor) == 1, "A rising particle below the floor survives");
        Check(simulator.LiveParticleCount == 3, "Expired particles are reclaimed from the live count");
    }

    [LumenFact]
    public static void EffectHandleUsesPlayOriginHeightAsTheFloor()
    {
        var asset = new EffectAsset
        {
            EffectId = "kill-below-floor-origin",
            Nodes = new[]
            {
                new EffectNodeSpec
                {
                    Name = "drop", SpacePlane = LumenSpacePlane.Vertical3D, LocalPosition = new Vector3(0f, 0.5f, 0f),
                    Emitter = new EmitterSpec
                    {
                        Name = "drop", PreferredTier = LumenParticleTier.L1_NativeCpu, KillBelowFloor = true,
                        Duration = 1f, Bursts = new[] { (0f, 1) }, StartLifetimeMin = 10f, StartLifetimeMax = 10f,
                        ForceFields = new[] { new ForceFieldSpec(ForceFieldKind.GravityDrag, new Vector3(0f, -1f, 0f), Vector3.Zero, 0f, 10f) },
                    },
                },
            },
        };
        LumenVfxService service = VfxTestData.Service(asset);
        using ILumenEffectHandle handle = service.Play(new LumenPlayRequest
        {
            EffectId = asset.EffectId, OriginWorld = new Vector3(0f, 5f, 0f),
        });
        VfxTestData.Step(service, 0.1f);
        Check(service.Simulator.LiveParticleCount == 1, "The drop spawns 0.5 above the play origin");
        // At 5.5 it falls to y = 3.0 within 0.5 s: below the origin height 5, above world zero.
        VfxTestData.Step(service, 0.5f);
        Check(service.Simulator.LiveParticleCount == 0, "The play origin height is the floor of KillBelowFloor emitters");
    }

    // IsoRim Waylith: pitch 55, yaw 45, world Y squashed by 0.003 inside the view matrix.
    private static (Matrix4x4 View, Vector3 Right, Vector3 Up) IsoRimView()
    {
        const float squash = 0.003f;
        float pitch = 55f * MathF.PI / 180f;
        float yaw = 45f * MathF.PI / 180f;
        Vector3 forward = Vector3.Normalize(new Vector3(
            MathF.Cos(pitch) * MathF.Sin(yaw), -MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw)));
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        Vector3 up = Vector3.Cross(forward, right);
        Matrix4x4 rigid = RigidView(right, up, forward);
        var view = new Matrix4x4(
            rigid.M11, rigid.M12 * squash, rigid.M13, 0f,
            rigid.M21, rigid.M22 * squash, rigid.M23, 0f,
            rigid.M31, rigid.M32 * squash, rigid.M33, 0f,
            0f, 0f, 0f, 1f);
        return (view, right, up);
    }

    // Column-vector rigid view whose rows are right, up and -forward.
    private static Matrix4x4 RigidView(Vector3 right, Vector3 up, Vector3 forward) => new(
        right.X, right.Y, right.Z, 0f,
        up.X, up.Y, up.Z, 0f,
        -forward.X, -forward.Y, -forward.Z, 0f,
        0f, 0f, 0f, 1f);

    private sealed class BatchSink : IL1ParticleBatchSink
    {
        public int Calls, Particles, Vertices, Indices;
        public void Submit(ReadOnlySpan<ParticleVertex> vertices, ReadOnlySpan<uint> indices, int liveParticleCount)
        {
            Calls++; Particles = liveParticleCount; Vertices = vertices.Length; Indices = indices.Length;
        }
    }

    private static void Step(CpuParticleSimulator simulator, float dt, Matrix4x4? view = null, int mapIndex = -1)
    {
        simulator.CaptureMainThread(new LumenCaptureContext
        {
            CurrentMapIndex = mapIndex,
            FrameIndex = 0, DeltaTime = dt, ViewportWidth = 100, ViewportHeight = 100,
            ViewMatrix = view ?? Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity, LumenRenderPassId = 0,
        });
    }

    private static void Close(float expected, float actual)
    {
        if (!(MathF.Abs(expected - actual) <= 1e-4f * MathF.Max(1f, MathF.Abs(expected))))
            throw new InvalidOperationException($"Expected {expected}, got {actual}");
    }

    private static void Vector(Vector3 expected, Vector3 actual)
    {
        Close(expected.X, actual.X); Close(expected.Y, actual.Y); Close(expected.Z, actual.Z);
    }

    private static void Uv(Vector2 expected, Vector2 actual)
    {
        Close(expected.X, actual.X); Close(expected.Y, actual.Y);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
