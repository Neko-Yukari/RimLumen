using System;
using RimLumen.Core.Lighting.Occlusion;

namespace RimLumen.Core.Tests.Rendering;

// Sprite art for tests: Width x Height alpha values, column 0 west, row 0 south, stretched over a placement.
internal sealed record Pattern(int Width, int Height, byte[] Alpha);

// The Occluder values OcclusionField.Stamp derives from a def, for sprites given directly as a pattern and placement.
internal static class OcclusionStamps
{
    internal static readonly Pattern Opaque = new(1, 1, [255]);

    // Ensures: the AlphaMask the host would build for art drawn at at: 4 texels per drawn cell, every fine sample the
    //          pattern value under its centre (nearest).
    internal static AlphaMask Fit(Pattern art, MaskPlacement at)
    {
        int width = Math.Max(1, (int)MathF.Round(at.SizeX * 4)), height = Math.Max(1, (int)MathF.Round(at.SizeZ * 4));
        int fineWidth = width * OcclusionLayout.MaskSamples, fineHeight = height * OcclusionLayout.MaskSamples;
        var fine = new byte[fineWidth * fineHeight];
        for (int z = 0; z < fineHeight; z++)
            for (int x = 0; x < fineWidth; x++)
                fine[z * fineWidth + x] = art.Alpha[(int)((z + 0.5f) / fineHeight * art.Height) * art.Width + (int)((x + 0.5f) / fineWidth * art.Width)];
        return new AlphaMask(width, height, fine);
    }

    // A blockLight thing over whole cells (a wall, rock, or the Q3 footprint), with its staticSunShadowHeight.
    internal static CellBox? Wall(this OcclusionBuilder builder, CellBox cells, float height = 1f) =>
        builder.Blocker(Opaque, Over(cells), height, null);

    internal static CellBox? Blocker(this OcclusionBuilder builder, Pattern art, MaskPlacement at, float height, DoorAxis? door, bool fade = false) =>
        builder.StampOccluder([new SpritePiece(Fit(art, at), at)], new Occluder(true, door, height, fade, Cover: 0f));

    // A non-blockLight height thing: fade for a shadowData height, hard for a staticSunShadowHeight.
    internal static CellBox? Height(this OcclusionBuilder builder, Pattern art, MaskPlacement at, float height, bool fade) =>
        builder.StampOccluder([new SpritePiece(Fit(art, at), at)], new Occluder(false, null, height, fade, Cover: 0f));

    internal static CellBox? Cover(this OcclusionBuilder builder, Pattern art, MaskPlacement at, float fillPercent) =>
        builder.StampOccluder([new SpritePiece(Fit(art, at), at)], new Occluder(false, null, 0f, Fade: false, fillPercent));

    internal static MaskPlacement Over(CellBox cells) => new((cells.MinX + cells.MaxX + 1) * 0.5f, (cells.MinZ + cells.MaxZ + 1) * 0.5f,
        cells.MaxX - cells.MinX + 1, cells.MaxZ - cells.MinZ + 1);
}
