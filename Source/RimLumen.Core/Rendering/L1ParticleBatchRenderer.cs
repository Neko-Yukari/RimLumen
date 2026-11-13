using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;
using Verse;
using Waylith;
using Waylith.Rendering;
using Waylith.Rendering.Compute;
using Waylith.Rendering.PostFx;

namespace RimLumen.Core.Rendering;

// Draws the L1 CPU quad batch inside the RimLumenHdr pass.
// Threading: CpuParticleSimulator.RecordHdrDraws calls Submit on the render thread, but its vertex/index
// arrays are rewritten (and resized) by the next main-thread CaptureMainThread. So the pipeline stages the
// batch on the main thread right after capture, and Submit only records the draw of the staged frame.
internal sealed class L1ParticleBatchRenderer : IL1ParticleBatchSink, IDisposable
{
    private const int VertexStride = 36;
    private readonly EngineApplication app;
    private readonly DeviceEx device;
    private readonly object gate = new();
    private readonly Slot[] slots;
    private readonly List<GpuComputeBuffer> retired = new();
    private readonly Dictionary<(VkRenderPass Pass, bool Depth), VkPipeline> pipelines = new();
    private readonly int submissionSlots;
    private int nextSlot;
    private LumenHdrDrawContext? recording;
    private VkShaderModule vertexShader, fragmentShader, fragmentWithoutDepth;
    private VkDescriptorSetLayout setLayout;
    private VkDescriptorPool pool;
    private VkPipelineLayout layout;
    private VkSampler sampler;
    private VkDescriptorSet[] sets = Array.Empty<VkDescriptorSet>();
    private bool disposed;
    private long verticesRecorded;
    private int drawsRecorded;

    // Requires: main thread; app has a GraphicsDevice and RenderContext.
    // Ensures: allocates no per-frame buffers yet (lazy, sized by Stage).
    internal L1ParticleBatchRenderer(EngineApplication app)
    {
        this.app = app;
        device = app.Device;
        submissionSlots = app.RenderContext!.FramesInFlight;
        slots = new Slot[2 * submissionSlots + 2];
        for (int i = 0; i < slots.Length; i++) slots[i] = new Slot();
    }

    // Counters read by the probe session; monotonically increasing.
    internal long VerticesRecorded => Interlocked.Read(ref verticesRecorded);
    internal int DrawsRecorded => Volatile.Read(ref drawsRecorded);

    // Requires: main thread, after the simulator's CaptureMainThread for frameIndex (= (ulong)timing.FrameNumber).
    // Ensures: copies vertices/indices into a host-visible slot owned by frameIndex (slot reuse waits for the
    //   engine fence, like SsboBarrierProbeContributor); vertices.Length == 4n, indices.Length == 6n; n == 0 stages nothing.
    internal void Stage(ulong frameIndex, ReadOnlySpan<ParticleVertex> vertices, ReadOnlySpan<uint> indices)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Slot slot = slots[nextSlot];
            int index = nextSlot;
            nextSlot = (nextSlot + 1) % slots.Length;
            slot.FrameIndex = frameIndex;
            slot.Captured = true;
            slot.VertexCount = vertices.Length;
            slot.IndexCount = indices.Length;
            if (vertices.IsEmpty) return;
            Grow(ref slot.Vertices, vertices.Length * VertexStride, VkBufferUsageFlags.VertexBufferBit, $"L1.Vertices.{index}");
            Grow(ref slot.Indices, indices.Length * sizeof(uint), VkBufferUsageFlags.IndexBufferBit, $"L1.Indices.{index}");
            slot.Vertices!.Upload(vertices);
            slot.Indices!.Upload(indices);
        }
    }

    private void Grow(ref GpuComputeBuffer? buffer, int bytes, VkBufferUsageFlags usage, string name)
    {
        if (buffer is not null && (long)buffer.Size.Bytes >= bytes) return;
        long capacity = Math.Max(bytes, buffer is null ? bytes : (long)buffer.Size.Bytes * 2);
        GpuComputeBuffer replacement = GpuComputeBuffer.Create(device, app.GraphicsDevice!.Allocator,
            "RimLumen." + name, capacity, extraUsage: usage);
        Owned("VkBuffer", replacement.Buffer.VkHandle, name);
        // Keep replaced generations until the owner's idle shutdown; queued packets can still reference them.
        if (buffer is not null) retired.Add(buffer);
        buffer = replacement;
    }

    // Requires: render thread, inside the RimLumenHdr pass, before contributors' RecordHdrDraws for context.FrameIndex.
    // Ensures: the next Submit records into context.Frame (pipeline cached per VkRenderPass; premultiplied blend,
    //   depth test reverse-Z GreaterOrEqual, depth write off; soft fade from SceneColorDepthSource via RimLumenDepth.glslinc).
    internal void BeginRecord(in LumenHdrDrawContext context)
    {
        recording = context;
    }

    // Requires: render thread, between BeginRecord and EndRecord.
    // Ensures: records one indexed draw of the batch staged for the current frame; the spans are NOT read
    //   (see threading note). No staged batch or no BeginRecord => records nothing.
    public void Submit(ReadOnlySpan<ParticleVertex> vertices, ReadOnlySpan<uint> indices, int liveParticleCount)
    {
        if (recording is not { } context) return;
        lock (gate)
        {
            if (disposed) return;
            Slot? batch = null;
            foreach (Slot slot in slots)
                if (slot.Captured && slot.FrameIndex == context.FrameIndex) { batch = slot; break; }
            if (batch is null || batch.IndexCount == 0) return;
            EnsureResources();
            FrameContext frame = context.Frame;
            bool hasDepth = context.SceneColorDepthSource is not null;
            var key = (frame.RenderPass, hasDepth);
            if (!pipelines.TryGetValue(key, out VkPipeline pipeline))
            {
                pipeline = CreatePipeline(frame.RenderPass, hasDepth);
                pipelines.Add(key, pipeline);
            }
            CommandBuffer command = frame.CommandBuffer;
            command.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
            VkViewport viewport = new() { Width = frame.Extent.Width, Height = frame.Extent.Height, MinDepth = 0, MaxDepth = 1 };
            VkRect2D scissor = new(frame.Extent);
            command.SetViewport(0, new ReadOnlySpan<VkViewport>(in viewport));
            command.SetScissor(0, new ReadOnlySpan<VkRect2D>(in scissor));
            if (hasDepth)
            {
                WriteDepth(frame.SubmissionFrameIndex, context.SceneColorDepthSource!.DepthSampleView);
                command.BindDescriptorSet(VkPipelineBindPoint.Graphics, layout, 0, sets[frame.SubmissionFrameIndex], default(Span<ByteSize32>));
            }
            // Same raw matrix upload as MeshBatchRenderer.CreateFrameDescriptorSet; no additional transpose.
            Push push = new() { ViewProjection = context.ViewProjection, Depth = context.SoftParticleDepthParams };
            command.PushConstants(layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, ByteSize.Zero, push);
            VkBuffer vertexBuffer = batch.Vertices!.Buffer;
            ByteSize64 offset = ByteSize.Zero;
            command.BindVertexBuffers(0, new ReadOnlySpan<VkBuffer>(in vertexBuffer), new ReadOnlySpan<ByteSize64>(in offset));
            VkDeviceExtensions.BindIndexBuffer(command, batch.Indices!.Buffer, ByteSize.Zero, VkIndexType.UInt32);
            command.DrawIndexed(batch.IndexCount, 1, 0, 0, 0);
            Interlocked.Add(ref verticesRecorded, batch.VertexCount);
            Interlocked.Increment(ref drawsRecorded);
        }
    }

    // Requires: render thread. Ensures: drops the recording context set by BeginRecord.
    internal void EndRecord()
    {
        recording = null;
    }

    private unsafe void EnsureResources()
    {
        if (!layout.IsNull()) return;
        const string vertexPath = "Particles/L1ParticleQuad.vert.glsl";
        const string fragmentPath = "Particles/L1ParticleQuad.frag.glsl";
        vertexShader = PostFxPassHelpers.Compile(device, ShaderSourceLoader.ReadShaderSource(vertexPath), RuntimeShaderStage.Vertex, vertexPath);
        Owned("VkShaderModule", vertexShader.VkHandle, "L1.Vertex");
        string source = ShaderSourceLoader.ReadShaderSource(fragmentPath);
        fragmentShader = PostFxPassHelpers.Compile(device, source, RuntimeShaderStage.Fragment, fragmentPath);
        Owned("VkShaderModule", fragmentShader.VkHandle, "L1.FragmentDepth");
        // A descriptor-free variant makes null scene depth legal without binding an invalid dummy image.
        fragmentWithoutDepth = PostFxPassHelpers.Compile(device, source.Replace("#define L1_SCENE_DEPTH 1", "#define L1_SCENE_DEPTH 0"), RuntimeShaderStage.Fragment, fragmentPath + ".NoDepth");
        Owned("VkShaderModule", fragmentWithoutDepth.VkHandle, "L1.FragmentNoDepth");
        VkDescriptorSetLayoutBinding binding = new() { Binding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, StageFlags = VkShaderStageFlags.FragmentBit };
        setLayout = device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo { Bindings = new Span<VkDescriptorSetLayoutBinding>(&binding, 1) }, device.VkAllocator);
        Owned("VkDescriptorSetLayout", setLayout.VkHandle, "L1.Depth");
        VkPushConstantRange range = new() { StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, Offset = ByteSize.Zero, Size = ByteSize.Of(Marshal.SizeOf<Push>()) };
        layout = device.CreatePipelineLayout(new ReadOnlySpan<VkDescriptorSetLayout>(in setLayout), new ReadOnlySpan<VkPushConstantRange>(in range), device.VkAllocator);
        Owned("VkPipelineLayout", layout.VkHandle, "L1");
        VkDescriptorPoolSize size = new() { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = submissionSlots };
        pool = device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo { MaxSets = submissionSlots, PoolSizeCount = 1, PoolSizes = &size }, device.VkAllocator);
        Owned("VkDescriptorPool", pool.VkHandle, "L1");
        sets = new VkDescriptorSet[submissionSlots];
        for (int i = 0; i < sets.Length; i++)
        {
            sets[i] = DescriptorSetWriter.AllocateDescriptorSet(device, pool, setLayout);
            Owned("VkDescriptorSet", sets[i].VkHandle, $"L1.Depth.{i}");
        }
        sampler = device.CreateSampler(new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Nearest, MinFilter = VkFilter.Nearest, MipmapMode = VkSamplerMipmapMode.Nearest,
            AddressModeU = VkSamplerAddressMode.ClampToEdge, AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge, MaxLod = 0
        }, device.VkAllocator);
        Owned("VkSampler", sampler.VkHandle, "L1.Depth");
    }

    private unsafe void WriteDepth(int index, VkImageView view)
    {
        VkDescriptorImageInfo image = new() { Sampler = sampler, ImageView = view, ImageLayout = VkImageLayout.DepthStencilReadOnlyOptimal };
        VkWriteDescriptorSet write = new() { DstSet = sets[index], DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &image };
        device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(in write), default);
    }

    private unsafe VkPipeline CreatePipeline(VkRenderPass pass, bool hasDepth)
    {
        fixed (byte* entry = "main\0"u8)
        {
            VkPipelineShaderStageCreateInfo* stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new() { Stage = VkShaderStageFlags.VertexBit, Module = vertexShader, Name = entry };
            stages[1] = new() { Stage = VkShaderStageFlags.FragmentBit, Module = hasDepth ? fragmentShader : fragmentWithoutDepth, Name = entry };
            VkVertexInputBindingDescription binding = new() { Binding = 0, Stride = ByteSize.Of(VertexStride), InputRate = VkVertexInputRate.Vertex };
            VkVertexInputAttributeDescription* attributes = stackalloc VkVertexInputAttributeDescription[3];
            attributes[0] = new() { Location = 0, Binding = 0, Format = VkFormat.R32G32B32SFloat, Offset = ByteSize.Zero };
            attributes[1] = new() { Location = 1, Binding = 0, Format = VkFormat.R32G32SFloat, Offset = ByteSize.Of(12) };
            attributes[2] = new() { Location = 2, Binding = 0, Format = VkFormat.R32G32B32A32SFloat, Offset = ByteSize.Of(20) };
            VkPipelineVertexInputStateCreateInfo input = new() { VertexBindingDescriptionCount = 1, VertexBindingDescriptions = &binding, VertexAttributeDescriptionCount = 3, VertexAttributeDescriptions = attributes };
            VkPipelineInputAssemblyStateCreateInfo assembly = new() { Topology = VkPrimitiveTopology.TriangleList };
            VkPipelineViewportStateCreateInfo viewport = new() { ViewportCount = 1, ScissorCount = 1 };
            VkPipelineRasterizationStateCreateInfo raster = new() { PolygonMode = VkPolygonMode.Fill, CullMode = VkCullModeFlags.None, FrontFace = VkFrontFace.CounterClockwise, LineWidth = 1 };
            VkPipelineMultisampleStateCreateInfo samples = new() { RasterizationSamples = VkSampleCountFlags._1Bit };
            VkPipelineDepthStencilStateCreateInfo depth = new() { DepthTestEnable = true, DepthWriteEnable = false, DepthCompareOp = VkCompareOp.GreaterOrEqual, StencilTestEnable = false };
            VkPipelineColorBlendAttachmentState blend = new()
            {
                BlendEnable = true, SrcColorBlendFactor = VkBlendFactor.One, DstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                SrcAlphaBlendFactor = VkBlendFactor.One, DstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = VkBlendOp.Add, AlphaBlendOp = VkBlendOp.Add,
                ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit
            };
            VkPipelineColorBlendStateCreateInfo blending = new() { AttachmentCount = 1, Attachments = &blend };
            VkDynamicState* states = stackalloc VkDynamicState[2] { VkDynamicState.Viewport, VkDynamicState.Scissor };
            VkPipelineDynamicStateCreateInfo dynamic = new() { DynamicStateCount = 2, DynamicStates = states };
            VkGraphicsPipelineCreateInfo create = new()
            {
                StageCount = 2, Stages = stages, VertexInputState = &input, InputAssemblyState = &assembly,
                ViewportState = &viewport, RasterizationState = &raster, MultisampleState = &samples,
                DepthStencilState = &depth, ColorBlendState = &blending, DynamicState = &dynamic,
                Layout = layout, RenderPass = pass, Subpass = 0
            };
            Span<VkPipeline> result = stackalloc VkPipeline[1];
            VkResult status = device.CreateGraphicsPipelines(default, new ReadOnlySpan<VkGraphicsPipelineCreateInfo>(in create), device.VkAllocator, result);
            if (status != VkResult.Success) throw new InvalidOperationException($"L1 particle pipeline creation failed: {status}");
            Owned("VkPipeline", result[0].VkHandle, hasDepth ? "L1.Depth" : "L1.NoDepth");
            return result[0];
        }
    }

    private static void Owned(string type, nint handle, string name)
        => Log.Message($"[RimLumen] OwnedVkObject type={type} handle=0x{handle:X} name={name}");

    // Requires: render thread idle and GPU idle (called from RimLumenPipeline.Dispose after Quiesce).
    // Ensures: destroys every buffer/pipeline/layout/descriptor pool/sampler it created; idempotent.
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            recording = null;
            foreach (Slot slot in slots) { slot.Vertices?.DisposeNow(); slot.Indices?.DisposeNow(); }
            foreach (GpuComputeBuffer buffer in retired) buffer.DisposeNow();
            retired.Clear();
            foreach (VkPipeline pipeline in pipelines.Values) device.DestroyPipeline(pipeline, device.VkAllocator);
            pipelines.Clear();
            if (!pool.IsNull()) device.DestroyDescriptorPool(pool, device.VkAllocator);
            if (!sampler.IsNull()) device.DestroySampler(sampler, device.VkAllocator);
            if (!layout.IsNull()) device.DestroyPipelineLayout(layout, device.VkAllocator);
            if (!setLayout.IsNull()) device.DestroyDescriptorSetLayout(setLayout, device.VkAllocator);
            if (!vertexShader.IsNull()) device.DestroyShaderModule(vertexShader, device.VkAllocator);
            if (!fragmentShader.IsNull()) device.DestroyShaderModule(fragmentShader, device.VkAllocator);
            if (!fragmentWithoutDepth.IsNull()) device.DestroyShaderModule(fragmentWithoutDepth, device.VkAllocator);
        }
    }

    private sealed class Slot
    {
        internal ulong FrameIndex;
        internal bool Captured;
        internal int VertexCount, IndexCount;
        internal GpuComputeBuffer? Vertices, Indices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Push
    {
        internal Matrix4x4 ViewProjection;
        internal Vector4 Depth;
    }
}
