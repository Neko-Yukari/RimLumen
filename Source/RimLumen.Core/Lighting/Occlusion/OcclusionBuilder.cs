using System;

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
        for (int z = box.MinZ * OcclusionLayout.TexelsPerCell; z < (box.MaxZ + 1) * OcclusionLayout.TexelsPerCell; z++)
            Array.Clear(texels, (z * texelWidth + box.MinX * OcclusionLayout.TexelsPerCell) * OcclusionLayout.Channels,
                (box.MaxX - box.MinX + 1) * OcclusionLayout.TexelsPerCell * OcclusionLayout.Channels);
    }

    // blockLight things (walls, smoothed walls, natural rock, doors, coolers, mod buildings using blockLight).
    // Ensures: texels whose centre falls inside at and whose box-filtered mask alpha >= 0.5 get R = Solid and
    //          G = max(G, sunHeight encoded); with door != null they also get FlagDoor (and FlagDoorAxisZ when the
    //          leaves slide along z). The closed leaves are stamped; the opening is resolved by DoorGap at sample time.
    //          Returns the cells of the texels it visited (null when none), like every mask-placed stamp below.
    internal CellBox? StampBlocker(AlphaMask mask, MaskPlacement at, float sunHeight, DoorAxis? door) =>
        Stamp(mask, at, (index, alpha) =>
        {
            if (alpha < 128) return;
            texels[index] = OcclusionLayout.Solid;
            texels[index + 1] = Math.Max(texels[index + 1], EncodeHeight(sunHeight));
            if (door.HasValue) texels[index + 3] |= (byte)(OcclusionLayout.FlagDoor | (door == DoorAxis.Z ? OcclusionLayout.FlagDoorAxisZ : 0));
        });

    // Q3 rule: a blockLight def whose sprite pixels cannot be read is solid over its def.size footprint.
    // Ensures: every texel of footprint gets R = Solid and G = max(G, sunHeight encoded).
    internal void StampSolidFootprint(CellBox footprint, float sunHeight) => StampFootprint(footprint, sunHeight, solid: true);

    // Requires: footprint is inside the map and sunHeight is positive.
    // Ensures: every footprint texel takes the greater of G and the encoded sunHeight; R, B and A are preserved.
    internal void StampSunFootprint(CellBox footprint, float sunHeight) => StampFootprint(footprint, sunHeight, solid: false);

    private void StampFootprint(CellBox footprint, float sunHeight, bool solid)
    {
        byte encodedHeight = EncodeHeight(sunHeight);
        for (int z = footprint.MinZ * OcclusionLayout.TexelsPerCell; z < (footprint.MaxZ + 1) * OcclusionLayout.TexelsPerCell; z++)
            for (int x = footprint.MinX * OcclusionLayout.TexelsPerCell; x < (footprint.MaxX + 1) * OcclusionLayout.TexelsPerCell; x++)
            {
                int i = (z * texelWidth + x) * OcclusionLayout.Channels;
                if (solid) texels[i] = OcclusionLayout.Solid;
                texels[i + 1] = Math.Max(texels[i + 1], encodedHeight);
            }
    }

    // Cover (!blockLight, Fillage Partial or Full, category Building/Plant/Item).
    // Requires: fillPercent in [0, 1].
    // Ensures: texels under at get R = max(R, min(MaxCover, round(alpha * fillPercent * 255))) unless R == Solid.
    internal CellBox? StampCover(AlphaMask mask, MaskPlacement at, float fillPercent) =>
        Stamp(mask, at, (index, alpha) => texels[index] = Math.Max(texels[index], (byte)Math.Min(OcclusionLayout.MaxCover, (int)MathF.Round(alpha * fillPercent))));

    // graphicData.shadowData box (volume x/z footprint, offset and rotation applied by the host).
    // Ensures: texels whose centre falls inside box get G = max(G, height encoded) and FlagSunFade.
    internal CellBox? StampSunBox(MaskPlacement box, float height) =>
        Stamp(new AlphaMask(1, 1, [byte.MaxValue]), box, (index, _) =>
        {
            texels[index + 1] = Math.Max(texels[index + 1], EncodeHeight(height));
            texels[index + 3] |= OcclusionLayout.FlagSunFade;
        });

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

    // Ensures: B (wall-core depth: chamfer distance in texels to the nearest non-solid texel, saturated at WallCoreDepth + 1; 0 for
    //          non-solid and door texels) is recomputed for the texels of box grown by WallCoreDepth texels, and the
    //          coarse texels of box grown by 1 cell are recomputed from the main texels. Coarse A (sun mask) is kept.
    internal void RecomputeDerived(CellBox box)
    {
        int minX = Math.Max(0, box.MinX * OcclusionLayout.TexelsPerCell - OcclusionLayout.WallCoreDepth);
        int minZ = Math.Max(0, box.MinZ * OcclusionLayout.TexelsPerCell - OcclusionLayout.WallCoreDepth);
        int maxX = Math.Min(texelWidth - 1, (box.MaxX + 1) * OcclusionLayout.TexelsPerCell - 1 + OcclusionLayout.WallCoreDepth);
        int maxZ = Math.Min(texelHeight - 1, (box.MaxZ + 1) * OcclusionLayout.TexelsPerCell - 1 + OcclusionLayout.WallCoreDepth);
        const int saturated = OcclusionLayout.WallCoreDepth + 1;
        int originX = Math.Max(0, minX - saturated), originZ = Math.Max(0, minZ - saturated);
        int windowWidth = Math.Min(texelWidth - 1, maxX + saturated) - originX + 1;
        int windowHeight = Math.Min(texelHeight - 1, maxZ + saturated) - originZ + 1;
        int[] depth = new int[windowWidth * windowHeight];
        for (int z = 0; z < windowHeight; z++)
            for (int x = 0; x < windowWidth; x++)
            {
                int p = z * windowWidth + x, worldX = x + originX, worldZ = z + originZ;
                int i = (worldZ * texelWidth + worldX) * OcclusionLayout.Channels;
                if (texels[i] != OcclusionLayout.Solid || (texels[i + 3] & OcclusionLayout.FlagDoor) != 0) continue;
                int d = saturated;
                if (worldX == 0 || worldZ == 0 || worldX == texelWidth - 1 || worldZ == texelHeight - 1) d = 1;
                if (x > 0) d = Math.Min(d, depth[p - 1] + 1);
                if (z > 0) d = Math.Min(d, depth[p - windowWidth] + 1);
                if (x > 0 && z > 0) d = Math.Min(d, depth[p - windowWidth - 1] + 1);
                if (x + 1 < windowWidth && z > 0) d = Math.Min(d, depth[p - windowWidth + 1] + 1);
                depth[p] = d;
            }
        for (int z = windowHeight - 1; z >= 0; z--)
            for (int x = windowWidth - 1; x >= 0; x--)
            {
                int p = z * windowWidth + x, d = depth[p];
                if (x + 1 < windowWidth) d = Math.Min(d, depth[p + 1] + 1);
                if (z + 1 < windowHeight) d = Math.Min(d, depth[p + windowWidth] + 1);
                if (x + 1 < windowWidth && z + 1 < windowHeight) d = Math.Min(d, depth[p + windowWidth + 1] + 1);
                if (x > 0 && z + 1 < windowHeight) d = Math.Min(d, depth[p + windowWidth - 1] + 1);
                depth[p] = d;
                int worldX = x + originX, worldZ = z + originZ;
                if (worldX >= minX && worldX <= maxX && worldZ >= minZ && worldZ <= maxZ)
                    texels[(worldZ * texelWidth + worldX) * OcclusionLayout.Channels + 2] = (byte)d;
            }
        CellBox area = box.Expanded(1, width, height);
        for (int z = area.MinZ; z <= area.MaxZ; z++)
            for (int x = area.MinX; x <= area.MaxX; x++)
            {
                int c = (z * width + x) * OcclusionLayout.Channels;
                coarse[c] = coarse[c + 1] = coarse[c + 2] = 0;
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

    // R2 sun mask (design 3.9): coarse A = 255 where vanilla's stencil bit 4 keeps sun shadows off the cell, else 0.
    // Requires: masked.Length == box cell count, row-major from (box.MinX, box.MinZ).
    // Ensures: only coarse A of box changes.
    internal void SetSunMask(CellBox box, ReadOnlySpan<bool> masked)
    {
        int boxWidth = box.MaxX - box.MinX + 1;
        for (int z = box.MinZ; z <= box.MaxZ; z++)
            for (int x = box.MinX; x <= box.MaxX; x++)
                coarse[(z * width + x) * OcclusionLayout.Channels + 3] =
                    masked[(z - box.MinZ) * boxWidth + x - box.MinX] ? byte.MaxValue : (byte)0;
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
