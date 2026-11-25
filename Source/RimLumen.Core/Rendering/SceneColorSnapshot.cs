using System;
using Brutal.VulkanApi;
using RimLumen.Core.Contracts;
using Waylith.Rendering;

namespace RimLumen.Core.Rendering;

internal static class SceneColorSnapshot
{
    // Built-in default pack in Legacy mode without distortion never samples sceneImage
    // (RimLumenBloomPrefilter.frag.glsl omits it; RimLumenComposite.frag.glsl returns before reading it when sceneUv == uv).
    // Any custom pack, Linear blend space, or active distortion still requires the same-frame scene colour snapshot.
    internal static bool NeedsSnapshot(bool isDefaultPack, LumenColorBlendSpace colorBlendSpace, bool hasDistortion) =>
        !isDefaultPack || colorBlendSpace != LumenColorBlendSpace.Legacy || hasDistortion;

    internal static void Record(CommandBuffer cmd, RenderTexture scene, RenderTexture target)
    {
        VkImageLayout previous = target.Color.CurrentLayout;
        Span<VkImageMemoryBarrier> barriers = stackalloc VkImageMemoryBarrier[2];
        barriers[0] = Barrier(scene.Color.Image, VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferSrcOptimal,
            VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.TransferReadBit);
        barriers[1] = Barrier(target.Color.Image, previous, VkImageLayout.TransferDstOptimal,
            previous == VkImageLayout.Undefined ? VkAccessFlags.None : VkAccessFlags.ShaderReadBit, VkAccessFlags.TransferWriteBit);
        cmd.PipelineBarrier(VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, default, default, barriers);
        VkImageSubresourceLayers layer = new() { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 };
        VkImageCopy copy = new()
        {
            SrcSubresource = layer, DstSubresource = layer, Extent = new VkExtent3D(scene.Width, scene.Height, 1)
        };
        cmd.CopyImage(scene.Color.Image, VkImageLayout.TransferSrcOptimal, target.Color.Image, VkImageLayout.TransferDstOptimal,
            new ReadOnlySpan<VkImageCopy>(in copy));
        barriers[0] = Barrier(scene.Color.Image, VkImageLayout.TransferSrcOptimal, VkImageLayout.ShaderReadOnlyOptimal,
            VkAccessFlags.TransferReadBit, VkAccessFlags.ColorAttachmentReadBit | VkAccessFlags.ColorAttachmentWriteBit);
        barriers[1] = Barrier(target.Color.Image, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal,
            VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit);
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit,
            VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkDependencyFlags.None, default, default, barriers);
        target.Color.SetMipLayout(0, VkImageLayout.ShaderReadOnlyOptimal);
    }

    private static VkImageMemoryBarrier Barrier(VkImage image, VkImageLayout before, VkImageLayout after,
        VkAccessFlags source, VkAccessFlags destination) => new()
    {
        Image = image, OldLayout = before, NewLayout = after, SrcAccessMask = source, DstAccessMask = destination,
        SrcQueueFamilyIndex = -1, DstQueueFamilyIndex = -1,
        SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 }
    };
}
