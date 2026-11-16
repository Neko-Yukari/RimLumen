using System;
using Brutal.VulkanApi;
using Waylith.Graphics;
using Waylith.Rendering;
using Waylith.Rendering.PostFx;

namespace RimLumen.Core.Rendering;

// Own the pass lifetime here: PostEffectPass.Record does not end its pass if recording throws.
internal sealed class OffscreenPostEffectPass(
    string name, string shader, ReadOnlySpan<int> bindings, BlendState blend = default,
    int pushConstantSize = 0, int maxPassesPerFrame = 1, int uniformBinding = -1)
    : PostEffectPass(name, shader, bindings, blend, pushConstantSize, maxPassesPerFrame, uniformBinding)
{
    private VkRenderPass outputPass;
    private Brutal.VulkanApi.Abstractions.DeviceEx? outputDevice;

    public override unsafe void Record(in FrameContext ctx, object? featurePayload)
    {
        RenderTexture output = Output!;
        if (outputPass.IsNull())
        {
            outputDevice = ctx.Device;
            outputPass = PostFxPassHelpers.CreateColorRenderPass(outputDevice, output.ColorFormat);
        }
        var frame = new FrameContext(ctx.RenderContext, ctx.Resources, ctx.CommandBuffer, outputPass,
            output.GetFramebuffer(outputPass), output.Extent, VkSampleCountFlags._1Bit, output.ColorFormat, VkFormat.Undefined);
        VkRenderPassBeginInfo begin = new()
        {
            RenderPass = frame.RenderPass, Framebuffer = frame.Framebuffer, RenderArea = new VkRect2D(frame.Extent)
        };
        ctx.CommandBuffer.BeginRenderPass(in begin, VkSubpassContents.Inline);
        try { RecordInline(in frame, frame.RenderPass, frame.SampleCount); }
        finally
        {
            ctx.CommandBuffer.EndRenderPass();
            output.Color.SetMipLayout(0, VkImageLayout.ShaderReadOnlyOptimal);
        }
        VkImageMemoryBarrier sample = new()
        {
            SrcAccessMask = VkAccessFlags.ColorAttachmentWriteBit, DstAccessMask = VkAccessFlags.ShaderReadBit,
            OldLayout = VkImageLayout.ShaderReadOnlyOptimal, NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = -1, DstQueueFamilyIndex = -1, Image = output.Color.Image,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 }
        };
        ctx.CommandBuffer.PipelineBarrier(VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.FragmentShaderBit,
            VkDependencyFlags.None, default, default, new ReadOnlySpan<VkImageMemoryBarrier>(in sample));
    }

    public override void Invalidate()
    {
        base.Invalidate();
        if (!outputPass.IsNull()) outputDevice!.DestroyRenderPass(outputPass, outputDevice!.VkAllocator);
        outputPass = default;
    }
}
