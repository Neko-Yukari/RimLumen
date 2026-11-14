using System;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Waylith.Rendering;
using Waylith.Rendering.PostFx;

namespace RimLumen.Core.Rendering;

// The shared attachment's STORE is a write, even in a read-only subpass. Sample a separate image
// so fragment reads cannot race that STORE: a PostEffectPass copies raw depth into an R32_SFLOAT
// color target that particle materials bind as a regular texture.
internal static class HdrDepthSnapshot
{
    // Requires: `blit` is the "RimLumenDepthCopy" pass and `snapshot` its per-submission-slot output.
    // Ensures: records one scene-depth -> snapshot copy and leaves the scene depth attachment in
    //          DepthStencilAttachmentOptimal for the caller's HDR render pass.
    internal static void Record(in FrameContext frame, RenderTexture scene, RenderTexture snapshot, PostEffectPass blit)
    {
        GpuTextureAllocation source = scene.Depth
            ?? throw new InvalidOperationException("[RimLumen] HDR scene has no depth attachment to copy.");
        VkImageAspectFlags aspects = DepthAspect(scene.DepthFormat);
        CommandBuffer command = frame.CommandBuffer;

        // PostEffectPass writes its input descriptor with ShaderReadOnlyOptimal, so the attachment
        // leaves the depth-stencil layout before the copy pass and returns before the HDR pass. The source
        // scope also covers a fragment-shader read left over from an earlier pre-backbuffer effect.
        Span<VkImageMemoryBarrier> barriers = stackalloc VkImageMemoryBarrier[1];
        barriers[0] = Barrier(source.Image, aspects, VkImageLayout.DepthStencilAttachmentOptimal,
            VkImageLayout.ShaderReadOnlyOptimal,
            VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit |
            VkAccessFlags.ShaderReadBit,
            VkAccessFlags.ShaderReadBit);
        command.PipelineBarrier(
            VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit |
            VkPipelineStageFlags.FragmentShaderBit,
            VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, default, default, barriers);
        source.SetMipLayout(0, VkImageLayout.ShaderReadOnlyOptimal);

        try
        {
            blit.SetInput(0, scene.DepthSampleView, scene.Sampler);
            blit.Output = snapshot;
            blit.Record(in frame, null);
        }
        catch
        {
            // Fail-safe: the depth image is already in ShaderReadOnlyOptimal with our barrier recorded, so put
            // it back before the exception reaches the feature fail-safe; otherwise the engine's next depth pass
            // would observe a mismatched layout. If the copy pass left the command buffer inside its render
            // pass the barrier is refused, and the layout tracker is still corrected before rethrowing.
            try
            {
                barriers[0] = Barrier(source.Image, aspects, VkImageLayout.ShaderReadOnlyOptimal,
                    VkImageLayout.DepthStencilAttachmentOptimal,
                    VkAccessFlags.ShaderReadBit,
                    VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit);
                command.PipelineBarrier(VkPipelineStageFlags.FragmentShaderBit,
                    VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit,
                    VkDependencyFlags.None, default, default, barriers);
            }
            catch (Exception)
            {
                // Best effort only: the original failure is what the fail-safe must report.
            }
            source.SetMipLayout(0, VkImageLayout.DepthStencilAttachmentOptimal);
            throw;
        }

        // The copy pass reads depth in its fragment shader; the HDR pass then tests against it again.
        barriers[0] = Barrier(source.Image, aspects, VkImageLayout.ShaderReadOnlyOptimal,
            VkImageLayout.DepthStencilAttachmentOptimal,
            VkAccessFlags.ShaderReadBit,
            VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit);
        command.PipelineBarrier(VkPipelineStageFlags.FragmentShaderBit,
            VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit,
            VkDependencyFlags.None, default, default, barriers);
        source.SetMipLayout(0, VkImageLayout.DepthStencilAttachmentOptimal);
    }

    private static VkImageAspectFlags DepthAspect(VkFormat format)
    {
        VkImageAspectFlags aspects = VkImageAspectFlags.DepthBit;
        if (format is VkFormat.D32SFloatS8UInt or VkFormat.D24UNormS8UInt or VkFormat.D16UNormS8UInt)
            aspects |= VkImageAspectFlags.StencilBit;
        return aspects;
    }

    private static VkImageMemoryBarrier Barrier(VkImage image, VkImageAspectFlags aspects,
        VkImageLayout before, VkImageLayout after, VkAccessFlags source, VkAccessFlags destination) => new()
    {
        Image = image, OldLayout = before, NewLayout = after,
        SrcAccessMask = source, DstAccessMask = destination,
        SrcQueueFamilyIndex = -1, DstQueueFamilyIndex = -1,
        SubresourceRange = new VkImageSubresourceRange { AspectMask = aspects, LevelCount = 1, LayerCount = 1 }
    };
}
