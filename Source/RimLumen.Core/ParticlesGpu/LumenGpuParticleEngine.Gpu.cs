using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Rendering;
using Waylith;
using Waylith.Rendering;
using Waylith.Rendering.Compute;

namespace RimLumen.Core.ParticlesGpu;

internal sealed partial class LumenGpuParticleEngine : ILumenHdrDrawAppender
{
    private readonly HashSet<string> warnedTextures = new(StringComparer.Ordinal);
    private readonly ParticleFrameQueue pendingFrames = new();
    private ParticleGpuDrawResources? drawResources;
    private GpuComputeBuffer? particles;
    private GpuComputeBuffer? deadBlocks;
    private GpuComputeBuffer? burstStates;
    private int burstStateEntries;
    // Replaced BurstState buffers in growth order. The render thread stamps the capture index of the frame that
    // copied past each one; it is disposed after the same reuse distance as a frame slot.
    private readonly List<SupersededBuffer> supersededBurstStates = new();
    // Render thread only: the BurstState buffer of the last recorded frame, the source of a growth copy.
    private GpuComputeBuffer? recordedBurstStates;

    private sealed class SupersededBuffer
    {
        public required GpuComputeBuffer Buffer;
        public ulong? CopiedAt;
    }
    // One prepared frame resource set per capture slot. Slot reuse waits 2*FramesInFlight+2 frames, so a
    // submission that may still reference a set has been retired before the slot is rewritten.
    private ParticleGpuFrame?[] frameSlots = Array.Empty<ParticleGpuFrame?>();
    private const double GpuIdleRetentionSeconds = 30;
    private bool idleRequested;
    private long? idleStartedAt;
    internal const string SimShaderPath = "Particles/ParticleSim.comp.glsl";
    internal const string TrailSimShaderPath = "Particles/ParticleTrailSim.comp.glsl";
    internal const int ComputePushConstantBytes = 16;

    internal static readonly ComputeBinding[] SimBindings =
    {
        new(0, ComputeBindingType.StorageBuffer),
        new(1, ComputeBindingType.StorageBuffer),
        new(2, ComputeBindingType.StorageBuffer),
        new(3, ComputeBindingType.StorageBuffer),
        new(4, ComputeBindingType.StorageBuffer),
        new(5, ComputeBindingType.StorageBuffer),
        new(6, ComputeBindingType.StorageBuffer),
        new(7, ComputeBindingType.StorageBuffer),
        new(8, ComputeBindingType.StorageBuffer),
        new(9, ComputeBindingType.StorageBuffer)
    };

    internal static readonly ComputeBinding[] TrailSimBindings =
    {
        new(0, ComputeBindingType.StorageBuffer),
        new(1, ComputeBindingType.StorageBuffer),
        new(2, ComputeBindingType.StorageBuffer),
        new(3, ComputeBindingType.StorageBuffer),
        new(4, ComputeBindingType.StorageBuffer),
        new(5, ComputeBindingType.StorageBuffer),
        new(6, ComputeBindingType.StorageBuffer)
    };

    private EngineApplication? application;
    private readonly object lifecycleGate = new();
    private VkDescriptorSetLayout computeLayout;
    private ComputeKernel? kernel;
    private GpuComputeBuffer? trailHeaders;
    private GpuComputeBuffer? trailPoints;
    private GpuComputeBuffer? particleToTrail;
    private VkDescriptorSetLayout trailComputeLayout;
    private ComputeKernel? trailKernel;
    private int[] emitterToTrail = Array.Empty<int>();
    private readonly List<EmitterSlotRange> initialTrailHeaderClears = new();
    private readonly List<EmitterSlotRange> initialTrailMapClears = new();
    private readonly HashSet<int> initializedInstances = new();
    private readonly HashSet<int> initializedTrailInstances = new();
    private readonly HashSet<int> liveInstanceIds = new();
    private readonly HashSet<int> liveTrailInstanceIds = new();

    // Requires: main thread; capture completed with active work.
    // Ensures: prepares this frame's GPU resources (device objects, emitter/curve buffers, frame slot) and
    //   queues the simulation without particle readback. Emitters-only: an idle engine touches no GPU state.
    public void EnqueuePreCompute(in LumenComputeContext context)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ParticleFrame snapshot = captured ?? throw new InvalidOperationException("Capture must precede enqueue.");
        if (snapshot.Context.FrameIndex != context.FrameIndex)
            throw new InvalidOperationException("Compute and capture frame indices differ.");
        captured = null;
        bool hasTrailWork = snapshot.TrailEmitters.Length != 0 || snapshot.TrailHeaderClears.Length != 0
            || snapshot.TrailMapClears.Length != 0;
        if (snapshot.Emitters.Length == 0 && snapshot.Clears.Length == 0 && !hasTrailWork) return;
        lock (lifecycleGate)
        {
            if (snapshot.Emitters.Length == 0 && !hasTrailWork && particles is null) return;
            if (application is null)
            {
                application = EngineApplication.Instance;
                if (application is not null)
                {
                    application.FrameRenderer.RenderThreadFrameStarting += ReleaseOnFrameStart;
                    application.FrameRenderer.RenderThreadFrameEnded += RetireFrame;
                }
            }
            // The graphics device may not exist yet (headless/idle host): keep the captured work pending.
            if (!EnsureGpu()) return;
            if (hasTrailWork) EnsureTrailGpu();
            EnsureBurstStates(snapshot.StateSlotCount);
            // Only the frame slot is prepared here; emitter/curve contents are uploaded by the render
            // thread when it records the simulation (first-frame markers and clear rows live there).
            ParticleGpuFrame gpu = FrameSlot(snapshot, hasTrailWork);
            for (int i = 0; i < snapshot.Emitters.Length; i++)
            {
                ParticleEmitterFrame emitter = snapshot.Emitters[i];
                string? key = emitter.TextureKey;
                Texture? texture = string.IsNullOrEmpty(key) ? null : TryResolveHostOrRegisteredTexture(key);
                bool textureSrgb = !string.IsNullOrEmpty(key) && texture is not null && LumenTextureRegistry.TextureIsSrgb(key, texture, emitter.ColorSpace);
                gpu.TexturePresent[i] = texture is not null;
                gpu.TextureSrgb[i] = texture is not null && textureSrgb;
                gpu.Textures[i] = SampledOrWhiteTexture(texture);
            }
            for (int i = 0; i < snapshot.TrailEmitters.Length; i++)
            {
                ParticleTrailEmitterFrame trail = snapshot.TrailEmitters[i];
                string? key = trail.TextureKey;
                Texture? texture = string.IsNullOrEmpty(key) ? null : TryResolveHostOrRegisteredTexture(key);
                gpu.TrailTexturePresent[i] = texture is not null;
                gpu.TrailTextureSrgb[i] = !string.IsNullOrEmpty(key) && texture is not null && LumenTextureRegistry.TextureIsSrgb(key, texture, trail.ColorSpace);
                gpu.TrailTextures[i] = SampledOrWhiteTexture(texture);
            }
            MarkFrameEnqueued(snapshot.Context.FrameIndex);
            pendingFrames.Enqueue(new ParticleQueuedFrame(snapshot, gpu.Textures, gpu.TexturePresent, gpu));
        }
    }

    private Texture? TryResolveHostOrRegisteredTexture(string key)
    {
        if (!LumenTextureRegistry.TryGetRegistered(key, out Texture? texture))
            texture = ResolveHostTexture(key);
        if (texture is not null)
        {
            Texture.EnsureSampledReady(texture);
            if (texture.GpuResource is null && !texture.EnsureResident()) texture = null;
        }
        if (texture is null && warnedTextures.Add(key)) WarnMissingTexture(key);
        return texture;
    }

    private static Texture SampledOrWhiteTexture(Texture? texture)
    {
        Texture resolved = texture ?? Texture2D.whiteTexture;
        Texture.EnsureSampledReady(resolved);
        return resolved;
    }

    // Kept out of constructor/capture so pure lifecycle tests never enter the host Content system.
    private static Texture? ResolveHostTexture(string key) =>
        Verse.ContentFinder<UnityEngine.Texture2D>.Get(key, reportFailure: false)?.CoreTexture;

    private static void WarnMissingTexture(string key) =>
        Verse.Log.Warning($"[RimLumen] Particle texture '{key}' was not found; using the procedural particle shape.");

    // Requires: open command buffer outside render pass.
    // Ensures: records the simulation for this frame and its compute->vertex barrier from the resources
    //   prepared on the main thread.
    public void RecordBufferBarriers(in LumenBarrierContext context)
    {
        if (!pendingFrames.TryTake(context.FrameIndex, out ParticleQueuedFrame? frame) || frame?.Gpu is null) return;
        RecordSimulation(context.Frame, frame.Snapshot, frame.Gpu);
    }

    // Preloads particle/trail compute kernels, registers mesh shaders, and builds static quad/strip meshes
    // during host initialization without allocating VRAM particle/trail pools or subscribing frame hooks.
    internal void InitializeStaticGpuResources(EngineApplication app)
    {
        if (app.GraphicsDevice is null || app.RenderContext is null) return;
        lock (lifecycleGate)
        {
            if (disposed) return;
            ParticleMeshShaders.EnsureParticleShadersRegistered();
            kernel ??= LoadSimKernel();
            trailKernel ??= LoadTrailSimKernel();
            drawResources ??= new ParticleGpuDrawResources(app.RenderContext.FramesInFlight);
        }
    }

    private static ComputeKernel LoadSimKernel() =>
        ComputeKernelCache.Load(SimShaderPath, "main", SimBindings, ComputePushConstantBytes);

    private static ComputeKernel LoadTrailSimKernel() =>
        ComputeKernelCache.Load(TrailSimShaderPath, "main", TrailSimBindings, ComputePushConstantBytes);

    private static VkDescriptorSetLayoutBinding[] StorageDescriptorBindings(int count)
    {
        var bindings = new VkDescriptorSetLayoutBinding[count];
        for (int i = 0; i < count; i++)
            bindings[i] = ParticleGpuDrawResources.Binding(i, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit);
        return bindings;
    }

    // Requires: main thread with a live graphics device.
    // Ensures: creates the device objects exactly once (particle buffer, quad/strip meshes + shaders, frame slots,
    //   compute layout/kernel). Returns false when the host has no graphics device yet.
    private bool EnsureGpu()
    {
        if (particles is not null) return true;
        EngineApplication? app = application;
        if (app?.GraphicsDevice is null || app.RenderContext is null) return false;
        DeviceEx device = app.Device;
        particles = GpuComputeBuffer.Create(device, app.GraphicsDevice.Allocator,
            "Lumen.Particles", (long)capacity * 64, VkBufferUsageFlags.TransferSrcBit, deviceLocal: true);
        deadBlocks = GpuComputeBuffer.Create(device, app.GraphicsDevice.Allocator,
            "Lumen.ParticleDeadBlocks", ParticleSimulationDispatch.ScratchBytes(capacity), deviceLocal: true);
        drawResources ??= new ParticleGpuDrawResources(app.RenderContext.FramesInFlight);
        frameSlots = new ParticleGpuFrame?[2 * app.RenderContext.FramesInFlight + 2];
        computeLayout = ParticleGpuDrawResources.CreateLayout(device, StorageDescriptorBindings(SimBindings.Length));
        kernel ??= LoadSimKernel();
        return true;
    }

    // Lazy-allocated only when at least one L2 emitter configures Trails or leaves pending trail clears.
    private void EnsureTrailGpu()
    {
        if (trailHeaders is not null) return;
        EngineApplication app = application!;
        DeviceEx device = app.Device;
        var allocator = app.GraphicsDevice!.Allocator;
        trailHeaders = GpuComputeBuffer.Create(device, allocator, "Lumen.TrailHeaders",
            (long)trailHeaderCapacity * Marshal.SizeOf<GpuTrailHeader>(), deviceLocal: true);
        trailPoints = GpuComputeBuffer.Create(device, allocator, "Lumen.TrailPoints",
            (long)trailPointCapacity * Marshal.SizeOf<GpuTrailPoint>(), deviceLocal: true);
        particleToTrail = GpuComputeBuffer.Create(device, allocator, "Lumen.ParticleToTrail",
            (long)trailMapCapacity * sizeof(uint), deviceLocal: true);
        trailComputeLayout = ParticleGpuDrawResources.CreateLayout(device, StorageDescriptorBindings(TrailSimBindings.Length));
        trailKernel ??= LoadTrailSimKernel();
    }

    // Requires: main thread under lifecycleGate, device resources exist.
    // Ensures: the persistent BurstState buffer holds at least `required` entries, one per live instance index.
    //   Growth doubles; RecordSimulation copies the previous contents forward before the new buffer's first use.
    private void EnsureBurstStates(int required)
    {
        if (burstStates is not null && required <= burstStateEntries) return;
        if (burstStates is not null) supersededBurstStates.Add(new SupersededBuffer { Buffer = burstStates });
        burstStateEntries = Math.Max(Math.Max(64, required), 2 * burstStateEntries);
        burstStates = GpuComputeBuffer.Create(application!.Device, application.GraphicsDevice!.Allocator,
            "Lumen.BurstStates", (long)burstStateEntries * 16, VkBufferUsageFlags.TransferSrcBit, deviceLocal: true);
    }

    // Requires: main thread, device resources exist.
    // Ensures: returns the frame resource set for this capture index, growing or shrinking it in place.
    private ParticleGpuFrame FrameSlot(ParticleFrame snapshot, bool hasTrailWork)
    {
        int requiredRows = snapshot.Emitters.Length + snapshot.Clears.Length;
        int index = (int)(snapshot.Context.FrameIndex % (ulong)frameSlots.Length);
        ParticleGpuFrame? existing = frameSlots[index];
        if (existing is not null && existing.EmitterCapacity >= requiredRows && existing.FieldCapacity >= snapshot.Fields.Length
            && existing.CellCapacity >= snapshot.CellCount && existing.EventCapacity >= snapshot.EventCapacity
            && existing.LinkCapacity >= snapshot.SubLinks.Length && existing.BurstCapacity >= snapshot.ExplicitBursts.Length
            && ReferenceEquals(existing.BurstStates, burstStates)
            && (snapshot.CellCount != 0 || existing.CellCapacity == 1)
            && (snapshot.EventCapacity != 0 || existing.EventCapacity == 1)
            && (!hasTrailWork || (existing.TrailPool is not null
                && existing.TrailEmitterCapacity >= snapshot.TrailEmitters.Length
                && ReferenceEquals(existing.TrailHeaders, trailHeaders)
                && ReferenceEquals(existing.TrailPoints, trailPoints)
                && ReferenceEquals(existing.ParticleToTrail, particleToTrail))))
            return existing;
        if (existing is not null)
        {
            existing.Dispose();
            frameSlots[index] = null;
        }
        var created = new ParticleGpuFrame(application!, snapshot, requiredRows, particles!, deadBlocks!, burstStates!,
            hasTrailWork ? trailHeaders : null, hasTrailWork ? trailPoints : null, hasTrailWork ? particleToTrail : null);
        frameSlots[index] = created;
        return created;
    }

    private void RecordSimulation(in FrameContext frame, ParticleFrame snapshot, ParticleGpuFrame resources)
    {
        GpuComputeBuffer states = resources.BurstStates;
        lock (lifecycleGate)
        {
            CompleteRecordedFrame(snapshot.Context.FrameIndex);
            if (!ReferenceEquals(recordedBurstStates, states))
            {
                // Frames record in capture order and buffers only grow, so every older buffer is done after this copy.
                if (recordedBurstStates is not null) RecordBurstStateCopy(frame, recordedBurstStates, states);
                recordedBurstStates = states;
                foreach (SupersededBuffer old in supersededBurstStates)
                {
                    if (ReferenceEquals(old.Buffer, states)) break;
                    old.CopiedAt ??= snapshot.Context.FrameIndex;
                }
            }
            for (int i = supersededBurstStates.Count - 1; i >= 0; i--)
            {
                if (supersededBurstStates[i].CopiedAt is not ulong copiedAt
                    || snapshot.Context.FrameIndex < copiedAt + (ulong)frameSlots.Length) continue;
                supersededBurstStates[i].Buffer.Dispose();
                supersededBurstStates.RemoveAt(i);
            }
        }
        // Do not clear on CPU capture: frames may be dropped before recording, and
        // allocated ranges can already have a newer owner in a subsequent snapshot.
        Span<ParticleEmitterFrame> emitters = resources.SimulationEmitters.AsSpan(0, snapshot.Emitters.Length + snapshot.Clears.Length);
        snapshot.Emitters.AsSpan().CopyTo(emitters);
        liveInstanceIds.Clear();
        bool eventWork = false;
        for (int i = 0; i < snapshot.Emitters.Length; i++)
        {
            liveInstanceIds.Add(emitters[i].InstanceId);
            eventWork |= emitters[i].Parameters.Events.Z != 0f || emitters[i].Parameters.EmitEvents != 0;
            if (initializedInstances.Add(emitters[i].InstanceId))
            {
                GpuEmitterParams parameters = emitters[i].Parameters;
                parameters.State.Z = 1;
                emitters[i] = emitters[i] with { Parameters = parameters };
            }
        }
        initializedInstances.IntersectWith(liveInstanceIds);
        for (int i = 0; i < snapshot.Clears.Length; i++)
        {
            EmitterSlotRange range = snapshot.Clears[i];
            emitters[snapshot.Emitters.Length + i] = new ParticleEmitterFrame(-1, new GpuEmitterParams
            {
                Slots = new System.Numerics.Vector4(range.Offset, range.Capacity, 0, 0),
                State = new System.Numerics.Vector4(0, 0, 1, 0)
            }, Array.Empty<GpuCurveSample>(), null, LumenBlendMode.Additive);
        }
        bool hasTrailWork = resources.TrailPool is not null &&
            (snapshot.TrailEmitters.Length != 0 || snapshot.TrailHeaderClears.Length != 0 || snapshot.TrailMapClears.Length != 0);
        resources.Upload(snapshot, emitters);
        Span<VkBufferMemoryBarrier> entryBarriers = stackalloc VkBufferMemoryBarrier[4];
        int entryCount = 1;
        entryBarriers[0] = BufferBarrier(particles!,
            VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit | VkAccessFlags.TransferWriteBit,
            VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit);
        if (hasTrailWork)
        {
            entryBarriers[entryCount++] = BufferBarrier(trailHeaders!,
                VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit | VkAccessFlags.TransferWriteBit,
                VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit);
            entryBarriers[entryCount++] = BufferBarrier(trailPoints!,
                VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit,
                VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit);
            entryBarriers[entryCount++] = BufferBarrier(particleToTrail!,
                VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit,
                VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit);
        }
        frame.CommandBuffer.PipelineBarrier(VkPipelineStageFlags.TransferBit | VkPipelineStageFlags.VertexShaderBit | VkPipelineStageFlags.ComputeShaderBit,
            VkPipelineStageFlags.ComputeShaderBit, VkDependencyFlags.None, default(ReadOnlySpan<VkMemoryBarrier>),
            entryBarriers[..entryCount], default(ReadOnlySpan<VkImageMemoryBarrier>));

        VkDescriptorSet set = DescriptorSetWriter.AllocateDescriptorSet(frame.Device, resources.Pool, computeLayout);
        ParticleGpuDrawResources.WriteBuffers(frame.Device, set, resources.ComputeBuffers, VkDescriptorType.StorageBuffer);
        VkDescriptorSet trailSet = default;
        if (hasTrailWork)
        {
            trailSet = DescriptorSetWriter.AllocateDescriptorSet(frame.Device, resources.TrailPool!.Value, trailComputeLayout);
            ParticleGpuDrawResources.WriteBuffers(frame.Device, trailSet, resources.TrailComputeBuffers!, VkDescriptorType.StorageBuffer);
        }

        bool trailKernelBound = false;
        if (hasTrailWork)
        {
            initialTrailHeaderClears.Clear();
            initialTrailMapClears.Clear();
            liveTrailInstanceIds.Clear();
            for (int t = 0; t < snapshot.TrailEmitters.Length; t++)
            {
                ParticleTrailEmitterFrame tr = snapshot.TrailEmitters[t];
                liveTrailInstanceIds.Add(tr.InstanceId);
                if (initializedTrailInstances.Add(tr.InstanceId))
                {
                    initialTrailHeaderClears.Add(new EmitterSlotRange((int)tr.Parameters.Ranges.X, (int)tr.Parameters.Ranges.Y));
                    initialTrailMapClears.Add(new EmitterSlotRange((int)tr.Parameters.MapBase, (int)tr.Parameters.DepthAndParticle.Z));
                }
            }
            initializedTrailInstances.IntersectWith(liveTrailInstanceIds);
            if (snapshot.TrailHeaderClears.Length != 0 || snapshot.TrailMapClears.Length != 0 || initialTrailHeaderClears.Count != 0)
            {
                BindComputeKernel(frame, trailKernel!, trailSet);
                trailKernelBound = true;
                foreach (EmitterSlotRange range in snapshot.TrailHeaderClears)
                    DispatchTrailSimulation(frame, new GpuSimPushConstants
                    {
                        EmitterIndex = (uint)range.Offset, FrameSeed = (uint)range.Capacity, Padding0 = 2u
                    }, ParticleSimulationDispatch.BlockCount(range.Capacity));
                foreach (EmitterSlotRange range in initialTrailHeaderClears)
                    DispatchTrailSimulation(frame, new GpuSimPushConstants
                    {
                        EmitterIndex = (uint)range.Offset, FrameSeed = (uint)range.Capacity, Padding0 = 2u
                    }, ParticleSimulationDispatch.BlockCount(range.Capacity));
                foreach (EmitterSlotRange range in snapshot.TrailMapClears)
                    DispatchTrailSimulation(frame, new GpuSimPushConstants
                    {
                        EmitterIndex = (uint)range.Offset, FrameSeed = (uint)range.Capacity, Padding0 = 3u
                    }, ParticleSimulationDispatch.BlockCount(range.Capacity));
                foreach (EmitterSlotRange range in initialTrailMapClears)
                    DispatchTrailSimulation(frame, new GpuSimPushConstants
                    {
                        EmitterIndex = (uint)range.Offset, FrameSeed = (uint)range.Capacity, Padding0 = 3u
                    }, ParticleSimulationDispatch.BlockCount(range.Capacity));
                RecordSimulationStageBarrier(frame, resources, eventWork, includeTrails: true);
            }
        }
        else
        {
            initializedTrailInstances.Clear();
        }

        if (emitterToTrail.Length < emitters.Length) emitterToTrail = new int[emitters.Length];
        Array.Fill(emitterToTrail, -1, 0, emitters.Length);
        for (int t = 0; t < snapshot.TrailEmitters.Length; t++)
            emitterToTrail[snapshot.TrailEmitters[t].ParticleRow] = t;

        BindComputeKernel(frame, kernel!, set);
        trailKernelBound = false;
        // Reset per-frame event counts and prepare GPU-owned explicit bursts before any source integrates.
        if (eventWork)
        {
            RecordSimulationStageBarrier(frame, resources, true, hasTrailWork);
            for (int i = 0; i < snapshot.Emitters.Length; i++)
            {
                GpuEmitterParams parameters = emitters[i].Parameters;
                if (parameters.EmitEvents == 0 && parameters.Events.Z == 0f) continue;
                DispatchSimulation(frame, new GpuSimPushConstants { EmitterIndex = (uint)i, Padding0 = 8u }, 1);
            }
            RecordSimulationStageBarrier(frame, resources, true, hasTrailWork);
        }
        for (int i = 0; i < emitters.Length; i++)
        {
            ParticleEmitterFrame emitterFrame = emitters[i];
            // Local-space transforms can move a particle below its floor even while game time is paused.
            if (emitterFrame.Parameters.State.X == 0f && emitterFrame.Parameters.Slots.Z == 0f &&
                emitterFrame.Parameters.State.Z == 0f && !emitterFrame.KillBelowFloor &&
                emitterFrame.Parameters.Map.Y == 0f && emitterFrame.Parameters.ExplicitCount == 0u) continue;
            var push = new GpuSimPushConstants
            {
                EmitterIndex = (uint)i, FrameSeed = unchecked((uint)snapshot.Context.FrameIndex),
                Padding0 = emitterFrame.KillBelowFloor ? 1u : 0u,
            };
            int emitterCapacity = (int)emitterFrame.Parameters.Slots.Y;
            int trailIndex = emitterToTrail[i];
            if (trailIndex < 0)
            {
                if (trailKernelBound)
                {
                    BindComputeKernel(frame, kernel!, set);
                    trailKernelBound = false;
                }
                if (!ParticleSimulationDispatch.UsesParallelScan(emitterCapacity))
                {
                    DispatchSimulation(frame, push, 1);
                    continue;
                }
                int blocks = ParticleSimulationDispatch.BlockCount(emitterCapacity);
                push.Padding0 |= 2u; // Integrate and count dead slots in parallel.
                DispatchSimulation(frame, push, blocks);
                if (emitterFrame.Parameters.Slots.Z == 0f && emitterFrame.Parameters.ExplicitCount == 0u) continue;
                RecordSimulationStageBarrier(frame, resources, eventWork, hasTrailWork);
                push.Padding0 = (push.Padding0 & 1u) | 4u; // Exclusive prefix of block counts.
                DispatchSimulation(frame, push, 1);
                RecordSimulationStageBarrier(frame, resources, eventWork, hasTrailWork);
                push.Padding0 = (push.Padding0 & 1u) | 6u; // Respawn in cursor order.
                DispatchSimulation(frame, push, blocks);
                RecordSimulationStageBarrier(frame, resources, eventWork, hasTrailWork); // Scratch is reused by the next emitter.
                continue;
            }

            // Trail emitter: always split Phase 1 -> PostIntegrate -> Phase 2/3 -> PostSpawn so dying
            // particles relinquish/detach their trails using their last frame's position/colour/size.
            int trailBlocks = ParticleSimulationDispatch.BlockCount(emitterCapacity);
            int headerCapacity = (int)snapshot.TrailEmitters[trailIndex].Parameters.Ranges.Y;
            int headerBlocks = ParticleSimulationDispatch.BlockCount(headerCapacity);
            if (trailKernelBound)
            {
                BindComputeKernel(frame, kernel!, set);
                trailKernelBound = false;
            }
            push.Padding0 |= 2u;
            DispatchSimulation(frame, push, trailBlocks);
            RecordSimulationStageBarrier(frame, resources, eventWork, includeTrails: true);

            BindComputeKernel(frame, trailKernel!, trailSet);
            trailKernelBound = true;
            DispatchTrailSimulation(frame, new GpuSimPushConstants
            {
                EmitterIndex = (uint)trailIndex, FrameSeed = unchecked((uint)snapshot.Context.FrameIndex), Padding0 = 0u
            }, headerBlocks);

            bool maySpawn = emitterFrame.Parameters.Slots.Z != 0f || emitterFrame.Parameters.ExplicitCount != 0u;
            if (maySpawn)
            {
                RecordSimulationStageBarrier(frame, resources, eventWork, includeTrails: true);
                BindComputeKernel(frame, kernel!, set);
                trailKernelBound = false;
                push.Padding0 = (push.Padding0 & 1u) | 4u;
                DispatchSimulation(frame, push, 1);
                RecordSimulationStageBarrier(frame, resources, eventWork, includeTrails: false);
                push.Padding0 = (push.Padding0 & 1u) | 6u;
                DispatchSimulation(frame, push, trailBlocks);
                RecordSimulationStageBarrier(frame, resources, eventWork, includeTrails: false);
                BindComputeKernel(frame, trailKernel!, trailSet);
                trailKernelBound = true;
                DispatchTrailSimulation(frame, new GpuSimPushConstants
                {
                    EmitterIndex = (uint)trailIndex, FrameSeed = unchecked((uint)snapshot.Context.FrameIndex), Padding0 = 1u
                }, trailBlocks);
            }
            RecordSimulationStageBarrier(frame, resources, eventWork, includeTrails: true);
        }
        if (snapshot.SubLinks.Length != 0)
        {
            RecordSimulationStageBarrier(frame, resources, eventWork, hasTrailWork);
            if (trailKernelBound)
            {
                BindComputeKernel(frame, kernel!, set);
                trailKernelBound = false;
            }
            bool dispatchedTrailSubTargets = false;
            for (int i = 0; i < snapshot.Emitters.Length; i++)
            {
                if (emitters[i].Parameters.SourceLinkCount == 0u) continue;
                DispatchSimulation(frame, new GpuSimPushConstants
                {
                    EmitterIndex = (uint)i, FrameSeed = unchecked((uint)snapshot.Context.FrameIndex), Padding0 = 10u
                }, 1);
                if (emitterToTrail[i] >= 0) dispatchedTrailSubTargets = true;
            }
            if (dispatchedTrailSubTargets)
            {
                RecordSimulationStageBarrier(frame, resources, eventWork, includeTrails: true);
                BindComputeKernel(frame, trailKernel!, trailSet);
                trailKernelBound = true;
                for (int t = 0; t < snapshot.TrailEmitters.Length; t++)
                {
                    int row = snapshot.TrailEmitters[t].ParticleRow;
                    if (emitters[row].Parameters.SourceLinkCount == 0u) continue;
                    int blocks = ParticleSimulationDispatch.BlockCount((int)emitters[row].Parameters.Slots.Y);
                    DispatchTrailSimulation(frame, new GpuSimPushConstants
                    {
                        EmitterIndex = (uint)t, FrameSeed = unchecked((uint)snapshot.Context.FrameIndex), Padding0 = 1u
                    }, blocks);
                }
            }
        }
        Span<VkBufferMemoryBarrier> exitBarriers = stackalloc VkBufferMemoryBarrier[3];
        int exitCount = 1;
        exitBarriers[0] = BufferBarrier(particles!, VkAccessFlags.ShaderWriteBit, VkAccessFlags.ShaderReadBit | VkAccessFlags.IndirectCommandReadBit);
        if (hasTrailWork)
        {
            exitBarriers[exitCount++] = BufferBarrier(trailHeaders!, VkAccessFlags.ShaderWriteBit, VkAccessFlags.ShaderReadBit);
            exitBarriers[exitCount++] = BufferBarrier(trailPoints!, VkAccessFlags.ShaderWriteBit, VkAccessFlags.ShaderReadBit);
        }
        frame.CommandBuffer.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit,
            VkPipelineStageFlags.VertexShaderBit | VkPipelineStageFlags.DrawIndirectBit, VkDependencyFlags.None,
            default(ReadOnlySpan<VkMemoryBarrier>), exitBarriers[..exitCount], default(ReadOnlySpan<VkImageMemoryBarrier>));
        RecordDevCompute(frame, snapshot, resources);
    }

    // Dev-only hooks, implemented under Showcase/. The ship build strips that directory, so the compiler removes both
    // calls together with their argument evaluation.
    // Requires: render thread, after this frame's simulation and its exit barrier.
    partial void RecordDevCompute(in FrameContext frame, ParticleFrame snapshot, ParticleGpuFrame resources);

    // Requires: main thread, inside AppendHdrDraws after this frame's billboard and trail draws.
    partial void AppendDevDraws(ParticleQueuedFrame frame, List<FinalDrawItem> draws, DrawItemFactory maker,
        DrawPropertyArena arena, int queueOrder);

    private static void RecordBurstStateCopy(in FrameContext frame, GpuComputeBuffer from, GpuComputeBuffer to)
    {
        var read = new VkBufferMemoryBarrier
        {
            SrcAccessMask = VkAccessFlags.ShaderWriteBit, DstAccessMask = VkAccessFlags.TransferReadBit,
            SrcQueueFamilyIndex = -1, DstQueueFamilyIndex = -1, Buffer = from.Buffer, Offset = ByteSize.Zero, Size = from.Size
        };
        frame.CommandBuffer.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.TransferBit,
            VkDependencyFlags.None, default(ReadOnlySpan<VkMemoryBarrier>), new ReadOnlySpan<VkBufferMemoryBarrier>(in read),
            default(ReadOnlySpan<VkImageMemoryBarrier>));
        var copy = new VkBufferCopy { SrcOffset = ByteSize.Zero, DstOffset = ByteSize.Zero, Size = from.Size };
        frame.CommandBuffer.CopyBuffer(from.Buffer, to.Buffer, new ReadOnlySpan<VkBufferCopy>(in copy));
        var written = new VkBufferMemoryBarrier
        {
            SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit,
            SrcQueueFamilyIndex = -1, DstQueueFamilyIndex = -1, Buffer = to.Buffer, Offset = ByteSize.Zero, Size = to.Size
        };
        frame.CommandBuffer.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.ComputeShaderBit,
            VkDependencyFlags.None, default(ReadOnlySpan<VkMemoryBarrier>), new ReadOnlySpan<VkBufferMemoryBarrier>(in written),
            default(ReadOnlySpan<VkImageMemoryBarrier>));
    }

    private void DispatchSimulation(in FrameContext frame, GpuSimPushConstants push, int groups)
    {
        frame.CommandBuffer.PushConstants(kernel!.PipelineLayout, VkShaderStageFlags.ComputeBit, 0,
            MemoryMarshal.AsBytes(new ReadOnlySpan<GpuSimPushConstants>(in push)));
        frame.CommandBuffer.Dispatch(groups, 1, 1);
    }

    private void DispatchTrailSimulation(in FrameContext frame, GpuSimPushConstants push, int groups)
    {
        frame.CommandBuffer.PushConstants(trailKernel!.PipelineLayout, VkShaderStageFlags.ComputeBit, 0,
            MemoryMarshal.AsBytes(new ReadOnlySpan<GpuSimPushConstants>(in push)));
        frame.CommandBuffer.Dispatch(groups, 1, 1);
    }

    private void RecordSimulationStageBarrier(in FrameContext frame, ParticleGpuFrame resources, bool eventWork, bool includeTrails = false)
    {
        Span<VkBufferMemoryBarrier> barriers = stackalloc VkBufferMemoryBarrier[7];
        int count = 2;
        barriers[0] = BufferBarrier(particles!, VkAccessFlags.ShaderWriteBit, VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit);
        barriers[1] = BufferBarrier(deadBlocks!, VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit,
            VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit);
        if (eventWork)
        {
            barriers[count++] = BufferBarrier(resources.BurstStates, VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit,
                VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit);
            barriers[count++] = BufferBarrier(resources.Events, VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit,
                VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit);
        }
        if (includeTrails)
        {
            barriers[count++] = BufferBarrier(trailHeaders!, VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit,
                VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit);
            barriers[count++] = BufferBarrier(trailPoints!, VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit,
                VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit);
            barriers[count++] = BufferBarrier(particleToTrail!, VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit,
                VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit);
        }
        frame.CommandBuffer.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.ComputeShaderBit,
            VkDependencyFlags.None, default(ReadOnlySpan<VkMemoryBarrier>), barriers[..count], default(ReadOnlySpan<VkImageMemoryBarrier>));
    }

    private static void BindComputeKernel(in FrameContext frame, ComputeKernel pipelineKernel, VkDescriptorSet descriptorSet)
    {
        frame.CommandBuffer.BindPipeline(VkPipelineBindPoint.Compute, pipelineKernel.Pipeline);
        frame.CommandBuffer.BindDescriptorSet(VkPipelineBindPoint.Compute, pipelineKernel.PipelineLayout, 0, descriptorSet, default(Span<ByteSize32>));
    }

    private VkBufferMemoryBarrier ParticleBarrier(VkAccessFlags source, VkAccessFlags destination) =>
        BufferBarrier(particles!, source, destination);

    private static VkBufferMemoryBarrier BufferBarrier(GpuComputeBuffer buffer, VkAccessFlags source, VkAccessFlags destination) => new()
    {
        SrcAccessMask = source, DstAccessMask = destination, SrcQueueFamilyIndex = -1, DstQueueFamilyIndex = -1,
        Buffer = buffer.Buffer, Offset = ByteSize.Zero, Size = buffer.Size
    };

    // Ensures: no command-buffer work. MeshBatchRenderer records this frame's instanced draws from the
    //   pass-0 list that AppendHdrDraws filled during Capture.
    public void RecordHdrDraws(in LumenHdrDrawContext context)
    {
    }

    // Requires: main thread, in RimLumenPipeline.Capture after the engine flushed mesh uploads.
    // Ensures: appends one instanced billboard draw per live emitter.
    void ILumenHdrDrawAppender.AppendHdrDraws(ulong frameIndex, List<FinalDrawItem> draws, DrawItemFactory maker,
        DrawPropertyArena arena, int queueOrder, Texture sceneDepth)
    {
        if (drawResources is null || particles is null) return;
        if (!pendingFrames.TryGet(frameIndex, out ParticleQueuedFrame? frame) || frame.Gpu is null) return;
        drawResources.AppendDraws(frame.Snapshot, frame.Gpu, sceneDepth, frame.Textures, frame.TexturePresent, frame.Gpu.TextureSrgb,
            draws, maker, arena, queueOrder, frameIndex);
        AppendDevDraws(frame, draws, maker, arena,
            queueOrder + frame.Snapshot.Emitters.Length + frame.Snapshot.TrailEmitters.Length);
    }

    private void RetireFrame(RenderFrame frame)
    {
        ulong number = unchecked((ulong)frame.Timing.FrameNumber);
        if (pendingFrames.TryTake(number, out _))
            RestoreDroppedFrame(number);
    }

    private void ReleaseOnFrameStart()
    {
        lock (lifecycleGate)
        {
            if (!Volatile.Read(ref idleRequested) || !pendingFrames.IsEmpty)
            {
                idleStartedAt = null;
                return;
            }
            long now = Stopwatch.GetTimestamp();
            idleStartedAt ??= now;
            if (Stopwatch.GetElapsedTime(idleStartedAt.Value, now).TotalSeconds < GpuIdleRetentionSeconds) return;
            // This event runs after the previous command buffer was submitted and before
            // this one begins. Never destroy objects referenced by an unsubmitted buffer.
            if (particles is not null)
            {
                application!.Device.WaitIdle();
                ReleaseGpu(application.Device);
            }
            ClearPendingGpuState();
            application!.FrameRenderer.RenderThreadFrameStarting -= ReleaseOnFrameStart;
            application.FrameRenderer.RenderThreadFrameEnded -= RetireFrame;
            application = null;
        }
    }

    private void ReleaseGpu(Brutal.VulkanApi.Abstractions.DeviceEx device)
    {
        foreach (ParticleGpuFrame? slot in frameSlots) slot?.Dispose();
        frameSlots = Array.Empty<ParticleGpuFrame?>();
        drawResources?.ClearMaterials();
        particles?.Dispose();
        particles = null;
        deadBlocks?.Dispose();
        deadBlocks = null;
        burstStates?.Dispose();
        burstStates = null;
        burstStateEntries = 0;
        foreach (SupersededBuffer old in supersededBurstStates) old.Buffer.Dispose();
        supersededBurstStates.Clear();
        recordedBurstStates = null;
        if (!computeLayout.IsNull()) device.DestroyDescriptorSetLayout(computeLayout, device.VkAllocator);
        computeLayout = default;
        trailHeaders?.Dispose();
        trailHeaders = null;
        trailPoints?.Dispose();
        trailPoints = null;
        particleToTrail?.Dispose();
        particleToTrail = null;
        if (!trailComputeLayout.IsNull()) device.DestroyDescriptorSetLayout(trailComputeLayout, device.VkAllocator);
        trailComputeLayout = default;
        initializedInstances.Clear();
        initializedTrailInstances.Clear();
        liveInstanceIds.Clear();
        liveTrailInstanceIds.Clear();
    }

    private void DisposeGpu()
    {
        if (particles is not null || drawResources is not null)
        {
            EngineApplication app = EngineApplication.Instance;
            app.WaitForRenderThreadIdle();
            app.Device.WaitIdle();
            if (particles is not null) ReleaseGpu(app.Device);
            drawResources?.Dispose();
            drawResources = null;
            kernel = null;
            trailKernel = null;
        }
        if (application is not null)
        {
            application.FrameRenderer.RenderThreadFrameStarting -= ReleaseOnFrameStart;
            application.FrameRenderer.RenderThreadFrameEnded -= RetireFrame;
            application = null;
        }
        pendingFrames.Clear();
    }
}
