using System;
using System.Numerics;
using RimLumen.Core.Lighting;
using RimLumen.Core.Lighting.Occlusion;

namespace RimLumen.Core.Tests.Rendering;

// Lighting takeover references (design 2, 3.3, 3.4, 3.9): sun visibility, single shading, vanilla glow attenuation.
[LumenTestSuite]
public static class TakeoverSuite
{
    // GenCelestial.GetLightSourceInfo: noon x = 0, z = -1.5; dusk x = 15, z = -1.5 - 2.5 * 15^2 / 100 = -7.125.
    private static readonly Vector2 Noon = new(0f, -1.5f);
    private static readonly Vector2 Dusk = new(15f, -7.125f);

    [LumenFact]
    public static void NoonShadowReachesOneAndAHalfCellsAndTheWallTopIsShaded()
    {
        var builder = new OcclusionBuilder(12, 12);
        builder.Wall(new CellBox(5, 5, 5, 5));
        OcclusionFrame frame = Frame(builder, 12);
        Equal(0f, SunTransport.Visibility(frame, new(5.5f, 5.5f), Noon));
        Equal(0f, SunTransport.Visibility(frame, new(5.5f, 4.0f), Noon));
        Equal(1f, SunTransport.Visibility(frame, new(5.5f, 3.5f), Noon));
        Equal(1f, SunTransport.Visibility(frame, new(5.5f, 6.5f), Noon));
        Equal(1f, SunTransport.Visibility(frame, new(7.5f, 4.0f), Noon));
    }

    [LumenFact]
    public static void SunShadowStaysInsideTheExtrudedFootprint()
    {
        // Vanilla SunShadow extrudes the wall's cell footprint along the cast vector: ground just outside it on the
        // sun-facing side and beside the cast shadow stays lit.
        var builder = new OcclusionBuilder(12, 12);
        builder.Wall(new CellBox(5, 5, 5, 5));
        OcclusionFrame frame = Frame(builder, 12);
        Equal(1f, SunTransport.Visibility(frame, new(5.5f, 6.05f), Noon));
        Equal(1f, SunTransport.Visibility(frame, new(4.95f, 4.5f), Noon));
        Equal(1f, SunTransport.Visibility(frame, new(6.05f, 4.5f), Noon));
    }

    [LumenFact]
    public static void WallShadowTipIsCastFromWhereTheRayEntersTheOutline()
    {
        // Noon tip of a unit-height wall whose south edge is z = 30: the ray reaches full edge EdgeRamp inside the
        // outline, at z = 30 + EdgeRamp / 4, so ground z sees height t = (that z - z) / 1.5 there and visibility
        // 1 - clamp((h - t) / HeightBand, 0, 1). One step per texel alone put it at the texel centre z = 30.125.
        var builder = new OcclusionBuilder(40, 40);
        builder.Wall(new CellBox(5, 30, 5, 30));
        OcclusionFrame frame = Frame(builder, 40);
        float h = 128f / 255f * OcclusionLayout.MaxSunHeight, entry = 30f + OcclusionLayout.EdgeRamp / OcclusionLayout.TexelsPerCell;
        foreach (float z in new[] { 28.5f, 28.6f, 28.7f, 28.78f, 28.9f })
            Near(1f - Math.Clamp((h - (entry - z) / 1.5f) / SunTransport.HeightBand, 0f, 1f), SunTransport.Visibility(frame, new(5.5f, z), Noon), 1e-4f);
    }

    [LumenFact]
    public static void SunWallEdgeRampLiesInsideTheFootprint()
    {
        // Noon shadow of wall cell (5, 30) at row z = 29.5: the onset stays on the footprint edge x = 5 and the ramp ends
        // EdgeRamp (0.125 texel, 0.03125 cell) inside it. Before the distance field it spanned half a texel (0.744 at 5.032).
        var builder = new OcclusionBuilder(32, 32);
        builder.Wall(new CellBox(5, 30, 5, 30));
        OcclusionFrame frame = Frame(builder, 32);
        Equal(1f, SunTransport.Visibility(frame, new(4.999f, 29.5f), Noon));
        Require(SunTransport.Visibility(frame, new(5.001f, 29.5f), Noon) < 1f, "The shadow must start on the footprint edge.");
        Equal(0f, SunTransport.Visibility(frame, new(5.032f, 29.5f), Noon));
    }

    [LumenFact]
    public static void FadedBoxShadowRisesMonotonicallyAwayFromTheBox()
    {
        // A shadowData box fades as t / h from its edge. Before the split, the bilinear spill just outside the box
        // carried no FlagSunFade and fine steps landing in it shaded fully: black bands and isolated zeros.
        var builder = new OcclusionBuilder(40, 40);
        builder.Height(OcclusionStamps.Opaque, new MaskPlacement(20.5f, 30.5f, 3f, 3f), 1f, fade: true);
        OcclusionFrame frame = Frame(builder, 40);
        float previous = 0f;
        for (int i = 0; i <= 40; i++)
        {
            float z = 29f - i * 0.025f; // from the box's south edge outwards
            float vis = SunTransport.Visibility(frame, new(20.5f, z), Noon);
            Require(vis > 0f && vis >= previous - 1e-6f, $"z = {z}: visibility {vis} after {previous}; expected a positive, monotonic rise away from the box.");
            previous = vis;
        }
    }

    [LumenFact]
    public static void DuskShadowStretchesAlongTheCastVector()
    {
        var builder = new OcclusionBuilder(40, 40);
        builder.Wall(new CellBox(5, 30, 5, 30));
        OcclusionFrame frame = Frame(builder, 40);
        var wall = new Vector2(5.5f, 30.5f);
        // Half a height unit along the dusk vector is about 8.3 cells away, far beyond the noon reach of 1.5 cells.
        Equal(0f, SunTransport.Visibility(frame, wall + Dusk * 0.5f, Dusk));
        Equal(1f, SunTransport.Visibility(frame, wall + Dusk * 1.5f, Dusk));
        Equal(1f, SunTransport.Visibility(frame, wall - Dusk * 0.5f, Dusk));
    }

    [LumenFact]
    public static void DuskColumnShadowIsCastFromItsWholeSprite()
    {
        // The sprite's upper half (z 5.5-6.25) casts as well: a point 0.1 height units along the dusk vector from it is
        // shadowed, and so is the same point cast from the sprite's base.
        OcclusionFrame column = Column();
        float beside = SunTransport.Visibility(column, new Vector2(5.5f, 6.0f) + Dusk * 0.1f, Dusk);
        float behind = SunTransport.Visibility(column, new Vector2(5.5f, 5.25f) + Dusk * 0.1f, Dusk);
        Require(beside < 0.5f && behind < 0.5f, $"Dusk shadow of the Column sprite: beside its upper half {beside}, behind its base {behind}; expected both below 0.5.");
    }

    [LumenFact]
    public static void ShadowDataBoxFadesAsOneMinusTOverH()
    {
        // 3 x 3 box, height 1, sampled 0.75 cells south of its south edge z = 4 at noon. The shadow peaks where the ray
        // reaches full edge, EdgeRamp inside the outline, at t = (0.75 + EdgeRamp / 4) / 1.5; the fade leaves
        // 1 - (1 - t / h) = t / h lit.
        var faded = new OcclusionBuilder(12, 12);
        faded.Height(OcclusionStamps.Opaque, new MaskPlacement(5.5f, 5.5f, 3f, 3f), 1f, fade: true);
        float vis = SunTransport.Visibility(Frame(faded, 12), new(5.5f, 3.25f), Noon);
        float expected = ((0.75f + OcclusionLayout.EdgeRamp / OcclusionLayout.TexelsPerCell) / 1.5f) / (128f / 255f * OcclusionLayout.MaxSunHeight);
        Require(MathF.Abs(vis - expected) < 1e-4f, $"Faded box: expected {expected}; got {vis}.");

        var hard = new OcclusionBuilder(12, 12);
        hard.Height(OcclusionStamps.Opaque, OcclusionStamps.Over(new CellBox(4, 4, 6, 6)), 1f, fade: false);
        Equal(0f, SunTransport.Visibility(Frame(hard, 12), new(5.5f, 3.25f), Noon));
    }

    [LumenFact]
    public static void DynamicShadowShadesEachPixelOnceAtTheDarkerFactor()
    {
        const float c = 0.3f;
        for (float s = c; s <= 1.0001f; s += 0.05f)
            for (float sd = c; sd <= 1.0001f; sd += 0.05f)
            {
                float final = s * SunShadowComposition.DynamicFactor(s, sd);
                Require(MathF.Abs(final - MathF.Min(s, sd)) < 1e-6f, $"S = {s}, s_d = {sd}: final {final}, expected {MathF.Min(s, sd)}.");
            }
        float ground = SunShadowComposition.Ground(c, 0f);
        Require(MathF.Abs(ground * SunShadowComposition.DynamicFactor(ground, c) - c) < 1e-6f, "Both shadows at c must stay c, not c^2.");
        Equal(1f, SunShadowComposition.Ground(c, 1f));
        Equal(0.65f, SunShadowComposition.DynamicFactor(1f, 0.65f));
    }

    [LumenFact]
    public static void OverlayMatchesVanillaLightOverlayWhenUnshadowed()
    {
        // Worked from vanilla LightOverlay.frag.glsl: sky (0.8, 0.7, 0.6, 1), a = 0.4, glow (0.9, 0.2, 0.1) gives
        // skyContribution (0.48, 0.42, 0.36, 0.6), colour (1.38, 0.62, 0.46, 1.0), clampBase 1.5 - 0.5 * 0.36 = 1.32,
        // and the red clamp scales everything by 1.32 / 1.38.
        var sky = new Vector4(0.8f, 0.7f, 0.6f, 1f);
        Near(new Vector4(1.32f, 0.5930435f, 0.44f, 0.9565217f), SunShadowComposition.Overlay(sky, 0.4f, Vector3.One, new Vector3(0.9f, 0.2f, 0.1f)));
        Near(new Vector4(0.6f, 0.6f, 0.6f, 1f), SunShadowComposition.Overlay(new Vector4(0.5f, 0.5f, 0.5f, 1f), 0f, Vector3.One, new Vector3(0.1f)));
        // Shadowed (s = 0.5): the sky term halves, clampBase still comes from the unshadowed 0.36, nothing clamps.
        Near(new Vector4(1.14f, 0.41f, 0.28f, 1f), SunShadowComposition.Overlay(sky, 0.4f, new Vector3(0.5f), new Vector3(0.9f, 0.2f, 0.1f)));
    }

    [LumenFact]
    public static void SunShadeKeepsThePoolTermAndShadesOnlyTheSkyShare()
    {
        var sky = new Vector4(0.8f, 0.7f, 0.6f, 1f);
        var shadow = new Vector3(0.5f, 0.55f, 0.6f);
        Near(Vector3.One, SunShadowComposition.ShadeFactor(sky, 0.4f, Vector3.One, new Vector3(0.9f, 0.2f, 0.1f)));
        // No pool and nothing clamps: the factor is the ground factor itself, which is what vanilla SunShadow multiplies by.
        Near(shadow, SunShadowComposition.ShadeFactor(sky, 0.4f, shadow, Vector3.Zero));
        // Inside the pool of OverlayMatchesVanillaLightOverlayWhenUnshadowed: (1.14, 0.41, 0.28) / (1.32, 0.5930435, 0.44).
        // Red keeps 86% instead of the sky-only 50%, because only the sky share is shaded.
        Near(new Vector3(0.8636364f, 0.6913490f, 0.6363636f),
            SunShadowComposition.ShadeFactor(sky, 0.4f, new Vector3(0.5f), new Vector3(0.9f, 0.2f, 0.1f)));
        // Full roof without a pool: no sky reaches the pixel, so the sun has nothing to shade.
        Near(Vector3.One, SunShadowComposition.ShadeFactor(sky, 1f, shadow, Vector3.Zero));
    }

    [LumenFact]
    public static void ColoredWeatherShadowPreservesVanillaTintWithoutDoubleDarkening()
    {
        Vector3 c = new Vector3(234, 200, 255) / 255f; // NoxiousHaze: SkyColorSet.shadow
        Vector3 ground = new(SunShadowComposition.Ground(c.X, 0.5f), SunShadowComposition.Ground(c.Y, 0.5f), SunShadowComposition.Ground(c.Z, 0.5f));
        Vector3 dynamic = Vector3.Lerp(c, Vector3.One, 0.25f); // vanilla SunShadowFade.vert.glsl
        Vector3 correction = new(SunShadowComposition.DynamicFactor(ground.X, dynamic.X), SunShadowComposition.DynamicFactor(ground.Y, dynamic.Y), SunShadowComposition.DynamicFactor(ground.Z, dynamic.Z));
        Vector4 overlay = SunShadowComposition.Overlay(Vector4.One, 0f, ground, Vector3.Zero);
        Vector3 rgb = new(overlay.X, overlay.Y, overlay.Z);
        Near(ground, rgb);
        Near(Vector3.Min(ground, dynamic), rgb * correction);
        Near(new Vector3(239.25f, 213.75f, 255f) / 255f, rgb * correction);
    }

    [LumenFact]
    public static void VanillaFalloffFollowsComputeGlowGrids()
    {
        // ComputeGlowGridsJob.SetGlowFromDist: lerp(1 - d/R, 1/d^2, 0.4) with the light's own cell at d = 1.
        const float radius = 6f;
        Near(0.9f, VisualPointLight.VanillaFalloff(0f, radius));
        Near(0.3444444f, VisualPointLight.VanillaFalloff(2f, radius));
        Near(0.0111111f, VisualPointLight.VanillaFalloff(5f, radius));
        Equal(0f, VisualPointLight.VanillaFalloff(5.5f, radius));
    }

    [LumenFact]
    public static void VanillaPoolProjectsEachLightOntoColor32Range()
    {
        Near(new Vector3(1f, 0.5f, 0.25f), VisualPointLight.ProjectVanilla(new Vector3(2f, 1f, 0.5f)));
        Near(new Vector3(0.5f, 0.2f, 0f), VisualPointLight.ProjectVanilla(new Vector3(0.5f, 0.2f, 0f)));
        var lamp = new VisualPointLight(3.5f, VisualPointLight.VanillaHeight, 3.5f, 6f, new Vector3(2f, 1f, 0.5f), VisualPointLightKind.Vanilla);
        var empty = new OcclusionBuilder(12, 12);
        // At the centre: 0.9 * (2, 1, 0.5) = (1.8, 0.9, 0.45), projected to max 1.
        Near(new Vector3(1f, 0.5f, 0.25f), lamp.EvaluateAt(3.5f, 3.5f, Frame(empty, 12)));
    }

    [LumenFact]
    public static void ColumnCastsAHeightShadowAwayFromATorchLamp()
    {
        // TorchLamp-height light south of the Column: the ground right behind its sprite (north edge z = 6.25) is shaded,
        // the shadow ends where the ray clears h over that edge (z = 9.97), and ground beside it stays lit.
        var torch = new VisualPointLight(5.5f, VisualPointLight.VanillaHeight, 2.5f, 10f, new Vector3(0.5f), VisualPointLightKind.Vanilla);
        OcclusionFrame column = Column();
        float behind = PoolVisibility(torch, column, 5.5f, 6.5f);
        Require(behind < 0.75f, $"Ground behind the Column: expected a shadow; visibility {behind}.");
        Equal(1f, PoolVisibility(torch, column, 5.5f, 11.0f));
        Equal(1f, PoolVisibility(torch, column, 7.0f, 6.0f));
    }

    [LumenFact]
    public static void ColumnNorthFaceShadowFadesLikeItsBody()
    {
        // A torch south-west of the Column casts north-east across the sprite's north face (z = 6.325), whose edge
        // texels carry no fade flag of their own. A fade occluder shades at most 1 - t / h, so a ground point keeps at
        // least rise * (its distance to the sprite, less one texel) / h lit, densely sampled and as marched.
        var torch = new VisualPointLight(3.5f, VisualPointLight.VanillaHeight, 3.5f, 10f, new Vector3(0.5f), VisualPointLightKind.Vanilla);
        OcclusionFrame column = Column();
        float h = MathF.Floor(255f * 0.5f) / 255f;
        foreach (Vector2 target in AcrossShadow(torch, 1.0f, 2.0f))
        {
            float dx = MathF.Max(0f, MathF.Abs(target.X - 5.5f) - 0.25f), dz = MathF.Max(0f, MathF.Max(5.075f - target.Y, target.Y - 6.325f));
            float rise = torch.Y / Vector2.Distance(target, new Vector2(torch.X, torch.Z));
            float bound = rise * MathF.Max(0f, MathF.Sqrt(dx * dx + dz * dz) - 0.25f) / h;
            float dense = DensePointVisibility(torch, column, target), march = SunTransport.PointVisibility(column, new Vector2(torch.X, torch.Z), torch.Y, target);
            Require(dense >= bound && march >= bound, $"Column shadow at {target}: dense {dense}, march {march}; expected both at least {bound}.");
        }
    }

    [LumenFact]
    public static void ColumnShadowSideMarchesLikeADenseMarch()
    {
        // Across the Column's diagonal shadow, including both shadow sides where rays graze the sprite outline, the
        // march (one fine step per texel crossed plus the outline entry) stays within 0.05 of the same march sampled
        // every 0.005 cell. Targets on the sprite itself are left out: there the own-footprint skip decides the first step.
        var torch = new VisualPointLight(3.5f, VisualPointLight.VanillaHeight, 3.5f, 10f, new Vector3(0.5f), VisualPointLightKind.Vanilla);
        OcclusionFrame column = Column();
        foreach (Vector2 target in AcrossShadow(torch, 0.6f, 1.0f, 1.5f, 2.0f))
        {
            if (target.X >= 5.25f && target.X <= 5.75f && target.Y >= 5.075f && target.Y <= 6.325f) continue;
            float dense = DensePointVisibility(torch, column, target), march = SunTransport.PointVisibility(column, new Vector2(torch.X, torch.Z), torch.Y, target);
            Require(MathF.Abs(march - dense) <= 0.05f, $"Column shadow at {target}: march {march}, dense {dense}.");
        }
    }

    [LumenFact]
    public static void ColumnLitByAPointLightKeepsItsOwnFootprintLit()
    {
        // Pools are evaluated per ground pixel and applied over sprites, so the Column's own footprint on the lit side must
        // not shade itself (the sun's t = 0 self-cell term would zero it).
        var torch = new VisualPointLight(5.5f, VisualPointLight.VanillaHeight, 2.5f, 10f, new Vector3(0.5f), VisualPointLightKind.Vanilla);
        OcclusionFrame column = Column();
        Equal(1f, PoolVisibility(torch, column, 5.5f, 5.2f));
        float behind = PoolVisibility(torch, column, 5.5f, 6.5f);
        Require(behind < 0.75f, $"Ground behind the Column: expected a shadow; visibility {behind}.");
    }

    [LumenFact]
    public static void APointLightBelowTheColumnTopCastsAnUnboundedShadow()
    {
        // H < h: the ray never clears the Column, so the shadow reaches the pool's edge; at H = 1 that point is lit.
        OcclusionFrame column = Column();
        var low = new VisualPointLight(5.5f, 0.4f, 2.5f, 10f, new Vector3(0.5f), VisualPointLightKind.Vanilla);
        var high = low with { Y = VisualPointLight.VanillaHeight };
        float edge = PoolVisibility(low, column, 5.5f, 11.0f);
        Require(edge < 1f, $"Low light at the pool edge behind the Column: expected a shadow; visibility {edge}.");
        Equal(1f, PoolVisibility(high, column, 5.5f, 11.0f));
    }

    [LumenFact]
    public static void PointLightWallShadowIsTheSolidShadowAlone()
    {
        // A wall is solid and carries G = 1. Solid and height are both opaque blockers and combine by the darker term,
        // so the wall's height shadow never darkens its solid penumbra a second time (product 0.2089 at this edge).
        var builder = new OcclusionBuilder(32, 32);
        builder.Wall(new CellBox(16, 20, 16, 20));
        OcclusionFrame wall = Frame(builder, 32);
        var flat = new OcclusionBuilder(32, 32);
        flat.Wall(new CellBox(16, 20, 16, 20), 0f);
        OcclusionFrame withoutHeight = Frame(flat, 32);
        var torch = new VisualPointLight(16.5f, VisualPointLight.VanillaHeight, 16.5f, 10f, new Vector3(0.5f), VisualPointLightKind.Vanilla);
        Near(0.4571f, PoolVisibility(torch, wall, 17.46f, 24f), 1e-4f);
        // For point lights a blockLight texel is governed only by the solid term: the same wall with and without G.
        foreach (Vector2 at in new Vector2[] { new(16.5f, 16.5f), new(13.2f, 17.7f), new(19.9f, 18.1f), new(16.5f, 23.5f) })
        {
            VisualPointLight lamp = torch with { X = at.X, Z = at.Y };
            for (float x = 10f; x <= 23f; x += 0.25f)
                for (float z = 12f; z <= 28f; z += 0.25f)
                {
                    Vector3 pool = lamp.EvaluateAt(x, z, wall), solidOnly = lamp.EvaluateAt(x, z, withoutHeight);
                    Require(pool == solidOnly, $"Light {at}, ({x}, {z}): {pool} instead of the wall without height {solidOnly}.");
                }
        }
    }

    [LumenFact]
    public static void BlockLightHeightNeverShadesAPointLight()
    {
        // Each case: the pool is the same with and without the blockLight thing's G (its sun height or sun box).
        var torch = new VisualPointLight(0f, VisualPointLight.VanillaHeight, 0f, 10f, new Vector3(0.5f), VisualPointLightKind.Vanilla);
        OcclusionFrame Corner(float height)
        {
            var builder = new OcclusionBuilder(12, 12);
            foreach (CellBox wall in new[] { new CellBox(5, 5, 5, 5), new CellBox(5, 6, 5, 6), new CellBox(6, 5, 6, 5) })
                builder.Wall(wall, height);
            return Frame(builder, 12);
        }
        // Inner corner of three walls: the bilinear height at the target must not see the walls' G.
        Equal(PoolVisibility(torch with { X = 8.5f, Z = 8.5f }, Corner(0f), 6.01f, 6.01f), PoolVisibility(torch with { X = 8.5f, Z = 8.5f }, Corner(1f), 6.01f, 6.01f));
        OcclusionFrame LampInWall(float height)
        {
            var builder = new OcclusionBuilder(12, 12);
            builder.Wall(new CellBox(5, 5, 5, 5), height);
            builder.Height(OcclusionStamps.Opaque, OcclusionStamps.Over(new CellBox(6, 5, 6, 5)), 0.5f, fade: false);
            return Frame(builder, 12);
        }
        // A light inside a wall: the wall's G must not join the neighbouring occluder into the light's own footprint.
        Equal(PoolVisibility(torch with { X = 5.5f, Z = 5.5f }, LampInWall(0f), 7.5f, 5.5f), PoolVisibility(torch with { X = 5.5f, Z = 5.5f }, LampInWall(1f), 7.5f, 5.5f));
        OcclusionFrame FadedBlocker(float height)
        {
            var builder = new OcclusionBuilder(12, 12);
            builder.Blocker(OcclusionStamps.Opaque, new MaskPlacement(5.5f, 5.5f, 1f, 1f), height, null, fade: true);
            return Frame(builder, 12);
        }
        // A blockLight thing with a shadowData height (Ship_ComputerCore) carries it on its solid texels: no point-light height.
        Equal(PoolVisibility(torch with { X = 6.25f, Z = 3f }, FadedBlocker(0f), 6.25f, 7.5f), PoolVisibility(torch with { X = 6.25f, Z = 3f }, FadedBlocker(1f), 6.25f, 7.5f));
    }

    [LumenFact]
    public static void PointLightPassesAnOpenDoor()
    {
        // A door is blockLight and carries G = 1 for the sun; for a point light only its gap decides (fully open: 230).
        var builder = new OcclusionBuilder(12, 12);
        builder.Blocker(OcclusionStamps.Opaque, new MaskPlacement(5.5f, 5.5f, 1f, 1f), 1f, DoorAxis.Z);
        builder.RecomputeDerived(new CellBox(0, 0, 11, 11));
        var state = new byte[12 * 12 * 4];
        state[(5 * 12 + 5) * 4] = 230;
        state[(5 * 12 + 5) * 4 + 2] = 1;
        var open = new OcclusionFrame(builder.Publish(), new DoorStates(12, 12, state));
        var torch = new VisualPointLight(5.5f, VisualPointLight.VanillaHeight, 3.5f, 10f, new Vector3(0.5f), VisualPointLightKind.Vanilla);
        Equal(1f, PoolVisibility(torch, open, 5.5f, 7.5f));
    }

    [LumenFact]
    public static void LightOnAWallEdgeIsBlockedByThatWall()
    {
        // A start on a texel edge does not own the solid texel behind it: the wall still blocks light leaving through it.
        var builder = new OcclusionBuilder(12, 12);
        builder.Wall(new CellBox(5, 5, 5, 5));
        var torch = new VisualPointLight(6f, VisualPointLight.VanillaHeight, 5.5f, 10f, new Vector3(0.5f), VisualPointLightKind.Vanilla);
        Equal(0f, PoolVisibility(torch, Frame(builder, 12), 3.5f, 5.5f));
    }

    [LumenFact]
    public static void StandingLampDoesNotShadeItsOwnPool()
    {
        // StandingLamp's sprite (drawSize 1) carries its shadowData height 0.6 and contains its own light (H = 1): the
        // light's own footprint is skipped at the source end, as a lamp inside a wall is not blocked by its own solid run.
        var builder = new OcclusionBuilder(16, 16);
        builder.Height(OcclusionStamps.Opaque, new MaskPlacement(8.5f, 8.5f, 1f, 1f), MathF.Floor(255f * 0.6f) / 255f, fade: true);
        OcclusionFrame lamp = Frame(builder, 16);
        var light = new VisualPointLight(8.5f, VisualPointLight.VanillaHeight, 8.5f, 10f, new Vector3(0.5f), VisualPointLightKind.Vanilla);
        for (float r = 0.05f; r <= 1.5f; r += 0.05f)
            for (int k = 0; k < 32; k++)
            {
                float x = 8.5f + r * MathF.Cos(k * MathF.Tau / 32), z = 8.5f + r * MathF.Sin(k * MathF.Tau / 32);
                Equal(1f, PoolVisibility(light, lamp, x, z));
            }
    }

    // Column (Buildings_Structure.xml:474-516) at cell (5, 5) as OcclusionField.Stamp prints it: its sprite, drawSize
    // (0.5, 1.25) at TrueCenter + drawOffset (0, 0, 0.2), so texels x 21-22, z 20-24; shadowData height
    // floor(255 * 0.5) / 255; height-bearing, so no fillPercent cover. The art is taken as opaque over the quad.
    private static OcclusionFrame Column()
    {
        var builder = new OcclusionBuilder(16, 16);
        builder.Height(OcclusionStamps.Opaque, new MaskPlacement(5.5f, 5.7f, 0.5f, 1.25f), MathF.Floor(255f * 0.5f) / 255f, fade: true);
        return Frame(builder, 16);
    }

    // Ground points every 0.05 cell on lines across the Column's shadow from light, each line `past` cells beyond the
    // sprite centre and 1.6 cells long.
    private static System.Collections.Generic.IEnumerable<Vector2> AcrossShadow(VisualPointLight light, params float[] pasts)
    {
        var centre = new Vector2(5.5f, 5.7f);
        Vector2 along = Vector2.Normalize(centre - new Vector2(light.X, light.Z)), across = new(-along.Y, along.X);
        foreach (float past in pasts)
            for (int i = -16; i <= 16; i++)
                yield return centre + along * past + across * (i * 0.05f);
    }

    // SunTransport.PointVisibility with the height march sampled every 0.005 cell instead of per texel crossed.
    private static float DensePointVisibility(VisualPointLight light, OcclusionFrame frame, Vector2 target)
    {
        var at = new Vector2(light.X, light.Z);
        float length = Vector2.Distance(at, target);
        Vector2 direction = (at - target) / length;
        float skipBefore = LightTransport.RunLength(frame, target, direction, length, true);
        float skipFrom = length - LightTransport.RunLength(frame, at, -direction, length, true);
        float shadow = skipBefore == 0f ? SunTransport.Shadow(frame.Map, target, 0f, true, out _) : 0f;
        for (float a = 0.0025f; a < length; a += 0.005f)
            if (a >= skipBefore && a < skipFrom) shadow = MathF.Max(shadow, SunTransport.Shadow(frame.Map, target + direction * a, a * light.Y / length, true, out _));
        return 1f - shadow;
    }

    // The pool's occlusion factor at (x, z): its value on frame over its value on an empty map (no projection clamp at 0.5).
    private static float PoolVisibility(VisualPointLight light, OcclusionFrame frame, float x, float z) =>
        light.EvaluateAt(x, z, frame).X / light.EvaluateAt(x, z, Empty(frame.Map.Width)).X;

    private static readonly System.Collections.Generic.Dictionary<int, OcclusionFrame> EmptyFrames = new();

    private static OcclusionFrame Empty(int size)
    {
        if (!EmptyFrames.TryGetValue(size, out OcclusionFrame frame)) EmptyFrames[size] = frame = Frame(new OcclusionBuilder(size, size), size);
        return frame;
    }


    private static OcclusionMap Publish(OcclusionBuilder builder, int size)
    {
        builder.RecomputeDerived(new CellBox(0, 0, size - 1, size - 1));
        return builder.Publish();
    }

    private static OcclusionFrame Frame(OcclusionBuilder builder, int size) =>
        new(Publish(builder, size), new DoorStates(size, size, new byte[size * size * 4]));

    private static void Equal(float expected, float actual) => Require(expected == actual, $"Expected {expected}; got {actual}.");
    private static void Near(float expected, float actual, float tolerance = 1e-5f) => Require(MathF.Abs(expected - actual) < tolerance, $"Expected {expected}; got {actual}.");
    private static void Near(Vector3 expected, Vector3 actual) => Require(Vector3.Distance(expected, actual) < 1e-5f, $"Expected {expected}; got {actual}.");
    private static void Near(Vector4 expected, Vector4 actual) => Require(Vector4.Distance(expected, actual) < 1e-5f, $"Expected {expected}; got {actual}.");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
