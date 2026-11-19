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
    //          0, 1) * e where e > 0, else 0, so the edge ramp lies inside the footprint; scaled by (1 - f * t / h) with f
    //          the FlagSunFade share of the occupied texels, bilerp(occupied * fade) / c, so h and the fade come from the
    //          same texels. t = 0 counts, so the occluder's own top is shadowed (vanilla SunShadow prints the cell).
    //          Coarse steps skip cells whose coarse G is 0; fine steps are one per texel crossed, at its middle, plus one
    //          where the ray enters an outline (Step).
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
        float shadow = 0f, distance = 0f, last = -1f, lastDistance = 0f;
        if (skipBefore == 0f && map.Coarse[(cellZ * map.Width + cellX) * OcclusionLayout.Channels + channel] != 0)
        {
            shadow = Shadow(map, p, 0f, point, out lastDistance);
            last = 0f;
        }
        (cellX, cellZ) = GridWalk.Enter(p, direction, 1f);
        while (distance < reach && shadow < 1f)
        {
            int x = cellX, z = cellZ;
            float end = GridWalk.Leave(p, direction, 1f, reach, ref cellX, ref cellZ);
            if (Inside(map, x, z) && map.Coarse[(z * map.Width + x) * OcclusionLayout.Channels + channel] != 0)
            {
                // One fine step per texel crossed, at the middle of the ray's run through it (plus Step's outline entry).
                // The texel walk starts in this cell's texels, whichever side of the cell edge the entry point rounds to.
                Vector2 entry = (p + direction * distance) * OcclusionLayout.TexelsPerCell;
                int tx = Math.Clamp((int)MathF.Floor(entry.X), x * OcclusionLayout.TexelsPerCell, x * OcclusionLayout.TexelsPerCell + OcclusionLayout.TexelsPerCell - 1);
                int tz = Math.Clamp((int)MathF.Floor(entry.Y), z * OcclusionLayout.TexelsPerCell, z * OcclusionLayout.TexelsPerCell + OcclusionLayout.TexelsPerCell - 1);
                for (float a = distance; a < end;)
                {
                    float b = GridWalk.Leave(p, direction, 1f / OcclusionLayout.TexelsPerCell, end, ref tx, ref tz);
                    float at = (a + b) * 0.5f;
                    if (at >= skipBefore && at < skipFrom) shadow = MathF.Max(shadow, Step(map, p, direction, rise, point, at, ref last, ref lastDistance));
                    else last = -1f;
                    a = b;
                }
            }
            else last = -1f;
            distance = end;
        }
        return 1f - shadow;
    }

    // One fine step at a: the shadow there and, where the split distance crossed the edge ramp's top since the previous
    // step at last (last >= 0: the step right before this one, the t = 0 step at p included), also the shadow where it
    // reaches the ramp top, placed linearly between the two. The shadow peaks where the ray enters an outline,
    // which one step per texel would otherwise quantise to the texel.
    private static float Step(OcclusionMap map, Vector2 p, Vector2 direction, float rise, bool point, float a, ref float last, ref float lastDistance)
    {
        float s = Shadow(map, p + direction * a, a * rise, point, out float d);
        if (last >= 0f && lastDistance < OcclusionLayout.EdgeRamp && d > OcclusionLayout.EdgeRamp)
        {
            float c = last + (a - last) * (OcclusionLayout.EdgeRamp - lastDistance) / (d - lastDistance);
            s = MathF.Max(s, Shadow(map, p + direction * c, c * rise, point, out _));
        }
        last = a;
        lastDistance = d;
        return s;
    }

    private static bool Inside(OcclusionMap map, int x, int z) => x >= 0 && z >= 0 && x < map.Width && z < map.Height;

    // point: texels without FlagPointHeight read G = 0. distance: the split's Distance (texels, positive inside).
    internal static float Shadow(OcclusionMap map, Vector2 q, float t, bool point, out float distance)
    {
        Vector2 uv = q * OcclusionLayout.TexelsPerCell - new Vector2(0.5f);
        int x = (int)MathF.Floor(uv.X), z = (int)MathF.Floor(uv.Y);
        var g = new Vector4(Height(map, x, z, point), Height(map, x + 1, z, point), Height(map, x, z + 1, point), Height(map, x + 1, z + 1, point));
        var occupied = new Vector4(g.X > 0f ? 1f : 0f, g.Y > 0f ? 1f : 0f, g.Z > 0f ? 1f : 0f, g.W > 0f ? 1f : 0f);
        var distances = new Vector4(LightTransport.Distance(map, x, z), LightTransport.Distance(map, x + 1, z),
            LightTransport.Distance(map, x, z + 1), LightTransport.Distance(map, x + 1, z + 1));
        TexelSplit split = TexelSplit.Of(g, occupied, distances, uv.X - x, uv.Y - z);
        distance = split.Distance;
        float edge = split.Edge;
        if (edge <= 0f) return 0f;
        float h = split.Inside / split.Coverage / 255f * OcclusionLayout.MaxSunHeight;
        var fades = new Vector4(Fade(map, x, z), Fade(map, x + 1, z), Fade(map, x, z + 1), Fade(map, x + 1, z + 1)) * occupied;
        float fade = TexelSplit.Bilerp(fades, uv.X - x, uv.Y - z) / split.Coverage;
        return Math.Clamp((h - t) / HeightBand, 0f, 1f) * edge * (1f - fade * t / h);
    }

    private static float Height(OcclusionMap map, int x, int z, bool point) =>
        point && (Flags(map, x, z) & OcclusionLayout.FlagPointHeight) == 0 ? 0f
            : x < 0 || z < 0 || x >= map.TexelWidth || z >= map.TexelHeight ? 0f : map.Texels[(z * map.TexelWidth + x) * OcclusionLayout.Channels + 1];

    private static float Fade(OcclusionMap map, int x, int z) => (Flags(map, x, z) & OcclusionLayout.FlagSunFade) != 0 ? 1f : 0f;

    private static byte Flags(OcclusionMap map, int x, int z) =>
        x < 0 || z < 0 || x >= map.TexelWidth || z >= map.TexelHeight ? (byte)0 : map.Texels[(z * map.TexelWidth + x) * OcclusionLayout.Channels + 3];
}
