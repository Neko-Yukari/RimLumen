using System;
using System.Threading;
using Brutal;
using Brutal.Pointers;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using RimLumen.Core.Rendering;
using RimLumen.Core.Rendering.Logic;
using UnityEngine;
using Verse;
using Waylith;
using Waylith.Graphics;
using Waylith.Rendering;

namespace RimLumen.Core.Lighting;

internal static class MapLightFieldCollector
{
    private static readonly object Sync = new();
    private static byte[]? cpuRgba;
    private static ulong cpuGeneration;
    private static int currentMapId = -1;
    private static int mapSizeX;
    private static int mapSizeZ;
    private static MapLightFieldGpuTexture? gpuTexture;

    // Main thread only.
    internal static void BeginCapture(EngineApplication app, Map? map)
    {
        if (!RimLumenRenderHost.IsMapLightFieldActive || map is null || map.Size.x <= 0 || map.Size.z <= 0)
        {
            // Zero-cost on ordinary disabled frames: nothing was ever allocated, so never wait for idle.
            if (gpuTexture is null && cpuRgba is null)
                return;
            Release(app);
            return;
        }

        int width = map.Size.x;
        int height = map.Size.z;
        bool needFullScan;
        lock (Sync)
        {
            needFullScan = cpuRgba is null
                || currentMapId != map.uniqueID
                || mapSizeX != width
                || mapSizeZ != height;
            if (needFullScan)
            {
                cpuRgba = new byte[width * height * 4];
                currentMapId = map.uniqueID;
                mapSizeX = width;
                mapSizeZ = height;
                FillRegionLocked(map, new CellRect(0, 0, width, height));
                Volatile.Write(ref cpuGeneration, cpuGeneration + 1);
            }
        }

        if (gpuTexture is null || gpuTexture.Width != width || gpuTexture.Height != height)
        {
            if (gpuTexture is not null)
            {
                app.WaitForRenderThreadIdle();
                app.Device.WaitIdle();
                gpuTexture.Dispose();
                gpuTexture = null;
            }
            gpuTexture = new MapLightFieldGpuTexture(app, width, height);
        }
    }

    // Main thread only (called from SectionLayer_RimLumenLightField.Regenerate).
    internal static void UpdateSection(Map? map, CellRect rect)
    {
        if (!RimLumenRenderHost.IsMapLightFieldActive || map is null)
            return;

        lock (Sync)
        {
            if (cpuRgba is null || currentMapId != map.uniqueID || map.Size.x != mapSizeX || map.Size.z != mapSizeZ)
                return;
            FillRegionLocked(map, rect);
            Volatile.Write(ref cpuGeneration, cpuGeneration + 1);
        }
    }

    // Render thread only, outside any render pass, before HDR/Bloom/Custom passes sample binding 4.
    internal static void PrepareForRender(in FrameContext ctx)
    {
        MapLightFieldGpuTexture? tex = gpuTexture;
        if (tex is null || !RimLumenRenderHost.IsMapLightFieldActive)
            return;

        ulong currentGen = Volatile.Read(ref cpuGeneration);
        if (tex.IsCurrent(currentGen))
            return;

        int slot = ctx.SubmissionFrameIndex;
        ulong copiedGen;
        lock (Sync)
        {
            if (cpuRgba is null || cpuRgba.Length != tex.ByteLength)
                return;
            tex.CopyToStaging(slot, cpuRgba);
            copiedGen = Volatile.Read(ref cpuGeneration);
        }

        tex.RecordUpload(ctx.CommandBuffer, slot, copiedGen);
    }

    internal static bool TryGetDescriptor(out VkImageView view, out VkSampler sampler, out int width, out int height)
    {
        MapLightFieldGpuTexture? tex = gpuTexture;
        if (tex is not null && tex.IsInitialized)
        {
            view = tex.View;
            sampler = tex.Sampler;
            width = tex.Width;
            height = tex.Height;
            return true;
        }
        view = default;
        sampler = default;
        width = 0;
        height = 0;
        return false;
    }

    // Requires: main thread. waitIdle=false is for a faulted render thread: the CPU mirror is always dropped, but the
    //   GPU texture is left allocated (a freed VkImage could still be referenced by submitted commands) and is
    //   reclaimed at process exit instead.
    internal static void Release(EngineApplication? app, bool waitIdle = true)
    {
        lock (Sync)
        {
            cpuRgba = null;
            currentMapId = -1;
            mapSizeX = 0;
            mapSizeZ = 0;
        }
        if (gpuTexture is null)
            return;
        if (!waitIdle)
            return;
        app?.WaitForRenderThreadIdle();
        app?.GraphicsDevice?.Device.WaitIdle();
        gpuTexture.Dispose();
        gpuTexture = null;
    }

    internal static void DisposeGpu(EngineApplication? app)
    {
        Release(app);
    }

    private static void FillRegionLocked(Map map, CellRect rect)
    {
        if (cpuRgba is null || map.roofGrid is null || map.glowGrid is null || map.fogGrid is null || map.terrainGrid is null)
            return;
        CellRect clipped = rect;
        clipped.ClipInsideMap(map);
        if (clipped.IsEmpty)
            return;

        for (int z = clipped.minZ; z <= clipped.maxZ; z++)
        {
            int rowOffset = z * mapSizeX;
            for (int x = clipped.minX; x <= clipped.maxX; x++)
            {
                int idx = rowOffset + x;
                bool roofed = map.roofGrid.Roofed(idx);
                Color32 glow = map.glowGrid.VisualGlowAt(idx);
                bool usesOutdoorTemp = map.regionGrid?.GetValidRegionAt_NoRebuild(new IntVec3(x, 0, z))?.Room?.UsesOutdoorTemperature ?? true;
                bool fogged = map.fogGrid.IsFogged(idx);
                MapLightFieldEncoder.EncodeCell(roofed, glow.r, glow.g, glow.b, glow.a, usesOutdoorTemp, fogged, cpuRgba.AsSpan(idx * 4, 4));
            }
        }
    }

    private sealed class MapLightFieldGpuTexture : IDisposable
    {
        private readonly DeviceEx device;
        private readonly VmaAllocator allocator;
        private readonly ImageAllocationInfo imageAlloc;
        private readonly BufferAllocationInfo[] stagingBuffers;
        private VkImageLayout currentLayout = VkImageLayout.Undefined;
        private ulong uploadedGeneration;
        private bool disposed;

        internal readonly int Width;
        internal readonly int Height;
        internal readonly int ByteLength;
        internal readonly VkImageView View;
        internal readonly VkSampler Sampler;

        internal bool IsInitialized => currentLayout == VkImageLayout.ShaderReadOnlyOptimal;

        internal MapLightFieldGpuTexture(EngineApplication app, int width, int height)
        {
            device = app.Device;
            allocator = app.GraphicsDevice!.Allocator;
            Width = width;
            Height = height;
            ByteLength = width * height * 4;
            int framesInFlight = Math.Max(1, app.RenderContext?.FramesInFlight ?? 3);

            var imageCreateInfo = new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = VkFormat.R8G8B8A8UNorm,
                Extent = new VkExtent3D(width, height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            };
            var imageAllocInfo = new AllocationInfo.CreateInfo
            {
                Preference = MemoryPreference.PreferGpu,
                RequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit,
                PreferredProperties = VkMemoryPropertyFlags.DeviceLocalBit,
                Priority = 1f,
            };
            imageAlloc = allocator.AllocateImageChecked("RimLumen.MapLightField", in imageCreateInfo, in imageAllocInfo);
            GpuMemoryTracker.Add(GpuMemoryCategory.GameTexture, in imageAlloc, null, "RimLumen.MapLightField");

            var viewCreateInfo = new VkImageViewCreateInfo
            {
                Image = imageAlloc.Image,
                ViewType = VkImageViewType._2D,
                Format = VkFormat.R8G8B8A8UNorm,
                Components = new VkComponentMapping
                {
                    R = VkComponentSwizzle.Identity,
                    G = VkComponentSwizzle.Identity,
                    B = VkComponentSwizzle.Identity,
                    A = VkComponentSwizzle.Identity,
                },
                SubresourceRange = new VkImageSubresourceRange
                {
                    AspectMask = VkImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };
            View = device.CreateImageView(in viewCreateInfo, device.VkAllocator);

            var samplerCreateInfo = new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear,
                MinFilter = VkFilter.Linear,
                MipmapMode = VkSamplerMipmapMode.Nearest,
                AddressModeU = VkSamplerAddressMode.ClampToEdge,
                AddressModeV = VkSamplerAddressMode.ClampToEdge,
                AddressModeW = VkSamplerAddressMode.ClampToEdge,
                MinLod = 0f,
                MaxLod = 1f,
                BorderColor = VkBorderColor.FloatOpaqueBlack,
            };
            Sampler = device.CreateSampler(in samplerCreateInfo, device.VkAllocator);

            stagingBuffers = new BufferAllocationInfo[framesInFlight];
            var stagingCreateInfo = new VkBufferCreateInfo
            {
                Size = ByteSize.Of(ByteLength),
                Usage = VkBufferUsageFlags.TransferSrcBit,
                SharingMode = VkSharingMode.Exclusive,
            };
            var stagingAllocInfo = new AllocationInfo.CreateInfo
            {
                Preference = MemoryPreference.PreferCpu,
                RequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
                PreferredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
                Priority = 1f,
            };
            for (int i = 0; i < framesInFlight; i++)
            {
                string label = $"RimLumen.MapLightField.Staging[{i}]";
                stagingBuffers[i] = allocator.AllocateBufferChecked(label, in stagingCreateInfo, in stagingAllocInfo);
                GpuMemoryTracker.Add(GpuMemoryCategory.Staging, in stagingBuffers[i], "Staging/Texture", label);
            }
        }

        internal bool IsCurrent(ulong generation)
            => IsInitialized && uploadedGeneration == generation;

        internal unsafe void CopyToStaging(int slotIndex, ReadOnlySpan<byte> source)
        {
            BufferAllocationInfo buffer = stagingBuffers[slotIndex % stagingBuffers.Length];
            var mapInfo = new AllocationMapInfo(buffer.AllocationInfo)
            {
                Region = new MapRegion { Offset = ByteSize.Zero, Size = buffer.BufferSize }
            };
            Ptr ptr = allocator.MapMemory(in mapInfo);
            try
            {
                source.CopyTo(new Span<byte>((void*)(nint)ptr, ByteLength));
            }
            finally
            {
                allocator.UnmapMemory(in mapInfo);
            }
        }

        internal void RecordUpload(CommandBuffer cmd, int slotIndex, ulong generation)
        {
            BufferAllocationInfo buffer = stagingBuffers[slotIndex % stagingBuffers.Length];
            var subresource = new VkImageSubresourceRange
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };

            VkImageLayout oldLayout = currentLayout;
            VkPipelineStageFlags srcStage = oldLayout == VkImageLayout.Undefined
                ? VkPipelineStageFlags.TopOfPipeBit
                : VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.TransferBit;
            VkAccessFlags srcAccess = oldLayout == VkImageLayout.Undefined
                ? VkAccessFlags.None
                : VkAccessFlags.ShaderReadBit | VkAccessFlags.TransferWriteBit;

            var toTransfer = new VkImageMemoryBarrier
            {
                SrcAccessMask = srcAccess,
                DstAccessMask = VkAccessFlags.TransferWriteBit,
                OldLayout = oldLayout,
                NewLayout = VkImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = -1,
                DstQueueFamilyIndex = -1,
                Image = imageAlloc.Image,
                SubresourceRange = subresource,
            };
            cmd.PipelineBarrier(srcStage, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None,
                default, default, new ReadOnlySpan<VkImageMemoryBarrier>(in toTransfer));

            var copy = new VkBufferImageCopy
            {
                BufferOffset = ByteSize.Zero,
                BufferRowLength = Width,
                BufferImageHeight = Height,
                ImageSubresource = new VkImageSubresourceLayers
                {
                    AspectMask = VkImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
                ImageOffset = new VkOffset3D(0, 0, 0),
                ImageExtent = new VkExtent3D(Width, Height, 1),
            };
            cmd.CopyBufferToImage(buffer.VkBuffer, imageAlloc.Image, VkImageLayout.TransferDstOptimal,
                new ReadOnlySpan<VkBufferImageCopy>(in copy));

            var toSample = new VkImageMemoryBarrier
            {
                SrcAccessMask = VkAccessFlags.TransferWriteBit,
                DstAccessMask = VkAccessFlags.ShaderReadBit,
                OldLayout = VkImageLayout.TransferDstOptimal,
                NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = -1,
                DstQueueFamilyIndex = -1,
                Image = imageAlloc.Image,
                SubresourceRange = subresource,
            };
            cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None,
                default, default, new ReadOnlySpan<VkImageMemoryBarrier>(in toSample));

            currentLayout = VkImageLayout.ShaderReadOnlyOptimal;
            uploadedGeneration = generation;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            for (int i = 0; i < stagingBuffers.Length; i++)
            {
                if (!stagingBuffers[i].VkBuffer.IsNull())
                {
                    GpuMemoryTracker.Remove(GpuMemoryCategory.Staging, in stagingBuffers[i]);
                    allocator.FreeAllocation(in stagingBuffers[i]);
                    stagingBuffers[i] = default;
                }
            }
            if (!Sampler.IsNull())
                device.DestroySampler(Sampler, device.VkAllocator);
            if (!View.IsNull())
                device.DestroyImageView(View, device.VkAllocator);
            if (!imageAlloc.Image.IsNull())
            {
                GpuMemoryTracker.Remove(GpuMemoryCategory.GameTexture, in imageAlloc);
                allocator.FreeAllocation(in imageAlloc);
            }
        }
    }
}
