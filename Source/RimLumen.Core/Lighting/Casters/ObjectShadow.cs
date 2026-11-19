using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Lighting.Occlusion;

namespace RimLumen.Core.Lighting.Casters;

// One non-blockLight thing with a height as a shadow caster (design object-shadows): its sprite silhouette as closed
// loops in world xz (each loop's last vertex joins its first), its height h > 0, whether it fades along height (a
// shadowData height), and its occupied cells.
internal sealed record Caster(Vector2[][] Loops, float Height, bool Fade, CellBox Rect);

// C# reference of RimLumenObjectShadow.{vert,frag}.glsl. Every silhouette edge (a, b) of a caster is extruded into one
// quad; a ground point p under it reads the height t at which the ray from p toward the light crosses the edge, and the
// edge writes strength(t) = 1 - t / h when the caster fades, else 1. When h >= H the shadow reaches the pool's rim and
// fades linearly along the ground from the edge to the rim, as vanilla's SunShadowFade fades along its extruded quad
// (t stays below H there, so 1 - t / h would leave a solid block for a light on the ground). The mask of one light keeps the maximum
// over all its edges (blend Max), so overlapping shadows of one light darken once. Sprite pixels reject every quad by
// depth (quads draw at the Shadows altitude), so a point on any silhouette reads 0.
internal static class ObjectShadow
{
    // light: (x, z) ground position, height H >= 0, pool radius R > 0.
    // Ensures: the strength edge (a, b) of a caster of height h writes at p: 0 when p lies outside the light's pool box,
    //          or when the segment from the light to p does not cross the edge at a height t in [0, min(h, H)].
    //          The crossing L + lambda (p - L) = a + s (b - a) has t = H (1 - lambda); the rim lies at L + rim (p - L).
    internal static float PointEdge(Vector4 light, Vector2 a, Vector2 b, float h, bool fade, Vector2 p)
    {
        var l = new Vector2(light.X, light.Y);
        if (MathF.Abs(p.X - l.X) > light.W || MathF.Abs(p.Y - l.Y) > light.W) return 0f;
        Vector2 d = p - l, e = b - a, w = a - l;
        float den = Cross(d, e);
        if (den == 0f) return 0f;
        float lambda = Cross(w, e) / den, s = Cross(w, d) / den;
        if (s < 0f || s > 1f || lambda <= 0f || lambda > 1f) return 0f;
        if (h >= light.Z)
        {
            float rim = light.W / d.Length();
            return rim <= 1f ? 0f : (rim - 1f) / (rim - lambda);
        }
        float t = light.Z * (1f - lambda);
        if (t > h) return 0f;
        return fade ? 1f - t / h : 1f;
    }

    // cast: vanilla _CastVect.xz, the ground offset of a unit height. Ensures: the strength edge (a, b) writes at p, 0
    //       when p - cast * t meets the edge for no t in [0, h]. The meeting p - cast t = a + s (b - a).
    internal static float SunEdge(Vector2 cast, Vector2 a, Vector2 b, float h, bool fade, Vector2 p)
    {
        Vector2 e = b - a, w = p - a;
        float den = Cross(cast, e);
        if (den == 0f) return 0f;
        float t = Cross(w, e) / den, s = -Cross(w, cast) / den;
        if (s < 0f || s > 1f || t < 0f || t > h) return 0f;
        return fade ? 1f - t / h : 1f;
    }

    // Ensures: the light's mask at p: 0 on any caster's silhouette, else the maximum PointEdge over the edges of every
    //          caster whose occupied cells do not hold the light's cell.
    internal static float PointMask(Vector4 light, IReadOnlyList<Caster> casters, Vector2 p)
    {
        if (OnAnySilhouette(casters, p)) return 0f;
        int cellX = (int)MathF.Floor(light.X), cellZ = (int)MathF.Floor(light.Y);
        float mask = 0f;
        foreach (Caster caster in casters)
        {
            CellBox r = caster.Rect;
            if (cellX >= r.MinX && cellX <= r.MaxX && cellZ >= r.MinZ && cellZ <= r.MaxZ) continue;
            foreach (Vector2[] loop in caster.Loops)
                for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
                    mask = MathF.Max(mask, PointEdge(light, loop[j], loop[i], caster.Height, caster.Fade, p));
        }
        return mask;
    }

    // Ensures: the sun's mask at p: 0 on any caster's silhouette, else the maximum SunEdge over every caster edge.
    internal static float SunMask(Vector2 cast, IReadOnlyList<Caster> casters, Vector2 p)
    {
        if (OnAnySilhouette(casters, p)) return 0f;
        float mask = 0f;
        foreach (Caster caster in casters)
            foreach (Vector2[] loop in caster.Loops)
                for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
                    mask = MathF.Max(mask, SunEdge(cast, loop[j], loop[i], caster.Height, caster.Fade, p));
        return mask;
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    // Even-odd rule over every loop of each caster.
    private static bool OnAnySilhouette(IReadOnlyList<Caster> casters, Vector2 p)
    {
        foreach (Caster caster in casters)
        {
            bool inside = false;
            foreach (Vector2[] loop in caster.Loops)
                for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
                    if ((loop[i].Y > p.Y) != (loop[j].Y > p.Y)
                        && p.X < (loop[j].X - loop[i].X) * (p.Y - loop[i].Y) / (loop[j].Y - loop[i].Y) + loop[i].X)
                        inside = !inside;
            if (inside) return true;
        }
        return false;
    }
}
