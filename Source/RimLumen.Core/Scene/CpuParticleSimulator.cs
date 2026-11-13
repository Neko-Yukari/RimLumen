using System;
using System.Numerics;
using System.Collections.Generic;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Scene;

/// <summary>
/// L1 (<see cref="LumenParticleTier.L1_NativeCpu"/>) pooled CPU particle simulator.
/// Steady-state frames allocate nothing: capacity is reserved per emitter on
/// <see cref="AcquireEmitter"/> and reused for the life of the emitter.
/// </summary>
public sealed class CpuParticleSimulator : ILumenFrameContributor
{
    private enum EmissionState { Paused, Emitting, Draining }

    private sealed class Emitter
    {
        public required EmitterSpec Spec;
        public Matrix4x4 Transform;
        public EmissionState State;
        public float RateMultiplier;
        public float IntensityMultiplier = 1f;
        public double Time;
        public double RateRemainder;
        public bool Started;
        public uint RandomState;
        public int LiveCount;
    }

    private readonly int seed;
    private readonly Dictionary<int, Emitter> emitters = new();
    private readonly List<int> emitterIds = new();
    private CpuParticle[] particles = Array.Empty<CpuParticle>();
    private ParticleVertex[] vertices = Array.Empty<ParticleVertex>();
    private uint[] indices = Array.Empty<uint>();
    private int nextId;
    private int reservedCapacity;
    private int particleCount;
    private int batchCount;
    private Matrix4x4 view = Matrix4x4.Identity;

    // Requires: seed is any int; identical seeds and inputs produce identical particle states.
    public CpuParticleSimulator(int seed = 0x5EED) => this.seed = seed;

    // Ensures: always "L1_NativeCpu".
    public string Name => "L1_NativeCpu";

    // Ensures: optional HDR submission target; null means "no HDR pass owner installed yet".
    public IL1ParticleBatchSink? BatchSink { get; set; }

    // Ensures: true while at least one emitter is emitting or at least one particle is alive;
    //          false once every emitter is drained and every particle expired.
    public bool HasActiveWork
    {
        get
        {
            if (particleCount > 0) return true;
            foreach (int id in emitterIds)
                if (emitters[id].State == EmissionState.Emitting) return true;
            return false;
        }
    }

    // Ensures: number of emitters that have not been released (emitting or draining).
    public int ActiveEmitterCount => emitterIds.Count;

    // Ensures: number of live particles after the last CaptureMainThread.
    public int LiveParticleCount => particleCount;

    // Ensures: read-only view of the live particles, ordered as stored (swap-remove compaction order).
    public ReadOnlySpan<CpuParticle> LiveParticles => particles.AsSpan(0, particleCount);

    // Ensures: 4 vertices and 6 indices per live particle from the last CaptureMainThread.
    public ReadOnlySpan<ParticleVertex> Vertices => vertices.AsSpan(0, batchCount * 4);

    public ReadOnlySpan<uint> Indices => indices.AsSpan(0, batchCount * 6);

    // Requires: spec is non-null; spec.MaxParticles > 0; worldTransform is finite.
    // Ensures: reserves capacity, seeds a deterministic per-emitter PRNG from (seed, emitterId),
    //          returns a non-negative emitter id that stays valid until ReleaseEmitter.
    public int AcquireEmitter(EmitterSpec spec, Matrix4x4 worldTransform, bool emitting = true, float rateMultiplier = 1f)
    {
        int id = nextId++;
        uint random = Hash(unchecked((uint)seed) ^ unchecked((uint)id + 0x9E3779B9u));
        emitters.Add(id, new Emitter
        {
            Spec = spec, Transform = worldTransform,
            State = emitting ? EmissionState.Emitting : EmissionState.Paused,
            RateMultiplier = rateMultiplier, RandomState = random == 0 ? 1u : random,
        });
        emitterIds.Add(id);
        reservedCapacity += spec.MaxParticles;
        if (particles.Length < reservedCapacity)
        {
            Array.Resize(ref particles, reservedCapacity);
            Array.Resize(ref vertices, checked(reservedCapacity * 4));
            Array.Resize(ref indices, checked(reservedCapacity * 6));
        }
        return id;
    }

    // Ensures: updates the emitter transform used for spawn shapes and (for local-space emitters)
    //          for transforming particle positions at vertex-build time. Unknown ids are ignored.
    public void SetEmitterTransform(int emitterId, Matrix4x4 worldTransform)
    {
        if (emitters.TryGetValue(emitterId, out var emitter)) emitter.Transform = worldTransform;
    }

    // Ensures: sets emission on/off plus the runtime rate/intensity multipliers. Turning emission off
    //          never kills live particles. Unknown ids are ignored.
    public void SetEmitterParams(int emitterId, bool emitting, float rateMultiplier, float intensityMultiplier)
    {
        if (!emitters.TryGetValue(emitterId, out var emitter) || emitter.State == EmissionState.Draining) return;
        emitter.State = emitting ? EmissionState.Emitting : EmissionState.Paused;
        emitter.RateMultiplier = rateMultiplier;
        emitter.IntensityMultiplier = intensityMultiplier;
    }

    // Requires: count >= 0.
    // Ensures: spawns min(count, spec.MaxParticles - liveOfThisEmitter) particles immediately from the
    //          emitter's shape with the same deterministic PRNG stream as timed emission.
    public void EmitBurst(int emitterId, int count)
    {
        if (emitters.TryGetValue(emitterId, out var emitter) && emitter.State != EmissionState.Draining)
            Spawn(emitterId, emitter, count);
    }

    // Ensures: live particle count owned by this emitter; 0 for unknown ids.
    public int GetLiveParticleCount(int emitterId)
        => emitters.TryGetValue(emitterId, out var emitter) ? emitter.LiveCount : 0;

    // Ensures: immediate == true removes the emitter and all of its particles now; immediate == false
    //          stops new emission and lets existing particles finish their lifetime.
    public void ReleaseEmitter(int emitterId, bool immediate = false)
    {
        if (!emitters.TryGetValue(emitterId, out var emitter)) return;
        emitter.State = EmissionState.Draining;
        if (immediate)
        {
            for (int i = particleCount - 1; i >= 0; i--)
                if (particles[i].EmitterId == emitterId) RemoveParticle(i);
            BuildBatch();
        }
        if (emitter.LiveCount == 0) RemoveEmitter(emitterId);
    }

    // Requires: main thread; context.DeltaTime >= 0.
    // Ensures: advances emitter time, spawns rate/burst particles, integrates the four force-field
    //          kinds, expires particles, and rebuilds the quad/vertex/index batch for this frame.
    // Ensures: a Looping emitter wraps its time at Duration and keeps emitting; a non-looping emitter
    //          stops emitting once its time passes Duration, while already spawned particles finish
    //          their own lifetime.
    public void CaptureMainThread(in LumenCaptureContext context)
    {
        int existingCount = particleCount;
        float dt = context.DeltaTime;
        foreach (int id in emitterIds)
        {
            var emitter = emitters[id];
            var spec = emitter.Spec;
            double start = emitter.Time;
            double end = start + dt;
            bool looping = spec.Looping && spec.Duration > 0f;
            emitter.Time = looping ? end % spec.Duration : end;
            if (emitter.State == EmissionState.Emitting)
            {
                emitter.RateRemainder += (double)spec.RateOverTime * emitter.RateMultiplier * dt;
                double count = Math.Floor(emitter.RateRemainder);
                emitter.RateRemainder -= count;
                Spawn(id, emitter, (int)Math.Min(Math.Max(0, count), spec.MaxParticles));
                foreach (var burst in spec.Bursts)
                {
                    double crossings = looping
                        ? Math.Floor((end - burst.Time) / spec.Duration) - Math.Floor((start - burst.Time) / spec.Duration)
                        : (start < burst.Time && end >= burst.Time ? 1 : 0);
                    if (looping && (burst.Time < 0 || burst.Time > spec.Duration)) crossings = 0;
                    if (!emitter.Started && burst.Time == 0f && dt > 0f) crossings++;
                    if (crossings > 0)
                        Spawn(id, emitter, (int)Math.Min(crossings * burst.Count, spec.MaxParticles));
                }
            }
            if (dt > 0f) emitter.Started = true;
        }
        for (int i = 0; i < existingCount; i++)
        {
            ref var particle = ref particles[i];
            ApplyForces(ref particle, emitters[particle.EmitterId], dt);
            particle.Position += particle.Velocity * dt;
            particle.Age += dt;
        }
        for (int i = particleCount - 1; i >= 0; i--)
            if (particles[i].Age >= particles[i].Lifetime) RemoveParticle(i);
        for (int i = emitterIds.Count - 1; i >= 0; i--)
        {
            int id = emitterIds[i];
            if (emitters[id].State == EmissionState.Draining && emitters[id].LiveCount == 0) RemoveEmitter(id);
        }
        view = context.ViewMatrix;
        BuildBatch();
    }

    // Ensures: no-op; L1 does no GPU compute.
    public void EnqueuePreCompute(in LumenComputeContext context) { }

    // Ensures: no-op; L1 records no buffer barriers.
    public void RecordBufferBarriers(in LumenBarrierContext context) { }

    // Requires: called inside RimLumenVfxPass; HasActiveWork was true for this frame.
    // Ensures: forwards the current batch to BatchSink when one is installed, otherwise submits nothing.
    public void RecordHdrDraws(in LumenHdrDrawContext context)
        => BatchSink?.Submit(Vertices, Indices, batchCount);

    private void RemoveParticle(int index)
    {
        emitters[particles[index].EmitterId].LiveCount--;
        particles[index] = particles[--particleCount];
    }

    private void RemoveEmitter(int id)
    {
        reservedCapacity -= emitters[id].Spec.MaxParticles;
        emitters.Remove(id);
        emitterIds.Remove(id);
    }

    private static uint Hash(uint value)
    {
        value ^= value >> 16;
        value = unchecked(value * 0x7FEB352Du);
        value ^= value >> 15;
        value = unchecked(value * 0x846CA68Bu);
        return value ^ (value >> 16);
    }

    private static float Random(Emitter emitter)
    {
        uint value = emitter.RandomState;
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        emitter.RandomState = value;
        return (value >> 8) * (1f / 16777216f);
    }

    private void Spawn(int id, Emitter emitter, int count)
    {
        var spec = emitter.Spec;
        count = Math.Min(count, spec.MaxParticles - emitter.LiveCount);
        for (int i = 0; i < count; i++)
        {
            // Every accepted spawn consumes exactly six draws, independent of shape/ranges.
            float lifetime = float.Lerp(spec.StartLifetimeMin, spec.StartLifetimeMax, Random(emitter));
            float size = float.Lerp(spec.StartSizeMin, spec.StartSizeMax, Random(emitter));
            float speed = float.Lerp(spec.StartSpeedMin, spec.StartSpeedMax, Random(emitter));
            float u = Random(emitter), v = Random(emitter), w = Random(emitter);
            Vector3 position = Vector3.Zero, direction = Vector3.UnitZ;
            float angle = 2f * MathF.PI * u;
            switch (spec.Shape)
            {
                case LumenShapeKind.Sphere:
                    float z = 2f * v - 1f;
                    float ring = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
                    direction = new Vector3(ring * MathF.Cos(angle), ring * MathF.Sin(angle), z);
                    position = direction * (MathF.Cbrt(w) * spec.ShapeExtents.X);
                    break;
                case LumenShapeKind.CircleXZ:
                    direction = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
                    position = direction * (MathF.Sqrt(v) * spec.ShapeExtents.X);
                    break;
                case LumenShapeKind.Cone:
                    float cos = float.Lerp(1f, MathF.Cos(spec.ConeAngleDegrees * MathF.PI / 180f), v);
                    float sin = MathF.Sqrt(MathF.Max(0f, 1f - cos * cos));
                    direction = new Vector3(sin * MathF.Cos(angle), sin * MathF.Sin(angle), cos);
                    break;
                case LumenShapeKind.Box:
                    position = new Vector3(2f * u - 1f, 2f * v - 1f, 2f * w - 1f) * spec.ShapeExtents;
                    break;
            }
            if (spec.WorldSpaceSimulation)
            {
                position = Vector3.Transform(position, emitter.Transform);
                direction = NormalizeOr(Vector3.TransformNormal(direction, emitter.Transform), Vector3.UnitZ);
            }
            particles[particleCount++] = new CpuParticle
            {
                EmitterId = id, Position = position, Velocity = direction * speed,
                Lifetime = lifetime, StartSize = size,
            };
            emitter.LiveCount++;
        }
    }

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback)
        => value.LengthSquared() > 1e-10f ? Vector3.Normalize(value) : fallback;

    private static void ApplyForces(ref CpuParticle particle, Emitter emitter, float dt)
    {
        foreach (var field in emitter.Spec.ForceFields)
        {
            switch (field.Kind)
            {
                case ForceFieldKind.GravityDrag:
                    particle.Velocity.Y -= field.Parameters.X * dt;
                    particle.Velocity *= MathF.Max(0f, 1f - field.Parameters.Y * dt);
                    break;
                case ForceFieldKind.Vortex:
                    Vector3 axis = NormalizeOr(field.CenterOrAxis, Vector3.UnitY);
                    Vector3 r = particle.Position - field.CenterOrAxis;
                    Vector3 radial = r - axis * Vector3.Dot(r, axis);
                    Vector3 tangent = NormalizeOr(Vector3.Cross(axis, radial), Vector3.Zero);
                    particle.Velocity += (tangent - NormalizeOr(radial, Vector3.Zero) * field.Parameters.X) * (field.Strength * dt);
                    break;
                case ForceFieldKind.PointAttractor:
                    Vector3 delta = field.CenterOrAxis - particle.Position;
                    float distance = delta.Length();
                    if (distance < field.Radius && distance > 1e-5f)
                        particle.Velocity += delta / distance * (field.Strength * (1f - distance / field.Radius) * dt);
                    break;
                case ForceFieldKind.CurlTurbulence:
                    Vector3 worldPosition = emitter.Spec.WorldSpaceSimulation ? particle.Position : Vector3.Transform(particle.Position, emitter.Transform);
                    Vector3 sample = worldPosition * field.Frequency;
                    Vector3 gradient = new(ValueNoise(sample), ValueNoise(sample + new Vector3(17, 31, 47)),
                        ValueNoise(sample + new Vector3(59, 71, 89)));
                    particle.Velocity += Vector3.Cross(gradient, Vector3.UnitY) * (field.Strength * dt);
                    break;
            }
        }
    }

    private static float ValueNoise(Vector3 point)
    {
        uint x = unchecked((uint)(int)MathF.Floor(point.X));
        uint y = unchecked((uint)(int)MathF.Floor(point.Y));
        uint z = unchecked((uint)(int)MathF.Floor(point.Z));
        uint hash = Hash(x ^ unchecked(y * 0x9E3779B9u) ^ unchecked(z * 0x85EBCA6Bu));
        return (hash >> 8) * (2f / 16777216f) - 1f;
    }

    private void BuildBatch()
    {
        Vector3 cameraRight = NormalizeOr(new Vector3(view.M11, view.M21, view.M31), Vector3.UnitX);
        Vector3 cameraUp = NormalizeOr(new Vector3(view.M12, view.M22, view.M32), Vector3.UnitY);
        Vector3 cameraForward = NormalizeOr(new Vector3(view.M13, view.M23, view.M33), Vector3.UnitZ);
        for (int i = 0; i < particleCount; i++)
        {
            ref var particle = ref particles[i];
            var emitter = emitters[particle.EmitterId];
            var spec = emitter.Spec;
            float age = Math.Clamp(particle.Age / particle.Lifetime, 0f, 1f);
            particle.Size = particle.StartSize * CurveEvaluator.Evaluate(spec.SizeOverLifetime, age) * spec.LengthScale;
            particle.Color = spec.StartColorLinear * GradientEvaluator.Evaluate(spec.ColorOverLifetime, age) * (spec.HdrIntensity * emitter.IntensityMultiplier);
            int tiles = spec.FlipbookTilesX * spec.FlipbookTilesY;
            particle.FlipbookFrame = Math.Clamp((int)(age * spec.FlipbookCycles * tiles), 0, tiles - 1);
            Vector3 position = particle.Position, velocity = particle.Velocity;
            if (!spec.WorldSpaceSimulation)
            {
                position = Vector3.Transform(position, emitter.Transform);
                velocity = Vector3.TransformNormal(velocity, emitter.Transform);
            }
            Vector3 right = cameraRight, up = cameraUp;
            float halfWidth = particle.Size * 0.5f, halfHeight = halfWidth;
            if (spec.BillboardMode == LumenBillboardMode.Horizontal)
            {
                up = Vector3.UnitY;
                right = NormalizeOr(new Vector3(cameraRight.X, 0f, cameraRight.Z), Vector3.UnitX);
            }
            else if (spec.BillboardMode == LumenBillboardMode.StretchedVelocity)
            {
                up = NormalizeOr(velocity, cameraUp);
                right = NormalizeOr(Vector3.Cross(up, cameraForward), cameraRight);
                halfHeight *= 1f + spec.VelocityStretchFactor * velocity.Length();
            }
            right *= halfWidth;
            up *= halfHeight;
            float u0 = (float)(particle.FlipbookFrame % spec.FlipbookTilesX) / spec.FlipbookTilesX;
            float v0 = 1f - (float)(particle.FlipbookFrame / spec.FlipbookTilesX + 1) / spec.FlipbookTilesY;
            float u1 = u0 + 1f / spec.FlipbookTilesX, v1 = v0 + 1f / spec.FlipbookTilesY;
            int vertex = i * 4, index = i * 6;
            vertices[vertex] = new(position - right - up, new Vector2(u0, v0), particle.Color);
            vertices[vertex + 1] = new(position + right - up, new Vector2(u1, v0), particle.Color);
            vertices[vertex + 2] = new(position + right + up, new Vector2(u1, v1), particle.Color);
            vertices[vertex + 3] = new(position - right + up, new Vector2(u0, v1), particle.Color);
            indices[index] = (uint)vertex;
            indices[index + 1] = (uint)vertex + 1;
            indices[index + 2] = (uint)vertex + 2;
            indices[index + 3] = (uint)vertex;
            indices[index + 4] = (uint)vertex + 2;
            indices[index + 5] = (uint)vertex + 3;
        }
        batchCount = particleCount;
    }
}
