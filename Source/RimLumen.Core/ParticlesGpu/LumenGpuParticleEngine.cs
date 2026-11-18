using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Rendering;
using RimLumen.Core.Scene;
using RimLumen.Core.ParticleWorld;

namespace RimLumen.Core.ParticlesGpu;

internal sealed partial class LumenGpuParticleEngine : ILumenGpuParticleEngine, IDisposable
{
    private enum InstanceState { Emitting, Draining, Remove }

    private sealed class Instance
    {
        public required int Id;
        public required EmitterSpec Spec;
        public required EmitterSlotRange Slots;
        // Index into the persistent BurstState buffer, stable for the instance's lifetime.
        public required int StateSlot;
        public required EmissionScheduler Scheduler;
        public required GpuCurveSample[] Curves;
        public LumenEmitterTransform Transform;
        public bool Emitting = true;
        public float Intensity = 1f;
        public float RateMultiplier = 1f;
        public bool Activated;
        public bool RealTime;
        public int MapIndex = -1;
        public bool IsVertical3D;
        public float? DepthReferenceZ;
        public double DrainRemaining;
        public int PendingBurst;
        public InstanceState State;
        public float NodeTime;
        public int[] Targets = Array.Empty<int>();
        public readonly HashSet<int> Sources = new();
        public float? FrameDelta;
        public bool GpuBurstCarry;
        public uint ResetVersion;
        public readonly List<GpuExplicitBurst> ExplicitBursts = new();
    }

    private readonly int capacity;
    private readonly EmitterSlotAllocator allocator;
    private readonly Dictionary<int, Instance> instances = new();
    private readonly List<ParticleEmitterFrame> captureRows = new();
    private readonly List<EmitterSlotRange> captureClears = new();
    private readonly List<int> captureRemoved = new();
    private readonly List<GpuForceField> captureFields = new();
    private readonly List<ParticleMapGrid> captureMaps = new();
    private readonly List<GpuSubLink> captureLinks = new();
    private readonly List<GpuExplicitBurst> captureBursts = new();
    private readonly Dictionary<int, int> rowByInstance = new();
    private readonly Dictionary<int, int> mapOffsets = new();
    private int nextId;
    private readonly Stack<int> freeStateSlots = new();
    private int stateSlotCount;
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
    public int CreateInstance(EmitterSpec spec, LumenEmitterTransform initialTransform, int mapIndex, bool realTime, uint emissionSeed)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.BillboardMode == LumenBillboardMode.Mesh)
            throw new NotSupportedException("L2 GPU particles do not support Mesh billboards.");
        var scheduler = new EmissionScheduler(spec, emissionSeed);
        var curves = new GpuCurveSample[64];
        GpuCurveAtlasPacker.Pack(spec.SizeOverLifetime, spec.ColorOverLifetime, curves);
        int id = checked(nextId++);
        EmitterSlotRange range = allocator.Allocate(spec.MaxParticles);
        instances.Add(id, new Instance
        {
            Id = id, Spec = spec, Slots = range, Scheduler = scheduler,
            StateSlot = freeStateSlots.Count != 0 ? freeStateSlots.Pop() : stateSlotCount++,
            Curves = curves, Transform = initialTransform, MapIndex = mapIndex, RealTime = realTime
        });
        lock (lifecycleGate)
        {
            System.Threading.Volatile.Write(ref idleRequested, false);
            idleStartedAt = null;
        }
        return id;
    }

    public void BindNodeInstances(int[] ids)
    {
        foreach (int id in ids)
        {
            if (id < 0) continue;
            Instance source = instances[id];
            source.Targets = new int[source.Spec.SubEmitters.Length];
            for (int i = 0; i < source.Targets.Length; i++)
            {
                int target = ids[source.Spec.SubEmitters[i].TargetNodeIndex];
                source.Targets[i] = target;
                instances[target].Sources.Add(id);
                instances[target].GpuBurstCarry = true;
            }
        }
    }

    // Requires: main thread. Unknown ids are ignored.
    // Ensures: the draw derives the 2D ground anchor from the uploaded World (lumenNodeGroundXZ); world-space particles
    //          record it at birth. Capture uploads the depth mask in ShapePosition.w (on) and ShapeScale.w (reference Z).
    internal void SetInstanceDisplayState(int instanceId, bool isVertical3D, float? depthReferenceZ)
    {
        if (!instances.TryGetValue(instanceId, out Instance? instance)) return;
        instance.IsVertical3D = isVertical3D;
        instance.DepthReferenceZ = depthReferenceZ;
    }

    // Requires: main thread, id from this engine; intensityMultiplier/rateMultiplier finite >= 0.
    // Ensures: next capture sees transform/emission/intensity/rate changes.
    public void UpdateInstance(int instanceId, in LumenEmitterTransform transform, bool emitting, float intensityMultiplier, float rateMultiplier)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!instances.TryGetValue(instanceId, out Instance? instance)) return;
        instance.Transform = transform;
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
        instance.NodeTime = 0f;
        instance.ResetVersion++;
        instance.Emitting = true;
        instance.Activated = true;
        instance.FrameDelta = null;
    }

    internal void SetInstanceFrameDelta(int instanceId, float frameDelta) => instances[instanceId].FrameDelta = frameDelta;

    public bool IsInstanceAlive(int instanceId) => !disposed && instances.ContainsKey(instanceId);

    public void EmitBurst(int instanceId, int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!instances.TryGetValue(instanceId, out Instance? instance) || instance.State != InstanceState.Emitting) return;
        if (instance.GpuBurstCarry)
            instance.ExplicitBursts.Add(new((uint)count, (uint)MathF.Floor(RimLumenRenderHost.ParticleDensityScale * 65536f), instance.ResetVersion, 0));
        else
        {
            int accepted = instance.Scheduler.AcceptBurst(count, RimLumenRenderHost.ParticleDensityScale);
            instance.PendingBurst = (int)Math.Min((long)instance.PendingBurst + accepted, instance.Slots.Capacity);
        }
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
        captureMaps.Clear(); captureLinks.Clear(); captureBursts.Clear(); rowByInstance.Clear(); mapOffsets.Clear();
        int cellCount = 0;
        ParticleEnvironment.BeginFrame(context.FrameIndex, context.CurrentMapIndex);
        int eventCapacity = 0;
        // Player-facing density setting; the default 1.0 leaves the emission budget untouched.
        float density = RimLumenRenderHost.ParticleDensityScale;
        foreach (Instance instance in instances.Values)
        {
            float hostDt = instance.RealTime && context.UnscaledDeltaTime > 0f ? context.UnscaledDeltaTime : context.DeltaTime;
            float scheduleDt = instance.FrameDelta ?? hostDt;
            instance.FrameDelta = null;
            instance.Activated |= instance.Emitting;
            bool drainedBeforeFrame = instance.State == InstanceState.Draining && instance.DrainRemaining <= 0;
            if (instance.State == InstanceState.Draining)
            {
                instance.DrainRemaining -= hostDt;
                // A target can receive new particles until all sources finish; then it gets a full final lifetime.
                if (instance.PendingBurst > 0 || instance.ExplicitBursts.Count != 0 || instance.Sources.Count != 0)
                    instance.DrainRemaining = instance.Spec.StartLifetimeMax;
            }
            if (instance.State == InstanceState.Remove ||
                (instance.State == InstanceState.Draining && (instance.Targets.Length == 0 ? instance.DrainRemaining <= 0 : drainedBeforeFrame)))
            {
                clears.Add(instance.Slots);
                removed.Add(instance.Id);
                allocator.Free(instance.Slots);
                continue;
            }
            EmitterSpec spec = instance.Spec;
            if (instance.Activated) instance.NodeTime += scheduleDt;
            double requested = instance.PendingBurst;
            if (instance.State == InstanceState.Emitting && instance.Activated && scheduleDt > 0f)
                requested += instance.Scheduler.Advance(scheduleDt, instance.Emitting, density, instance.RateMultiplier);
            int budget = (int)Math.Min(instance.Slots.Capacity, requested);
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
            Matrix4x4 inverse = default;
            bool worldInteraction = spec.WorldSpaceSimulation || Matrix4x4.Invert(instance.Transform.World, out inverse);
            if (worldInteraction)
                foreach (ParticleEnvironmentSource source in ParticleEnvironment.ForMap(instance.MapIndex, spec.EnvironmentChannels).Span)
                {
                    fields.Add(new GpuForceField
                    {
                        CenterOrAxisAndKind = new(source.Center, source.Channel == LumenEnvironmentChannel.Explosion ? 5f : 6f),
                        ParametersAndRadius = new(source.WaveRadius, source.Thickness, 0f, source.Radius),
                        StrengthAndFrequency = new(source.Strength * spec.EnvironmentForceScale, 0f, 0f, 0f)
                    });
                }
            Vector4 mapInfo = Vector4.Zero;
            ParticleMapGrid? grid = worldInteraction ? ParticleCollisionFields.Capture(instance.MapIndex, spec.Collision) : null;
            if (grid is not null)
            {
                if (!mapOffsets.TryGetValue(grid.MapId, out int cellOffset))
                {
                    cellOffset = cellCount;
                    mapOffsets.Add(grid.MapId, cellOffset);
                    captureMaps.Add(grid);
                    cellCount += grid.Cells.Length;
                }
                mapInfo = new(cellOffset, grid.Width, grid.Height, spec.Collision.KillUnderRoof ? 1f : 0f);
            }
            int ownEventCapacity = instance.Targets.Length == 0 ? 0 : checked(2 * spec.MaxParticles);
            int explicitOffset = captureBursts.Count;
            captureBursts.AddRange(instance.ExplicitBursts);
            int explicitCount = instance.ExplicitBursts.Count;
            instance.ExplicitBursts.Clear();
            rowByInstance.Add(instance.Id, row);
            var parameters = new GpuEmitterParams
            {
                // Slang's RowMajor SSBO uses matrix * column vector; scene transforms use row vectors.
                World = Matrix4x4.Transpose(instance.Transform.World),
                Slots = new Vector4(instance.Slots.Offset, instance.Slots.Capacity, budget, 0),
                Shape = new Vector4((float)spec.Shape, spec.ShapeExtents.X, spec.ShapeExtents.Y, spec.ShapeExtents.Z),
                Ranges = new Vector4(spec.StartSpeedMin, spec.StartSpeedMax, spec.StartLifetimeMin, spec.StartLifetimeMax),
                SizeCone = new Vector4(spec.StartSizeMin, spec.StartSizeMax, spec.ConeAngleDegrees * (MathF.PI / 180), spec.WorldSpaceSimulation ? 1 : 0),
                StartColor = spec.StartColor,
                Flipbook = new Vector4(spec.FlipbookTilesX, spec.FlipbookTilesY, spec.FlipbookCycles, (float)spec.BillboardMode),
                Appearance = new Vector4(spec.VelocityStretchFactor, spec.LengthScale, spec.HdrIntensity, ParticleAppearanceMath.SizeScale(instance.Transform.World)),
                Tables = new Vector4(row, fieldOffset, fields.Count - fieldOffset, instance.Id & 0x00ffffff),
                State = new Vector4(hostDt, spec.SoftParticleFadeDistance, 0, instance.Intensity),
                ShapePosition = new Vector4(spec.ShapePosition, instance.DepthReferenceZ.HasValue ? 1f : 0f),
                ShapeRotation = new Vector4(spec.ShapeRotation.X, spec.ShapeRotation.Y, spec.ShapeRotation.Z, spec.ShapeRotation.W),
                ShapeScale = new Vector4(spec.ShapeScale, instance.DepthReferenceZ.GetValueOrDefault()),
                ShapeOptions = new Vector4(spec.RadiusThickness, spec.ArcDegrees * (MathF.PI / 180f), spec.ArcSpread, spec.RadiusSpread),
                ShapeMix = new Vector4(spec.RandomDirectionAmount, spec.SphericalDirectionAmount, spec.RandomPositionAmount, spec.ConeLength),
                BoxThicknessDonut = new Vector4(spec.BoxThickness, spec.DonutRadius),
                WorldRotation = new Vector4(instance.Transform.Rotation.X, instance.Transform.Rotation.Y, instance.Transform.Rotation.Z, instance.Transform.Rotation.W),
                WorldInverse = Matrix4x4.Transpose(inverse),
                WorldPhysics = new(instance.NodeTime, worldInteraction ? 1f : 0f, 0f, 0f),
                Collision = new((float)spec.Collision.Mode, spec.Collision.Bounce, spec.Collision.Dampen, spec.Collision.LifetimeLoss),
                Map = mapInfo,
                Events = new(eventCapacity, ownEventCapacity, instance.GpuBurstCarry ? 1f : 0f, 0f),
                DensityQ = (uint)MathF.Floor(density * 65536f), ResetVersion = instance.ResetVersion,
                ExplicitOffset = (uint)explicitOffset, ExplicitCount = (uint)explicitCount,
                StateSlot = (uint)instance.StateSlot, EmitEvents = instance.Targets.Length == 0 ? 0u : 1u
            };
            rows.Add(new ParticleEmitterFrame(instance.Id, parameters, instance.Curves, spec.TextureKey, spec.BlendMode,
                instance.MapIndex, instance.IsVertical3D, spec.KillBelowFloor)
                { MaterialTint = spec.MaterialTint, ColorSpace = spec.ColorSpace });
            eventCapacity += ownEventCapacity;
        }
        // Links are grouped by target so one GPU invocation owns its carry and consumes every source event.
        for (int row = 0; row < rows.Count; row++)
        {
            Instance target = instances[rows[row].InstanceId];
            GpuEmitterParams parameters = rows[row].Parameters;
            parameters.SourceLinkOffset = (uint)captureLinks.Count;
            foreach (int sourceId in target.Sources)
            {
                if (!rowByInstance.TryGetValue(sourceId, out int sourceRow)) continue;
                Instance source = instances[sourceId];
                for (int i = 0; i < source.Targets.Length; i++)
                    if (source.Targets[i] == target.Id)
                    {
                        SubEmitterSpec sub = source.Spec.SubEmitters[i];
                        captureLinks.Add(new() { Source = (uint)sourceRow, Trigger = (uint)sub.Trigger, Count = (uint)sub.Count,
                            Inherit = (uint)sub.Inherit, Probability = sub.Probability });
                    }
            }
            parameters.SourceLinkCount = (uint)captureLinks.Count - parameters.SourceLinkOffset;
            rows[row] = rows[row] with { Parameters = parameters };
        }
        foreach (int id in removed)
        {
            foreach (int target in instances[id].Targets)
                if (instances.TryGetValue(target, out Instance? child)) child.Sources.Remove(id);
            // A reused index is reinitialised by the new instance's first-frame flag (State.z) on the GPU.
            freeStateSlots.Push(instances[id].StateSlot);
            instances.Remove(id);
        }
        captured = new ParticleFrame(context, rows.ToArray(), fields.ToArray(), clears.ToArray())
        { Maps = captureMaps.ToArray(), CellCount = cellCount, SubLinks = captureLinks.ToArray(), ExplicitBursts = captureBursts.ToArray(), EventCapacity = eventCapacity, StateSlotCount = stateSlotCount };
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
    bool IsVertical3D = false, bool KillBelowFloor = false)
{
    internal Vector4 MaterialTint { get; init; } = Vector4.One;
    internal LumenColorSpace ColorSpace { get; init; }

    // -1 on either side means unscoped (no map known), which keeps offline frames drawing everything.
    internal bool IsVisibleOnMap(int currentMapIndex) =>
        MapIndex < 0 || currentMapIndex < 0 || MapIndex == currentMapIndex;
}

internal sealed record ParticleFrame(LumenCaptureContext Context, ParticleEmitterFrame[] Emitters,
    GpuForceField[] Fields, EmitterSlotRange[] Clears)
{
    internal ParticleMapGrid[] Maps { get; init; } = Array.Empty<ParticleMapGrid>();
    internal int CellCount { get; init; }
    internal GpuSubLink[] SubLinks { get; init; } = Array.Empty<GpuSubLink>();
    internal GpuExplicitBurst[] ExplicitBursts { get; init; } = Array.Empty<GpuExplicitBurst>();
    internal int EventCapacity { get; init; }
    // Persistent BurstState entries required: one past the highest state index in use.
    internal int StateSlotCount { get; init; }
}
