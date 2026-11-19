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

// Reference of RimLumenOcclusion.glsl, line for line; the offline tests call this instead of the shader.
internal static class LightTransport
{
    // Transmittance of the segment target -> light through the occlusion map (design 1.6-1.8 and 2), returned as the
    // solid term with the cover term in cover; the caller multiplies cover and combines solid with other opaque terms.
    // Ensures: both in [0, 1]; 1 when target == light; both 0 when the segment is blocked. Smoothed per 1.8: solid as min(1 - s_i)
    //          with s_i the TexelSplit edge of the solid texels, cover as prod (1 - o_i)^(ds_i), both from the 2 x 2 texels
    //          around each step. Wall-body rule per 1.7: a target on a solid texel first skips its own contiguous solid
    //          run without loss, and is 0 if that run reaches a wall-core texel (d >= DeepCore; never a door). The exit band right after
    //          that run (from the first step whose nearest texel is not solid until the first step whose 2 x 2 solid
    //          fraction s is 0) ignores s but still accumulates cover; a nearest texel turning solid again inside the band
    //          gives 0. The light's own solid run and its exit band never count s either. Positions outside the map are transparent. Door texels take DoorGap with the
    //          frame's DoorStates. Independent of step length for cover.
    internal static float Visibility(in OcclusionFrame frame, Vector2 light, Vector2 target, out float cover)
    {
        cover = 1f;
        Vector2 delta = light - target;
        float length = delta.Length();
        if (length == 0f) return 1f;
        cover = 0f;
        Vector2 direction = delta / length;
        float lightRun = RunLength(frame, light, -direction, length, false);
        bool skipTarget = Solid(frame, target, out bool deepTarget);
        if (skipTarget && deepTarget) return 0f;
        bool exitBand = false;
        float solidVisibility = 1f, pendingSolid = 1f, coverVisibility = 1f, distance = 0f;
        int cellX = (int)MathF.Floor(target.X), cellZ = (int)MathF.Floor(target.Y);
        if (direction.X < 0f && target.X == cellX) cellX--;
        if (direction.Y < 0f && target.Y == cellZ) cellZ--;
        while (distance < length)
        {
            float nextX = direction.X > 0f ? (cellX + 1 - target.X) / direction.X : direction.X < 0f ? (cellX - target.X) / direction.X : float.PositiveInfinity;
            float nextZ = direction.Y > 0f ? (cellZ + 1 - target.Y) / direction.Y : direction.Y < 0f ? (cellZ - target.Y) / direction.Y : float.PositiveInfinity;
            float end = MathF.Min(length, MathF.Min(nextX, nextZ));
            bool inside = cellX >= 0 && cellZ >= 0 && cellX < frame.Map.Width && cellZ < frame.Map.Height;
            int coarseIndex = inside ? (cellZ * frame.Map.Width + cellX) * 4 : 0;
            if (!inside || frame.Map.Coarse[coarseIndex] == 0)
            {
                skipTarget = false;
                exitBand = false;
                solidVisibility = MathF.Min(solidVisibility, pendingSolid);
                pendingSolid = 1f;
                distance = end;
                if (nextX <= end) cellX += direction.X > 0f ? 1 : -1;
                if (nextZ <= end) cellZ += direction.Y > 0f ? 1 : -1;
                continue;
            }
            bool doors = frame.Map.Coarse[coarseIndex + 2] != 0;
            int steps = (int)MathF.Ceiling((end - distance) * 4f);
            float ds = (end - distance) / steps;
            for (int step = 0; step < steps; step++)
            {
                float at = distance + (step + 0.5f) * ds;
                Vector2 p = target + direction * at;
                bool deep = false;
                bool solid = (skipTarget || exitBand || lightRun > 0f) && Solid(frame, p, out deep);
                if (skipTarget)
                {
                    if (solid)
                    {
                        if (deep) return 0f;
                        continue;
                    }
                    skipTarget = false;
                    exitBand = true;
                }
                if (exitBand && solid) return 0f;
                if (at >= length - lightRun)
                {
                    pendingSolid = 1f;
                    continue;
                }
                Sample(frame, p, doors, out float s, out float o);
                if (exitBand)
                {
                    if (s == 0f) exitBand = false;
                }
                else if (lightRun > 0f && !solid && s > 0f)
                {
                    pendingSolid = MathF.Min(pendingSolid, 1f - s);
                }
                else
                {
                    solidVisibility = MathF.Min(solidVisibility, MathF.Min(pendingSolid, 1f - s));
                    pendingSolid = 1f;
                }
                coverVisibility *= MathF.Pow(1f - o, ds);
                if (solidVisibility * coverVisibility < 1f / 255f) return 0f;
            }
            distance = end;
            if (nextX <= end) cellX += direction.X > 0f ? 1 : -1;
            if (nextZ <= end) cellZ += direction.Y > 0f ? 1 : -1;
        }
        cover = coverVisibility;
        return MathF.Min(solidVisibility, lightRun > 0f ? 1f : pendingSolid);
    }

    // Length of the contiguous run of solid texels (height: FlagPointHeight texels with G > 0, the point-light height
    // texels of SunTransport.PointVisibility) from start along direction, at most limit;
    // 0 when start's own texel is not solid (solid), or when the first texel along direction carries no height (height:
    // a start on a texel edge belongs to the texel ahead, because shadowData boxes are rasterised at texel centres and a
    // glower at its cell centre, a texel corner, would miss the box it stands in). The light-end skip of Visibility and
    // both footprint skips of SunTransport.PointVisibility.
    internal static float RunLength(in OcclusionFrame frame, Vector2 start, Vector2 direction, float limit, bool height)
    {
        if (!height && !Solid(frame, start, out _)) return 0f;
        float distance = 0f;
        Vector2 texel = start * 4f;
        int x = (int)MathF.Floor(texel.X), z = (int)MathF.Floor(texel.Y);
        if (direction.X < 0f && texel.X == x) x--;
        if (direction.Y < 0f && texel.Y == z) z--;
        while (distance < limit)
        {
            if (!RunTexel(frame, x, z, height)) break;
            float tx = direction.X > 0f ? ((x + 1) * 0.25f - start.X) / direction.X : direction.X < 0f ? (x * 0.25f - start.X) / direction.X : float.PositiveInfinity;
            float tz = direction.Y > 0f ? ((z + 1) * 0.25f - start.Y) / direction.Y : direction.Y < 0f ? (z * 0.25f - start.Y) / direction.Y : float.PositiveInfinity;
            distance = MathF.Min(limit, MathF.Min(tx, tz));
            if (tx <= distance) x += direction.X > 0f ? 1 : -1;
            if (tz <= distance) z += direction.Y > 0f ? 1 : -1;
        }
        return distance;
    }

    private static bool RunTexel(in OcclusionFrame frame, int x, int z, bool height) => height
        ? x >= 0 && z >= 0 && x < frame.Map.TexelWidth && z < frame.Map.TexelHeight && frame.Map.Texels[(z * frame.Map.TexelWidth + x) * 4 + 1] != 0
            && (frame.Map.Texels[(z * frame.Map.TexelWidth + x) * 4 + 3] & OcclusionLayout.FlagPointHeight) != 0
        : SolidTexel(frame, x, z, out _);

    private static bool Solid(in OcclusionFrame frame, Vector2 position, out bool deep) =>
        SolidTexel(frame, (int)MathF.Floor(position.X * 4f), (int)MathF.Floor(position.Y * 4f), out deep);

    private static bool SolidTexel(in OcclusionFrame frame, int x, int z, out bool deep)
    {
        deep = false;
        if (x < 0 || z < 0 || x >= frame.Map.TexelWidth || z >= frame.Map.TexelHeight) return false;
        int i = (z * frame.Map.TexelWidth + x) * 4;
        byte flags = frame.Map.Texels[i + 3];
        deep = (flags & OcclusionLayout.FlagDoor) == 0 && OcclusionLayout.DecodeDistance(frame.Map.Texels[i + 2]) >= OcclusionLayout.DeepCore;
        return Opacity(frame, x, z, frame.Map.Texels[i], flags) == 1f;
    }

    private static float Opacity(in OcclusionFrame frame, int x, int z, byte value, byte flags)
    {
        if ((flags & OcclusionLayout.FlagDoor) == 0) return value / 255f;
        int cellX = x / 4, cellZ = z / 4;
        int i = (cellZ * frame.Map.Width + cellX) * 4;
        byte[] doors = frame.Doors.Cells;
        bool axisZ = (flags & OcclusionLayout.FlagDoorAxisZ) != 0;
        float local = ((axisZ ? z : x) % 4 + 0.5f) * 0.25f;
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
