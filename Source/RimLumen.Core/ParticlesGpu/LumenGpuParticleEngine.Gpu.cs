using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RimLumen.Core.Contracts;
using Waylith;
using Waylith.Rendering;
using Waylith.Rendering.Compute;

namespace RimLumen.Core.ParticlesGpu;

public sealed partial class LumenGpuParticleEngine
{
    private readonly Dictionary<string, Texture> registeredTextures = new(StringComparer.Ordinal);
    private readonly HashSet<string> warnedTextures = new(StringComparer.Ordinal);
    private readonly ParticleFrameQueue pendingFrames = new();
    private ParticleGpuDrawResources? drawResources;
    private GpuComputeBuffer? particles;
    private List<ParticleGpuFrame>[] frameResources = Array.Empty<List<ParticleGpuFrame>>();
    private ParticleGpuFrame? drawFrameResources;
    private int frameResourceCursor;
    private bool idleRequested;
    private EngineApplication? application;
    private readonly object lifecycleGate = new();
    private VkDescriptorSetLayout computeLayout;
    private ComputeKernel? kernel;
    private readonly HashSet<int> initializedInstances = new();
    private ParticleQueuedFrame? recording;

    // Textures remain owned by the registrant; registration and removal are main-thread operations.
    public void RegisterTexture(string key, Texture texture)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(texture);
        registeredTextures[key] = texture;
    }

    public void UnregisterTexture(string key)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        registeredTextures.Remove(key);
    }

    // Requires: capture completed with active work.
    // Ensures: queues simulation without particle readback.
    public void EnqueuePreCompute(in LumenComputeContext context)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ParticleFrame snapshot = captured ?? throw new InvalidOperationException("Capture must precede enqueue.");
        if (snapshot.Context.FrameIndex != context.FrameIndex)
            throw new InvalidOperationException("Compute and capture frame indices differ.");
        captured = null;
        if (snapshot.Emitters.Length == 0) return;
        var textures = new Texture[snapshot.Emitters.Length];
        var present = new bool[textures.Length];
        for (int i = 0; i < textures.Length; i++)
        {
            string? key = snapshot.Emitters[i].TextureKey;
            Texture? texture = null;
            if (!string.IsNullOrEmpty(key))
            {
                if (!registeredTextures.TryGetValue(key, out texture)) texture = ResolveHostTexture(key);
                if (texture is not null)
                {
                    Texture.EnsureSampledReady(texture);
                    if (texture.GpuResource is null && !texture.EnsureResident()) texture = null;
                }
                if (texture is null && warnedTextures.Add(key)) WarnMissingTexture(key);
            }
            present[i] = texture is not null;
            textures[i] = texture ?? Texture2D.whiteTexture;
            Texture.EnsureSampledReady(textures[i]);
        }
        lock (lifecycleGate)
        {
            if (application is null)
            {
                application = EngineApplication.Instance;
                application.FrameRenderer.RenderThreadFrameStarting += ReleaseOnFrameStart;
                application.FrameRenderer.RenderThreadFrameEnded += RetireFrame;
            }
            pendingFrames.Enqueue(new ParticleQueuedFrame(snapshot, textures, present));
        }
    }

    // Kept out of constructor/capture so pure lifecycle tests never enter the host Content system.
    private static Texture? ResolveHostTexture(string key) =>
        Verse.ContentFinder<UnityEngine.Texture2D>.Get(key, reportFailure: false)?.CoreTexture;

    private static void WarnMissingTexture(string key) =>
        Verse.Log.Warning($"[RimLumen] Particle texture '{key}' was not found; using the procedural particle shape.");

    // Requires: open command buffer outside render pass.
    // Ensures: compute writes become visible to vertex/indirect consumers.
    public void RecordBufferBarriers(in LumenBarrierContext context)
    {
        if (!pendingFrames.TryTake(context.FrameIndex, out ParticleQueuedFrame? frame)) return;
        frameResourceCursor = 0;
        EnsureGpu(context.Frame);
        RecordSimulation(context.Frame, frame!.Snapshot);
        recording = frame;
    }

    private void EnsureGpu(in FrameContext frame)
    {
        if (particles is not null) return;
        particles = GpuComputeBuffer.Create(frame.Device, frame.GraphicsDevice.Allocator,
            "Lumen.Particles", (long)capacity * 64, deviceLocal: true);
        drawResources = new ParticleGpuDrawResources(frame);
        frameResources = new List<ParticleGpuFrame>[frame.RenderContext.FramesInFlight];
        for (int i = 0; i < frameResources.Length; i++) frameResources[i] = new List<ParticleGpuFrame>();
        computeLayout = ParticleGpuDrawResources.CreateLayout(frame.Device, new[]
        {
            ParticleGpuDrawResources.Binding(0, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit),
            ParticleGpuDrawResources.Binding(1, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit),
            ParticleGpuDrawResources.Binding(2, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit)
        });
        kernel = ComputeKernelCache.Load("Particles/ParticleSim.comp.glsl", "main", new[]
        {
            new ComputeBinding(0, ComputeBindingType.StorageBuffer),
            new ComputeBinding(1, ComputeBindingType.StorageBuffer),
            new ComputeBinding(2, ComputeBindingType.StorageBuffer)
        }, 16);
    }

    private void RecordSimulation(in FrameContext frame, ParticleFrame snapshot)
    {
        int slot = frame.SubmissionFrameIndex;
        List<ParticleGpuFrame> ringSlot = frameResources[slot];
        int requiredRows = snapshot.Emitters.Length + snapshot.Clears.Length;
        ParticleGpuFrame resources;
        if (frameResourceCursor == ringSlot.Count)
        {
            resources = new ParticleGpuFrame(frame, requiredRows, snapshot.Fields.Length);
            ringSlot.Add(resources);
        }
        else
        {
            resources = ringSlot[frameResourceCursor];
            if (resources.EmitterCapacity < requiredRows || resources.FieldCapacity < snapshot.Fields.Length)
            {
                resources.Dispose();
                resources = new ParticleGpuFrame(frame, requiredRows, snapshot.Fields.Length);
                ringSlot[frameResourceCursor] = resources;
            }
        }
        frameResourceCursor++;
        drawFrameResources = resources;
        // Do not clear on CPU capture: frames may be dropped before recording, and
        // allocated ranges can already have a newer owner in a subsequent snapshot.
        var emitters = new ParticleEmitterFrame[snapshot.Emitters.Length + snapshot.Clears.Length];
        snapshot.Emitters.CopyTo(emitters, 0);
        var liveIds = new HashSet<int>();
        for (int i = 0; i < snapshot.Emitters.Length; i++)
        {
            liveIds.Add(emitters[i].InstanceId);
            if (initializedInstances.Add(emitters[i].InstanceId))
            {
                GpuEmitterParams parameters = emitters[i].Parameters;
                parameters.State.Z = 1;
                emitters[i] = emitters[i] with { Parameters = parameters };
            }
        }
        initializedInstances.IntersectWith(liveIds);
        for (int i = 0; i < snapshot.Clears.Length; i++)
        {
            EmitterSlotRange range = snapshot.Clears[i];
            emitters[snapshot.Emitters.Length + i] = new ParticleEmitterFrame(-1, new GpuEmitterParams
            {
                Slots = new System.Numerics.Vector4(range.Offset, range.Capacity, 0, 0),
                State = new System.Numerics.Vector4(0, 0, 1, 0)
            }, Array.Empty<GpuCurveSample>(), null, LumenBlendMode.Additive);
        }
        snapshot = snapshot with { Emitters = emitters };
        resources.Upload(snapshot);
        var entry = ParticleBarrier(VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit | VkAccessFlags.TransferWriteBit,
            VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit);
        frame.CommandBuffer.PipelineBarrier(VkPipelineStageFlags.TransferBit | VkPipelineStageFlags.VertexShaderBit | VkPipelineStageFlags.ComputeShaderBit,
            VkPipelineStageFlags.ComputeShaderBit, VkDependencyFlags.None, default(ReadOnlySpan<VkMemoryBarrier>),
            new ReadOnlySpan<VkBufferMemoryBarrier>(in entry), default(ReadOnlySpan<VkImageMemoryBarrier>));
        VkDescriptorSet set = DescriptorSetWriter.AllocateDescriptorSet(frame.Device, resources.Pool, computeLayout);
        ParticleGpuDrawResources.WriteBuffers(frame.Device, set, new[] { 0, 1, 2 },
            new[] { particles!, resources.Emitters, resources.Fields }, VkDescriptorType.StorageBuffer);
        frame.CommandBuffer.BindPipeline(VkPipelineBindPoint.Compute, kernel!.Pipeline);
        frame.CommandBuffer.BindDescriptorSet(VkPipelineBindPoint.Compute, kernel.PipelineLayout, 0, set, default(Span<ByteSize32>));
        for (int i = 0; i < snapshot.Emitters.Length; i++)
        {
            var push = new GpuSimPushConstants { EmitterIndex = (uint)i, FrameSeed = unchecked((uint)snapshot.Context.FrameIndex) };
            frame.CommandBuffer.PushConstants(kernel.PipelineLayout, VkShaderStageFlags.ComputeBit, 0,
                MemoryMarshal.AsBytes(new ReadOnlySpan<GpuSimPushConstants>(in push)));
            frame.CommandBuffer.Dispatch(1, 1, 1);
        }
        var exit = ParticleBarrier(VkAccessFlags.ShaderWriteBit, VkAccessFlags.ShaderReadBit | VkAccessFlags.IndirectCommandReadBit);
        frame.CommandBuffer.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit,
            VkPipelineStageFlags.VertexShaderBit | VkPipelineStageFlags.DrawIndirectBit, VkDependencyFlags.None,
            default(ReadOnlySpan<VkMemoryBarrier>), new ReadOnlySpan<VkBufferMemoryBarrier>(in exit), default(ReadOnlySpan<VkImageMemoryBarrier>));
    }

    private VkBufferMemoryBarrier ParticleBarrier(VkAccessFlags source, VkAccessFlags destination) => new()
    {
        SrcAccessMask = source, DstAccessMask = destination, SrcQueueFamilyIndex = -1, DstQueueFamilyIndex = -1,
        Buffer = particles!.Buffer, Offset = ByteSize.Zero, Size = particles.Size
    };

    // Requires: inside compatible HDR render pass.
    // Ensures: draws particle SSBO through instanced billboards.
    public void RecordHdrDraws(in LumenHdrDrawContext context)
    {
        ParticleQueuedFrame? frame = recording;
        if (frame is null || frame.Snapshot.Emitters.Length == 0) return;
        if (frame.Snapshot.Context.FrameIndex != context.FrameIndex)
            throw new InvalidOperationException("HDR draw must follow simulation of the same captured frame.");
        drawResources!.Draw(context, frame.Snapshot, drawFrameResources!,
            particles!, frame.Textures, frame.TexturePresent);
        recording = null;
    }

    private void RetireFrame(RenderFrame frame)
    {
        ulong number = unchecked((ulong)frame.Timing.FrameNumber);
        pendingFrames.Retire(number);
        if (recording?.Snapshot.Context.FrameIndex == number) recording = null;
    }

    private void ReleaseOnFrameStart()
    {
        lock (lifecycleGate)
        {
            if (!Volatile.Read(ref idleRequested) || !pendingFrames.IsEmpty) return;
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
        foreach (List<ParticleGpuFrame> slot in frameResources)
            foreach (ParticleGpuFrame frame in slot) frame.Dispose();
        frameResources = Array.Empty<List<ParticleGpuFrame>>();
        drawFrameResources = null;
        drawResources?.Dispose(); drawResources = null;
        particles?.Dispose(); particles = null;
        if (!computeLayout.IsNull()) device.DestroyDescriptorSetLayout(computeLayout, device.VkAllocator);
        computeLayout = default;
        initializedInstances.Clear();
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
        registeredTextures.Clear();
    }
}
