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
    // One prepared frame resource set per capture slot. Slot reuse waits 2*FramesInFlight+2 frames, so a
    // submission that may still reference a set has been retired before the slot is rewritten.
    private ParticleGpuFrame?[] frameSlots = Array.Empty<ParticleGpuFrame?>();
    private const double GpuIdleRetentionSeconds = 30;
    private bool idleRequested;
    private long? idleStartedAt;
    private EngineApplication? application;
    private readonly object lifecycleGate = new();
    private VkDescriptorSetLayout computeLayout;
    private ComputeKernel? kernel;
    private readonly HashSet<int> initializedInstances = new();
    private readonly HashSet<int> liveInstanceIds = new();

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
        if (snapshot.Emitters.Length == 0) return;
        lock (lifecycleGate)
        {
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
            // Only the frame slot is prepared here; emitter/curve contents are uploaded by the render
            // thread when it records the simulation (first-frame markers and clear rows live there).
            ParticleGpuFrame gpu = FrameSlot(snapshot);
            for (int i = 0; i < snapshot.Emitters.Length; i++)
            {
                ParticleEmitterFrame emitter = snapshot.Emitters[i];
                string? key = emitter.TextureKey;
                Texture? texture = null;
                bool textureSrgb = false;
                if (!string.IsNullOrEmpty(key))
                {
                    if (!LumenTextureRegistry.TryGetRegistered(key, out texture))
                        texture = ResolveHostTexture(key);
                    if (texture is not null)
                    {
                        textureSrgb = LumenTextureRegistry.TextureIsSrgb(key, texture, emitter.ColorSpace);
                        Texture.EnsureSampledReady(texture);
                        if (texture.GpuResource is null && !texture.EnsureResident()) texture = null;
                    }
                    if (texture is null && warnedTextures.Add(key)) WarnMissingTexture(key);
                }
                gpu.TexturePresent[i] = texture is not null;
                gpu.TextureSrgb[i] = texture is not null && textureSrgb;
                gpu.Textures[i] = texture ?? Texture2D.whiteTexture;
                Texture.EnsureSampledReady(gpu.Textures[i]);
            }
            pendingFrames.Enqueue(new ParticleQueuedFrame(snapshot, gpu.Textures, gpu.TexturePresent, gpu));
        }
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

    // Requires: main thread with a live graphics device.
    // Ensures: creates the device objects exactly once (particle buffer, quad mesh + materials, frame slots,
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
        drawResources = new ParticleGpuDrawResources(app.RenderContext.FramesInFlight);
        frameSlots = new ParticleGpuFrame?[2 * app.RenderContext.FramesInFlight + 2];
        computeLayout = ParticleGpuDrawResources.CreateLayout(device, new[]
        {
            ParticleGpuDrawResources.Binding(0, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit),
            ParticleGpuDrawResources.Binding(1, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit),
            ParticleGpuDrawResources.Binding(2, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit),
            ParticleGpuDrawResources.Binding(3, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit)
        });
        kernel = ComputeKernelCache.Load("Particles/ParticleSim.comp.glsl", "main", new[]
        {
            new ComputeBinding(0, ComputeBindingType.StorageBuffer),
            new ComputeBinding(1, ComputeBindingType.StorageBuffer),
            new ComputeBinding(2, ComputeBindingType.StorageBuffer),
            new ComputeBinding(3, ComputeBindingType.StorageBuffer)
        }, 16);
        return true;
    }

    // Requires: main thread, device resources exist.
    // Ensures: returns the frame resource set for this capture index, growing or shrinking it in place.
    private ParticleGpuFrame FrameSlot(ParticleFrame snapshot)
    {
        int requiredRows = snapshot.Emitters.Length + snapshot.Clears.Length;
        int index = (int)(snapshot.Context.FrameIndex % (ulong)frameSlots.Length);
        ParticleGpuFrame? existing = frameSlots[index];
        if (existing is not null && existing.EmitterCapacity >= requiredRows && existing.FieldCapacity >= snapshot.Fields.Length)
            return existing;
        if (existing is not null)
        {
            existing.Dispose();
            frameSlots[index] = null;
        }
        var created = new ParticleGpuFrame(application!, requiredRows, snapshot.Fields.Length, particles!, deadBlocks!);
        frameSlots[index] = created;
        return created;
    }

    private void RecordSimulation(in FrameContext frame, ParticleFrame snapshot, ParticleGpuFrame resources)
    {
        // Do not clear on CPU capture: frames may be dropped before recording, and
        // allocated ranges can already have a newer owner in a subsequent snapshot.
        Span<ParticleEmitterFrame> emitters = resources.SimulationEmitters.AsSpan(0, snapshot.Emitters.Length + snapshot.Clears.Length);
        snapshot.Emitters.AsSpan().CopyTo(emitters);
        liveInstanceIds.Clear();
        for (int i = 0; i < snapshot.Emitters.Length; i++)
        {
            liveInstanceIds.Add(emitters[i].InstanceId);
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
        resources.Upload(snapshot, emitters);
        var entry = ParticleBarrier(VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit | VkAccessFlags.TransferWriteBit,
            VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit);
        frame.CommandBuffer.PipelineBarrier(VkPipelineStageFlags.TransferBit | VkPipelineStageFlags.VertexShaderBit | VkPipelineStageFlags.ComputeShaderBit,
            VkPipelineStageFlags.ComputeShaderBit, VkDependencyFlags.None, default(ReadOnlySpan<VkMemoryBarrier>),
            new ReadOnlySpan<VkBufferMemoryBarrier>(in entry), default(ReadOnlySpan<VkImageMemoryBarrier>));
        VkDescriptorSet set = DescriptorSetWriter.AllocateDescriptorSet(frame.Device, resources.Pool, computeLayout);
        ParticleGpuDrawResources.WriteBuffers(frame.Device, set, resources.ComputeBuffers, VkDescriptorType.StorageBuffer);
        frame.CommandBuffer.BindPipeline(VkPipelineBindPoint.Compute, kernel!.Pipeline);
        frame.CommandBuffer.BindDescriptorSet(VkPipelineBindPoint.Compute, kernel.PipelineLayout, 0, set, default(Span<ByteSize32>));
        for (int i = 0; i < emitters.Length; i++)
        {
            ParticleEmitterFrame emitterFrame = emitters[i];
            // Local-space transforms can move a particle below its floor even while game time is paused.
            if (emitterFrame.Parameters.State.X == 0f && emitterFrame.Parameters.Slots.Z == 0f &&
                emitterFrame.Parameters.State.Z == 0f && !emitterFrame.KillBelowFloor) continue;
            // Padding0 bit 0 = KillBelowFloor, upper bits = simulation phase; Padding1 = floor height bits.
            var push = new GpuSimPushConstants
            {
                EmitterIndex = (uint)i, FrameSeed = unchecked((uint)snapshot.Context.FrameIndex),
                Padding0 = emitterFrame.KillBelowFloor ? 1u : 0u,
                Padding1 = BitConverter.SingleToUInt32Bits(emitterFrame.BaseY),
            };
            int emitterCapacity = (int)emitterFrame.Parameters.Slots.Y;
            if (!ParticleSimulationDispatch.UsesParallelScan(emitterCapacity))
            {
                DispatchSimulation(frame, push, 1);
                continue;
            }
            int blocks = ParticleSimulationDispatch.BlockCount(emitterCapacity);
            push.Padding0 |= 2u; // Integrate and count dead slots in parallel.
            DispatchSimulation(frame, push, blocks);
            if (emitterFrame.Parameters.Slots.Z == 0f) continue;
            RecordSimulationStageBarrier(frame);
            push.Padding0 = (push.Padding0 & 1u) | 4u; // Exclusive prefix of block counts.
            DispatchSimulation(frame, push, 1);
            RecordSimulationStageBarrier(frame);
            push.Padding0 = (push.Padding0 & 1u) | 6u; // Respawn in cursor order.
            DispatchSimulation(frame, push, blocks);
            RecordSimulationStageBarrier(frame); // Scratch is reused by the next emitter.
        }
        var exit = ParticleBarrier(VkAccessFlags.ShaderWriteBit, VkAccessFlags.ShaderReadBit | VkAccessFlags.IndirectCommandReadBit);
        frame.CommandBuffer.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit,
            VkPipelineStageFlags.VertexShaderBit | VkPipelineStageFlags.DrawIndirectBit, VkDependencyFlags.None,
            default(ReadOnlySpan<VkMemoryBarrier>), new ReadOnlySpan<VkBufferMemoryBarrier>(in exit), default(ReadOnlySpan<VkImageMemoryBarrier>));
    }

    private void DispatchSimulation(in FrameContext frame, GpuSimPushConstants push, int groups)
    {
        frame.CommandBuffer.PushConstants(kernel!.PipelineLayout, VkShaderStageFlags.ComputeBit, 0,
            MemoryMarshal.AsBytes(new ReadOnlySpan<GpuSimPushConstants>(in push)));
        frame.CommandBuffer.Dispatch(groups, 1, 1);
    }

    private void RecordSimulationStageBarrier(in FrameContext frame)
    {
        Span<VkBufferMemoryBarrier> barriers = stackalloc VkBufferMemoryBarrier[2];
        barriers[0] = ParticleBarrier(VkAccessFlags.ShaderWriteBit, VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit);
        barriers[1] = new VkBufferMemoryBarrier
        {
            SrcAccessMask = VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit,
            DstAccessMask = VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit,
            SrcQueueFamilyIndex = -1, DstQueueFamilyIndex = -1,
            Buffer = deadBlocks!.Buffer, Offset = ByteSize.Zero, Size = deadBlocks.Size
        };
        frame.CommandBuffer.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.ComputeShaderBit,
            VkDependencyFlags.None, default(ReadOnlySpan<VkMemoryBarrier>), barriers, default(ReadOnlySpan<VkImageMemoryBarrier>));
    }

    private VkBufferMemoryBarrier ParticleBarrier(VkAccessFlags source, VkAccessFlags destination) => new()
    {
        SrcAccessMask = source, DstAccessMask = destination, SrcQueueFamilyIndex = -1, DstQueueFamilyIndex = -1,
        Buffer = particles!.Buffer, Offset = ByteSize.Zero, Size = particles.Size
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
    }

    private void RetireFrame(RenderFrame frame)
    {
        ulong number = unchecked((ulong)frame.Timing.FrameNumber);
        pendingFrames.Retire(number);
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
            application!.FrameRenderer.RenderThreadFrameStarting -= ReleaseOnFrameStart;
            application.FrameRenderer.RenderThreadFrameEnded -= RetireFrame;
            application = null;
        }
    }

    private void ReleaseGpu(Brutal.VulkanApi.Abstractions.DeviceEx device)
    {
        foreach (ParticleGpuFrame? slot in frameSlots) slot?.Dispose();
        frameSlots = Array.Empty<ParticleGpuFrame?>();
        drawResources?.Dispose();
        drawResources = null;
        particles?.Dispose();
        particles = null;
        deadBlocks?.Dispose();
        deadBlocks = null;
        if (!computeLayout.IsNull()) device.DestroyDescriptorSetLayout(computeLayout, device.VkAllocator);
        computeLayout = default;
        initializedInstances.Clear();
        liveInstanceIds.Clear();
    }

    private void DisposeGpu()
    {
        if (particles is not null)
        {
            EngineApplication app = EngineApplication.Instance;
            app.WaitForRenderThreadIdle();
            app.Device.WaitIdle();
            ReleaseGpu(app.Device);
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
