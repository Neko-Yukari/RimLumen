using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.ParticlesGpu;

public sealed partial class LumenGpuParticleEngine : ILumenGpuParticleEngine, IDisposable
{
    private enum InstanceState { Emitting, Draining, Remove }

    private sealed class Instance
    {
        public required int Id;
        public required EmitterSpec Spec;
        public required EmitterSlotRange Slots;
        public required EmissionScheduler Scheduler;
        public required GpuCurveSample[] Curves;
        public Matrix4x4 World;
        public bool Emitting = true;
        public float Intensity = 1f;
        public double DrainRemaining;
        public InstanceState State;
    }

    private readonly int capacity;
    private readonly EmitterSlotAllocator allocator;
    private readonly Dictionary<int, Instance> instances = new();
    private int nextId;
    private bool disposed;
    private ParticleFrame? captured;

    public LumenGpuParticleEngine(int capacity = 65536)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
        allocator = new EmitterSlotAllocator(capacity);
    }

    public string Name => "RimLumen.GpuParticles";
    public bool HasActiveWork => !disposed && instances.Count != 0;

    // Requires: main thread, positive emitter capacity.
    // Ensures: reserves slots without allocating GPU resources while idle.
    public int CreateInstance(EmitterSpec spec, Matrix4x4 initialWorldTransform)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.MaxParticles <= 0) throw new ArgumentOutOfRangeException(nameof(spec.MaxParticles));
        if (spec.FlipbookTilesX <= 0 || spec.FlipbookTilesY <= 0)
            throw new ArgumentOutOfRangeException(nameof(spec.FlipbookTilesX), "Flipbook dimensions must be positive.");
        if (!Enum.IsDefined(spec.Shape) || !Enum.IsDefined(spec.BlendMode) || !Enum.IsDefined(spec.BillboardMode))
            throw new ArgumentOutOfRangeException(nameof(spec), "Unknown particle shape or render mode.");
        if (spec.BillboardMode == LumenBillboardMode.Mesh)
            throw new NotSupportedException("L2 GPU particles support Camera, Horizontal and StretchedVelocity billboards, not Mesh.");
        if (!float.IsFinite(spec.StartLifetimeMax) || spec.StartLifetimeMax <= 0 ||
            !float.IsFinite(spec.StartLifetimeMin) || spec.StartLifetimeMin <= 0 || spec.StartLifetimeMin > spec.StartLifetimeMax)
            throw new ArgumentOutOfRangeException(nameof(spec.StartLifetimeMax));
        var scheduler = new EmissionScheduler(spec);
        var curves = new GpuCurveSample[64];
        GpuCurveAtlasPacker.Pack(spec.SizeOverLifetime, spec.ColorOverLifetime, curves);
        int id = checked(nextId++);
        EmitterSlotRange range = allocator.Allocate(spec.MaxParticles);
        instances.Add(id, new Instance
        {
            Id = id, Spec = spec, Slots = range, Scheduler = scheduler,
            Curves = curves, World = initialWorldTransform
        });
        return id;
    }

    // Requires: main thread, id from this engine.
    // Ensures: next capture sees transform/emission/intensity changes.
    public void UpdateInstance(int instanceId, in Matrix4x4 worldTransform, bool emitting, float intensityMultiplier = 1f)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!instances.TryGetValue(instanceId, out Instance? instance)) return;
        instance.World = worldTransform;
        instance.Intensity = intensityMultiplier;
        if (instance.State == InstanceState.Emitting) instance.Emitting = emitting;
    }

    // Requires: main thread.
    // Ensures: no further spawns; immediate reclaim occurs on next captured frame.
    public void DestroyInstance(int instanceId, bool immediate = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!instances.TryGetValue(instanceId, out Instance? instance)) return;
        if (immediate) instance.State = InstanceState.Remove;
        else if (instance.State == InstanceState.Emitting)
        {
            instance.State = InstanceState.Draining;
            instance.DrainRemaining = instance.Spec.StartLifetimeMax;
        }
        instance.Emitting = false;
    }

    // Requires: main thread capture.
    // Ensures: freezes frame inputs and advances drain/emission state.
    public void CaptureMainThread(in LumenCaptureContext context)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!float.IsFinite(context.DeltaTime) || context.DeltaTime < 0)
            throw new ArgumentOutOfRangeException(nameof(context.DeltaTime));
        var rows = new List<ParticleEmitterFrame>(instances.Count);
        var clears = new List<EmitterSlotRange>();
        var removed = new List<int>();
        var fields = new List<GpuForceField>();
        foreach (Instance instance in instances.Values)
        {
            if (instance.State == InstanceState.Draining) instance.DrainRemaining -= context.DeltaTime;
            if (instance.State == InstanceState.Remove ||
                (instance.State == InstanceState.Draining && instance.DrainRemaining <= 0))
            {
                clears.Add(instance.Slots);
                removed.Add(instance.Id);
                allocator.Free(instance.Slots);
                continue;
            }
            EmitterSpec spec = instance.Spec;
            int budget = instance.State == InstanceState.Emitting
                ? Math.Min(instance.Slots.Capacity, instance.Scheduler.Advance(context.DeltaTime, instance.Emitting)) : 0;
            // Spawns are simulated at the end of this step, so even a long final step
            // must retain the newly spawned particles for a complete maximum lifetime.
            if (instance.State == InstanceState.Emitting && instance.Scheduler.Finished)
            {
                instance.State = InstanceState.Draining;
                instance.DrainRemaining = spec.StartLifetimeMax;
            }
            int row = rows.Count;
            int fieldOffset = fields.Count;
            foreach (ForceFieldSpec field in spec.ForceFields)
            {
                fields.Add(new GpuForceField
                {
                    CenterOrAxisAndKind = new Vector4(field.CenterOrAxis, (float)field.Kind),
                    ParametersAndRadius = new Vector4(field.Parameters, field.Radius),
                    StrengthAndFrequency = new Vector4(field.Strength, field.Frequency, 0, 0)
                });
            }
            var parameters = new GpuEmitterParams
            {
                // Slang's RowMajor SSBO uses matrix * column vector; scene transforms use row vectors.
                World = Matrix4x4.Transpose(instance.World),
                Slots = new Vector4(instance.Slots.Offset, instance.Slots.Capacity, budget, 0),
                Shape = new Vector4((float)spec.Shape, spec.ShapeExtents.X, spec.ShapeExtents.Y, spec.ShapeExtents.Z),
                Ranges = new Vector4(spec.StartSpeedMin, spec.StartSpeedMax, spec.StartLifetimeMin, spec.StartLifetimeMax),
                SizeCone = new Vector4(spec.StartSizeMin, spec.StartSizeMax, spec.ConeAngleDegrees * (MathF.PI / 180), spec.WorldSpaceSimulation ? 1 : 0),
                StartColor = spec.StartColorLinear,
                Flipbook = new Vector4(spec.FlipbookTilesX, spec.FlipbookTilesY, spec.FlipbookCycles, (float)spec.BillboardMode),
                Appearance = new Vector4(spec.VelocityStretchFactor, spec.LengthScale, spec.HdrIntensity, spec.BloomWeight),
                Tables = new Vector4(row, fieldOffset, spec.ForceFields.Length, instance.Id & 0x00ffffff),
                State = new Vector4(context.DeltaTime, spec.SoftParticleFadeDistance, 0, instance.Intensity)
            };
            rows.Add(new ParticleEmitterFrame(instance.Id, parameters, instance.Curves, spec.TextureKey, spec.BlendMode));
        }
        foreach (int id in removed) instances.Remove(id);
        captured = new ParticleFrame(context, rows.ToArray(), fields.ToArray(), clears.ToArray());
        System.Threading.Volatile.Write(ref idleRequested, rows.Count == 0);
    }

    public void Dispose()
    {
        if (disposed) return;
        DisposeGpu();
        disposed = true;
        instances.Clear();
        captured = null;
    }
}

internal sealed record ParticleEmitterFrame(int InstanceId, GpuEmitterParams Parameters,
    GpuCurveSample[] Curves, string? TextureKey, LumenBlendMode Blend);

internal sealed record ParticleFrame(LumenCaptureContext Context, ParticleEmitterFrame[] Emitters,
    GpuForceField[] Fields, EmitterSlotRange[] Clears);
