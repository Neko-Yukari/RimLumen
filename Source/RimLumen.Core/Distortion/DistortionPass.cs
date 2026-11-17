using System;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RimLumen.Core.Rendering;
using Waylith;
using Waylith.Rendering;

namespace RimLumen.Core.Distortion;

// The `distortion` ShaderPack input: one RG16F target per submission slot holding the sum of this frame's
// distortion-pass draws (RimLumenRenderHost.DistortionPassId, routed from layer 14), each an additive signed scene
// sample offset in framebuffer pixels (+x right, +y down). A frame without such draws allocates nothing and records
// no pass; the composite then samples the black placeholder and the scene stays bit-exact.
internal sealed class DistortionPass(EngineApplication app) : IDisposable
{
    private readonly DeviceEx device = app.Device;
    private readonly MeshBatchRenderer renderer = new(app.GraphicsDevice!, app.RenderContext!);
    private RenderTexture?[] targets = Array.Empty<RenderTexture?>();
    private VkFramebuffer[] framebuffers = Array.Empty<VkFramebuffer>();
    private VkRenderPass renderPass;
    private int width;
    private int height;

    // Requires: render thread, no render pass open, draws captured for this frame.
    // Ensures: null when draws is empty; otherwise the slot's target, cleared to 0, holding the additive sum of draws
    //          and left in ShaderReadOnlyOptimal for this submission's ShaderPack passes.
    internal RenderTexture? Record(in FrameContext ctx, MeshDrawFrame draws, RenderTexture scene)
    {
        if (draws.Draws.Count == 0) return null;
        int slot = ctx.SubmissionFrameIndex;
        VkFramebuffer framebuffer = Ensure(slot, scene.Width, scene.Height);
        RenderTexture target = targets[slot]!;
        var frame = new FrameContext(ctx.RenderContext, ctx.Resources, ctx.CommandBuffer, renderPass, framebuffer,
            target.Extent, VkSampleCountFlags._1Bit, VkFormat.R16G16SFloat, VkFormat.Undefined);
        ctx.CommandBuffer.BeginDebugLabel("RimLumen/Distortion\0"u8, new float4(0.3f, 0.6f, 1f, 1f));
        try
        {
            RecordPass(in frame, draws);
        }
        finally { ctx.CommandBuffer.EndDebugLabel(); }
        target.Color.SetMipLayout(0, VkImageLayout.ShaderReadOnlyOptimal);
        return target;
    }

    private unsafe void RecordPass(in FrameContext frame, MeshDrawFrame draws)
    {
        VkClearValue clear = default;
        VkRenderPassBeginInfo begin = new()
        {
            RenderPass = frame.RenderPass, Framebuffer = frame.Framebuffer, RenderArea = new VkRect2D(frame.Extent),
            ClearValueCount = 1, ClearValues = &clear
        };
        frame.CommandBuffer.BeginRenderPass(in begin, VkSubpassContents.Inline);
        try
        {
            VkViewport viewport = new() { Width = frame.Extent.Width, Height = frame.Extent.Height, MinDepth = 0, MaxDepth = 1 };
            frame.CommandBuffer.SetViewport(0, new ReadOnlySpan<VkViewport>(in viewport));
            VkRect2D scissor = new(frame.Extent);
            frame.CommandBuffer.SetScissor(0, new ReadOnlySpan<VkRect2D>(in scissor));
            renderer.InvalidateCommandBufferBindings();
            renderer.RecordPass(in frame, draws, RimLumenRenderHost.DistortionPassId);
        }
        finally { frame.CommandBuffer.EndRenderPass(); }
    }

    private VkFramebuffer Ensure(int slot, int sceneWidth, int sceneHeight)
    {
        int slots = app.RenderContext!.FramesInFlight;
        if (targets.Length != slots || width != sceneWidth || height != sceneHeight)
        {
            if (targets.Length > 0) device.WaitIdle();
            DisposeTargets();
            width = sceneWidth;
            height = sceneHeight;
            targets = new RenderTexture?[slots];
            framebuffers = new VkFramebuffer[slots];
        }
        if (renderPass.IsNull()) renderPass = CreateRenderPass();
        if (targets[slot] is null)
        {
            var target = new RenderTexture(width, height, colorFormat: VkFormat.R16G16SFloat,
                filter: RenderTextureFilter.Bilinear, wrap: RenderTextureWrap.Clamp, name: $"RimLumen.Distortion.{slot}");
            targets[slot] = target;
            target.Create();
        }
        if (framebuffers[slot].IsNull()) framebuffers[slot] = CreateFramebuffer(targets[slot]!);
        return framebuffers[slot];
    }

    private unsafe VkFramebuffer CreateFramebuffer(RenderTexture target)
    {
        VkImageView view = target.ColorView;
        return device.CreateFramebuffer(new VkFramebufferCreateInfo
        {
            RenderPass = renderPass, AttachmentCount = 1, Attachments = &view, Width = width, Height = height, Layers = 1
        }, device.VkAllocator);
    }

    private unsafe VkRenderPass CreateRenderPass()
    {
        VkAttachmentDescription attachment = new()
        {
            Format = VkFormat.R16G16SFloat, Samples = VkSampleCountFlags._1Bit,
            LoadOp = VkAttachmentLoadOp.Clear, StoreOp = VkAttachmentStoreOp.Store,
            StencilLoadOp = VkAttachmentLoadOp.DontCare, StencilStoreOp = VkAttachmentStoreOp.DontCare,
            InitialLayout = VkImageLayout.Undefined, FinalLayout = VkImageLayout.ShaderReadOnlyOptimal
        };
        VkAttachmentReference color = new() { Attachment = 0, Layout = VkImageLayout.ColorAttachmentOptimal };
        VkSubpassDescription subpass = new()
        {
            PipelineBindPoint = VkPipelineBindPoint.Graphics, ColorAttachmentCount = 1, ColorAttachments = &color
        };
        // The previous use of this slot's target was a ShaderPack read; the next is this submission's ShaderPack read.
        VkSubpassDependency* dependencies = stackalloc VkSubpassDependency[2];
        dependencies[0] = new VkSubpassDependency
        {
            SrcSubpass = -1, DstSubpass = 0,
            SrcStageMask = VkPipelineStageFlags.FragmentShaderBit, DstStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = VkAccessFlags.ShaderReadBit, DstAccessMask = VkAccessFlags.ColorAttachmentWriteBit
        };
        dependencies[1] = new VkSubpassDependency
        {
            SrcSubpass = 0, DstSubpass = -1,
            SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit, DstStageMask = VkPipelineStageFlags.FragmentShaderBit,
            SrcAccessMask = VkAccessFlags.ColorAttachmentWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit
        };
        return device.CreateRenderPass(new VkRenderPassCreateInfo
        {
            AttachmentCount = 1, Attachments = &attachment, SubpassCount = 1, Subpasses = &subpass,
            DependencyCount = 2, Dependencies = dependencies
        }, device.VkAllocator);
    }

    // Requires: the engine waited idle (FrameRenderer.NotifyRenderTargetRebuilt).
    internal void OnRenderTargetRebuilt()
    {
        DisposeTargets();
        renderer.OnRenderContextRebuilt(app.RenderContext!);
    }

    private void DisposeTargets()
    {
        foreach (VkFramebuffer framebuffer in framebuffers)
            if (!framebuffer.IsNull()) device.DestroyFramebuffer(framebuffer, device.VkAllocator);
        foreach (RenderTexture? target in targets) target?.Dispose();
        framebuffers = Array.Empty<VkFramebuffer>();
        targets = Array.Empty<RenderTexture?>();
        width = 0;
        height = 0;
    }

    public void Dispose()
    {
        renderer.Dispose();
        DisposeTargets();
        if (!renderPass.IsNull()) device.DestroyRenderPass(renderPass, device.VkAllocator);
        renderPass = default;
    }
}
