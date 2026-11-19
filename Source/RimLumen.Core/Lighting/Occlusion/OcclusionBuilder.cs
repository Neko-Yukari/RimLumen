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
    // StampOccluder's d of every texel each piece reaches, in piece and row order; reused across stamps.
    private float[] reached = new float[256];

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

    // One non-blockLight thing (design 1.2): its sprite is the only footprint of every channel. Requires: sprite non-empty.
    // Ensures: d = the signed distance of a texel centre to the sprite's alpha >= 0.5 edge (AlphaMask.Distance).
    //          1. Height > 0, when some texel of the sprite has d > 0: every texel within DistanceBand of a piece takes
    //             B = max(B, d); texels with d > 0 get G = max(G, Height encoded), FlagPointHeight, and FlagSunFade
    //             when Fade.
    //          2. Otherwise the thing stamps its cover: every texel under a piece gets R = max(R, min(MaxCover,
    //             round(alpha * Cover))) unless R == Solid. Cover 0 stamps nothing.
    //          Returns the cells of the texels it wrote; null when none.
    internal CellBox? StampOccluder(IReadOnlyList<SpritePiece> sprite, Occluder occluder)
    {
        if (occluder.Height > 0f && Reached(sprite))
        {
            byte height = EncodeHeight(occluder.Height);
            byte flags = (byte)((occluder.Fade ? OcclusionLayout.FlagSunFade : 0) | OcclusionLayout.FlagPointHeight);
            int next = 0, writtenMinX = int.MaxValue, writtenMinZ = int.MaxValue, writtenMaxX = -1, writtenMaxZ = -1;
            foreach (SpritePiece piece in sprite)
            {
                (int minX, int minZ, int maxX, int maxZ) = Reach(piece.At);
                for (int z = minZ; z <= maxZ; z++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        float d = reached[next++];
                        byte encoded = OcclusionLayout.EncodeDistance(d);
                        if (encoded == 0) continue;
                        int index = (z * texelWidth + x) * OcclusionLayout.Channels;
                        texels[index + 2] = Math.Max(texels[index + 2], encoded);
                        writtenMinX = Math.Min(writtenMinX, x); writtenMaxX = Math.Max(writtenMaxX, x);
                        writtenMinZ = Math.Min(writtenMinZ, z); writtenMaxZ = Math.Max(writtenMaxZ, z);
                        if (d <= 0f) continue;
                        texels[index + 1] = Math.Max(texels[index + 1], height);
                        texels[index + 3] |= flags;
                    }
            }
            return new CellBox(writtenMinX / OcclusionLayout.TexelsPerCell, writtenMinZ / OcclusionLayout.TexelsPerCell,
                writtenMaxX / OcclusionLayout.TexelsPerCell, writtenMaxZ / OcclusionLayout.TexelsPerCell);
        }
        if (occluder.Cover == 0f) return null;
        CellBox? covered = null;
        foreach (SpritePiece piece in sprite)
            covered = covered.Union(Stamp(piece.Mask, piece.At, (index, alpha) =>
                texels[index] = Math.Max(texels[index], (byte)Math.Min(OcclusionLayout.MaxCover, (int)MathF.Round(alpha * occluder.Cover)))));
        return covered;
    }

    // Ensures: reached holds d of every texel each piece reaches; true when some d > 0. d at the centre of texel (x, z) is
    //          AlphaMask.Distance sampled bilinearly, scaled from mask texels to map texels by the mean of the two axes'
    //          stretch. Requires: masks built at 4 texels per drawn cell, so the stretch differs from 1 only by rounding.
    private bool Reached(IReadOnlyList<SpritePiece> sprite)
    {
        const int s = OcclusionLayout.MaskSamples;
        int next = 0;
        bool inside = false;
        foreach (SpritePiece piece in sprite)
        {
            AlphaMask mask = piece.Mask;
            MaskPlacement at = piece.At;
            (int minX, int minZ, int maxX, int maxZ) = Reach(at);
            int count = (maxX - minX + 1) * (maxZ - minZ + 1);
            if (next + count > reached.Length) Array.Resize(ref reached, Math.Max(reached.Length * 2, next + count));
            // Fine sample coordinate u = u0 + x * du of texel x's centre, likewise v.
            float stretchX = mask.Width / (at.SizeX * OcclusionLayout.TexelsPerCell), stretchZ = mask.Height / (at.SizeZ * OcclusionLayout.TexelsPerCell);
            float du = stretchX * s, dv = stretchZ * s;
            float u0 = (0.5f - (at.CenterX - at.SizeX * 0.5f) * OcclusionLayout.TexelsPerCell) * du + OcclusionLayout.DistanceBand * s - 0.5f;
            float v0 = (0.5f - (at.CenterZ - at.SizeZ * 0.5f) * OcclusionLayout.TexelsPerCell) * dv + OcclusionLayout.DistanceBand * s - 0.5f;
            float scale = 1f / MathF.Sqrt(stretchX * stretchZ);
            int w = mask.DistanceWidth, h = mask.DistanceHeight;
            float[] f = mask.Distance;
            for (int z = minZ; z <= maxZ; z++)
            {
                float v = Math.Clamp(v0 + z * dv, 0f, h - 1);
                int j = Math.Min((int)v, h - 2);
                float fz = v - j;
                for (int x = minX; x <= maxX; x++)
                {
                    float u = Math.Clamp(u0 + x * du, 0f, w - 1);
                    int i = Math.Min((int)u, w - 2);
                    float fx = u - i;
                    int a = j * w + i;
                    float d = ((f[a] * (1 - fx) + f[a + 1] * fx) * (1 - fz) + (f[a + w] * (1 - fx) + f[a + w + 1] * fx) * fz) * scale;
                    reached[next++] = d;
                    inside |= d > 0f;
                }
            }
        }
        return inside;
    }

    // The texels whose centre lies within DistanceBand texels of at, clipped to the map.
    private (int MinX, int MinZ, int MaxX, int MaxZ) Reach(MaskPlacement at)
    {
        float left = (at.CenterX - at.SizeX * 0.5f) * OcclusionLayout.TexelsPerCell - OcclusionLayout.DistanceBand;
        float bottom = (at.CenterZ - at.SizeZ * 0.5f) * OcclusionLayout.TexelsPerCell - OcclusionLayout.DistanceBand;
        float right = (at.CenterX + at.SizeX * 0.5f) * OcclusionLayout.TexelsPerCell + OcclusionLayout.DistanceBand;
        float top = (at.CenterZ + at.SizeZ * 0.5f) * OcclusionLayout.TexelsPerCell + OcclusionLayout.DistanceBand;
        return (Math.Max(0, (int)MathF.Ceiling(left - 0.5f)), Math.Max(0, (int)MathF.Ceiling(bottom - 0.5f)),
            Math.Min(texelWidth - 1, (int)MathF.Floor(right - 0.5f)), Math.Min(texelHeight - 1, (int)MathF.Floor(top - 0.5f)));
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
                        if ((texels[i + 3] & OcclusionLayout.FlagPointHeight) != 0)
                            coarse[c + 3] = Math.Max(coarse[c + 3], texels[i + 1]);
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
