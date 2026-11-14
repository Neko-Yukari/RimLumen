using System;
using System.Collections.Generic;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Waylith.Rendering;
using Waylith.Rendering.PostFx;

namespace RimLumen.Core.Rendering.Probes.Contributors;

// One graphics pipeline for the probe pass: set 0 holds either no binding, the instance SSBO (vertex
// stage) or the vanilla scene depth (fragment stage). The render pass belongs to the lead pipeline and
// can be rebuilt on resize, so pipelines are cached per VkRenderPass handle.
internal sealed class ProbeDrawResources
{
    private readonly DeviceEx device;
    private readonly VkShaderModule vertex;
    private readonly VkShaderModule fragment;
    private readonly VkDescriptorSetLayout setLayout;
    private readonly VkDescriptorPool pool;
    private readonly bool depthTest;
    private readonly VkDescriptorType descriptorType;
    private readonly Dictionary<VkRenderPass, VkPipeline> pipelines = new();

    internal VkPipelineLayout Layout { get; }
    internal VkDescriptorSet[] Sets { get; }

    internal unsafe ProbeDrawResources(DeviceEx device, string vertexPath, string fragmentPath, bool depthTest,
        int pushBytes, VkDescriptorType? descriptorType, int setCount)
    {
        this.device = device;
        this.depthTest = depthTest;
        this.descriptorType = descriptorType ?? default;
        vertex = PostFxPassHelpers.Compile(device, ShaderSourceLoader.ReadShaderSource(vertexPath), RuntimeShaderStage.Vertex, vertexPath);
        fragment = PostFxPassHelpers.CompileFragment(device, fragmentPath);

        VkPushConstantRange push = new()
        {
            StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
            Offset = ByteSize.Zero,
            Size = ByteSize.Of(pushBytes)
        };

        if (!descriptorType.HasValue)
        {
            Sets = Array.Empty<VkDescriptorSet>();
            Layout = device.CreatePipelineLayout(default,
                pushBytes > 0 ? new ReadOnlySpan<VkPushConstantRange>(in push) : default, device.VkAllocator);
            return;
        }

        VkDescriptorSetLayoutBinding binding = new()
        {
            Binding = 0,
            DescriptorCount = 1,
            DescriptorType = descriptorType.Value,
            StageFlags = descriptorType == VkDescriptorType.StorageBuffer ? VkShaderStageFlags.VertexBit : VkShaderStageFlags.FragmentBit
        };
        setLayout = device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo
        {
            Bindings = new Span<VkDescriptorSetLayoutBinding>(&binding, 1)
        }, device.VkAllocator);
        VkDescriptorPoolSize size = new() { Type = descriptorType.Value, DescriptorCount = setCount };
        pool = device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo
        {
            MaxSets = setCount, PoolSizeCount = 1, PoolSizes = &size
        }, device.VkAllocator);
        Sets = new VkDescriptorSet[setCount];
        for (int i = 0; i < setCount; i++)
            Sets[i] = DescriptorSetWriter.AllocateDescriptorSet(device, pool, setLayout);

        VkDescriptorSetLayout layout = setLayout;
        Layout = device.CreatePipelineLayout(new ReadOnlySpan<VkDescriptorSetLayout>(in layout),
            pushBytes > 0 ? new ReadOnlySpan<VkPushConstantRange>(in push) : default, device.VkAllocator);
    }

    internal void Bind(in FrameContext frame, int setIndex)
    {
        if (!pipelines.TryGetValue(frame.RenderPass, out VkPipeline pipeline))
        {
            pipeline = CreatePipeline(frame.RenderPass);
            pipelines.Add(frame.RenderPass, pipeline);
        }
        CommandBuffer command = frame.CommandBuffer;
        command.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
        VkViewport viewport = new() { Width = frame.Extent.Width, Height = frame.Extent.Height, MinDepth = 0, MaxDepth = 1 };
        VkRect2D scissor = new(frame.Extent);
        command.SetViewport(0, new ReadOnlySpan<VkViewport>(in viewport));
        command.SetScissor(0, new ReadOnlySpan<VkRect2D>(in scissor));
        if (setIndex >= 0)
            command.BindDescriptorSet(VkPipelineBindPoint.Graphics, Layout, 0, Sets[setIndex], default(Span<ByteSize32>));
    }

    internal void Push<T>(in FrameContext frame, in T data) where T : unmanaged
    {
        frame.CommandBuffer.PushConstants(Layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, ByteSize.Zero, data);
    }

    internal unsafe void WriteStorage(int setIndex, VkBuffer buffer, ByteSize size)
    {
        VkDescriptorBufferInfo info = new() { Buffer = buffer, Offset = ByteSize.Zero, Range = size };
        VkWriteDescriptorSet write = new()
        {
            DstSet = Sets[setIndex], DstBinding = 0, DescriptorCount = 1,
            DescriptorType = descriptorType, BufferInfo = &info
        };
        device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(in write), default);
    }

    internal unsafe void WriteDepth(int setIndex, in VkImageView view, VkSampler sampler, VkImageLayout layout)
    {
        VkDescriptorImageInfo info = new()
        {
            Sampler = sampler, ImageView = view, ImageLayout = layout
        };
        VkWriteDescriptorSet write = new()
        {
            DstSet = Sets[setIndex], DstBinding = 0, DescriptorCount = 1,
            DescriptorType = descriptorType, ImageInfo = &info
        };
        device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(in write), default);
    }

    // Only call after EngineApplication.WaitForRenderThreadIdle + GraphicsDevice.WaitIdleAndRun.
    internal void DestroyAfterIdle()
    {
        foreach (VkPipeline pipeline in pipelines.Values)
            device.DestroyPipeline(pipeline, device.VkAllocator);
        pipelines.Clear();
        if (Sets.Length > 0)
        {
            device.DestroyDescriptorPool(pool, device.VkAllocator);
            device.DestroyDescriptorSetLayout(setLayout, device.VkAllocator);
        }
        device.DestroyPipelineLayout(Layout, device.VkAllocator);
        device.DestroyShaderModule(vertex, device.VkAllocator);
        device.DestroyShaderModule(fragment, device.VkAllocator);
    }

    private unsafe VkPipeline CreatePipeline(VkRenderPass renderPass)
    {
        fixed (byte* entry = "main\0"u8)
        {
            VkPipelineShaderStageCreateInfo* stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new() { Stage = VkShaderStageFlags.VertexBit, Module = vertex, Name = entry };
            stages[1] = new() { Stage = VkShaderStageFlags.FragmentBit, Module = fragment, Name = entry };
            VkPipelineVertexInputStateCreateInfo input = new();
            VkPipelineInputAssemblyStateCreateInfo assembly = new() { Topology = VkPrimitiveTopology.TriangleList };
            VkPipelineViewportStateCreateInfo viewport = new() { ViewportCount = 1, ScissorCount = 1 };
            VkPipelineRasterizationStateCreateInfo raster = new()
            {
                PolygonMode = VkPolygonMode.Fill,
                CullMode = VkCullModeFlags.None,
                FrontFace = VkFrontFace.CounterClockwise,
                LineWidth = 1
            };
            VkPipelineMultisampleStateCreateInfo samples = new() { RasterizationSamples = VkSampleCountFlags._1Bit };
            VkPipelineDepthStencilStateCreateInfo depth = new()
            {
                DepthTestEnable = depthTest,
                DepthWriteEnable = false,
                DepthCompareOp = depthTest ? VkCompareOp.GreaterOrEqual : VkCompareOp.Always,
                StencilTestEnable = false
            };
            VkPipelineColorBlendAttachmentState blend = new()
            {
                BlendEnable = true,
                SrcColorBlendFactor = VkBlendFactor.One,
                DstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                SrcAlphaBlendFactor = VkBlendFactor.One,
                DstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = VkBlendOp.Add,
                AlphaBlendOp = VkBlendOp.Add,
                ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit
            };
            VkPipelineColorBlendStateCreateInfo blending = new() { AttachmentCount = 1, Attachments = &blend };
            VkDynamicState* dynamicStates = stackalloc VkDynamicState[2] { VkDynamicState.Viewport, VkDynamicState.Scissor };
            VkPipelineDynamicStateCreateInfo dynamic = new() { DynamicStateCount = 2, DynamicStates = dynamicStates };
            VkGraphicsPipelineCreateInfo create = new()
            {
                StageCount = 2,
                Stages = stages,
                VertexInputState = &input,
                InputAssemblyState = &assembly,
                ViewportState = &viewport,
                RasterizationState = &raster,
                MultisampleState = &samples,
                DepthStencilState = &depth,
                ColorBlendState = &blending,
                DynamicState = &dynamic,
                Layout = Layout,
                RenderPass = renderPass,
                Subpass = 0
            };
            Span<VkPipeline> result = stackalloc VkPipeline[1];
            VkResult status = device.CreateGraphicsPipelines(default, new ReadOnlySpan<VkGraphicsPipelineCreateInfo>(in create), device.VkAllocator, result);
            if (status != VkResult.Success)
                throw new InvalidOperationException($"Probe graphics pipeline creation failed: {status}");
            return result[0];
        }
    }
}
