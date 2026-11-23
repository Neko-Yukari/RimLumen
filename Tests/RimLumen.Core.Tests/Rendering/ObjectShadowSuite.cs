using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RimLumen.Core.Lighting;
using RimLumen.Core.Lighting.Casters;
using RimLumen.Core.Lighting.Occlusion;

namespace RimLumen.Core.Tests.Rendering;

// Object shadows (design object-shadows): the reference of the extruded silhouette quads against a ground truth that
// walks the 3D segment from each ground point to the light and asks where it passes through the caster's prism.
[LumenTestSuite]
public static class ObjectShadowSuite
{
    private static readonly Vector2 Lamp = new(10.5f, 10.5f);

    [LumenFact]
    public static void ColumnRingShadowsAWholeSilhouetteWideBandBehindEachColumn()
    {
        // Eight vanilla columns (drawSize 0.5 x 1.25, drawOffset z 0.2, shadowData height 0.5, fading) around a vanilla
        // glower (H = 1). Behind each column the mask equals the prism's shadow over the column's whole silhouette width.
        var light = new Vector4(Lamp, VisualPointLight.VanillaHeight, 7f);
        Caster[] ring = ColumnRing();
        foreach (Caster column in ring)
        {
            int shadowed = 0;
            foreach (Vector2 p in Around(column, 2.5f))
            {
                if (!Stable(q => Truth(light, ring, q), ring, p, out float truth)) continue;
                float mask = ObjectShadow.PointMask(light, ring, p);
                Require(MathF.Abs(mask - truth) < 0.02f, $"Column at {column.Rect}: point {p} reads {mask}, the prism casts {truth}.");
                if (truth > 0f) shadowed++;
            }
            Require(shadowed >= 40, $"Column at {column.Rect} cast a band of only {shadowed} sample points.");
        }
    }

    [LumenFact]
    public static void ColumnSpriteGetsNoShadowFromItself()
    {
        var light = new Vector4(Lamp, VisualPointLight.VanillaHeight, 7f);
        Caster[] ring = ColumnRing();
        foreach (Caster column in ring)
        {
            Vector2 centre = Centre(column);
            foreach (Vector2 p in Around(column, 0.7f).Where(p => Inside(column, p)))
                Equal(0f, ObjectShadow.PointMask(light, ring, p));
            Vector2 away = Vector2.Normalize(centre - Lamp);
            Vector2 behind = centre + away * (Reach(column, centre, away) + 0.1f);
            Require(ObjectShadow.PointMask(light, ring, behind) > 0.5f, $"Ground just behind the column at {column.Rect} is lit.");
        }
    }

    [LumenFact]
    public static void MonumentCastsNoShadowBeyondItsProjectedSilhouette()
    {
        // A 3 x 3 cross-shaped monument (hard height 0.8) beside the lamp: its concave corners must not leak wedges.
        var light = new Vector4(Lamp, VisualPointLight.VanillaHeight, 9f);
        var cross = new Pattern(3, 3, [0, 255, 0, 255, 255, 255, 0, 255, 0]);
        var at = new MaskPlacement(14.5f, 12.5f, 3f, 3f);
        Caster[] monument = [Make(cross, at, 0.8f, fade: false, new CellBox(13, 11, 15, 13))];
        int shadowed = 0;
        for (float z = 7f; z < 19f; z += 0.07f)
            for (float x = 12f; x < 19.5f; x += 0.07f)
            {
                var p = new Vector2(x, z);
                if (!Stable(q => Truth(light, monument, q), monument, p, out float truth)) continue;
                float mask = ObjectShadow.PointMask(light, monument, p);
                Require(truth > 0f || mask == 0f, $"Point {p} outside the monument's shadow reads {mask}.");
                Require(MathF.Abs(mask - truth) < 0.02f, $"Point {p} reads {mask}, the prism casts {truth}.");
                if (truth > 0f) shadowed++;
            }
        Require(shadowed > 200, $"The monument cast only {shadowed} sample points.");
    }

    [LumenFact]
    public static void ThingAsTallAsTheLightShadowsToThePoolEdgeFading()
    {
        // A hard block of height h = 1 under lights at H = 1, 0.4 and 0 (an effect light on the ground): the shadow
        // reaches the pool's rim and fades linearly along the ground from the outline's far edge to the rim, as vanilla's
        // SunShadowFade fades along its extruded quad, so even H = 0 casts no solid block. The far edge lies within the
        // simplification tolerance (1/32 cell) of the sprite's edge x = 13; the rim is x = 16.5 along the ray.
        Caster[] block = [Make(OcclusionStamps.Opaque, new MaskPlacement(12.5f, 10.5f, 1f, 1f), 1f, fade: false, new CellBox(12, 10, 12, 10))];
        float far = block[0].Loops.SelectMany(l => l.Select((a, i) => (a, b: l[(i + 1) % l.Length])))
            .Where(e => (e.a.Y > 10.5f) != (e.b.Y > 10.5f))
            .Max(e => e.a.X + (e.b.X - e.a.X) * (10.5f - e.a.Y) / (e.b.Y - e.a.Y));
        Require(MathF.Abs(far - 13f) <= 1f / 32f, $"The outline's far edge sits at x = {far}, off the sprite edge 13.");
        foreach (float height in new[] { 1f, 0.4f, 0f })
        {
            var light = new Vector4(Lamp, height, 6f);
            float previous = 1f;
            for (float x = 13.05f; x < 16.5f; x += 0.1f)
            {
                float mask = ObjectShadow.PointMask(light, block, new Vector2(x, 10.5f));
                float expected = (16.5f - x) / (16.5f - far);
                Require(MathF.Abs(mask - expected) < 1e-3f && mask < previous,
                    $"H = {height}, x = {x}: mask {mask}, expected {expected} below {previous}.");
                previous = mask;
            }
            Equal(0f, ObjectShadow.PointMask(light, block, new Vector2(16.6f, 10.5f)));
        }
        // The rim is the pool's circle, not its box: on the diagonal behind a block the shadow ends at distance R.
        Caster[] diagonal = [Make(OcclusionStamps.Opaque, new MaskPlacement(12.5f, 12.5f, 1f, 1f), 1f, fade: false, new CellBox(12, 12, 12, 12))];
        var ground = new Vector4(Lamp, 0f, 6f);
        Require(ObjectShadow.PointMask(ground, diagonal, new Vector2(14.6f, 14.6f)) > 0f, "The diagonal shadow ends before the rim.");
        Equal(0f, ObjectShadow.PointMask(ground, diagonal, new Vector2(14.8f, 14.8f)));
    }

    [LumenFact]
    public static void TwoOverlappingShadowsOfOneLightDarkenOnce()
    {
        var light = new Vector4(Lamp, VisualPointLight.VanillaHeight, 9f);
        Caster near = Make(OcclusionStamps.Opaque, new MaskPlacement(12.5f, 10.5f, 1f, 1f), 0.6f, fade: true, new CellBox(12, 10, 12, 10));
        Caster side = Make(OcclusionStamps.Opaque, new MaskPlacement(13.5f, 11.3f, 1f, 1f), 0.6f, fade: true, new CellBox(13, 10, 13, 11));
        Caster[] both = [near, side];
        int overlapping = 0;
        for (float z = 9f; z < 14f; z += 0.05f)
            for (float x = 13f; x < 18f; x += 0.05f)
            {
                var p = new Vector2(x, z);
                float a = ObjectShadow.PointMask(light, [near], p), b = ObjectShadow.PointMask(light, [side], p);
                if (Inside(near, p) || Inside(side, p)) continue;
                float mask = ObjectShadow.PointMask(light, both, p);
                Require(mask == MathF.Max(a, b), $"Point {p}: both read {mask}, alone {a} and {b}.");
                if (a > 0.1f && b > 0.1f && a < 0.9f && b < 0.9f) overlapping++;
            }
        Require(overlapping > 20, $"Only {overlapping} points lie in both shadows.");
    }

    [LumenFact]
    public static void ThingHoldingTheLightsCellCastsNothingForThatLight()
    {
        // StandingLamp (drawSize 1, shadowData height 0.6) holds its own light (H = 1): its pool reads no shadow from it
        // (Q4), while a lamp two cells west still gets the lamp's shadow.
        Caster[] lamp = [Make(OcclusionStamps.Opaque, new MaskPlacement(8.5f, 8.5f, 1f, 1f), 0.6f, fade: true, new CellBox(8, 8, 8, 8))];
        var own = new Vector4(8.5f, 8.5f, 1f, 7f);
        for (float z = 2f; z < 15f; z += 0.1f)
            for (float x = 2f; x < 15f; x += 0.1f)
                Equal(0f, ObjectShadow.PointMask(own, lamp, new Vector2(x, z)));
        Require(ObjectShadow.PointMask(new Vector4(6.5f, 8.5f, 1f, 7f), lamp, new Vector2(9.2f, 8.5f)) > 0.5f,
            "A lamp in another cell gets no shadow from the standing lamp.");
    }

    [LumenFact]
    public static void SunCastsEachWholeSilhouetteAndFadesShadowDataHeights()
    {
        // A dusk _CastVect over the vanilla column ring (fading) and a 3 x 3 cross-shaped monument (hard, 0.8): the sun
        // mask equals the prisms' shadow, from the whole sprite, fading as 1 - t / h only for shadowData heights.
        var cast = new Vector2(1.4f, -0.5f);
        var cross = new Pattern(3, 3, [0, 255, 0, 255, 255, 255, 0, 255, 0]);
        Caster[] casters = [.. ColumnRing(), Make(cross, new MaskPlacement(16.5f, 16.5f, 3f, 3f), 0.8f, fade: false, new CellBox(15, 15, 17, 17))];
        int faded = 0, hard = 0;
        for (float z = 6f; z < 19f; z += 0.05f)
            for (float x = 6f; x < 20f; x += 0.05f)
            {
                var p = new Vector2(x, z);
                if (!Stable(q => SunTruth(cast, casters, q), casters, p, out float truth)) continue;
                float mask = ObjectShadow.SunMask(cast, casters, p);
                Require(MathF.Abs(mask - truth) < 0.02f, $"Point {p} reads {mask}, the prisms cast {truth}.");
                if (truth is > 0f and < 1f) faded++;
                if (truth == 1f) hard++;
            }
        Require(faded > 200 && hard > 200, $"Only {faded} faded and {hard} hard shadow points.");
    }

    [LumenFact]
    public static void LightsWhosePoolBoxesOverlapNeverShareAMaskChannel()
    {
        var random = new Random(7);
        var lights = Enumerable.Range(0, 60).Select(_ => new Vector3(random.Next(0, 80) + 0.5f, random.Next(0, 80) + 0.5f, random.Next(2, 13))).ToList();
        foreach (bool sun in new[] { false, true })
        {
            ShadowSlot[] slots = ShadowChannels.Assign(lights, sun);
            Equal(lights.Count, slots.Length);
            for (int i = 0; i < lights.Count; i++)
            {
                Require(slots[i].Channel is >= 0 and < 4 && slots[i].Round >= 0, $"Light {i} got {slots[i]}.");
                Require(!sun || slots[i] != new ShadowSlot(0, ShadowChannels.SunChannel), $"Light {i} took the sun's channel.");
                for (int j = 0; j < i; j++)
                    Require(slots[i] != slots[j] || !Overlap(lights[i], lights[j]), $"Lights {j} and {i} overlap and share {slots[i]}.");
            }
        }
    }

    [LumenFact]
    public static void CastShadowsFalseLightTakesNoShadowChannelAndNeverBumpsShadowedLights()
    {
        var color = new Vector3(1f, 0.8f, 0.6f);
        VisualPointLight shadowed = new(10.5f, 1f, 10.5f, 6f, color, VisualPointLightKind.Effect, CastShadows: true);
        VisualPointLight unshadowed = shadowed with { CastShadows = false };

        ShadowSlot[] mixed = ShadowChannels.Assign([unshadowed, shadowed, shadowed, shadowed, shadowed, unshadowed], objectShadows: true, sun: false);
        Equal(6, mixed.Length);
        Require(!mixed[0].HasChannel && mixed[0].Selector == Vector4.Zero, "First CastShadows=false light must get no channel.");
        Require(!mixed[5].HasChannel && mixed[5].Selector == Vector4.Zero, "Last CastShadows=false light must get no channel.");
        for (int i = 1; i <= 4; i++)
        {
            Require(mixed[i].HasChannel && mixed[i].Round == 0, $"Shadowed light {i} should stay in round 0, got {mixed[i]}.");
        }
        Equal(4, mixed.Skip(1).Take(4).Select(s => s.Channel).Distinct().Count());

        ShadowSlot[] withSun = ShadowChannels.Assign([unshadowed, shadowed, shadowed, shadowed, unshadowed], objectShadows: true, sun: true);
        Require(!withSun[0].HasChannel && !withSun[4].HasChannel, "Unshadowed lights take no channel even with sun.");
        for (int i = 1; i <= 3; i++)
        {
            Require(withSun[i].HasChannel && withSun[i].Round == 0 && withSun[i].Channel != ShadowChannels.SunChannel,
                $"Shadowed light {i} must stay in round 0 off SunChannel, got {withSun[i]}.");
        }

        ShadowSlot[] disabled = ShadowChannels.Assign([shadowed, unshadowed], objectShadows: false, sun: false);
        Require(!disabled[0].HasChannel && !disabled[1].HasChannel, "Global objectShadows=false disables all channels.");

        Require(!LightPoolRenderer.NeedsOcclusion([unshadowed], takeover: false), "Unshadowed-only lights need no occlusion without takeover.");
        Require(LightPoolRenderer.NeedsOcclusion([unshadowed], takeover: true), "Takeover still needs occlusion.");
        Require(LightPoolRenderer.NeedsOcclusion([unshadowed, shadowed], takeover: false), "Any shadowed light needs occlusion.");

        Equal(new Vector4(color, 1f), LightPoolRenderer.EncodeLightUniform(shadowed));
        Equal(new Vector4(color, 0f), LightPoolRenderer.EncodeLightUniform(unshadowed));
    }

    private static Caster[] ColumnRing()
    {
        (int X, int Z)[] offsets = [(3, 0), (-3, 0), (0, 3), (0, -3), (2, 2), (-2, 2), (2, -2), (-2, -2)];
        return offsets.Select(o =>
        {
            int x = 10 + o.X, z = 10 + o.Z;
            return Make(OcclusionStamps.Opaque, new MaskPlacement(x + 0.5f, z + 0.5f + 0.2f, 0.5f, 1.25f), 0.5f, fade: true, new CellBox(x, z, x, z));
        }).ToArray();
    }

    private static Caster Make(Pattern art, MaskPlacement at, float height, bool fade, CellBox rect) =>
        OcclusionStamps.Caster(art, at, height, fade, rect);

    private static bool Overlap(Vector3 a, Vector3 b) => MathF.Abs(a.X - b.X) <= a.Z + b.Z && MathF.Abs(a.Y - b.Y) <= a.Z + b.Z;

    // Sample points every 0.05 cells within margin of the caster's silhouette bounds.
    private static IEnumerable<Vector2> Around(Caster caster, float margin)
    {
        Vector2 min = new(caster.Loops.SelectMany(l => l).Min(v => v.X), caster.Loops.SelectMany(l => l).Min(v => v.Y));
        Vector2 max = new(caster.Loops.SelectMany(l => l).Max(v => v.X), caster.Loops.SelectMany(l => l).Max(v => v.Y));
        for (float z = min.Y - margin; z <= max.Y + margin; z += 0.05f)
            for (float x = min.X - margin; x <= max.X + margin; x += 0.05f) yield return new Vector2(x, z);
    }

    private static Vector2 Centre(Caster caster) => caster.Loops.SelectMany(l => l).Aggregate(Vector2.Zero, (s, v) => s + v) / caster.Loops.Sum(l => l.Length);

    // Distance from centre along direction to where it leaves the silhouette.
    private static float Reach(Caster caster, Vector2 centre, Vector2 direction)
    {
        float d = 0f;
        while (Inside(caster, centre + direction * d)) d += 0.005f;
        return d;
    }

    // Ground truth: the light at (x, z, H) and p on the ground; t = H * (1 - lambda) is the height of the segment above the
    // ground point L + lambda * (p - L). The prism of a caster holds that point when it lies on the silhouette at
    // t <= h. The strength is that of the nearest such point to p; p on any silhouette, outside the pool box, or a
    // caster whose rect holds the light's cell cast nothing.
    private static float Truth(Vector4 light, IReadOnlyList<Caster> casters, Vector2 p)
    {
        var l = new Vector2(light.X, light.Y);
        float height = light.Z, radius = light.W;
        if (MathF.Abs(p.X - l.X) > radius || MathF.Abs(p.Y - l.Y) > radius) return 0f;
        if (casters.Any(c => Inside(c, p))) return 0f;
        float best = 0f;
        foreach (Caster caster in casters)
        {
            if (MathF.Floor(l.X) >= caster.Rect.MinX && MathF.Floor(l.X) <= caster.Rect.MaxX
                && MathF.Floor(l.Y) >= caster.Rect.MinZ && MathF.Floor(l.Y) <= caster.Rect.MaxZ) continue;
            for (int i = 4000; i >= 0; i--)
            {
                float lambda = i / 4000f, t = height * (1f - lambda);
                if (t > caster.Height) break;
                if (!Inside(caster, l + (p - l) * lambda)) continue;
                best = MathF.Max(best, caster.Fade || caster.Height >= height ? 1f - t / caster.Height : 1f);
                break;
            }
        }
        return best;
    }

    // Sun ground truth: p - cast * t walks up the sun ray from p; the strength is that of the lowest t <= h at which it
    // lies on a caster's silhouette. p on any silhouette casts nothing.
    private static float SunTruth(Vector2 cast, IReadOnlyList<Caster> casters, Vector2 p)
    {
        if (casters.Any(c => Inside(c, p))) return 0f;
        float best = 0f;
        foreach (Caster caster in casters)
            for (int i = 0; i <= 4000; i++)
            {
                float t = caster.Height * i / 4000f;
                if (!Inside(caster, p - cast * t)) continue;
                best = MathF.Max(best, caster.Fade ? 1f - t / caster.Height : 1f);
                break;
            }
        return best;
    }

    // truth(p), and whether shadowed-or-not agrees at p and 0.03 cells around it (points off every outline).
    private static bool Stable(Func<Vector2, float> truthAt, IReadOnlyList<Caster> casters, Vector2 p, out float truth)
    {
        truth = truthAt(p);
        bool lit = truth == 0f;
        const float e = 0.03f;
        foreach (Vector2 q in new Vector2[] { new(e, 0f), new(-e, 0f), new(0f, e), new(0f, -e) })
            if ((truthAt(p + q) == 0f) != lit || casters.Any(c => Inside(c, p + q))) return false;
        return true;
    }

    // Even-odd point in polygon over every loop.
    private static bool Inside(Caster caster, Vector2 p)
    {
        bool inside = false;
        foreach (Vector2[] loop in caster.Loops)
            for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
                if ((loop[i].Y > p.Y) != (loop[j].Y > p.Y)
                    && p.X < (loop[j].X - loop[i].X) * (p.Y - loop[i].Y) / (loop[j].Y - loop[i].Y) + loop[i].X)
                    inside = !inside;
        return inside;
    }

    private static void Equal<T>(T expected, T actual) => Require(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; got {actual}.");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
