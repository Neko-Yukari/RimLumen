using System;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RimLumen.Core.Rendering.Logic;
using Waylith;
using Waylith.Rendering;

namespace RimLumen.Core.Rendering;

internal sealed class HdrTargets(EngineApplication app) : IDisposable
{
    internal sealed class Slot
    {
        internal RenderTexture Color = null!;
        internal RenderTexture DepthSnapshot = null!;
        internal RenderTexture[] Down = Array.Empty<RenderTexture>();
        internal RenderTexture[] Up = Array.Empty<RenderTexture>();
        internal VkFramebuffer Framebuffer;
        internal VkImageView SceneDepth;
        internal RenderTexture? Bloom => Down.Length == 0 ? null : (Up.Length == 0 ? Down[0] : Up[0]);
    }

    private readonly DeviceEx device = app.Device;
    internal Slot[] Slots { get; private set; } = Array.Empty<Slot>();
    internal VkRenderPass RenderPass { get; private set; }
    private VkFormat depthFormat;
    private int width;
    private int height;
    private int bloomMipLevels = 5;

    internal unsafe Slot Ensure(int index, RenderTexture scene, int mipLevels = 5)
    {
        if (Slots.Length == 0 || width != scene.Width || height != scene.Height || depthFormat != scene.DepthFormat || bloomMipLevels != mipLevels)
        {
            // Rebuild notification normally retires these after the engine waits idle; also covers scene-only resize.
            device.WaitIdle();
            Dispose();
            width = scene.Width;
            height = scene.Height;
            depthFormat = scene.DepthFormat;
            bloomMipLevels = mipLevels;
            RenderPass = CreateRenderPass(depthFormat);
            MipSize[] sizes = bloomMipLevels > 0 ? BloomMipChain.Compute(width, height, bloomMipLevels, 8) : Array.Empty<MipSize>();
            Slots = new Slot[app.RenderContext!.FramesInFlight];
            try
            {
                for (int frame = 0; frame < Slots.Length; frame++)
                {
                    // Published before any GPU object exists: a mid-allocation failure leaves every
                    // already-created texture reachable by Dispose.
                    Slot slot = new();
                    Slots[frame] = slot;
                    slot.Color = Make(width, height, $"RimLumen.Hdr.{frame}");
                    slot.DepthSnapshot = MakeDepthSnapshot(width, height, $"RimLumen.DepthSnapshot.{frame}");
                    slot.Down = new RenderTexture[sizes.Length];
                    slot.Up = new RenderTexture[Math.Max(0, sizes.Length - 1)];
                    for (int level = 0; level < sizes.Length; level++)
                    {
                        slot.Down[level] = Make(sizes[level].Width, sizes[level].Height, $"RimLumen.BloomDown.{frame}.{level}");
                        if (level < slot.Up.Length)
                            slot.Up[level] = Make(sizes[level].Width, sizes[level].Height, $"RimLumen.BloomUp.{frame}.{level}");
                    }
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }
        Slot result = Slots[index];
        if (result.Framebuffer.IsNull() || !result.SceneDepth.Equals(scene.DepthView))
        {
            if (!result.Framebuffer.IsNull()) device.DestroyFramebuffer(result.Framebuffer, device.VkAllocator);
            result.SceneDepth = scene.DepthView;
            VkImageView* views = stackalloc VkImageView[2] { result.Color.ColorView, scene.DepthView };
            result.Framebuffer = device.CreateFramebuffer(new VkFramebufferCreateInfo
            {
                RenderPass = RenderPass, AttachmentCount = 2, Attachments = views,
                Width = width, Height = height, Layers = 1
            }, device.VkAllocator);
        }
        return result;
    }

    private static RenderTexture Make(int width, int height, string name)
    {
        RenderTexture result = new(width, height, VkFormat.R16G16B16A16SFloat, name: name);
        try
        {
            result.Create();
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static RenderTexture MakeDepthSnapshot(int width, int height, string name)
    {
        // The snapshot is a COLOR target: the depth copy pass writes raw depth into it as R32_SFLOAT,
        // so it can be sampled through a regular Material texture slot instead of a depth aspect view.
        RenderTexture result = new(width, height, colorFormat: VkFormat.R32SFloat,
            filter: RenderTextureFilter.Point, wrap: RenderTextureWrap.Clamp, name: name);
        try
        {
            result.Create();
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private unsafe VkRenderPass CreateRenderPass(VkFormat depth)
    {
        VkAttachmentDescription* attachments = stackalloc VkAttachmentDescription[2];
        attachments[0] = new VkAttachmentDescription
        {
            Format = VkFormat.R16G16B16A16SFloat, Samples = VkSampleCountFlags._1Bit,
            LoadOp = VkAttachmentLoadOp.Clear, StoreOp = VkAttachmentStoreOp.Store,
            StencilLoadOp = VkAttachmentLoadOp.DontCare, StencilStoreOp = VkAttachmentStoreOp.DontCare,
            InitialLayout = VkImageLayout.Undefined, FinalLayout = VkImageLayout.ShaderReadOnlyOptimal
        };
        // None exists in Brutal but the host requests Vulkan 1.2 (GraphicsDevice.cs:197),
        // and does not enable EXT/KHR_load_store_op_none. Preserve Store and synchronize its write.
        attachments[1] = new VkAttachmentDescription
        {
            Format = depth, Samples = VkSampleCountFlags._1Bit,
            LoadOp = VkAttachmentLoadOp.Load, StoreOp = VkAttachmentStoreOp.Store,
            StencilLoadOp = VkAttachmentLoadOp.Load, StencilStoreOp = VkAttachmentStoreOp.Store,
            InitialLayout = VkImageLayout.DepthStencilAttachmentOptimal, FinalLayout = VkImageLayout.DepthStencilAttachmentOptimal
        };
        VkAttachmentReference color = new() { Attachment = 0, Layout = VkImageLayout.ColorAttachmentOptimal };
        VkAttachmentReference depthRef = new() { Attachment = 1, Layout = VkImageLayout.DepthStencilReadOnlyOptimal };
        VkSubpassDescription subpass = new()
        {
            PipelineBindPoint = VkPipelineBindPoint.Graphics,
            ColorAttachmentCount = 1, ColorAttachments = &color, DepthStencilAttachment = &depthRef
        };
        VkSubpassDependency* dependencies = stackalloc VkSubpassDependency[2];
        dependencies[0] = new VkSubpassDependency
        {
            SrcSubpass = -1, DstSubpass = 0,
            SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit | VkPipelineStageFlags.FragmentShaderBit,
            DstStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit | VkPipelineStageFlags.FragmentShaderBit,
            SrcAccessMask = VkAccessFlags.ColorAttachmentWriteBit | VkAccessFlags.DepthStencilAttachmentWriteBit | VkAccessFlags.ShaderReadBit,
            DstAccessMask = VkAccessFlags.ColorAttachmentWriteBit | VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.ShaderReadBit
        };
        dependencies[1] = new VkSubpassDependency
        {
            SrcSubpass = 0, DstSubpass = -1,
            SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit,
            DstStageMask = VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit,
            SrcAccessMask = VkAccessFlags.ColorAttachmentWriteBit | VkAccessFlags.ShaderReadBit | VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = VkAccessFlags.ShaderReadBit | VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit
        };
        return device.CreateRenderPass(new VkRenderPassCreateInfo
        {
            AttachmentCount = 2, Attachments = attachments, SubpassCount = 1, Subpasses = &subpass,
            DependencyCount = 2, Dependencies = dependencies
        }, device.VkAllocator);
    }

    public void Dispose()
    {
        foreach (Slot? slot in Slots)
        {
            if (slot is null) continue;
            if (!slot.Framebuffer.IsNull()) device.DestroyFramebuffer(slot.Framebuffer, device.VkAllocator);
            slot.Color?.Dispose();
            slot.DepthSnapshot?.Dispose();
            foreach (RenderTexture? texture in slot.Down) texture?.Dispose();
            foreach (RenderTexture? texture in slot.Up) texture?.Dispose();
        }
        Slots = Array.Empty<Slot>();
        if (!RenderPass.IsNull()) device.DestroyRenderPass(RenderPass, device.VkAllocator);
        RenderPass = default;
    }
}
