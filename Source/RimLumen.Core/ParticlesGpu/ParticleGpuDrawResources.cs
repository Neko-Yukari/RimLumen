using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RimLumen.Core.Contracts;
using Waylith.Rendering;
using Waylith.Rendering.Compute;
using Waylith.Rendering.PostFx;

namespace RimLumen.Core.ParticlesGpu;

// All entry points belong to the render thread. A slot is touched only after
// TryAcquireNextFrame has waited for its submission fence (RenderThread:249-253).
internal sealed class ParticleGpuDrawResources : IDisposable
{
    private readonly DeviceEx device;
    private readonly VkDescriptorSetLayout frameLayout;
    private readonly VkDescriptorSetLayout emptyLayout;
    private readonly VkDescriptorSetLayout materialLayout;
    private readonly VkPipelineLayout layout;
    private readonly VkShaderModule vertex;
    private readonly VkShaderModule fragment;
    private readonly Dictionary<(VkRenderPass, VkSampleCountFlags, LumenBlendMode), VkPipeline> pipelines = new();

    internal ParticleGpuDrawResources(in FrameContext frame)
    {
        device = frame.Device;
        frameLayout = CreateLayout(device, new[] { Binding(0, VkDescriptorType.UniformBuffer, VkShaderStageFlags.VertexBit) });
        emptyLayout = CreateLayout(device, Array.Empty<VkDescriptorSetLayoutBinding>());
        materialLayout = CreateLayout(device, new[]
        {
            Binding(0, VkDescriptorType.UniformBuffer, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit),
            Binding(1, VkDescriptorType.StorageBuffer, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit),
            Binding(2, VkDescriptorType.StorageBuffer, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit),
            Binding(3, VkDescriptorType.StorageBuffer, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit),
            Binding(4, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.FragmentBit),
            Binding(5, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.FragmentBit)
        });
        layout = device.CreatePipelineLayout(new[] { frameLayout, emptyLayout, materialLayout },
            default(ReadOnlySpan<VkPushConstantRange>), device.VkAllocator);
        vertex = PostFxPassHelpers.Compile(device, ShaderSourceLoader.ReadShaderSource("Particles/ParticleBillboard.vert.glsl"),
            RuntimeShaderStage.Vertex, "RimLumen.ParticleBillboard.Vertex");
        fragment = PostFxPassHelpers.Compile(device, ShaderSourceLoader.ReadShaderSource("Particles/ParticleBillboard.frag.glsl"),
            RuntimeShaderStage.Fragment, "RimLumen.ParticleBillboard.Fragment");
        LogHandle("VkPipelineLayout", $"{layout.VkHandle:X}", "ParticleDraw");
        LogHandle("VkShaderModule", $"{vertex.VkHandle:X}", "ParticleVertex");
        LogHandle("VkShaderModule", $"{fragment.VkHandle:X}", "ParticleFragment");
    }

    internal static void LogHandle(string type, string hex, string name) =>
        Verse.Log.Message($"[RimLumen] OwnedVkObject type={type} handle=0x{hex} name={name}");

    internal static VkDescriptorSetLayoutBinding Binding(int binding, VkDescriptorType type, VkShaderStageFlags stages) => new()
    {
        Binding = binding, DescriptorCount = 1, DescriptorType = type, StageFlags = stages
    };

    internal static VkDescriptorSetLayout CreateLayout(DeviceEx device, VkDescriptorSetLayoutBinding[] bindings)
    {
        var result = device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo { Bindings = bindings }, device.VkAllocator);
        LogHandle("VkDescriptorSetLayout", $"{result.VkHandle:X}", "ParticleLayout");
        return result;
    }

    internal static unsafe VkDescriptorPool CreatePool(DeviceEx device, int emitterCount)
    {
        int count = Math.Max(1, emitterCount);
        VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[3];
        sizes[0] = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = count * 3 + 3 };
        sizes[1] = new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = count + 1 };
        sizes[2] = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = count * 2 };
        var result = device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo
        {
            MaxSets = count + 2, PoolSizeCount = 3, PoolSizes = sizes
        }, device.VkAllocator);
        LogHandle("VkDescriptorPool", $"{result.VkHandle:X}", "ParticlePool");
        return result;
    }

    internal unsafe void Draw(in LumenHdrDrawContext context, ParticleFrame snapshot,
        ParticleGpuFrame resources, GpuComputeBuffer particles, Texture[] textures, bool[] texturePresent)
    {
        FrameContext frame = context.Frame;
        VkDescriptorSet cameraSet = DescriptorSetWriter.AllocateDescriptorSet(device, resources.Pool, frameLayout);
        LogHandle("VkDescriptorSet", $"{cameraSet.VkHandle:X}", "ParticleCamera");
        Matrix4x4 viewProjection = context.ViewProjection;
        resources.Camera.Upload(new ReadOnlySpan<Matrix4x4>(in viewProjection));
        WriteBuffers(device, cameraSet, new[] { 0 }, new[] { resources.Camera }, VkDescriptorType.UniformBuffer);
        var viewport = new VkViewport { Width = frame.Extent.Width, Height = frame.Extent.Height, MinDepth = 0, MaxDepth = 1 };
        frame.CommandBuffer.SetViewport(0, new ReadOnlySpan<VkViewport>(in viewport));
        var scissor = new VkRect2D(frame.Extent);
        frame.CommandBuffer.SetScissor(0, new ReadOnlySpan<VkRect2D>(in scissor));
        Matrix4x4.Invert(snapshot.Context.ViewMatrix, out Matrix4x4 inverseView);
        Vector3 right = Vector3.Normalize(new Vector3(inverseView.M11, inverseView.M12, inverseView.M13));
        Vector3 up = Vector3.Normalize(new Vector3(inverseView.M21, inverseView.M22, inverseView.M23));
        Matrix4x4 projection = snapshot.Context.ProjectionMatrix;
        RenderTexture? source = context.SceneColorDepthSource;
        VkImageView depthView = default;
        if (source is not null && !source.HasDepth) source = null;
        if (source is not null)
        {
            // The HDR owner's subpass uses DepthStencilReadOnlyOptimal. Its tracked
            // outer layout stays DepthStencilAttachmentOptimal until pass end.
            depthView = source.DepthSampleView;
            if (depthView.IsNull()) throw new InvalidOperationException("Scene depth has no depth-only sample view.");
        }
        VkImageView fallbackView = default;
        VkImageLayout fallbackLayout = default;
        if (source is null)
        {
            Texture.EnsureSampledReady(Texture2D.whiteTexture);
            if (!GpuTextureService.TryGetSampledImageInfo(Texture2D.whiteTexture, out fallbackView, out fallbackLayout))
                throw new InvalidOperationException("Procedural particle fallback has no native white texture.");
        }
        VkDescriptorImageInfo* images = stackalloc VkDescriptorImageInfo[2];
        VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[3];
        for (int i = 0; i < snapshot.Emitters.Length; i++)
        {
            ParticleEmitterFrame emitter = snapshot.Emitters[i];
            Texture texture = textures[i];
            if (!GpuTextureService.TryGetSampledImageInfo(texture, out VkImageView textureView, out VkImageLayout textureLayout))
                throw new InvalidOperationException($"Particle texture '{emitter.TextureKey}' has no resident native image.");
            var uniforms = new DrawUniforms
            {
                Draw = new Vector4(i, texturePresent[i] ? 1 : 0, source is null ? 0 : 1,
                    emitter.Blend == LumenBlendMode.Premultiplied ? 1 : 0),
                CameraRight = new Vector4(right, 0), CameraUp = new Vector4(up, 0),
                Projection = new Vector4(projection.M33, projection.M43, projection.M34, projection.M44),
                DepthUv = new Vector4(1f / frame.Extent.Width, 1f / frame.Extent.Height, 0, 0)
            };
            resources.Materials.Upload(new ReadOnlySpan<DrawUniforms>(in uniforms), (long)i * resources.UniformStride);
            VkDescriptorSet set = DescriptorSetWriter.AllocateDescriptorSet(device, resources.Pool, materialLayout);
            LogHandle("VkDescriptorSet", $"{set.VkHandle:X}", "ParticleMaterial");
            WriteBuffers(device, set, new[] { 1, 2, 3 }, new[] { particles, resources.Emitters, resources.Curves }, VkDescriptorType.StorageBuffer);
            var uniformInfo = new VkDescriptorBufferInfo
            {
                Buffer = resources.Materials.Buffer, Offset = ByteSize.Of((long)i * resources.UniformStride), Range = ByteSize.Of(80)
            };
            images[0] = new VkDescriptorImageInfo { Sampler = ComputeDispatchQueue.BilinearClampSampler, ImageView = textureView, ImageLayout = textureLayout };
            images[1] = new VkDescriptorImageInfo
            {
                Sampler = ComputeDispatchQueue.PointClampSampler,
                ImageView = source is null ? fallbackView : depthView,
                ImageLayout = source is null ? fallbackLayout : VkImageLayout.DepthStencilReadOnlyOptimal
            };
            writes[0] = new VkWriteDescriptorSet { DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &uniformInfo };
            writes[1] = new VkWriteDescriptorSet { DstSet = set, DstBinding = 4, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = images };
            writes[2] = new VkWriteDescriptorSet { DstSet = set, DstBinding = 5, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = images + 1 };
            device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(writes, 3), default(ReadOnlySpan<VkCopyDescriptorSet>));
            var key = (frame.RenderPass, frame.SampleCount, emitter.Blend);
            if (!pipelines.TryGetValue(key, out VkPipeline pipeline))
            {
                BlendState blend = emitter.Blend switch
                {
                    LumenBlendMode.Additive => BlendState.Additive,
                    LumenBlendMode.AlphaBlend => BlendState.Alpha,
                    LumenBlendMode.Premultiplied => new BlendState(true, BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add,
                        BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add),
                    _ => throw new ArgumentOutOfRangeException(nameof(emitter.Blend))
                };
                pipeline = PostFxPassHelpers.CreateFullscreenPipeline(device, frame.RenderPass, frame.SampleCount,
                    vertex, fragment, layout, blend, "RimLumen.Particles");
                pipelines.Add(key, pipeline);
                LogHandle("VkPipeline", $"{pipeline.VkHandle:X}", "ParticleDraw");
            }
            frame.CommandBuffer.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
            frame.CommandBuffer.BindDescriptorSet(VkPipelineBindPoint.Graphics, layout, 0, cameraSet, default(Span<ByteSize32>));
            frame.CommandBuffer.BindDescriptorSet(VkPipelineBindPoint.Graphics, layout, 2, set, default(Span<ByteSize32>));
            frame.CommandBuffer.Draw(6, (int)emitter.Parameters.Slots.Y, 0, 0);
        }
    }

    internal static unsafe void WriteBuffers(DeviceEx device, VkDescriptorSet set, int[] bindings,
        GpuComputeBuffer[] buffers, VkDescriptorType type)
    {
        VkDescriptorBufferInfo* infos = stackalloc VkDescriptorBufferInfo[buffers.Length];
        VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[buffers.Length];
        for (int i = 0; i < buffers.Length; i++)
        {
            infos[i] = new VkDescriptorBufferInfo { Buffer = buffers[i].Buffer, Offset = ByteSize.Zero, Range = buffers[i].Size };
            writes[i] = new VkWriteDescriptorSet
            {
                DstSet = set, DstBinding = bindings[i], DescriptorCount = 1, DescriptorType = type, BufferInfo = infos + i
            };
        }
        device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(writes, buffers.Length), default(ReadOnlySpan<VkCopyDescriptorSet>));
    }

    // Caller has waited for the device or is retiring this entire resource set after its fences.
    public void Dispose()
    {
        foreach (VkPipeline pipeline in pipelines.Values) device.DestroyPipeline(pipeline, device.VkAllocator);
        device.DestroyShaderModule(vertex, device.VkAllocator);
        device.DestroyShaderModule(fragment, device.VkAllocator);
        device.DestroyPipelineLayout(layout, device.VkAllocator);
        device.DestroyDescriptorSetLayout(frameLayout, device.VkAllocator);
        device.DestroyDescriptorSetLayout(emptyLayout, device.VkAllocator);
        device.DestroyDescriptorSetLayout(materialLayout, device.VkAllocator);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DrawUniforms
    {
        public Vector4 Draw, CameraRight, CameraUp, Projection, DepthUv;
    }
}

internal sealed class ParticleGpuFrame : IDisposable
{
    private readonly DeviceEx device;
    internal readonly VkDescriptorPool Pool;
    internal readonly GpuComputeBuffer Emitters, Fields, Curves, Camera, Materials;
    internal readonly int UniformStride;
    internal readonly int EmitterCapacity, FieldCapacity;

    internal ParticleGpuFrame(in FrameContext frame, int emitterCount, int fieldCount)
    {
        device = frame.Device;
        EmitterCapacity = Math.Max(1, emitterCount);
        FieldCapacity = Math.Max(1, fieldCount);
        int alignment = Math.Max(1, frame.GraphicsDevice.PhysicalDevice.GetProperties().Limits.MinUniformBufferOffsetAlignment);
        UniformStride = ((80 + alignment - 1) / alignment) * alignment;
        Pool = ParticleGpuDrawResources.CreatePool(device, EmitterCapacity);
        Emitters = GpuComputeBuffer.Create(device, frame.GraphicsDevice.Allocator, "Lumen.Emitters", (long)EmitterCapacity * 208);
        Fields = GpuComputeBuffer.Create(device, frame.GraphicsDevice.Allocator, "Lumen.Fields", (long)FieldCapacity * 48);
        Curves = GpuComputeBuffer.Create(device, frame.GraphicsDevice.Allocator, "Lumen.Curves", (long)EmitterCapacity * 64 * 32);
        Camera = GpuComputeBuffer.Create(device, frame.GraphicsDevice.Allocator, "Lumen.Camera", 64, VkBufferUsageFlags.UniformBufferBit);
        Materials = GpuComputeBuffer.Create(device, frame.GraphicsDevice.Allocator, "Lumen.Materials", (long)EmitterCapacity * UniformStride, VkBufferUsageFlags.UniformBufferBit);
        foreach (var buffer in new[] { Emitters, Fields, Curves, Camera, Materials })
            ParticleGpuDrawResources.LogHandle("VkBuffer", $"{buffer.Buffer.VkHandle:X}", "ParticleFrame");
    }

    internal void Upload(ParticleFrame frame)
    {
        device.ResetDescriptorPool(Pool, 0);
        var emitters = new GpuEmitterParams[frame.Emitters.Length];
        for (int i = 0; i < emitters.Length; i++)
        {
            emitters[i] = frame.Emitters[i].Parameters;
            Curves.Upload<GpuCurveSample>(frame.Emitters[i].Curves, (long)i * 64 * 32);
        }
        Emitters.Upload<GpuEmitterParams>(emitters);
        if (frame.Fields.Length > 0) Fields.Upload<GpuForceField>(frame.Fields);
    }

    public void Dispose()
    {
        device.DestroyDescriptorPool(Pool, device.VkAllocator);
        Emitters.Dispose(); Fields.Dispose(); Curves.Dispose(); Camera.Dispose(); Materials.Dispose();
    }
}
