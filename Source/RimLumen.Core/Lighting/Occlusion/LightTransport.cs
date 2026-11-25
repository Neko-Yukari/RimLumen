using System;
using System.Numerics;

namespace RimLumen.Core.Lighting.Occlusion;

// Door opening (design 1.5), mirrored by lumenDoorOpacity in RimLumenOcclusion.glsl.
internal static class DoorGap
{
    // u: texel centre offset from the door's centre along its axis, in door widths ([-0.5, 0.5] inside the door).
    // halfGap: the decoded DoorStates R (door widths, [0, 0.5]); door-type specific geometry is resolved by the host.
    // Requires: halfGap in [0, 0.5], closedOpacity in [0, 1].
    // Ensures: 0 when |u| < halfGap, else closedOpacity.
    internal static float Opacity(float u, float halfGap, float closedOpacity) => MathF.Abs(u) < halfGap ? 0f : closedOpacity;
}

// The one grid walk of every march (lumenGridEnter and lumenGridLeave in RimLumenOcclusion.glsl): the cells of spacing size
// crossed by start + direction * a, a >= 0.
internal static class GridWalk
{
    // The cell holding start; a start on a cell edge belongs to the cell ahead along direction.
    internal static (int X, int Z) Enter(Vector2 start, Vector2 direction, float size)
    {
        Vector2 g = start / size;
        int x = (int)MathF.Floor(g.X), z = (int)MathF.Floor(g.Y);
        if (direction.X < 0f && g.X == x) x--;
        if (direction.Y < 0f && g.Y == z) z--;
        return (x, z);
    }

    // Ensures: the distance where the ray leaves cell (x, z), at most limit; (x, z) steps into the next cell on every
    //          axis whose edge it reaches by then.
    internal static float Leave(Vector2 start, Vector2 direction, float size, float limit, ref int x, ref int z)
    {
        float tx = direction.X > 0f ? ((x + 1) * size - start.X) / direction.X : direction.X < 0f ? (x * size - start.X) / direction.X : float.PositiveInfinity;
        float tz = direction.Y > 0f ? ((z + 1) * size - start.Y) / direction.Y : direction.Y < 0f ? (z * size - start.Y) / direction.Y : float.PositiveInfinity;
        float end = MathF.Min(limit, MathF.Min(tx, tz));
        if (tx <= end) x += direction.X > 0f ? 1 : -1;
        if (tz <= end) z += direction.Y > 0f ? 1 : -1;
        return end;
    }
}

// Reference of RimLumenOcclusion.glsl, line for line; the offline tests call this instead of the shader.
internal static class LightTransport
{
    // Transmittance of the segment target -> light through the occlusion map (design 1.6-1.8 and 2), returned as the
    // solid term with the cover term in cover; the caller multiplies cover and combines solid with other opaque terms.
    // Ensures: both in [0, 1]; 1 when target == light off a solid texel. Wall-body rule per 1.7 (2026-11-19): a target on a solid texel
    //          lies in a blockLight cell and takes the value of the point half a texel outside the cell, level with the
    //          target, across each cell edge that faces the light (the light lies beyond its line) and whose texel
    //          outside is not solid, the one with the larger solid * cover; when both edges face the light and neither is
    //          free, the point half a texel outside the corner between them, if that is not solid (an inner corner, lit as
    //          vanilla's 8-connected GlowGrid lights it); 0 otherwise. So only the first solid layer is lit, each cell as
    //          the ground just in front of its free face or corner, and the cells behind it stay dark.
    internal static float Visibility(in OcclusionFrame frame, Vector2 light, Vector2 target, out float cover)
    {
        Vector2 c0 = default, c1 = default;
        int count = 0;
        if (!Solid(frame, target))
        {
            c0 = target;
            count = 1;
        }
        else
        {
            float cellX = MathF.Floor(target.X), cellZ = MathF.Floor(target.Y);
            float faceX = light.X > cellX ? cellX + 1.125f : cellX - 0.125f, faceZ = light.Y > cellZ ? cellZ + 1.125f : cellZ - 0.125f;
            bool facesX = light.X > cellX + 1f || light.X < cellX, facesZ = light.Y > cellZ + 1f || light.Y < cellZ;
            var cx = new Vector2(faceX, target.Y);
            var cz = new Vector2(target.X, faceZ);
            if (facesX && !Solid(frame, cx)) { c0 = cx; count = 1; }
            if (facesZ && !Solid(frame, cz))
            {
                if (count == 0) c0 = cz;
                else c1 = cz;
                count++;
            }
            if (count == 0 && facesX && facesZ)
            {
                var corner = new Vector2(faceX, faceZ);
                if (!Solid(frame, corner)) { c0 = corner; count = 1; }
            }
        }
        float best = 0f;
        cover = 0f;
        for (int i = 0; i < count; i++)
        {
            float solid = Transport(frame, light, i == 0 ? c0 : c1, out float c);
            if (solid * c > best * cover)
            {
                best = solid;
                cover = c;
                if (best * cover >= 1f) break;
            }
        }
        return best;
    }

    // Ensures: both in [0, 1]; 1 when target == light; both 0 when the segment is blocked. Smoothed per 1.8: solid as
    //          min(1 - s_i) with s_i the TexelSplit edge of the solid texels, cover as prod (1 - o_i)^(ds_i), both from
    //          the 2 x 2 texels around each step. The light's own solid run and its exit band never count s. Positions
    //          outside the map are transparent. Door texels take DoorGap with the frame's DoorStates. Independent of
    //          step length for cover. Requires: target not on a solid texel.
    private static float Transport(in OcclusionFrame frame, Vector2 light, Vector2 target, out float cover)
    {
        cover = 1f;
        Vector2 delta = light - target;
        float length = delta.Length();
        if (length == 0f) return 1f;
        cover = 0f;
        Vector2 direction = delta / length;
        float lightRun = RunLength(frame, light, -direction, length);
        float solidVisibility = 1f, pendingSolid = 1f, coverVisibility = 1f, distance = 0f;
        (int cellX, int cellZ) = GridWalk.Enter(target, direction, 1f);
        while (distance < length)
        {
            int x = cellX, z = cellZ;
            float end = GridWalk.Leave(target, direction, 1f, length, ref cellX, ref cellZ);
            bool inside = x >= 0 && z >= 0 && x < frame.Map.Width && z < frame.Map.Height;
            int coarseIndex = inside ? (z * frame.Map.Width + x) * 4 : 0;
            if (!inside || frame.Map.Coarse[coarseIndex] == 0)
            {
                solidVisibility = MathF.Min(solidVisibility, pendingSolid);
                pendingSolid = 1f;
                distance = end;
                continue;
            }
            bool doors = frame.Map.Coarse[coarseIndex + 2] != 0;
            int steps = (int)MathF.Ceiling((end - distance) * 4f);
            float ds = (end - distance) / steps;
            for (int step = 0; step < steps; step++)
            {
                float at = distance + (step + 0.5f) * ds;
                Vector2 p = target + direction * at;
                bool solid = lightRun > 0f && Solid(frame, p);
                if (at >= length - lightRun)
                {
                    pendingSolid = 1f;
                    continue;
                }
                Sample(frame, p, doors, out float s, out float o);
                if (lightRun > 0f && !solid && s > 0f)
                {
                    pendingSolid = MathF.Min(pendingSolid, 1f - s);
                }
                else
                {
                    solidVisibility = MathF.Min(solidVisibility, MathF.Min(pendingSolid, 1f - s));
                    pendingSolid = 1f;
                }
                if (o > 0f) coverVisibility *= MathF.Pow(1f - o, ds);
                if (solidVisibility * coverVisibility < 1f / 255f) return 0f;
            }
            distance = end;
        }
        cover = coverVisibility;
        return MathF.Min(solidVisibility, lightRun > 0f ? 1f : pendingSolid);
    }

    // Length of the contiguous run of solid texels from start along direction, at most limit; 0 when start's own texel is
    // not solid. The light-end skip of Visibility.
    private static float RunLength(in OcclusionFrame frame, Vector2 start, Vector2 direction, float limit)
    {
        if (!Solid(frame, start)) return 0f;
        float distance = 0f;
        (int x, int z) = GridWalk.Enter(start, direction, 0.25f);
        while (distance < limit && SolidTexel(frame, x, z))
            distance = GridWalk.Leave(start, direction, 0.25f, limit, ref x, ref z);
        return distance;
    }

    private static bool Solid(in OcclusionFrame frame, Vector2 position) =>
        SolidTexel(frame, (int)MathF.Floor(position.X * 4f), (int)MathF.Floor(position.Y * 4f));

    private static bool SolidTexel(in OcclusionFrame frame, int x, int z)
    {
        if (x < 0 || z < 0 || x >= frame.Map.TexelWidth || z >= frame.Map.TexelHeight) return false;
        int i = (z * frame.Map.TexelWidth + x) * 4;
        return Opacity(frame, x, z, frame.Map.Texels[i], frame.Map.Texels[i + 3]) == 1f;
    }

    private static float Opacity(in OcclusionFrame frame, int x, int z, byte value, byte flags)
    {
        if ((flags & OcclusionLayout.FlagDoor) == 0) return value / 255f;
        int cellX = x >> 2, cellZ = z >> 2;
        int i = (cellZ * frame.Map.Width + cellX) * 4;
        byte[] doors = frame.Doors.Cells;
        bool axisZ = (flags & OcclusionLayout.FlagDoorAxisZ) != 0;
        float local = (((axisZ ? z : x) & 3) + 0.5f) * 0.25f;
        float u = (doors[i + 1] + local) / doors[i + 2] - 0.5f;
        return DoorGap.Opacity(u, doors[i] / 510f, value / 255f);
    }

    private static void Sample(in OcclusionFrame frame, Vector2 p, bool doors, out float solid, out float cover)
    {
        Vector2 uv = p * 4f - new Vector2(0.5f);
        int x = (int)MathF.Floor(uv.X), z = (int)MathF.Floor(uv.Y);
        var opacity = new Vector4(Read(frame, x, z, doors), Read(frame, x + 1, z, doors), Read(frame, x, z + 1, doors), Read(frame, x + 1, z + 1, doors));
        var solids = new Vector4(opacity.X == 1f ? 1f : 0f, opacity.Y == 1f ? 1f : 0f, opacity.Z == 1f ? 1f : 0f, opacity.W == 1f ? 1f : 0f);
        OcclusionMap map = frame.Map;
        var distances = new Vector4(Distance(map, x, z), Distance(map, x + 1, z), Distance(map, x, z + 1), Distance(map, x + 1, z + 1));
        TexelSplit split = TexelSplit.Of(opacity, solids, distances, uv.X - x, uv.Y - z);
        solid = split.Edge;
        cover = split.Outside;
    }

    // Decoded B of texel (x, z); -DistanceBand outside the map. Shared with SunTransport.
    internal static float Distance(OcclusionMap map, int x, int z) =>
        x < 0 || z < 0 || x >= map.TexelWidth || z >= map.TexelHeight ? -OcclusionLayout.DistanceBand
            : OcclusionLayout.DecodeDistance(map.Texels[(z * map.TexelWidth + x) * 4 + 2]);

    private static float Read(in OcclusionFrame frame, int x, int z, bool doors)
    {
        if (x < 0 || z < 0 || x >= frame.Map.TexelWidth || z >= frame.Map.TexelHeight) return 0f;
        int i = (z * frame.Map.TexelWidth + x) * 4;
        byte value = frame.Map.Texels[i];
        return doors ? Opacity(frame, x, z, value, frame.Map.Texels[i + 3]) : value / 255f;
    }
}
