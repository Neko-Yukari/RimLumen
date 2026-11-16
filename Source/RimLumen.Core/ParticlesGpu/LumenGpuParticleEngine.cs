using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Rendering;
using RimLumen.Core.Scene;

namespace RimLumen.Core.ParticlesGpu;

internal sealed partial class LumenGpuParticleEngine : ILumenGpuParticleEngine, IDisposable
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
        public float RateMultiplier = 1f;
        public bool Activated;
        public bool RealTime;
        public int MapIndex = -1;
        public bool IsVertical3D;
        public float BaseY;
        public double DrainRemaining;
        public int PendingBurst;
        public InstanceState State;
    }

    private readonly int capacity;
    private readonly EmitterSlotAllocator allocator;
    private readonly Dictionary<int, Instance> instances = new();
    private readonly List<ParticleEmitterFrame> captureRows = new();
    private readonly List<EmitterSlotRange> captureClears = new();
    private readonly List<int> captureRemoved = new();
    private readonly List<GpuForceField> captureFields = new();
    private int nextId;
    private bool disposed;
    private ParticleFrame? captured;
    internal ParticleFrame? CapturedFrame => captured;

    public LumenGpuParticleEngine(int capacity = EmitterSlotAllocator.DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
        allocator = new EmitterSlotAllocator(capacity);
    }

    public int Capacity => capacity;
    public string Name => "RimLumen.GpuParticles";
    public bool HasActiveWork => !disposed && instances.Count != 0;

    // Requires: main thread; spec passed LumenAssetRules validation.
    // Ensures: reserves slots without allocating GPU resources while idle.
    public int CreateInstance(EmitterSpec spec, Matrix4x4 initialWorldTransform)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.BillboardMode == LumenBillboardMode.Mesh)
            throw new NotSupportedException("L2 GPU particles support Camera, Horizontal and StretchedVelocity billboards, not Mesh.");
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
        lock (lifecycleGate)
        {
            System.Threading.Volatile.Write(ref idleRequested, false);
            idleStartedAt = null;
        }
        return id;
    }

    public int CreateInstance(EmitterSpec spec, Matrix4x4 initialWorldTransform, int mapIndex, bool realTime)
    {
        int id = CreateInstance(spec, initialWorldTransform);
        Instance instance = instances[id];
        instance.MapIndex = mapIndex;
        instance.RealTime = realTime;
        return id;
    }

    // Requires: main thread. Unknown ids are ignored.
    // Ensures: the next capture carries the Vertical3D display flag and the floor height (the effect's play-origin Y).
    internal void SetInstanceVerticalState(int instanceId, bool isVertical3D, float baseY)
    {
        if (!instances.TryGetValue(instanceId, out Instance? instance)) return;
        instance.IsVertical3D = isVertical3D;
        instance.BaseY = baseY;
    }

    // Requires: main thread, id from this engine.
    // Ensures: next capture sees transform/emission/intensity/rate changes.
    public void UpdateInstance(int instanceId, in Matrix4x4 worldTransform, bool emitting, float intensityMultiplier, float rateMultiplier)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!instances.TryGetValue(instanceId, out Instance? instance)) return;
        instance.World = worldTransform;
        instance.Intensity = intensityMultiplier;
        instance.RateMultiplier = rateMultiplier;
        if (instance.State == InstanceState.Emitting)
        {
            instance.Emitting = emitting;
            instance.Activated |= emitting;
        }
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

    public void RestartInstance(int instanceId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!instances.TryGetValue(instanceId, out Instance? instance) || instance.State != InstanceState.Emitting) return;
        instance.Scheduler.Restart();
        instance.Emitting = true;
        instance.Activated = true;
    }

    public bool IsInstanceAlive(int instanceId) => !disposed && instances.ContainsKey(instanceId);

    public void EmitBurst(int instanceId, int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!instances.TryGetValue(instanceId, out Instance? instance) || instance.State != InstanceState.Emitting) return;
        int accepted = instance.Scheduler.AcceptBurst(Math.Max(0, count), RimLumenRenderHost.ParticleDensityScale);
        instance.PendingBurst = (int)Math.Min((long)instance.PendingBurst + accepted, instance.Slots.Capacity);
    }

    // Requires: main thread capture.
    // Ensures: freezes frame inputs and advances drain/emission state. A non-looping instance whose schedule has
    //          run out keeps its slots and only simulates its live particles until DestroyInstance, so
    //          RestartInstance can run it again (the owner, EffectInstanceHandle, always destroys it at its end).
    public void CaptureMainThread(in LumenCaptureContext context)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!float.IsFinite(context.DeltaTime) || context.DeltaTime < 0)
            throw new ArgumentOutOfRangeException(nameof(context.DeltaTime));
        var rows = captureRows;
        var clears = captureClears;
        var removed = captureRemoved;
        var fields = captureFields;
        rows.Clear(); clears.Clear(); removed.Clear(); fields.Clear();
        // Player-facing density setting; the default 1.0 leaves the emission budget untouched.
        float density = RimLumenRenderHost.ParticleDensityScale;
        foreach (Instance instance in instances.Values)
        {
            float dt = instance.RealTime && context.UnscaledDeltaTime > 0f ? context.UnscaledDeltaTime : context.DeltaTime;
            instance.Activated |= instance.Emitting;
            if (instance.State == InstanceState.Draining)
            {
                instance.DrainRemaining -= dt;
                // A pulse accepted before DestroyInstance spawns now, so the drain lasts its full lifetime from now.
                if (instance.PendingBurst > 0) instance.DrainRemaining = Math.Max(instance.DrainRemaining, instance.Spec.StartLifetimeMax);
            }
            if (instance.State == InstanceState.Remove ||
                (instance.State == InstanceState.Draining && instance.DrainRemaining <= 0))
            {
                clears.Add(instance.Slots);
                removed.Add(instance.Id);
                allocator.Free(instance.Slots);
                continue;
            }
            EmitterSpec spec = instance.Spec;
            int budget = instance.State == InstanceState.Emitting && instance.Activated && dt > 0f
                ? Math.Min(instance.Slots.Capacity, instance.Scheduler.Advance(dt, instance.Emitting, density, instance.RateMultiplier)) : 0;
            budget = Math.Min(instance.Slots.Capacity, budget + instance.PendingBurst);
            instance.PendingBurst = 0;
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
                StartColor = spec.StartColor,
                Flipbook = new Vector4(spec.FlipbookTilesX, spec.FlipbookTilesY, spec.FlipbookCycles, (float)spec.BillboardMode),
                Appearance = new Vector4(spec.VelocityStretchFactor, spec.LengthScale, spec.HdrIntensity, ParticleAppearanceMath.SizeScale(instance.World)),
                Tables = new Vector4(row, fieldOffset, spec.ForceFields.Length, instance.Id & 0x00ffffff),
                State = new Vector4(dt, spec.SoftParticleFadeDistance, 0, instance.Intensity),
                ShapePosition = new Vector4(spec.ShapePosition, 0f),
                ShapeRotation = new Vector4(spec.ShapeRotation.X, spec.ShapeRotation.Y, spec.ShapeRotation.Z, spec.ShapeRotation.W),
                ShapeScale = new Vector4(spec.ShapeScale, 0f)
            };
            rows.Add(new ParticleEmitterFrame(instance.Id, parameters, instance.Curves, spec.TextureKey, spec.BlendMode,
                instance.MapIndex, instance.IsVertical3D, instance.BaseY, spec.KillBelowFloor));
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

internal readonly record struct ParticleEmitterFrame(int InstanceId, GpuEmitterParams Parameters,
    GpuCurveSample[] Curves, string? TextureKey, LumenBlendMode Blend, int MapIndex = -1,
    bool IsVertical3D = false, float BaseY = 0f, bool KillBelowFloor = false)
{
    // -1 on either side means unscoped (no map known), which keeps offline frames drawing everything.
    internal bool IsVisibleOnMap(int currentMapIndex) =>
        MapIndex < 0 || currentMapIndex < 0 || MapIndex == currentMapIndex;
}

internal sealed record ParticleFrame(LumenCaptureContext Context, ParticleEmitterFrame[] Emitters,
    GpuForceField[] Fields, EmitterSlotRange[] Clears);
