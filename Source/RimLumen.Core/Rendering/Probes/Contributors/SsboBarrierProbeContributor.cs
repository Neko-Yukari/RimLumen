using System;
using System.Runtime.InteropServices;
using System.Threading;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RimLumen.Core.Contracts;
using Waylith;
using Waylith.Rendering;
using Waylith.Rendering.Compute;

namespace RimLumen.Core.Rendering.Probes.Contributors;

// T1b: in-frame ComputeDispatchQueue.Enqueue writes per-instance quad data into a GpuComputeBuffer,
// a VkBufferMemoryBarrier (ComputeShader -> VertexShader|DrawIndirect, ShaderWrite -> ShaderRead|IndirectCommandRead)
// is recorded outside the render pass, then the HDR pass draws instances by reading that SSBO in the vertex shader.
public sealed class SsboBarrierProbeContributor : ILumenFrameContributor, IDisposable
{
    internal const int InstanceCount = 32;
    internal const int InstanceBytes = 32;
    private const string ComputeShader = "Probes/SsboQuads.comp.glsl";
    private const string VertexShader = "Probes/SsboQuads.vert.glsl";
    private const string FragmentShader = "Probes/SsboQuads.frag.glsl";

    private readonly bool disableBarrier;
    private readonly object gate = new();
    private Slot[] slots = Array.Empty<Slot>();
    private ProbeDrawResources? resources;
    private ComputeKernel? kernel;
    private bool armed;
    private bool disposed;
    private int dispatchesEnqueued;
    private int barriersRecorded;
    private int drawsRecorded;

    // Requires: disableBarrier only for the red/control run (-rimlumen-probe-nobarrier).
    public SsboBarrierProbeContributor(bool disableBarrier)
    {
        this.disableBarrier = disableBarrier;
    }

    public string Name => "RimLumen.Probe.SsboBarrier";

    // Ensures: true while the probe is armed (Arm() called, Disarm() not yet).
    public bool HasActiveWork => Volatile.Read(ref armed);

    // Counters read by the probe session after the run; monotonically increasing.
    public int DispatchesEnqueued => Volatile.Read(ref dispatchesEnqueued);
    public int BarriersRecorded => Volatile.Read(ref barriersRecorded);
    public int DrawsRecorded => Volatile.Read(ref drawsRecorded);

    // Requires: main thread. Ensures: HasActiveWork becomes true from the next CaptureMainThread.
    public void Arm()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            armed = true;
        }
    }

    // Requires: main thread. Ensures: HasActiveWork false; GPU resources kept until Dispose.
    public void Disarm()
    {
        lock (gate)
        {
            armed = false;
            foreach (Slot slot in slots) slot.EnqueuedFrame = 0;
        }
    }

    public void CaptureMainThread(in LumenCaptureContext context)
    {
        if (!HasActiveWork) return;
        lock (gate)
        {
            if (slots.Length == 0) CreateResources();
            SlotFor(context.FrameIndex).Aspect = (float)context.ViewportWidth / context.ViewportHeight;
        }
    }

    public void EnqueuePreCompute(in LumenComputeContext context)
    {
        if (!HasActiveWork) return;
        Slot slot;
        lock (gate)
        {
            if (slots.Length == 0) return;
            slot = SlotFor(context.FrameIndex);
            if (kernel is null) return;
            slot.EnqueuedFrame = context.FrameIndex + 1;
        }

        ComputeDispatch dispatch = ComputeDispatch.Rent(kernel!, 1, 1, 1);
        dispatch.SetStorageBuffer(0, slot.Instances);
        ComputePush push = new()
        {
            FramePhase = (float)(context.FrameIndex % 4096) * 0.137f,
            QuadSize = 0.035f,
            Aspect = slot.Aspect,
            Reserved = 0f
        };
        dispatch.SetPushConstants(in push);
        ComputeDispatchQueue.Enqueue(dispatch, this, slot.Index);
        System.Threading.Interlocked.Increment(ref dispatchesEnqueued);
    }

    public void RecordBufferBarriers(in LumenBarrierContext context)
    {
        if (!HasActiveWork || disableBarrier) return;
        Slot slot;
        lock (gate)
        {
            if (slots.Length == 0) return;
            slot = SlotFor(context.FrameIndex);
            if (slot.EnqueuedFrame != context.FrameIndex + 1) return;
        }

        Span<VkBufferMemoryBarrier> bars = stackalloc VkBufferMemoryBarrier[1];
        bars[0] = new VkBufferMemoryBarrier
        {
            SrcAccessMask = VkAccessFlags.ShaderWriteBit,
            DstAccessMask = VkAccessFlags.ShaderReadBit | VkAccessFlags.IndirectCommandReadBit,
            SrcQueueFamilyIndex = -1,
            DstQueueFamilyIndex = -1,
            Buffer = slot.Instances.Buffer,
            Offset = ByteSize.Zero,
            Size = ByteSize.Of(InstanceCount * InstanceBytes)
        };
        context.Frame.CommandBuffer.PipelineBarrier(
            VkPipelineStageFlags.ComputeShaderBit,
            VkPipelineStageFlags.VertexShaderBit | VkPipelineStageFlags.DrawIndirectBit,
            VkDependencyFlags.None,
            default,
            bars,
            default);
        System.Threading.Interlocked.Increment(ref barriersRecorded);
    }

    public void RecordHdrDraws(in LumenHdrDrawContext context)
    {
        if (!HasActiveWork) return;
        Slot slot;
        ProbeDrawResources draw;
        lock (gate)
        {
            if (slots.Length == 0) return;
            slot = SlotFor(context.FrameIndex);
            if (slot.EnqueuedFrame != context.FrameIndex + 1) return;
            draw = resources!;
        }

        FrameContext frame = context.Frame;
        draw.Bind(in frame, slot.Index);
        frame.CommandBuffer.Draw(6, InstanceCount, 0, 0);
        System.Threading.Interlocked.Increment(ref drawsRecorded);
    }

    // Ensures: GPU buffers/pipelines released via the engine's deferred disposal.
    public void Dispose()
    {
        Slot[] old;
        ProbeDrawResources? draw;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            armed = false;
            old = slots;
            slots = Array.Empty<Slot>();
            draw = resources;
            resources = null;
        }
        ComputeDispatchQueue.CancelPending(this);
        DestroyAfterIdle(draw);
        foreach (Slot slot in old)
        {
            slot.Instances.Dispose();
        }
    }

    private void CreateResources()
    {
        EngineApplication app = EngineApplication.Instance ?? throw new InvalidOperationException("Waylith 主循环尚未创建。");
        DeviceEx device = app.Device;
        int slotCount = app.RenderContext!.FramesInFlight + 2;

        slots = new Slot[slotCount];
        for (int i = 0; i < slotCount; i++)
        {
            slots[i] = new Slot
            {
                Index = i,
                Instances = GpuTextureService.CreateComputeBuffer($"RimLumen.Probe.Ssbo{i}",
                    InstanceCount * InstanceBytes) ?? throw new InvalidOperationException("GPU compute buffer service is not configured.")
            };
        }
        resources = new ProbeDrawResources(device, VertexShader, FragmentShader, depthTest: false,
            0, VkDescriptorType.StorageBuffer, slotCount);
        for (int i = 0; i < slotCount; i++)
            resources.WriteStorage(i, slots[i].Instances.Buffer, ByteSize.Of(InstanceCount * InstanceBytes));
        kernel = ComputeKernelCache.Load(ComputeShader, "main",
            new ReadOnlySpan<ComputeBinding>(new[]
            {
                new ComputeBinding(0, ComputeBindingType.StorageBuffer)
            }), Marshal.SizeOf<ComputePush>());
    }

    private Slot SlotFor(ulong frameIndex) => slots[(int)(frameIndex % (ulong)slots.Length)];

    private static void DestroyAfterIdle(ProbeDrawResources? draw)
    {
        if (draw is null) return;
        EngineApplication? app = EngineApplication.Instance;
        if (app?.GraphicsDevice is null) return;
        app.WaitForRenderThreadIdle();
        app.GraphicsDevice.WaitIdleAndRun(draw.DestroyAfterIdle);
    }

    private sealed class Slot
    {
        internal int Index;
        internal float Aspect;
        internal GpuComputeBuffer Instances = null!;
        // FrameIndex + 1 of the dispatch enqueued into this slot; 0 means "nothing pending".
        internal ulong EnqueuedFrame;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ComputePush
    {
        public float FramePhase;
        public float QuadSize;
        public float Aspect;
        public float Reserved;
    }
}
