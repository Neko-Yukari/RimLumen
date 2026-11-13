using System;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Waylith.Rendering;

namespace RimLumen.Core.Rendering;

// The shared attachment's STORE is a write, even in a read-only subpass. Sample a separate image
// so fragment reads cannot race that STORE. RenderTexture.AllocateImages gives depth TransferSrc/Dst.
internal static class HdrDepthSnapshot
{
    internal static void Record(CommandBuffer command, RenderTexture scene, RenderTexture snapshot)
    {
        GpuTextureAllocation source = scene.Depth!;
        GpuTextureAllocation target = snapshot.Depth!;
        VkImageAspectFlags aspects = VkImageAspectFlags.DepthBit;
        if (scene.DepthFormat is VkFormat.D32SFloatS8UInt or VkFormat.D24UNormS8UInt or VkFormat.D16UNormS8UInt)
            aspects |= VkImageAspectFlags.StencilBit;
        // Transition both aspects together: the host does not enable separate depth/stencil layouts.
        Span<VkImageMemoryBarrier> barriers = stackalloc VkImageMemoryBarrier[2];
        barriers[0] = Barrier(source.Image, aspects, VkImageLayout.DepthStencilAttachmentOptimal,
            VkImageLayout.TransferSrcOptimal, VkAccessFlags.DepthStencilAttachmentReadBit |
            VkAccessFlags.DepthStencilAttachmentWriteBit | VkAccessFlags.ShaderReadBit, VkAccessFlags.TransferReadBit);
        barriers[1] = Barrier(target.Image, aspects, target.GetMipLayout(0), VkImageLayout.TransferDstOptimal,
            target.GetMipLayout(0) == VkImageLayout.Undefined ? VkAccessFlags.None : VkAccessFlags.ShaderReadBit,
            VkAccessFlags.TransferWriteBit);
        command.PipelineBarrier(VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit |
            VkPipelineStageFlags.FragmentShaderBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None,
            default, default, barriers);

        VkImageSubresourceLayers depth = new() { AspectMask = VkImageAspectFlags.DepthBit, LayerCount = 1 };
        VkImageCopy copy = new()
        {
            SrcSubresource = depth, DstSubresource = depth,
            Extent = new VkExtent3D(scene.Width, scene.Height, 1)
        };
        command.CopyImage(source.Image, VkImageLayout.TransferSrcOptimal, target.Image,
            VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkImageCopy>(in copy));

        barriers[0] = Barrier(source.Image, aspects, VkImageLayout.TransferSrcOptimal,
            VkImageLayout.DepthStencilAttachmentOptimal, VkAccessFlags.TransferReadBit,
            VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit);
        barriers[1] = Barrier(target.Image, aspects, VkImageLayout.TransferDstOptimal,
            VkImageLayout.DepthStencilReadOnlyOptimal, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit);
        command.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.EarlyFragmentTestsBit |
            VkPipelineStageFlags.LateFragmentTestsBit | VkPipelineStageFlags.FragmentShaderBit,
            VkDependencyFlags.None, default, default, barriers);
        source.SetMipLayout(0, VkImageLayout.DepthStencilAttachmentOptimal);
        target.SetMipLayout(0, VkImageLayout.DepthStencilReadOnlyOptimal);
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
