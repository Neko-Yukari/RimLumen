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
        builder.StampSolidFootprint(new CellBox(5, 5, 5, 5), 1f);
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
        builder.StampSolidFootprint(new CellBox(5, 5, 5, 5), 1f);
        OcclusionFrame frame = Frame(builder, 12);
        Equal(1f, SunTransport.Visibility(frame, new(5.5f, 6.05f), Noon));
        Equal(1f, SunTransport.Visibility(frame, new(4.95f, 4.5f), Noon));
        Equal(1f, SunTransport.Visibility(frame, new(6.05f, 4.5f), Noon));
    }

    [LumenFact]
    public static void SplitKeepsTheShadowTipOfACellAlignedWall()
    {
        // Pre-split values (bilinear G) at the noon tip of a unit-height wall whose south edge is z = 30: the coverage
        // split moves only the ramp at the footprint edge, so a tip cast from the wall's interior height is unchanged.
        var builder = new OcclusionBuilder(40, 40);
        builder.StampSolidFootprint(new CellBox(5, 30, 5, 30), 1f);
        OcclusionFrame frame = Frame(builder, 40);
        Equal(1f, SunTransport.Visibility(frame, new(5.5f, 28.6f), Noon));
        Near(0.5686f, SunTransport.Visibility(frame, new(5.5f, 28.7f), Noon), 1e-4f);
        Near(0.1419f, SunTransport.Visibility(frame, new(5.5f, 28.78f), Noon), 1e-4f);
        Equal(0f, SunTransport.Visibility(frame, new(5.5f, 28.9f), Noon));
    }

    [LumenFact]
    public static void FadedBoxShadowRisesMonotonicallyAwayFromTheBox()
    {
        // A shadowData box fades as t / h from its edge. Before the split, the bilinear spill just outside the box
        // carried no FlagSunFade and fine steps landing in it shaded fully: black bands and isolated zeros.
        var builder = new OcclusionBuilder(40, 40);
        builder.StampSunBox(new MaskPlacement(20.5f, 30.5f, 3f, 3f), 1f);
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
        builder.StampSolidFootprint(new CellBox(5, 30, 5, 30), 1f);
        OcclusionFrame frame = Frame(builder, 40);
        var wall = new Vector2(5.5f, 30.5f);
        // Half a height unit along the dusk vector is about 8.3 cells away, far beyond the noon reach of 1.5 cells.
        Equal(0f, SunTransport.Visibility(frame, wall + Dusk * 0.5f, Dusk));
        Equal(1f, SunTransport.Visibility(frame, wall + Dusk * 1.5f, Dusk));
        Equal(1f, SunTransport.Visibility(frame, wall - Dusk * 0.5f, Dusk));
    }

    [LumenFact]
    public static void ShadowDataBoxFadesAsOneMinusTOverH()
    {
        // 3 x 3 box, height 1, sampled 0.75 cells south of its south edge at noon. The first fine step inside the box
        // is the texel centre z = 4.125, reached at t = 0.875 / 1.5; the fade leaves 1 - (1 - t / h) = t / h lit.
        var faded = new OcclusionBuilder(12, 12);
        faded.StampSunBox(new MaskPlacement(5.5f, 5.5f, 3f, 3f), 1f);
        float vis = SunTransport.Visibility(Frame(faded, 12), new(5.5f, 3.25f), Noon);
        float expected = (0.875f / 1.5f) / (128f / 255f * OcclusionLayout.MaxSunHeight);
        Require(MathF.Abs(vis - expected) < 1e-4f, $"Faded box: expected {expected}; got {vis}.");

        var hard = new OcclusionBuilder(12, 12);
        hard.StampSunFootprint(new CellBox(4, 4, 6, 6), 1f);
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
        var lamp = new VisualPointLight(3.5f, 3.5f, 6f, new Vector3(2f, 1f, 0.5f), VisualPointLightKind.Vanilla);
        var empty = new OcclusionBuilder(12, 12);
        // At the centre: 0.9 * (2, 1, 0.5) = (1.8, 0.9, 0.45), projected to max 1.
        Near(new Vector3(1f, 0.5f, 0.25f), lamp.EvaluateAt(3.5f, 3.5f, Frame(empty, 12)));
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
