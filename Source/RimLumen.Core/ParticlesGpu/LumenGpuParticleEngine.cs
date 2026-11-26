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
        public bool NewBurstPending;
        public float UnspawnedBurstAge;
        public float LastHostDt;
        public InstanceState State;
        public float NodeTime;
        public int[] Targets = Array.Empty<int>();
        public readonly HashSet<int> Sources = new();
        public float? FrameDelta;
        public bool GpuBurstCarry;
        public uint ResetVersion;
        public readonly List<GpuExplicitBurst> ExplicitBursts = new();
        public float MaxObservedSizeScale = 1f;
        public float MaxInheritedSizeFactor = 1f;
        public TrailState? Trail;
    }

    private sealed class TrailState
    {
        public required EmitterSlotRange HeaderSlots;
        public required EmitterSlotRange PointSlots;
        public required EmitterSlotRange MapSlots;
        // 128 samples: [0..63] = WidthOverTrail / ColorOverTrail; [64..127] = Constant(1) / ColorOverLifetime.
        public required GpuCurveSample[] Curves;
        public required uint RandomSeed;
    }

    private readonly record struct PendingClear(EmitterSlotRange Range, ulong? EnqueuedFrame = null);
    private readonly record struct InFlightGpuDt(int InstanceId, float Dt, ulong FrameIndex);

    private readonly int capacity;
    private readonly int trailHeaderCapacity;
    private readonly int trailPointCapacity;
    private readonly int trailMapCapacity;
    private readonly EmitterSlotAllocator allocator;
    private EmitterSlotAllocator? trailHeaderAllocator;
    private EmitterSlotAllocator? trailPointAllocator;
    private EmitterSlotAllocator? trailMapAllocator;
    private readonly Dictionary<int, Instance> instances = new();
    private readonly List<ParticleEmitterFrame> captureRows = new();
    private readonly List<ParticleTrailEmitterFrame> captureTrailRows = new();
    private readonly List<EmitterSlotRange> captureClears = new();
    private readonly List<EmitterSlotRange> captureTrailHeaderClears = new();
    private readonly List<EmitterSlotRange> captureTrailMapClears = new();
    private readonly List<PendingClear> pendingClears = new();
    private readonly List<PendingClear> pendingTrailHeaderClears = new();
    private readonly List<PendingClear> pendingTrailMapClears = new();
    private readonly Dictionary<int, float> pendingGpuDts = new();
    private readonly List<InFlightGpuDt> inFlightGpuDts = new();
    private readonly List<int> staleGpuDtIds = new();
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
        : this(capacity, ParticleTrailMath.DefaultHeaderCapacity, ParticleTrailMath.DefaultPointCapacity, ParticleTrailMath.DefaultMapCapacity) { }

    internal LumenGpuParticleEngine(int capacity, int trailHeaderCapacity, int trailPointCapacity, int trailMapCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (trailHeaderCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(trailHeaderCapacity));
        if (trailPointCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(trailPointCapacity));
        if (trailMapCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(trailMapCapacity));
        this.capacity = capacity;
        this.trailHeaderCapacity = trailHeaderCapacity;
        this.trailPointCapacity = trailPointCapacity;
        this.trailMapCapacity = trailMapCapacity;
        allocator = new EmitterSlotAllocator(capacity);
    }

    public int Capacity => capacity;
    public string Name => "RimLumen.GpuParticles";
    public bool HasActiveWork
    {
        get
        {
            if (disposed) return false;
            if (instances.Count != 0) return true;
            lock (lifecycleGate)
                return (particles is not null && HasUnqueuedClears(pendingClears))
                    || (trailHeaders is not null && (HasUnqueuedClears(pendingTrailHeaderClears) || HasUnqueuedClears(pendingTrailMapClears)));
        }
    }

    // Requires: main thread; spec passed LumenAssetRules validation.
    // Ensures: reserves slots without allocating GPU resources while idle.
    public int CreateInstance(EmitterSpec spec, LumenEmitterTransform initialTransform, int mapIndex, bool realTime, uint emissionSeed)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(spec);
        var scheduler = new EmissionScheduler(spec, emissionSeed);
        var curves = new GpuCurveSample[64];
        GpuCurveAtlasPacker.Pack(spec.SizeOverLifetime, spec.ColorOverLifetime, curves);
        int id = checked(nextId++);
        EmitterSlotRange range = allocator.Allocate(spec.MaxParticles);
        TrailState? trailState = null;
        if (spec.Trails is { } trailSpec)
        {
            trailHeaderAllocator ??= new EmitterSlotAllocator(trailHeaderCapacity);
            trailPointAllocator ??= new EmitterSlotAllocator(trailPointCapacity);
            trailMapAllocator ??= new EmitterSlotAllocator(trailMapCapacity);
            EmitterSlotRange? headers = null, points = null, maps = null;
            try
            {
                headers = trailHeaderAllocator.Allocate(ParticleTrailMath.ComputeHeaderCapacity(spec));
                points = trailPointAllocator.Allocate(ParticleTrailMath.ComputePointCapacity(spec));
                maps = trailMapAllocator.Allocate(spec.MaxParticles);
            }
            catch
            {
                if (maps is { } m) trailMapAllocator.Free(m);
                if (points is { } p) trailPointAllocator.Free(p);
                if (headers is { } h) trailHeaderAllocator.Free(h);
                allocator.Free(range);
                throw;
            }
            var trailCurves = new GpuCurveSample[GpuCurveAtlasPacker.SamplesPerRow * 2];
            GpuCurveAtlasPacker.Pack(trailSpec.WidthOverTrail, trailSpec.ColorOverTrail,
                trailCurves.AsSpan(0, GpuCurveAtlasPacker.SamplesPerRow));
            GpuCurveAtlasPacker.Pack(ScalarCurve.Constant(1f), trailSpec.ColorOverLifetime,
                trailCurves.AsSpan(GpuCurveAtlasPacker.SamplesPerRow, GpuCurveAtlasPacker.SamplesPerRow));
            uint tSeed = ParticleTrailMath.Hash(emissionSeed ^ unchecked((uint)id * 0x9E3779B9u) ^ 0x517CC1B7u);
            trailState = new TrailState
            {
                HeaderSlots = headers.Value,
                PointSlots = points.Value,
                MapSlots = maps.Value,
                Curves = trailCurves,
                RandomSeed = tSeed == 0u ? 1u : tSeed,
            };
        }
        instances.Add(id, new Instance
        {
            Id = id, Spec = spec, Slots = range, Scheduler = scheduler,
            StateSlot = freeStateSlots.Count != 0 ? freeStateSlots.Pop() : stateSlotCount++,
            Curves = curves, Transform = initialTransform, MapIndex = mapIndex, RealTime = realTime,
            MaxObservedSizeScale = ParticleAppearanceMath.SizeScale(initialTransform.World),
            Trail = trailState,
        });
        lock (lifecycleGate)
        {
            SubtractPendingClears(pendingClears, range);
            if (trailState is not null)
            {
                SubtractPendingClears(pendingTrailHeaderClears, trailState.HeaderSlots);
                SubtractPendingClears(pendingTrailMapClears, trailState.MapSlots);
            }
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
    public void SetInstanceDisplayState(int instanceId, bool isVertical3D, float? depthReferenceZ)
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
        instance.MaxObservedSizeScale = MathF.Max(instance.MaxObservedSizeScale, ParticleAppearanceMath.SizeScale(transform.World));
        instance.Intensity = intensityMultiplier;
        instance.RateMultiplier = rateMultiplier;
        if (instance.State == InstanceState.Emitting)
        {
            instance.Emitting = emitting;
            instance.Activated |= emitting;
        }
    }

    private float PeakDisplayParticleSize(Instance instance)
        => instance.Spec.StartSizeMax * ParticleTrailMath.PeakCurveSize(instance.Curves)
            * instance.MaxObservedSizeScale * LiveInheritedSizeFactor(instance);

    private float LiveInheritedSizeFactor(Instance instance)
    {
        float factor = instance.MaxInheritedSizeFactor;
        foreach (int sourceId in instance.Sources)
        {
            if (!instances.TryGetValue(sourceId, out Instance? source)) continue;
            for (int i = 0; i < source.Targets.Length; i++)
                if (source.Targets[i] == instance.Id && (source.Spec.SubEmitters[i].Inherit & LumenSubEmitterInherit.Size) != 0)
                    factor = MathF.Max(factor, PeakDisplayParticleSize(source));
        }
        return factor;
    }

    private double DrainDuration(Instance instance)
    {
        if (instance.Spec.Trails is not { DieWithParticles: false } trails)
            return instance.Spec.StartLifetimeMax;
        float scaleFactor = trails.SizeAffectsLifetime
            ? instance.MaxObservedSizeScale * LiveInheritedSizeFactor(instance)
            : 1f;
        return instance.Spec.StartLifetimeMax + ParticleTrailMath.MaxTrailLifetimeSeconds(
            instance.Spec, instance.Curves, scaleFactor);
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
            instance.DrainRemaining = DrainDuration(instance);
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

    public void SetInstanceFrameDelta(int instanceId, float frameDelta) => instances[instanceId].FrameDelta = frameDelta;

    public bool IsInstanceAlive(int instanceId) => !disposed && instances.ContainsKey(instanceId);

    public void EmitBurst(int instanceId, int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!instances.TryGetValue(instanceId, out Instance? instance) || instance.State != InstanceState.Emitting) return;
        if (instance.GpuBurstCarry)
        {
            instance.ExplicitBursts.Add(new((uint)count, (uint)MathF.Floor(RimLumenRenderHost.ParticleDensityScale * 65536f), instance.ResetVersion, 0));
            instance.NewBurstPending = true;
            instance.UnspawnedBurstAge = 0f;
        }
        else
        {
            int accepted = instance.Scheduler.AcceptBurst(count, RimLumenRenderHost.ParticleDensityScale);
            if (accepted > 0)
            {
                instance.PendingBurst = (int)Math.Min((long)instance.PendingBurst + accepted, instance.Slots.Capacity);
                instance.NewBurstPending = true;
                instance.UnspawnedBurstAge = 0f;
            }
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
        bool rendersMap = context.RendersMap;
        if (!rendersMap && instances.Count == 0)
        {
            captured = null;
            System.Threading.Volatile.Write(ref idleRequested, true);
            return;
        }
        var rows = captureRows;
        var trailRows = captureTrailRows;
        var clears = captureClears;
        var trailHeaderClears = captureTrailHeaderClears;
        var trailMapClears = captureTrailMapClears;
        var removed = captureRemoved;
        var fields = captureFields;
        rows.Clear(); trailRows.Clear(); clears.Clear(); trailHeaderClears.Clear(); trailMapClears.Clear(); removed.Clear(); fields.Clear();
        captureMaps.Clear(); captureLinks.Clear(); captureBursts.Clear(); rowByInstance.Clear(); mapOffsets.Clear();
        int cellCount = 0;
        ParticleEnvironment.BeginFrame(in context);
        int eventCapacity = 0;
        // Player-facing density setting; the default 1.0 leaves the emission budget untouched.
        float density = RimLumenRenderHost.ParticleDensityScale;
        foreach (Instance instance in instances.Values)
        {
            float hostDt = instance.RealTime && context.UnscaledDeltaTime > 0f ? context.UnscaledDeltaTime : context.DeltaTime;
            instance.LastHostDt = hostDt;
            float scheduleDt = instance.FrameDelta ?? hostDt;
            instance.FrameDelta = null;
            instance.Activated |= instance.Emitting;
            bool drainedBeforeFrame = instance.State == InstanceState.Draining && instance.DrainRemaining <= 0;
            if (instance.State == InstanceState.Draining)
            {
                instance.DrainRemaining -= hostDt;
                // A target can receive new particles until all sources finish; then it gets a full final lifetime.
                if (instance.NewBurstPending || instance.Sources.Count != 0)
                    instance.DrainRemaining = DrainDuration(instance);
            }
            instance.NewBurstPending = false;
            if (instance.State == InstanceState.Remove ||
                (instance.State == InstanceState.Draining && (instance.Targets.Length == 0 ? instance.DrainRemaining <= 0 : drainedBeforeFrame)))
            {
                clears.Add(instance.Slots);
                if (instance.Trail is { } deadTrail)
                {
                    trailHeaderClears.Add(deadTrail.HeaderSlots);
                    trailMapClears.Add(deadTrail.MapSlots);
                    trailHeaderAllocator!.Free(deadTrail.HeaderSlots);
                    trailPointAllocator!.Free(deadTrail.PointSlots);
                    trailMapAllocator!.Free(deadTrail.MapSlots);
                }
                removed.Add(instance.Id);
                allocator.Free(instance.Slots);
                continue;
            }
            EmitterSpec spec = instance.Spec;
            if (instance.Activated) instance.NodeTime += scheduleDt;
            double scheduled = 0.0;
            if (instance.State == InstanceState.Emitting && instance.Activated && scheduleDt > 0f)
                scheduled = instance.Scheduler.Advance(scheduleDt, instance.Emitting, density, instance.RateMultiplier);
            if (!rendersMap)
            {
                if (scheduled > 0.0)
                {
                    instance.PendingBurst = (int)Math.Min(instance.Slots.Capacity, instance.PendingBurst + scheduled);
                    instance.UnspawnedBurstAge = 0f;
                }
                else if (instance.PendingBurst > 0 || instance.ExplicitBursts.Count != 0)
                {
                    instance.UnspawnedBurstAge += hostDt;
                    if (instance.UnspawnedBurstAge >= spec.StartLifetimeMax)
                    {
                        instance.PendingBurst = 0;
                        instance.ExplicitBursts.Clear();
                        instance.UnspawnedBurstAge = 0f;
                    }
                }
                continue;
            }
            int budget = (int)Math.Min(instance.Slots.Capacity, instance.PendingBurst + scheduled);
            instance.PendingBurst = 0;
            instance.UnspawnedBurstAge = 0f;
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
            if (worldInteraction && spec.EnvironmentChannels != LumenEnvironmentChannel.None && spec.EnvironmentForceScale > 0f)
                foreach (LumenEnvironmentForce source in LumenHostRegistry.GetEnvironmentForces(instance.MapIndex, spec.EnvironmentChannels))
                {
                    float kind = source.Shape switch
                    {
                        LumenEnvironmentForceShape.SphericalWave => 5f,
                        LumenEnvironmentForceShape.HorizontalRadial => 6f,
                        _ => 7f,
                    };
                    fields.Add(new GpuForceField
                    {
                        CenterOrAxisAndKind = new(source.CenterWorld, kind),
                        ParametersAndRadius = new(source.WaveRadius, source.ShellHalfThickness, 0f, source.Radius),
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
            if (spec.Trails is { } trailSpec && instance.Trail is { } trail)
            {
                uint trailFlags =
                    (trailSpec.DieWithParticles ? 1u : 0u) |
                    ((spec.WorldSpaceSimulation || trailSpec.WorldSpace) ? 2u : 0u) |
                    (trailSpec.SizeAffectsWidth ? 4u : 0u) |
                    (trailSpec.SizeAffectsLifetime ? 8u : 0u) |
                    (trailSpec.InheritParticleColor ? 16u : 0u) |
                    (instance.IsVertical3D ? 32u : 0u) |
                    (spec.WorldSpaceSimulation ? 64u : 0u);
                var trailParameters = new GpuTrailEmitterParams
                {
                    World = Matrix4x4.Transpose(instance.Transform.World),
                    Ranges = new Vector4(trail.HeaderSlots.Offset, trail.HeaderSlots.Capacity, trail.PointSlots.Offset, instance.Slots.Offset),
                    PointsAndCurve = new Vector4(trailSpec.MaxPoints, 0f, trailSpec.Ratio, trailSpec.Lifetime),
                    Params = new Vector4(trailSpec.MinVertexDistance, trailSpec.Width, trailSpec.SoftParticleFadeDistance,
                        ParticleAppearanceMath.SizeScale(instance.Transform.World)),
                    Appearance = new Vector4((float)trailSpec.TextureMode, (float)trailSpec.Alignment, trailSpec.HdrIntensity, instance.Intensity),
                    DepthAndParticle = new Vector4(instance.DepthReferenceZ.HasValue ? 1f : 0f,
                        instance.DepthReferenceZ.GetValueOrDefault(), instance.Slots.Capacity, row),
                    MapBase = (uint)trail.MapSlots.Offset,
                    Flags = trailFlags,
                    BankCount = (uint)ParticleTrailMath.ComputeBankCount(trailSpec),
                    RandomSeed = trail.RandomSeed,
                };
                trailRows.Add(new ParticleTrailEmitterFrame(instance.Id, row, trailParameters, trail.Curves,
                    trailSpec.TextureKey, trailSpec.BlendMode, instance.MapIndex, trailSpec.MaxPoints, instance.IsVertical3D)
                    { MaterialTint = trailSpec.MaterialTint, ColorSpace = trailSpec.ColorSpace });
            }
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
            Instance source = instances[id];
            for (int i = 0; i < source.Targets.Length; i++)
                if ((source.Spec.SubEmitters[i].Inherit & LumenSubEmitterInherit.Size) != 0
                    && instances.TryGetValue(source.Targets[i], out Instance? child))
                    child.MaxInheritedSizeFactor = MathF.Max(child.MaxInheritedSizeFactor, PeakDisplayParticleSize(source));
        }
        foreach (int id in removed)
        {
            foreach (int target in instances[id].Targets)
                if (instances.TryGetValue(target, out Instance? child)) child.Sources.Remove(id);
            // A reused index is reinitialised by the new instance's first-frame flag (State.z) on the GPU.
            freeStateSlots.Push(instances[id].StateSlot);
            instances.Remove(id);
        }
        lock (lifecycleGate)
        {
            foreach (int id in removed)
            {
                pendingGpuDts.Remove(id);
                inFlightGpuDts.RemoveAll(step => step.InstanceId == id);
            }
            if (pendingGpuDts.Count != 0)
            {
                staleGpuDtIds.Clear();
                foreach (int id in pendingGpuDts.Keys)
                    if (!instances.ContainsKey(id)) staleGpuDtIds.Add(id);
                foreach (int id in staleGpuDtIds) pendingGpuDts.Remove(id);
            }
            foreach (EmitterSlotRange range in clears) AddPendingClear(pendingClears, range);
            foreach (EmitterSlotRange range in trailHeaderClears) AddPendingClear(pendingTrailHeaderClears, range);
            foreach (EmitterSlotRange range in trailMapClears) AddPendingClear(pendingTrailMapClears, range);
            foreach (Instance live in instances.Values)
                pendingGpuDts[live.Id] = (pendingGpuDts.TryGetValue(live.Id, out float prior) ? prior : 0f) + live.LastHostDt;
            if (!rendersMap)
            {
                captured = null;
                System.Threading.Volatile.Write(ref idleRequested, instances.Count == 0);
                return;
            }
            for (int row = 0; row < rows.Count; row++)
            {
                int id = rows[row].InstanceId;
                GpuEmitterParams parameters = rows[row].Parameters;
                parameters.State.X = pendingGpuDts[id];
                rows[row] = rows[row] with { Parameters = parameters };
            }
            CollectUnqueuedClears(pendingClears, clears);
            CollectUnqueuedClears(pendingTrailHeaderClears, trailHeaderClears);
            CollectUnqueuedClears(pendingTrailMapClears, trailMapClears);
        }
        captured = new ParticleFrame(
            context,
            rows.Count == 0 ? Array.Empty<ParticleEmitterFrame>() : rows.ToArray(),
            fields.Count == 0 ? Array.Empty<GpuForceField>() : fields.ToArray(),
            clears.Count == 0 ? Array.Empty<EmitterSlotRange>() : clears.ToArray())
        {
            Maps = captureMaps.Count == 0 ? Array.Empty<ParticleMapGrid>() : captureMaps.ToArray(),
            CellCount = cellCount,
            SubLinks = captureLinks.Count == 0 ? Array.Empty<GpuSubLink>() : captureLinks.ToArray(),
            ExplicitBursts = captureBursts.Count == 0 ? Array.Empty<GpuExplicitBurst>() : captureBursts.ToArray(),
            EventCapacity = eventCapacity,
            StateSlotCount = stateSlotCount,
            TrailEmitters = trailRows.Count == 0 ? Array.Empty<ParticleTrailEmitterFrame>() : trailRows.ToArray(),
            TrailHeaderClears = trailHeaderClears.Count == 0 ? Array.Empty<EmitterSlotRange>() : trailHeaderClears.ToArray(),
            TrailMapClears = trailMapClears.Count == 0 ? Array.Empty<EmitterSlotRange>() : trailMapClears.ToArray(),
        };
        System.Threading.Volatile.Write(ref idleRequested, rows.Count == 0 && trailRows.Count == 0);
    }

    private static bool HasUnqueuedClears(List<PendingClear> list)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i].EnqueuedFrame is null) return true;
        return false;
    }

    private static void CollectUnqueuedClears(List<PendingClear> list, List<EmitterSlotRange> destination)
    {
        destination.Clear();
        foreach (PendingClear pending in list)
            if (pending.EnqueuedFrame is null) destination.Add(pending.Range);
    }

    private void ClearPendingGpuState()
    {
        pendingClears.Clear();
        pendingTrailHeaderClears.Clear();
        pendingTrailMapClears.Clear();
        pendingGpuDts.Clear();
        inFlightGpuDts.Clear();
    }

    private static void AddPendingClear(List<PendingClear> list, EmitterSlotRange range)
    {
        int start = range.Offset, end = start + range.Capacity;
        int insertAt = 0;
        for (int i = 0; i < list.Count;)
        {
            PendingClear existing = list[i];
            int exStart = existing.Range.Offset, exEnd = exStart + existing.Range.Capacity;
            if (existing.EnqueuedFrame is null && exEnd >= start && exStart <= end)
            {
                start = Math.Min(start, exStart);
                end = Math.Max(end, exEnd);
                list.RemoveAt(i);
                continue;
            }
            if (exStart < start) insertAt = i + 1;
            i++;
        }
        list.Insert(insertAt, new PendingClear(new EmitterSlotRange(start, end - start)));
    }

    private static void SubtractPendingClears(List<PendingClear> list, EmitterSlotRange allocated)
    {
        int cutStart = allocated.Offset, cutEnd = cutStart + allocated.Capacity;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            PendingClear existing = list[i];
            int exStart = existing.Range.Offset, exEnd = exStart + existing.Range.Capacity;
            if (exEnd <= cutStart || exStart >= cutEnd) continue;
            list.RemoveAt(i);
            if (exEnd > cutEnd)
                list.Insert(i, existing with { Range = new EmitterSlotRange(cutEnd, exEnd - cutEnd) });
            if (exStart < cutStart)
                list.Insert(i, existing with { Range = new EmitterSlotRange(exStart, cutStart - exStart) });
        }
    }

    private static void StampEnqueuedClears(List<PendingClear> list, ulong frameIndex)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i].EnqueuedFrame is null)
                list[i] = list[i] with { EnqueuedFrame = frameIndex };
    }

    private static void RestoreEnqueuedClears(List<PendingClear> list, ulong frameIndex)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].EnqueuedFrame != frameIndex) continue;
            EmitterSlotRange range = list[i].Range;
            list.RemoveAt(i);
            AddPendingClear(list, range);
        }
    }

    internal void MarkFrameEnqueued(ulong frameIndex)
    {
        lock (lifecycleGate)
        {
            StampEnqueuedClears(pendingClears, frameIndex);
            StampEnqueuedClears(pendingTrailHeaderClears, frameIndex);
            StampEnqueuedClears(pendingTrailMapClears, frameIndex);
            foreach (KeyValuePair<int, float> entry in pendingGpuDts)
                if (entry.Value > 0f)
                    inFlightGpuDts.Add(new InFlightGpuDt(entry.Key, entry.Value, frameIndex));
            pendingGpuDts.Clear();
        }
    }

    internal void CompleteRecordedFrame(ulong frameIndex)
    {
        lock (lifecycleGate)
        {
            pendingClears.RemoveAll(clear => clear.EnqueuedFrame == frameIndex);
            pendingTrailHeaderClears.RemoveAll(clear => clear.EnqueuedFrame == frameIndex);
            pendingTrailMapClears.RemoveAll(clear => clear.EnqueuedFrame == frameIndex);
            inFlightGpuDts.RemoveAll(step => step.FrameIndex == frameIndex);
        }
    }

    internal void RestoreDroppedFrame(ulong frameIndex)
    {
        lock (lifecycleGate)
        {
            RestoreEnqueuedClears(pendingClears, frameIndex);
            RestoreEnqueuedClears(pendingTrailHeaderClears, frameIndex);
            RestoreEnqueuedClears(pendingTrailMapClears, frameIndex);
            for (int i = inFlightGpuDts.Count - 1; i >= 0; i--)
            {
                InFlightGpuDt step = inFlightGpuDts[i];
                if (step.FrameIndex != frameIndex) continue;
                inFlightGpuDts.RemoveAt(i);
                pendingGpuDts[step.InstanceId] = (pendingGpuDts.TryGetValue(step.InstanceId, out float current) ? current : 0f) + step.Dt;
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        DisposeGpu();
        disposed = true;
        instances.Clear();
        lock (lifecycleGate) ClearPendingGpuState();
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
    internal ParticleTrailEmitterFrame[] TrailEmitters { get; init; } = Array.Empty<ParticleTrailEmitterFrame>();
    internal EmitterSlotRange[] TrailHeaderClears { get; init; } = Array.Empty<EmitterSlotRange>();
    internal EmitterSlotRange[] TrailMapClears { get; init; } = Array.Empty<EmitterSlotRange>();
}

internal readonly record struct ParticleTrailEmitterFrame(int InstanceId, int ParticleRow, GpuTrailEmitterParams Parameters,
    GpuCurveSample[] Curves, string? TextureKey, LumenBlendMode Blend, int MapIndex, int MaxPoints, bool IsVertical3D)
{
    internal Vector4 MaterialTint { get; init; } = Vector4.One;
    internal LumenColorSpace ColorSpace { get; init; }

    internal bool IsVisibleOnMap(int currentMapIndex) =>
        MapIndex < 0 || currentMapIndex < 0 || MapIndex == currentMapIndex;
}
