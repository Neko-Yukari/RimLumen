using System;
using System.Numerics;
using System.Collections.Generic;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Rendering;
using RimLumen.Core.ParticlesGpu;

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
        public required GpuCurveSample[] Curves;
        public Matrix4x4 Transform;
        public EmissionState State;
        public float RateMultiplier;
        public float IntensityMultiplier = 1f;
        public double Time;
        public double RateRemainder;
        public long BurstRemainder;
        public bool Started;
        public bool RealTime;
        public int MapIndex;
        public int Group;
        public uint RandomState;
        public int LiveCount;
        public int PendingBurst;
        public bool IsVertical3D;
        public float BaseY;
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
    private int activeMapIndex = -1;
    private Matrix4x4 view = Matrix4x4.Identity;

    // Requires: seed is any int; identical seeds and inputs produce identical particle states.
    public CpuParticleSimulator(int seed = 0x5EED) => this.seed = seed;

    // Ensures: always "L1_NativeCpu".
    public string Name => "L1_NativeCpu";

    // Set by the pipeline while its HDR renderer is attached.
    internal bool HdrRendererAttached { get; set; }

    // Ensures: true while at least one emitter is emitting or at least one particle is alive;
    //          false once every emitter is drained and every particle expired.
    public bool HasActiveWork
    {
        get
        {
            if (particleCount > 0) return true;
            foreach (int id in emitterIds)
                if (emitters[id].State == EmissionState.Emitting || emitters[id].PendingBurst > 0) return true;
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

    private readonly List<L1SubBatch> subBatches = new();
    private readonly Dictionary<(string? TextureKey, LumenBlendMode BlendMode, float SoftParticleFadeDistance), int> groupIds = new();
    private readonly List<(string? TextureKey, LumenBlendMode BlendMode, float SoftParticleFadeDistance)> groupKeys = new();
    private int[] groupCounts = Array.Empty<int>();
    private int[] groupCursors = Array.Empty<int>();

    // Ensures: contiguous index sub-batches grouped by (TextureKey, BlendMode) for the last CaptureMainThread,
    //          in group first-acquisition order, empty groups omitted. IndexStart/IndexCount address Indices;
    //          together the ranges cover exactly LiveParticleCount * 6 indices. A single-group frame keeps
    //          LiveParticles order in Vertices/Indices.
    public IReadOnlyList<L1SubBatch> SubBatches => subBatches;

    // Requires: spec is non-null; spec.MaxParticles > 0; worldTransform is finite; rateMultiplier is finite and >= 0.
    // Ensures: reserves capacity, seeds a deterministic per-emitter PRNG from (seed, emitterId),
    //          returns a non-negative emitter id that stays valid until ReleaseEmitter.
    // Ensures: realTime == true makes this emitter advance on context.UnscaledDeltaTime when that is
    //          positive (walls keep burning while the game is paused or running at another speed).
    public int AcquireEmitter(EmitterSpec spec, Matrix4x4 worldTransform, bool emitting = true, float rateMultiplier = 1f, bool realTime = false, int mapIndex = -1)
    {
        int id = nextId++;
        uint random = Hash(unchecked((uint)seed) ^ unchecked((uint)id + 0x9E3779B9u));
        var curves = new GpuCurveSample[GpuCurveAtlasPacker.SamplesPerRow];
        GpuCurveAtlasPacker.Pack(spec.SizeOverLifetime, spec.ColorOverLifetime, curves);
        emitters.Add(id, new Emitter
        {
            Curves = curves,
            Spec = spec, Transform = worldTransform, RealTime = realTime, MapIndex = mapIndex, Group = GroupOf(spec),
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

    // Ensures: marks the emitter as Vertical3D (display remap in BuildBatch) and sets the floor height used by
    //          Vertical3D display and KillBelowFloor (the effect's play-origin Y). Unknown ids are ignored.
    internal void SetEmitterVerticalState(int emitterId, bool isVertical3D, float baseY)
    {
        if (!emitters.TryGetValue(emitterId, out var emitter)) return;
        emitter.IsVertical3D = isVertical3D;
        emitter.BaseY = baseY;
    }

    // Requires: rateMultiplier is finite and >= 0 (validated by the public SetParam boundary).
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
    // Ensures: queues a pulse for the next capture, admitted after expired particles reclaim their slots.
    //          Newly admitted particles have age zero; pausing or restarting does not discard accepted pulses.
    // Ensures: global particle density uses this emitter's shared burst carry at enqueue time.
    public void EmitBurst(int emitterId, int count)
    {
        if (emitters.TryGetValue(emitterId, out var emitter) && emitter.State != EmissionState.Draining)
        {
            emitter.PendingBurst = (int)Math.Min((long)emitter.PendingBurst
                + ParticleEmissionBudget.AccumulateBurst(ref emitter.BurstRemainder, count, RimLumenRenderHost.ParticleDensityScale), emitter.Spec.MaxParticles);
        }
    }

    // Ensures: the emitter emits again from the start of its schedule: its clock, rate remainder and time-0 burst
    //          reset exactly as on AcquireEmitter, so burst times and Duration count from now. Live particles keep
    //          their age. Same semantics as ILumenGpuParticleEngine.RestartInstance. Unknown or released ids are ignored.
    public void RestartEmitter(int emitterId)
    {
        if (!emitters.TryGetValue(emitterId, out var emitter) || emitter.State == EmissionState.Draining) return;
        emitter.Time = 0d;
        emitter.RateRemainder = 0d;
        emitter.BurstRemainder = 0;
        emitter.Started = false;
        emitter.State = EmissionState.Emitting;
    }

    internal bool IsEmitterAlive(int emitterId) => emitters.ContainsKey(emitterId);

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
            emitter.PendingBurst = 0;
            for (int i = particleCount - 1; i >= 0; i--)
                if (particles[i].EmitterId == emitterId) RemoveParticle(i);
            BuildBatch();
        }
        if (emitter.LiveCount == 0 && emitter.PendingBurst == 0) RemoveEmitter(emitterId);
    }

    // Requires: main thread; context.DeltaTime >= 0.
    // Ensures: advances emitter time, spawns rate/burst particles, integrates the four force-field
    //          kinds, expires particles, and rebuilds the quad/vertex/index batch for this frame.
    // Ensures: a real-time emitter uses context.UnscaledDeltaTime when it is positive, so it keeps
    //          running while the game is paused; every particle owned by it uses the same delta.
    //          RimLumenRenderHost.ParticleDensityScale < 1 thins rate/burst spawns (1.0 stays bit-exact).
    // Ensures: a Looping emitter wraps its time at Duration and keeps emitting. A non-looping emitter
    //          accumulates its own emission clock, advanced only while it is Emitting, and stops emitting
    //          once that clock reaches Duration, so an emitter activated later (a node that started paused)
    //          still gets a full Duration window instead of dying before it runs. A spent emitter pauses and
    //          stays acquired until ReleaseEmitter, so RestartEmitter can run it again.
    //          Burst times are emission-local for a non-looping emitter. Particles already spawned always
    //          finish their own lifetime.
    public void CaptureMainThread(in LumenCaptureContext context)
    {
        activeMapIndex = context.CurrentMapIndex;
        float dt = context.DeltaTime;
        float unscaledDt = context.UnscaledDeltaTime;
        float density = RimLumenRenderHost.ParticleDensityScale;
        for (int i = 0; i < particleCount; i++)
        {
            ref var particle = ref particles[i];
            Emitter owner = emitters[particle.EmitterId];
            float particleDt = owner.RealTime && unscaledDt > 0f ? unscaledDt : dt;
            particle.Age += particleDt;
            if (particle.Age >= particle.Lifetime) continue;
            ApplyForces(ref particle, owner, particleDt);
            particle.Position += particle.Velocity * particleDt;
            if (owner.Spec.KillBelowFloor && IsDescendingBelow(owner, particle, owner.BaseY))
                particle.Age = particle.Lifetime;
        }
        for (int i = particleCount - 1; i >= 0; i--)
            if (particles[i].Age >= particles[i].Lifetime) RemoveParticle(i);
        foreach (int id in emitterIds)
        {
            var emitter = emitters[id];
            var spec = emitter.Spec;
            Spawn(id, emitter, emitter.PendingBurst);
            emitter.PendingBurst = 0;
            float emitterDt = emitter.RealTime && unscaledDt > 0f ? unscaledDt : dt;
            bool looping = spec.Looping && spec.Duration > 0f;
            bool limited = !spec.Looping && spec.Duration > 0f;
            if (!looping && emitter.State != EmissionState.Emitting) continue;
            double start = emitter.Time;
            double end = start + emitterDt;
            double emissionEnd = limited ? Math.Min(end, spec.Duration) : end;
            emitter.Time = looping ? end % spec.Duration : emissionEnd;
            if (emitter.State == EmissionState.Emitting)
            {
                double emissionDt = Math.Max(0d, emissionEnd - start);
                double count = ParticleEmissionBudget.AccumulateRate(ref emitter.RateRemainder,
                    (double)spec.RateOverTime * emitter.RateMultiplier, emissionDt, density);
                Spawn(id, emitter, (int)Math.Min(Math.Max(0, count), spec.MaxParticles));
                foreach (var burst in spec.Bursts)
                {
                    double crossings = looping
                        ? Math.Floor((end - burst.Time) / spec.Duration) - Math.Floor((start - burst.Time) / spec.Duration)
                        : (start < burst.Time && emissionEnd >= burst.Time ? 1 : 0);
                    if (looping && (burst.Time < 0 || burst.Time > spec.Duration)) crossings = 0;
                    if (!emitter.Started && burst.Time == 0f && emitterDt > 0f) crossings++;
                    for (double occurrence = 0; occurrence < crossings; occurrence++)
                        Spawn(id, emitter, ParticleEmissionBudget.AccumulateBurst(ref emitter.BurstRemainder, burst.Count, density));
                }
            }
            if (limited && end >= spec.Duration) emitter.State = EmissionState.Paused;
            if (emitterDt > 0f) emitter.Started = true;
        }
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

    // Ensures: no command-buffer work. MeshBatchRenderer records the draws appended by the pipeline
    //   from the L1 mesh staged during main-thread capture.
    public void RecordHdrDraws(in LumenHdrDrawContext context) { }

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
            Vector3 position = Vector3.Zero, direction = Vector3.UnitY;
            float angle = 2f * MathF.PI * u;
            switch (spec.Shape)
            {
                case LumenShapeKind.Sphere:
                    float y = 2f * v - 1f;
                    float ring = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
                    direction = new Vector3(ring * MathF.Cos(angle), y, ring * MathF.Sin(angle));
                    position = direction * MathF.Cbrt(w) * spec.ShapeExtents;
                    break;
                case LumenShapeKind.CircleXZ:
                    direction = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
                    position = direction * MathF.Sqrt(v) * spec.ShapeExtents;
                    break;
                case LumenShapeKind.Cone:
                    position = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle)) * MathF.Sqrt(v) * spec.ShapeExtents;
                    float cos = float.Lerp(1f, MathF.Cos(spec.ConeAngleDegrees * MathF.PI / 180f), w);
                    float sin = MathF.Sqrt(MathF.Max(0f, 1f - cos * cos));
                    direction = new Vector3(sin * MathF.Cos(angle), cos, sin * MathF.Sin(angle));
                    break;
                case LumenShapeKind.Box:
                    position = new Vector3(2f * u - 1f, 2f * v - 1f, 2f * w - 1f) * spec.ShapeExtents;
                    break;
            }
            ParticleShapeTransform.Apply(spec, ref position, ref direction);
            if (spec.WorldSpaceSimulation)
            {
                position = Vector3.Transform(position, emitter.Transform);
                direction = Vector3.TransformNormal(direction, emitter.Transform);
                size *= ParticleAppearanceMath.SizeScale(emitter.Transform);
            }
            particles[particleCount++] = new CpuParticle
            {
                EmitterId = id, Position = position, Velocity = direction * speed,
                Lifetime = lifetime, StartSize = size,
            };
            emitter.LiveCount++;
        }
    }

    private static bool IsDescendingBelow(Emitter owner, in CpuParticle particle, float floorY)
    {
        Vector3 position = particle.Position, velocity = particle.Velocity;
        if (!owner.Spec.WorldSpaceSimulation)
        {
            position = Vector3.Transform(position, owner.Transform);
            velocity = Vector3.TransformNormal(velocity, owner.Transform);
        }
        return position.Y < floorY && velocity.Y < 0f;
    }

    private int GroupOf(EmitterSpec spec)
    {
        var key = (spec.TextureKey, spec.BlendMode, spec.SoftParticleFadeDistance);
        if (groupIds.TryGetValue(key, out int group)) return group;
        group = groupKeys.Count;
        groupIds.Add(key, group);
        groupKeys.Add(key);
        Array.Resize(ref groupCounts, group + 1);
        Array.Resize(ref groupCursors, group + 1);
        return group;
    }

    private static void ApplyForces(ref CpuParticle particle, Emitter emitter, float dt)
    {
        Vector3 force = ParticleForceMath.Acceleration(emitter.Spec, emitter.Transform, particle.Position, out float drag);
        particle.Velocity = (particle.Velocity + force * dt) * MathF.Exp(-drag * dt);
    }

    private void BuildBatch()
    {
        subBatches.Clear();
        if (particleCount == 0)
        {
            batchCount = 0;
            return;
        }

        // LiveCount is kept in step with particles[], so per-group sizes need no extra particle pass.
        Array.Clear(groupCounts);
        foreach (int id in emitterIds)
        {
            var owner = emitters[id];
            if (activeMapIndex >= 0 && owner.MapIndex >= 0 && owner.MapIndex != activeMapIndex) continue;
            groupCounts[owner.Group] += owner.LiveCount;
        }

        int offset = 0;
        for (int group = 0; group < groupCounts.Length; group++)
        {
            groupCursors[group] = offset;
            int count = groupCounts[group];
            if (count > 0)
            {
                var (textureKey, blendMode, softFade) = groupKeys[group];
                subBatches.Add(new L1SubBatch(textureKey, blendMode, offset * 6, count * 6, softFade));
            }

            offset += count;
        }

        CameraBillboardBasis.Extract(view, out Vector3 cameraRight, out Vector3 cameraUp, out float yScaleRatio);
        bool isTopDown2D = CameraBillboardBasis.IsStraightTopDown2D(cameraUp, yScaleRatio);
        float vanillaMoteY = Verse.Altitudes.AltitudeFor(Verse.AltitudeLayer.MoteOverhead);
        for (int i = 0; i < particleCount; i++)
        {
            ref var particle = ref particles[i];
            var emitter = emitters[particle.EmitterId];
            var spec = emitter.Spec;
            float age = Math.Clamp(particle.Age / particle.Lifetime, 0f, 1f);
            float samplePosition = age * (GpuCurveAtlasPacker.SamplesPerRow - 1);
            int lower = (int)samplePosition;
            GpuCurveSample a = emitter.Curves[lower], b = emitter.Curves[Math.Min(lower + 1, GpuCurveAtlasPacker.SamplesPerRow - 1)];
            float blend = samplePosition - lower;
            particle.Size = particle.StartSize * float.Lerp(a.Size.X, b.Size.X, blend)
                * (spec.WorldSpaceSimulation ? 1f : ParticleAppearanceMath.SizeScale(emitter.Transform));
            Vector4 color = spec.StartColor * Vector4.Lerp(a.Color, b.Color, blend);
            float intensity = spec.HdrIntensity * emitter.IntensityMultiplier;
            particle.Color = new Vector4(color.X * intensity, color.Y * intensity, color.Z * intensity, color.W);
            int tiles = spec.FlipbookTilesX * spec.FlipbookTilesY;
            int frame = (int)MathF.Floor(age * spec.FlipbookCycles * tiles);
            particle.FlipbookFrame = tiles > 1 && spec.FlipbookCycles > 1f
                ? frame % tiles : Math.Clamp(frame, 0, tiles - 1);
            Vector3 position = particle.Position, velocity = particle.Velocity;
            if (!spec.WorldSpaceSimulation)
            {
                position = Vector3.Transform(position, emitter.Transform);
                velocity = Vector3.TransformNormal(velocity, emitter.Transform);
            }
            if (emitter.IsVertical3D)
                CameraBillboardBasis.ProjectVertical3D(isTopDown2D, yScaleRatio, emitter.BaseY, vanillaMoteY, ref position, ref velocity);
            Vector3 right = cameraRight, up = cameraUp;
            float halfWidth = particle.Size * 0.5f, halfHeight = halfWidth;
            if (spec.BillboardMode == LumenBillboardMode.Horizontal)
            {
                right = Vector3.UnitX;
                up = Vector3.UnitZ;
            }
            else if (spec.BillboardMode == LumenBillboardMode.StretchedVelocity)
            {
                CameraBillboardBasis.ComputeStretchedAxes(cameraRight, cameraUp, yScaleRatio, velocity, out right, out up,
                    scaleVelocityY: !emitter.IsVertical3D);
                halfHeight = (particle.Size * spec.LengthScale + spec.VelocityStretchFactor * velocity.Length()) * 0.5f;
            }
            right *= halfWidth;
            up *= halfHeight;
            float u0 = (float)(particle.FlipbookFrame % spec.FlipbookTilesX) / spec.FlipbookTilesX;
            // Vanilla MeshMakerPlanes.NewPlaneMesh has uv (0,1) at +Z (v=1 is image top);
            // RimUnity ParticleSystemRenderer.cs TextureSheet uses 1f - (row+1)/numTilesY.
            float v0 = 1f - (float)(particle.FlipbookFrame / spec.FlipbookTilesX + 1) / spec.FlipbookTilesY;
            float u1 = u0 + 1f / spec.FlipbookTilesX, v1 = v0 + 1f / spec.FlipbookTilesY;
            if (activeMapIndex >= 0 && emitter.MapIndex >= 0 && emitter.MapIndex != activeMapIndex) continue;
            int slot = groupCursors[emitter.Group]++;
            int vertex = slot * 4, index = slot * 6;
            vertices[vertex] = new(position - right - up, new Vector2(u0, v0), particle.Color, Vector2.Zero);
            vertices[vertex + 1] = new(position + right - up, new Vector2(u1, v0), particle.Color, Vector2.UnitX);
            vertices[vertex + 2] = new(position + right + up, new Vector2(u1, v1), particle.Color, Vector2.One);
            vertices[vertex + 3] = new(position - right + up, new Vector2(u0, v1), particle.Color, Vector2.UnitY);
            indices[index] = (uint)vertex;
            indices[index + 1] = (uint)vertex + 1;
            indices[index + 2] = (uint)vertex + 2;
            indices[index + 3] = (uint)vertex;
            indices[index + 4] = (uint)vertex + 2;
            indices[index + 5] = (uint)vertex + 3;
        }
        batchCount = offset;
    }
}
