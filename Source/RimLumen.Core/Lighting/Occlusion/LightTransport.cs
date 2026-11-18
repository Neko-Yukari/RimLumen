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
    // Transmittance of the segment target -> light through the occlusion map (design 1.6-1.8 and 2).
    // Ensures: in [0, 1]; 1 when target == light. Smoothed per 1.8: solid as min(1 - s_i), cover as
    //          prod (1 - o_i)^(ds_i), both bilinear from the 2 x 2 texels around each step. Wall-body rule per 1.7:
    //          a target on a solid texel first skips its own contiguous solid run without loss, and is 0 if that run
    //          reaches a texel with wall-core depth > WallCoreDepth; door texels count as depth 0. The exit band right after
    //          that run (from the first step whose nearest texel is not solid until the first step whose 2 x 2 solid
    //          fraction s is 0) ignores s but still accumulates cover; a nearest texel turning solid again inside the band
    //          gives 0. The light's own solid run and its exit band never count s either. Positions outside the map are transparent. Door texels take DoorGap with the
    //          frame's DoorStates. Independent of step length for cover.
    internal static float Visibility(in OcclusionFrame frame, Vector2 light, Vector2 target)
    {
        Vector2 delta = light - target;
        float length = delta.Length();
        if (length == 0f) return 1f;
        Vector2 direction = delta / length;
        float lightRun = SolidRunLength(frame, light, -direction, length);
        bool skipTarget = Solid(frame, target, out int initialDepth);
        if (skipTarget && initialDepth > OcclusionLayout.WallCoreDepth) return 0f;
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
                int depth = 0;
                bool solid = (skipTarget || exitBand || lightRun > 0f) && Solid(frame, p, out depth);
                if (skipTarget)
                {
                    if (solid)
                    {
                        if (depth > OcclusionLayout.WallCoreDepth) return 0f;
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
        return MathF.Min(solidVisibility, lightRun > 0f ? 1f : pendingSolid) * coverVisibility;
    }

    private static float SolidRunLength(in OcclusionFrame frame, Vector2 start, Vector2 direction, float limit)
    {
        if (!Solid(frame, start, out _)) return 0f;
        float distance = 0f;
        Vector2 texel = start * 4f;
        int x = (int)MathF.Floor(texel.X), z = (int)MathF.Floor(texel.Y);
        if (direction.X < 0f && texel.X == x) x--;
        if (direction.Y < 0f && texel.Y == z) z--;
        while (distance < limit)
        {
            if (!SolidTexel(frame, x, z, out _)) break;
            float tx = direction.X > 0f ? ((x + 1) * 0.25f - start.X) / direction.X : direction.X < 0f ? (x * 0.25f - start.X) / direction.X : float.PositiveInfinity;
            float tz = direction.Y > 0f ? ((z + 1) * 0.25f - start.Y) / direction.Y : direction.Y < 0f ? (z * 0.25f - start.Y) / direction.Y : float.PositiveInfinity;
            distance = MathF.Min(limit, MathF.Min(tx, tz));
            if (tx <= distance) x += direction.X > 0f ? 1 : -1;
            if (tz <= distance) z += direction.Y > 0f ? 1 : -1;
        }
        return distance;
    }

    private static bool Solid(in OcclusionFrame frame, Vector2 position, out int depth) =>
        SolidTexel(frame, (int)MathF.Floor(position.X * 4f), (int)MathF.Floor(position.Y * 4f), out depth);

    private static bool SolidTexel(in OcclusionFrame frame, int x, int z, out int depth)
    {
        depth = 0;
        if (x < 0 || z < 0 || x >= frame.Map.TexelWidth || z >= frame.Map.TexelHeight) return false;
        int i = (z * frame.Map.TexelWidth + x) * 4;
        byte flags = frame.Map.Texels[i + 3];
        depth = frame.Map.Texels[i + 2];
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
        float fx = uv.X - x, fz = uv.Y - z;
        float s00 = 0f, s10 = 0f, s01 = 0f, s11 = 0f;
        float o00 = 0f, o10 = 0f, o01 = 0f, o11 = 0f;
        Read(frame, x, z, doors, out s00, out o00);
        Read(frame, x + 1, z, doors, out s10, out o10);
        Read(frame, x, z + 1, doors, out s01, out o01);
        Read(frame, x + 1, z + 1, doors, out s11, out o11);
        solid = (s00 * (1 - fx) + s10 * fx) * (1 - fz) + (s01 * (1 - fx) + s11 * fx) * fz;
        cover = (o00 * (1 - fx) + o10 * fx) * (1 - fz) + (o01 * (1 - fx) + o11 * fx) * fz;
    }

    private static void Read(in OcclusionFrame frame, int x, int z, bool doors, out float solid, out float cover)
    {
        solid = cover = 0f;
        if (x < 0 || z < 0 || x >= frame.Map.TexelWidth || z >= frame.Map.TexelHeight) return;
        int i = (z * frame.Map.TexelWidth + x) * 4;
        byte value = frame.Map.Texels[i];
        float opacity = doors ? Opacity(frame, x, z, value, frame.Map.Texels[i + 3]) : value / 255f;
        if (opacity == 1f) solid = 1f;
        else cover = opacity;
    }
}
