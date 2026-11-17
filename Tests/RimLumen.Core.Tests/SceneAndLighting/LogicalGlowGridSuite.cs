using System;
using System.Collections.Generic;
using RimLumen.Core.Lighting;
using Verse;

namespace RimLumen.Core.Tests.SceneAndLighting;

// Expected values are worked by hand from vanilla GlowGrid (Assembly-CSharp:239060-239108, :69525-69544) for a
// 255-white glower: intDist starts at 100, cardinal +100, diagonal +141; atten = Lerp(1 - d/r, 1/d^2, 0.4);
// channel = (int)(255 * atten); GroundGlowAt = Min(0.5, channel / 255 * 3.6).
[LumenTestSuite]
public static class LogicalGlowGridSuite
{
    private const int Size = 30;

    private static float Channel(int channel) => channel / 255f * 3.6f;

    // r = 5: origin d = 1 -> atten 0.88 -> 0.5; (3,0) d = 4 -> atten 0.145 -> 36 -> 0.5;
    //        (4,0) d = 5 -> atten 0.016 -> 4; (3,2) intDist 482 -> atten 0.0388 -> 9;
    //        (5,0) intDist 600 and (4,1) intDist 541 exceed RoundToInt(500).
    [LumenFact]
    public static void OpenGroundFalloffFollowsTheVanillaCurveAndCutoff()
    {
        var grid = new LogicalGlowGrid(Size, Size);
        grid.Set(1, new LogicalLight(15, 15, 5f));
        Rebuild(grid);

        Expect(grid, 15, 15, 0.5f, "origin");
        foreach ((int dx, int dz) in new[] { (3, 0), (-3, 0), (0, 3), (0, -3) })
            Expect(grid, 15 + dx, 15 + dz, 0.5f, $"d=4 at ({dx},{dz})");
        foreach ((int dx, int dz) in new[] { (4, 0), (-4, 0), (0, 4), (0, -4) })
            Expect(grid, 15 + dx, 15 + dz, Channel(4), $"d=5 at ({dx},{dz})");
        Expect(grid, 18, 17, Channel(9), "intDist 482 at (3,2)");
        Expect(grid, 20, 15, 0f, "(5,0) lies beyond the radius");
        Expect(grid, 19, 16, 0f, "(4,1) at intDist 541 lies beyond RoundToInt(r * 100)");
        Expect(grid, 0, 0, 0f, "far corner");
    }

    // A full wall at x = 12 hides (13,10); a doorway at (12,10) (intDist 300) lets the light through to (13,10)
    // at d = 4 and round one jamb to (13,11) by the 441 diagonal: atten 0.0914 -> channel 23.
    [LumenFact]
    public static void WallsOccludeAndDoorwaysWrapLikeVanilla()
    {
        var grid = new LogicalGlowGrid(Size, Size);
        grid.Set(1, new LogicalLight(10, 10, 5f));
        for (int z = 5; z <= 15; z++) grid.SetBlocker(Index(12, z), true);
        Rebuild(grid);
        Expect(grid, 11, 10, 0.5f, "lit side of the wall");
        Expect(grid, 12, 10, 0f, "wall cell itself is never entered");
        Expect(grid, 13, 10, 0f, "behind the wall");
        Expect(grid, 13, 11, 0f, "behind the wall, off axis");

        grid.SetBlocker(Index(12, 10), false);
        List<int> changed = Rebuild(grid);
        Expect(grid, 13, 10, 0.5f, "through the doorway at d = 4");
        Expect(grid, 13, 11, Channel(23), "wrapped round the door jamb at intDist 441");
        VfxAssert.Check(changed.Contains(Index(13, 10)) && changed.Contains(Index(12, 10)), "opening the door reports the newly lit cells");

        grid.SetBlocker(Index(12, 10), true);
        Rebuild(grid);
        Expect(grid, 13, 10, 0f, "closing the gap occludes again");
    }

    // r = 3: the (1,1) diagonal at intDist 241 is lit (channel 47 -> 0.5) past one cardinal blocker, and closed
    // when both (1,0) and (0,1) block; every detour exceeds RoundToInt(300).
    [LumenFact]
    public static void DiagonalClosesOnlyBetweenTwoCardinalBlockers()
    {
        var grid = new LogicalGlowGrid(Size, Size);
        grid.Set(1, new LogicalLight(10, 10, 3f));
        grid.SetBlocker(Index(11, 10), true);
        Rebuild(grid);
        Expect(grid, 11, 11, 0.5f, "one cardinal blocker leaves the diagonal open");

        grid.SetBlocker(Index(10, 11), true);
        Rebuild(grid);
        Expect(grid, 11, 11, 0f, "two cardinal blockers close the diagonal");
        Expect(grid, 9, 11, 0.5f, "the other diagonals stay open");
    }

    // Two r = 5 lights 8 cells apart both reach (14,10) at d = 5 with channel 4: the field keeps the Max.
    [LumenFact]
    public static void OverlappingLightsCombineByMax()
    {
        var grid = new LogicalGlowGrid(Size, Size);
        grid.Set(1, new LogicalLight(10, 10, 5f));
        grid.Set(2, new LogicalLight(18, 10, 5f));
        Rebuild(grid);
        Expect(grid, 14, 10, Channel(4), "equal fringes combine by Max");
        Expect(grid, 13, 10, 0.5f, "inside the first light");
        Expect(grid, 21, 10, 0.5f, "inside the second light");
    }

    [LumenFact]
    public static void RemovingTheLastLightClearsTheFieldAndReportsEveryLitCell()
    {
        var grid = new LogicalGlowGrid(Size, Size);
        grid.Set(7, new LogicalLight(15, 15, 5f));
        List<int> lit = Rebuild(grid);
        VfxAssert.Check(lit.Count > 0 && lit.Contains(Index(15, 15)), "lighting reports the lit cells");

        grid.Set(7, new LogicalLight(15, 15, 5f));
        VfxAssert.Equal(0, Rebuild(grid).Count);

        grid.Remove(7);
        List<int> cleared = Rebuild(grid);
        VfxAssert.Check(grid.Glow is null, "no lit light publishes no field");
        VfxAssert.Equal(lit.Count, cleared.Count);
    }

    [LumenFact]
    public static void BlockersOutsideEveryLightsReachLeaveTheFieldUntouched()
    {
        var grid = new LogicalGlowGrid(Size, Size);
        grid.Set(1, new LogicalLight(5, 5, 3f));
        Rebuild(grid);
        float[] before = grid.Glow!;

        grid.SetBlocker(Index(20, 20), true);
        VfxAssert.Equal(0, Rebuild(grid).Count);
        VfxAssert.Check(ReferenceEquals(before, grid.Glow), "a distant blocker does not republish the field");

        // (7,5) sat at intDist 300 (channel 11); every detour round (6,5) exceeds RoundToInt(300).
        Expect(grid, 7, 5, Channel(11), "edge of the r = 3 light");
        grid.SetBlocker(Index(6, 5), true);
        VfxAssert.Check(Rebuild(grid).Contains(Index(7, 5)), "a blocker inside the reach shadows the cell behind it");
        Expect(grid, 7, 5, 0f, "shadowed edge");
    }

    [LumenFact]
    public static void RadiusMustBeFiniteAndWithinVanillaMaxLightRadius()
    {
        foreach (float bad in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, 40.001f })
        {
            VfxAssert.Throws<ArgumentOutOfRangeException>(() => _ = new LogicalLight(0, 0, bad));
            VfxAssert.Check(HasConfigError(bad), $"ConfigErrors rejects glowRadius {bad}");
        }

        _ = new LogicalLight(0, 0, 40f);
        VfxAssert.Check(!HasConfigError(40f) && !HasConfigError(5f), "ConfigErrors accepts (0, 40]");
    }

    private static bool HasConfigError(float glowRadius)
    {
        var props = new CompProperties_RimLumenLogicalLight { glowRadius = glowRadius };
        foreach (string _ in props.ConfigErrors(new ThingDef { defName = "TestLamp" })) return true;
        return false;
    }

    private static int Index(int x, int z) => z * Size + x;

    private static List<int> Rebuild(LogicalGlowGrid grid)
    {
        var changed = new List<int>();
        grid.Rebuild(changed);
        return changed;
    }

    private static void Expect(LogicalGlowGrid grid, int x, int z, float expected, string what)
    {
        float actual = grid.Glow?[Index(x, z)] ?? 0f;
        VfxAssert.Check(MathF.Abs(actual - expected) < 1e-6f, $"{what}: expected {expected}, got {actual} at ({x},{z}).");
    }
}
