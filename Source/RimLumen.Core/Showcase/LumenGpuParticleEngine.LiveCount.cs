using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Waylith;
using Waylith.Rendering.Compute;

namespace RimLumen.Core.ParticlesGpu;

// Lives under Showcase/ so the release build strips it with the rest of the benchmark harness.
internal sealed partial class LumenGpuParticleEngine
{
    // Requires: main thread. Benchmark-only synchronous readback; the normal simulation path never calls it.
    // Ensures: counts particles whose lifetime slot is occupied across the engine's allocated ranges.
    internal int CountLiveParticles()
    {
        var app = EngineApplication.Instance;
        app.WaitForRenderThreadIdle();
        int live = 0;
        app.GraphicsDevice!.WaitIdleAndRun(() =>
        {
            if (particles is null) return;
            var staging = GpuComputeBuffer.Create(app.Device, app.GraphicsDevice!.Allocator,
                "RimLumen.ParticleBenchReadback", (long)capacity * 64, readback: true);
            try
            {
                app.GraphicsDevice.ExecuteOneShot(cmd =>
                {
                    var barrier = ParticleBarrier(VkAccessFlags.ShaderWriteBit, VkAccessFlags.TransferReadBit);
                    cmd.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.TransferBit,
                        VkDependencyFlags.None, default(ReadOnlySpan<VkMemoryBarrier>),
                        new ReadOnlySpan<VkBufferMemoryBarrier>(in barrier), default(ReadOnlySpan<VkImageMemoryBarrier>));
                    var copy = new VkBufferCopy { Size = particles.Size };
                    cmd.CopyBuffer(particles.Buffer, staging.Buffer, new ReadOnlySpan<VkBufferCopy>(in copy));
                    var host = new VkBufferMemoryBarrier
                    {
                        Buffer = staging.Buffer, Size = staging.Size, SrcQueueFamilyIndex = -1, DstQueueFamilyIndex = -1,
                        SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.HostReadBit
                    };
                    cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.HostBit,
                        VkDependencyFlags.None, default(ReadOnlySpan<VkMemoryBarrier>),
                        new ReadOnlySpan<VkBufferMemoryBarrier>(in host), default(ReadOnlySpan<VkImageMemoryBarrier>));
                });
                var data = new GpuParticle[capacity];
                staging.Read(MemoryMarshal.AsBytes(data.AsSpan()));
                // Unallocated slots are undefined. Inspect only the engine's allocated ranges.
                foreach (Instance instance in instances.Values)
                    for (int i = instance.Slots.Offset; i < instance.Slots.Offset + instance.Slots.Capacity; i++)
                        if (data[i].ActiveEmitter != 0u) live++;
            }
            finally
            {
                staging.DisposeNow();
            }
        });
        return live;
    }
}
