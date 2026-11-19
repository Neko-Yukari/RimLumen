using System;
using System.Numerics;

namespace RimLumen.Core.Lighting.Occlusion;

// Reference of lumenHeightVisibility and its sun and point-light entries in RimLumenOcclusion.glsl, line for line
// (design 1.8 and 3.4).
internal static class SunTransport
{
    // Width of the soft height comparison, in height units (design 1.8).
    internal const float HeightBand = 0.125f;

    // castXZ: vanilla _CastVect.xz, the ground offset of a unit-height shadow (SkyManager; |castXZ| > 0).
    // Ensures: in [0, 1]. 1 outside the map (sun-masked cells are vanilla's stencil bit 4, applied by the GPU).
    //          Otherwise 1 - max over t in [0, MaxSunHeight] of shadow(t), q = p - castXZ * t. TexelSplit of G at q with
    //          occupied = G > 0 gives edge e, coverage c and h = Inside / c decoded; shadow(t) = clamp((h - t) / HeightBand,
    //          0, 1) * e where e > 0, else 0, so the edge ramp lies inside the footprint; scaled by (1 - t / h) where
    //          the nearest texel at q has FlagSunFade. t = 0 counts, so the occluder's own top is shadowed (vanilla SunShadow prints the cell).
    //          Coarse steps skip cells whose coarse G is 0; fine steps are one texel along castXZ.
    internal static float Visibility(in OcclusionFrame frame, Vector2 p, Vector2 castXZ)
    {
        float length = castXZ.Length();
        return HeightVisibility(frame, p, -castXZ / length, 1f / length, length * OcclusionLayout.MaxSunHeight, 0f, float.PositiveInfinity, false);
    }

    // Point light at ground position light and height (>= 0) over ground point target.
    // Ensures: in [0, 1]; 1 when target == light. The ray rises height / D per cell over the D cells to the light and is
    //          marched until it passes MaxSunHeight or reaches the light; height <= h shadows unboundedly. The target's
    //          and the light's own contiguous height footprints (LightTransport.RunLength) are skipped without loss.
    //          blockLight things are governed only by LightTransport's solid and gap term: G counts only on
    //          FlagPointHeight texels (non-blockLight height occluders), and the coarse gate is coarse A.
    internal static float PointVisibility(in OcclusionFrame frame, Vector2 light, float height, Vector2 target)
    {
        Vector2 delta = light - target;
        float length = delta.Length();
        if (length == 0f) return 1f;
        Vector2 direction = delta / length;
        float reach = height > OcclusionLayout.MaxSunHeight ? length * OcclusionLayout.MaxSunHeight / height : length;
        return HeightVisibility(frame, target, direction, height / length, reach,
            LightTransport.RunLength(frame, target, direction, length, true),
            length - LightTransport.RunLength(frame, light, -direction, length, true), true);
    }

    // The one height march (design 1.8): 1 - max of shadow(t) at q = p + direction * a, t = a * rise, a in [0, reach),
    // skipping a < skipBefore and a >= skipFrom. The two sun/point differences: the sun skips nothing, so p's own
    // footprint counts at t = 0 (an occluder's top is shaded; the sun shade draws at ground height and is depth-rejected
    // off sprites), while a point light skips the target's and the light's own footprints, because pools are evaluated
    // per ground pixel and applied over sprites; and a point light reads G only on FlagPointHeight texels (PointVisibility).
    private static float HeightVisibility(in OcclusionFrame frame, Vector2 p, Vector2 direction, float rise, float reach,
        float skipBefore, float skipFrom, bool point)
    {
        OcclusionMap map = frame.Map;
        int cellX = (int)MathF.Floor(p.X), cellZ = (int)MathF.Floor(p.Y);
        if (!Inside(map, cellX, cellZ)) return 1f;
        int channel = point ? 3 : 1;
        float shadow = skipBefore == 0f && map.Coarse[(cellZ * map.Width + cellX) * OcclusionLayout.Channels + channel] != 0 ? Shadow(map, p, 0f, point) : 0f;
        float distance = 0f;
        if (direction.X < 0f && p.X == cellX) cellX--;
        if (direction.Y < 0f && p.Y == cellZ) cellZ--;
        while (distance < reach && shadow < 1f)
        {
            float nextX = direction.X > 0f ? (cellX + 1 - p.X) / direction.X : direction.X < 0f ? (cellX - p.X) / direction.X : float.PositiveInfinity;
            float nextZ = direction.Y > 0f ? (cellZ + 1 - p.Y) / direction.Y : direction.Y < 0f ? (cellZ - p.Y) / direction.Y : float.PositiveInfinity;
            float end = MathF.Min(reach, MathF.Min(nextX, nextZ));
            if (Inside(map, cellX, cellZ) && map.Coarse[(cellZ * map.Width + cellX) * OcclusionLayout.Channels + channel] != 0)
            {
                int steps = (int)MathF.Ceiling((end - distance) * OcclusionLayout.TexelsPerCell);
                float ds = (end - distance) / steps;
                for (int step = 0; step < steps; step++)
                {
                    float at = distance + (step + 0.5f) * ds;
                    if (at < skipBefore || at >= skipFrom) continue;
                    shadow = MathF.Max(shadow, Shadow(map, p + direction * at, at * rise, point));
                }
            }
            distance = end;
            if (nextX <= end) cellX += direction.X > 0f ? 1 : -1;
            if (nextZ <= end) cellZ += direction.Y > 0f ? 1 : -1;
        }
        return 1f - shadow;
    }

    private static bool Inside(OcclusionMap map, int x, int z) => x >= 0 && z >= 0 && x < map.Width && z < map.Height;

    // point: texels without FlagPointHeight read G = 0.
    private static float Shadow(OcclusionMap map, Vector2 q, float t, bool point)
    {
        Vector2 uv = q * OcclusionLayout.TexelsPerCell - new Vector2(0.5f);
        int x = (int)MathF.Floor(uv.X), z = (int)MathF.Floor(uv.Y);
        var g = new Vector4(Height(map, x, z, point), Height(map, x + 1, z, point), Height(map, x, z + 1, point), Height(map, x + 1, z + 1, point));
        var occupied = new Vector4(g.X > 0f ? 1f : 0f, g.Y > 0f ? 1f : 0f, g.Z > 0f ? 1f : 0f, g.W > 0f ? 1f : 0f);
        var distances = new Vector4(LightTransport.Distance(map, x, z), LightTransport.Distance(map, x + 1, z),
            LightTransport.Distance(map, x, z + 1), LightTransport.Distance(map, x + 1, z + 1));
        TexelSplit split = TexelSplit.Of(g, occupied, distances, uv.X - x, uv.Y - z);
        float edge = split.Edge;
        if (edge <= 0f) return 0f;
        float h = split.Inside / split.Coverage / 255f * OcclusionLayout.MaxSunHeight;
        float s = Math.Clamp((h - t) / HeightBand, 0f, 1f) * edge;
        if (s > 0f && (Flags(map, (int)MathF.Floor(q.X * OcclusionLayout.TexelsPerCell), (int)MathF.Floor(q.Y * OcclusionLayout.TexelsPerCell)) & OcclusionLayout.FlagSunFade) != 0)
            s *= 1f - t / h;
        return s;
    }

    private static float Height(OcclusionMap map, int x, int z, bool point) =>
        point && (Flags(map, x, z) & OcclusionLayout.FlagPointHeight) == 0 ? 0f
            : x < 0 || z < 0 || x >= map.TexelWidth || z >= map.TexelHeight ? 0f : map.Texels[(z * map.TexelWidth + x) * OcclusionLayout.Channels + 1];

    private static byte Flags(OcclusionMap map, int x, int z) =>
        x < 0 || z < 0 || x >= map.TexelWidth || z >= map.TexelHeight ? (byte)0 : map.Texels[(z * map.TexelWidth + x) * OcclusionLayout.Channels + 3];
}
