using System;
using System.Numerics;

namespace RimLumen.Core.Lighting.Occlusion;

// Reference of lumenSunVisibility in RimLumenOcclusion.glsl, line for line (design 1.8 and 3.4).
internal static class SunTransport
{
    // Width of the soft height comparison, in height units (design 1.8).
    internal const float HeightBand = 0.125f;

    // castXZ: vanilla _CastVect.xz, the ground offset of a unit-height shadow (SkyManager; |castXZ| > 0).
    // Ensures: in [0, 1]. 1 when the coarse sun mask (coarse A) at p is set, and outside the map.
    //          Otherwise 1 - max over t in [0, MaxSunHeight] of shadow(t), q = p - castXZ * t, h = bilinear G(q) decoded,
    //          shadow(t) = clamp((h - t) / HeightBand, 0, 1), scaled by (1 - t / h) where the nearest texel at q has
    //          FlagSunFade. t = 0 counts, so the occluder's own top is shadowed (vanilla SunShadow prints the cell).
    //          Coarse steps skip cells whose coarse G is 0; fine steps are one texel along castXZ.
    internal static float Visibility(in OcclusionFrame frame, Vector2 p, Vector2 castXZ)
    {
        OcclusionMap map = frame.Map;
        int cellX = (int)MathF.Floor(p.X), cellZ = (int)MathF.Floor(p.Y);
        if (!Inside(map, cellX, cellZ) || map.Coarse[(cellZ * map.Width + cellX) * OcclusionLayout.Channels + 3] != 0) return 1f;
        float length = castXZ.Length();
        Vector2 direction = -castXZ / length;
        float reach = length * OcclusionLayout.MaxSunHeight;
        float shadow = map.Coarse[(cellZ * map.Width + cellX) * OcclusionLayout.Channels + 1] != 0 ? Shadow(map, p, 0f) : 0f;
        float distance = 0f;
        if (direction.X < 0f && p.X == cellX) cellX--;
        if (direction.Y < 0f && p.Y == cellZ) cellZ--;
        while (distance < reach && shadow < 1f)
        {
            float nextX = direction.X > 0f ? (cellX + 1 - p.X) / direction.X : direction.X < 0f ? (cellX - p.X) / direction.X : float.PositiveInfinity;
            float nextZ = direction.Y > 0f ? (cellZ + 1 - p.Y) / direction.Y : direction.Y < 0f ? (cellZ - p.Y) / direction.Y : float.PositiveInfinity;
            float end = MathF.Min(reach, MathF.Min(nextX, nextZ));
            if (Inside(map, cellX, cellZ) && map.Coarse[(cellZ * map.Width + cellX) * OcclusionLayout.Channels + 1] != 0)
            {
                int steps = (int)MathF.Ceiling((end - distance) * OcclusionLayout.TexelsPerCell);
                float ds = (end - distance) / steps;
                for (int step = 0; step < steps; step++)
                {
                    float at = distance + (step + 0.5f) * ds;
                    shadow = MathF.Max(shadow, Shadow(map, p + direction * at, at / length));
                }
            }
            distance = end;
            if (nextX <= end) cellX += direction.X > 0f ? 1 : -1;
            if (nextZ <= end) cellZ += direction.Y > 0f ? 1 : -1;
        }
        return 1f - shadow;
    }

    private static bool Inside(OcclusionMap map, int x, int z) => x >= 0 && z >= 0 && x < map.Width && z < map.Height;

    private static float Shadow(OcclusionMap map, Vector2 q, float t)
    {
        Vector2 uv = q * OcclusionLayout.TexelsPerCell - new Vector2(0.5f);
        int x = (int)MathF.Floor(uv.X), z = (int)MathF.Floor(uv.Y);
        float fx = uv.X - x, fz = uv.Y - z;
        float g = (Height(map, x, z) * (1 - fx) + Height(map, x + 1, z) * fx) * (1 - fz) +
            (Height(map, x, z + 1) * (1 - fx) + Height(map, x + 1, z + 1) * fx) * fz;
        float h = g / 255f * OcclusionLayout.MaxSunHeight;
        float s = Math.Clamp((h - t) / HeightBand, 0f, 1f);
        if (s > 0f && (Flags(map, (int)MathF.Floor(q.X * OcclusionLayout.TexelsPerCell), (int)MathF.Floor(q.Y * OcclusionLayout.TexelsPerCell)) & OcclusionLayout.FlagSunFade) != 0)
            s *= 1f - t / h;
        return s;
    }

    private static float Height(OcclusionMap map, int x, int z) =>
        x < 0 || z < 0 || x >= map.TexelWidth || z >= map.TexelHeight ? 0f : map.Texels[(z * map.TexelWidth + x) * OcclusionLayout.Channels + 1];

    private static byte Flags(OcclusionMap map, int x, int z) =>
        x < 0 || z < 0 || x >= map.TexelWidth || z >= map.TexelHeight ? (byte)0 : map.Texels[(z * map.TexelWidth + x) * OcclusionLayout.Channels + 3];
}
