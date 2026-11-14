using System;
using Waylith.Rendering;

namespace RimLumen.Core.Rendering;

// Non-owning Texture wrapper exposing an active RenderTexture.Color to Material.SetTexture
// without registering or releasing the underlying GpuTextureAllocation.
internal sealed class BorrowedRenderTexture : Texture
{
    public override int width { get; set; }
    public override int height { get; set; }

    internal BorrowedRenderTexture()
    {
        filterMode = FilterMode.Point;
        wrapMode = TextureWrapMode.Clamp;
    }

    // Requires: target.Color is allocated and currently in ShaderReadOnlyOptimal layout.
    // Ensures: width/height/GpuResource mirror target.Color without transferring ownership.
    internal void Bind(RenderTexture target)
    {
        ArgumentNullException.ThrowIfNull(target);
        // The allocation is borrowed: GpuResource is only read (sampling), never disposed here.
        width = target.Width;
        height = target.Height;
        GpuResource = target.Color;
    }

    // Drops the borrowed pointer without touching the allocation: the owner (HdrTargets) is being disposed, so
    // anything still holding this proxy must not keep a freed GpuTextureAllocation around.
    internal void Clear()
    {
        GpuResource = null;
        width = 0;
        height = 0;
    }

    public override void Dispose()
    {
        Clear();
        base.Dispose();
    }
}
