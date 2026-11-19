using System;
using System.Collections.Generic;

namespace RimLumen.Core.Lighting.Occlusion;

// Mutable CPU side of one map's occlusion map (design 1.2-1.4). Pure logic: the host (OcclusionField) feeds it masks
// and placements; nothing here touches Verse or the GPU. Every stamp takes the per-texel maximum, so stamping order
// never matters.
internal sealed class OcclusionBuilder
{
    private readonly int width, height, texelWidth, texelHeight;
    private readonly byte[] texels, coarse;

    // Requires: width, height > 0 (map size in cells).
    internal OcclusionBuilder(int width, int height)
    {
        this.width = width; this.height = height;
        texelWidth = width * OcclusionLayout.TexelsPerCell; texelHeight = height * OcclusionLayout.TexelsPerCell;
        texels = new byte[texelWidth * texelHeight * OcclusionLayout.Channels];
        coarse = new byte[width * height * OcclusionLayout.Channels];
    }

    // Ensures: R, G, B and A of every texel inside box are 0.
    internal void ClearCells(CellBox box)
    {
        int count = (box.MaxX - box.MinX + 1) * OcclusionLayout.TexelsPerCell;
        for (int z = box.MinZ * OcclusionLayout.TexelsPerCell; z < (box.MaxZ + 1) * OcclusionLayout.TexelsPerCell; z++)
        {
            int first = z * texelWidth + box.MinX * OcclusionLayout.TexelsPerCell;
            Array.Clear(texels, first * OcclusionLayout.Channels, count * OcclusionLayout.Channels);
        }
    }

    // One blockLight thing (design 1.2, 2026-11-19): its occupied cells, never its sprite.
    // Ensures: every texel within DistanceBand of cells takes B = max(B, d), d the signed distance of its centre to the
    //          cells' rectangle; the texels of cells get R = Solid, G = max(G, sunHeight encoded), FlagSunFade when fade, and
    //          for a door FlagDoor, plus FlagDoorAxisZ when the leaves slide along z (DoorGap resolves the opening at
    //          sample time). Returns the cells of the texels it wrote.
    internal CellBox StampBlocker(CellBox cells, DoorAxis? door, float sunHeight, bool fade)
    {
        const int t = OcclusionLayout.TexelsPerCell, band = OcclusionLayout.DistanceBand;
        int left = cells.MinX * t, bottom = cells.MinZ * t, right = (cells.MaxX + 1) * t, top = (cells.MaxZ + 1) * t;
        byte encodedHeight = EncodeHeight(sunHeight);
        byte flags = (byte)((fade ? OcclusionLayout.FlagSunFade : 0) | (door.HasValue ? OcclusionLayout.FlagDoor : 0)
            | (door == DoorAxis.Z ? OcclusionLayout.FlagDoorAxisZ : 0));
        for (int z = Math.Max(0, bottom - band); z < Math.Min(texelHeight, top + band); z++)
            for (int x = Math.Max(0, left - band); x < Math.Min(texelWidth, right + band); x++)
            {
                float outX = MathF.Max(left - (x + 0.5f), x + 0.5f - right), outZ = MathF.Max(bottom - (z + 0.5f), z + 0.5f - top);
                float d = outX < 0f && outZ < 0f ? -MathF.Max(outX, outZ) : -MathF.Sqrt(Square(MathF.Max(outX, 0f)) + Square(MathF.Max(outZ, 0f)));
                int index = (z * texelWidth + x) * OcclusionLayout.Channels;
                texels[index + 2] = Math.Max(texels[index + 2], OcclusionLayout.EncodeDistance(d));
                if (d <= 0f) continue;
                texels[index] = OcclusionLayout.Solid;
                texels[index + 1] = Math.Max(texels[index + 1], encodedHeight);
                texels[index + 3] |= flags;
            }
        return cells.Expanded(1, width, height);
    }

    private static float Square(float v) => v * v;

    // One non-blockLight cover thing without a height (design 1.2; a height thing is an ObjectShadow caster instead).
    // Requires: sprite non-empty, cover > 0. Ensures: every texel under a piece gets R = max(R, min(MaxCover,
    //          round(alpha * cover))) unless R == Solid. Returns the cells of the texels it wrote; null when none.
    internal CellBox? StampCover(IReadOnlyList<SpritePiece> sprite, float cover)
    {
        CellBox? covered = null;
        foreach (SpritePiece piece in sprite)
            covered = covered.Union(Stamp(piece.Mask, piece.At, (index, alpha) =>
                texels[index] = Math.Max(texels[index], (byte)Math.Min(OcclusionLayout.MaxCover, (int)MathF.Round(alpha * cover)))));
        return covered;
    }

    private CellBox? Stamp(AlphaMask mask, MaskPlacement at, Action<int, byte> apply)
    {
        float left = at.CenterX - at.SizeX * 0.5f, bottom = at.CenterZ - at.SizeZ * 0.5f;
        int minX = Math.Max(0, (int)MathF.Ceiling(left * OcclusionLayout.TexelsPerCell - 0.5f));
        int minZ = Math.Max(0, (int)MathF.Ceiling(bottom * OcclusionLayout.TexelsPerCell - 0.5f));
        int maxX = Math.Min(texelWidth, (int)MathF.Ceiling((left + at.SizeX) * OcclusionLayout.TexelsPerCell - 0.5f));
        int maxZ = Math.Min(texelHeight, (int)MathF.Ceiling((bottom + at.SizeZ) * OcclusionLayout.TexelsPerCell - 0.5f));
        for (int z = minZ; z < maxZ; z++)
            for (int x = minX; x < maxX; x++)
            {
                int mx = (int)(((x + 0.5f) * 0.25f - left) / at.SizeX * mask.Width);
                int mz = (int)(((z + 0.5f) * 0.25f - bottom) / at.SizeZ * mask.Height);
                apply((z * texelWidth + x) * OcclusionLayout.Channels, mask.Alpha[mz * mask.Width + mx]);
            }
        if (minX >= maxX || minZ >= maxZ) return null;
        return new CellBox(minX / OcclusionLayout.TexelsPerCell, minZ / OcclusionLayout.TexelsPerCell,
            (maxX - 1) / OcclusionLayout.TexelsPerCell, (maxZ - 1) / OcclusionLayout.TexelsPerCell);
    }

    private static byte EncodeHeight(float height) => (byte)Math.Clamp((int)MathF.Round(height * 127.5f), 0, byte.MaxValue);

    // Ensures: the coarse texels of box grown by 1 cell are recomputed from the main texels. The result depends only on
    //          the texels, never on box, so incremental updates equal a full rebuild.
    internal void RecomputeDerived(CellBox box)
    {
        CellBox area = box.Expanded(1, width, height);
        for (int z = area.MinZ; z <= area.MaxZ; z++)
            for (int x = area.MinX; x <= area.MaxX; x++)
            {
                int c = (z * width + x) * OcclusionLayout.Channels;
                coarse[c] = coarse[c + 1] = coarse[c + 2] = coarse[c + 3] = 0;
                for (int tz = Math.Max(0, (z - 1) * OcclusionLayout.TexelsPerCell); tz < Math.Min(texelHeight, (z + 2) * OcclusionLayout.TexelsPerCell); tz++)
                    for (int tx = Math.Max(0, (x - 1) * OcclusionLayout.TexelsPerCell); tx < Math.Min(texelWidth, (x + 2) * OcclusionLayout.TexelsPerCell); tx++)
                    {
                        int i = (tz * texelWidth + tx) * OcclusionLayout.Channels;
                        coarse[c] = Math.Max(coarse[c], texels[i]);
                        coarse[c + 1] = Math.Max(coarse[c + 1], texels[i + 1]);
                        if ((texels[i + 3] & OcclusionLayout.FlagDoor) != 0)
                            coarse[c + 2] = OcclusionLayout.CoarseDoor;
                    }
            }
    }

    // Ensures: a new snapshot holding copies of the current arrays; later edits do not reach it.
    internal OcclusionMap Publish() => new(width, height, (byte[])texels.Clone(), (byte[])coarse.Clone());
}

// Door leaves slide along X or Z in world space.
internal enum DoorAxis : byte
{
    X,
    Z,
}
