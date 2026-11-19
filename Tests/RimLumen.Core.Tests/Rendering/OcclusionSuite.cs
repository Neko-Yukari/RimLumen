using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RimLumen.Core.Lighting.Occlusion;

namespace RimLumen.Core.Tests.Rendering;

[LumenTestSuite]
public static class OcclusionSuite
{
    private static readonly Pattern Full = new(1, 1, [255]);
    private static readonly Pattern WestHalf = new(2, 1, [255, 0]);

    [LumenFact]
    public static void TwoCellWallHasDarkCore()
    {
        var builder = new OcclusionBuilder(12, 12);
        builder.Wall(new CellBox(5, 1, 6, 10));
        OcclusionFrame frame = Frame(builder);
        Equal(0f, Transmission(frame, new(3.5f, 5.5f), new(6.0f, 5.5f)));
    }

    [LumenFact]
    public static void OneCellCoverHasExpectedTransmittance()
    {
        var builder = new OcclusionBuilder(12, 12);
        builder.Cover(Full, new MaskPlacement(5.5f, 5.5f, 1f, 1f), 0.5f);
        OcclusionFrame frame = Frame(builder);
        float value = Transmission(frame, new(3.5f, 5.5f), new(7.5f, 5.5f));
        float shifted = Transmission(frame, new(3.8f, 5.5f), new(7.1f, 5.5f));
        Require(MathF.Abs(value - (1f - 128f / 255f)) < 0.002f && MathF.Abs(value - shifted) < 0.002f,
            $"Expected one-cell cover transmittance independent of endpoints; got {value}, {shifted}.");
    }

    [LumenFact]
    public static void WallEdgeRampStartsOnTheFootprintEdge()
    {
        // Design 1.8 (lead 2026-11-19, S2 inside): the solid term rises from 0 on the wall's north edge z = 5 to 1 over
        // EdgeRamp (0.125 texel, 0.03125 cell) inside it.
        var builder = new OcclusionBuilder(12, 12);
        builder.Wall(new CellBox(5, 4, 5, 4));
        OcclusionFrame frame = Frame(builder);
        float inside = Transmission(frame, new(3.5f, 4.96f), new(7.5f, 4.96f));
        float edge = Transmission(frame, new(3.5f, 4.985f), new(7.5f, 4.985f));
        float onEdge = Transmission(frame, new(3.5f, 5f), new(7.5f, 5f));
        Require(inside == 0f && edge > inside && edge < onEdge && onEdge == 1f, $"Expected the ramp inside the edge; got {inside}, {edge}, {onEdge}.");
    }

    [LumenFact]
    public static void RockCellTouchingTheRoomOnlyAtACornerStaysDark()
    {
        // Rock around room cells [5, 8]^2, lamp in the room. Rock cell (4, 4) meets the room only at its corner (5, 5): it
        // has no face toward the room, so every texel of it stays dark. A wall-core depth rule lit the texels within 3.5
        // texels of that corner, the bright notch at every concave corner of a mined-out room.
        OcclusionFrame room = Room();
        foreach (Vector2 target in CellTexels(4, 4))
            Equal(0f, Transmission(room, new(6.5f, 6.5f), target));
    }

    [LumenFact]
    public static void FirstRockLayerFacingTheLampIsLitAcrossTheWholeCell()
    {
        // Rock cell (6, 4) faces the room across its north edge z = 5: all its texels, down to the row 3.5 texels deep,
        // take the light of the ground just north of that edge. The rock behind it, cell (6, 3), stays dark.
        OcclusionFrame room = Room();
        var lamp = new Vector2(6.5f, 6.5f);
        foreach (Vector2 target in CellTexels(6, 4))
            Equal(Transmission(room, lamp, new(target.X, 5.125f)), Transmission(room, lamp, target));
        Equal(1f, Transmission(room, lamp, new(6.5f, 4.03f)));
        foreach (Vector2 target in CellTexels(6, 3))
            Equal(0f, Transmission(room, lamp, target));
    }

    [LumenFact]
    public static void WallEndPointingAtTheLampLightsOnlyItsNearCell()
    {
        // Wall cells (8, 4) and (8, 5), lamp due north within their x span. Only (8, 5)'s north edge faces the lamp; (8, 4)
        // lies behind it, and its east and west edges do not face a lamp between their lines.
        var builder = new OcclusionBuilder(12, 12);
        builder.Wall(new CellBox(8, 4, 8, 5));
        OcclusionFrame frame = Frame(builder);
        var lamp = new Vector2(8.5f, 11.5f);
        foreach (Vector2 target in CellTexels(8, 4))
            Equal(0f, Transmission(frame, lamp, target));
        Equal(1f, Transmission(frame, lamp, new(8.5f, 5.875f)));
    }

    [LumenFact]
    public static void ConvexBlockCornerCellIsLitFromEitherFace()
    {
        // A 3 x 3-cell block [4, 6]^2 lit from the south-west: its corner cell (4, 4) faces the lamps across its west and
        // south edges and is lit whole; the centre cell (5, 5) has no free face and stays dark.
        var builder = new OcclusionBuilder(12, 12);
        builder.Wall(new CellBox(4, 4, 6, 6));
        OcclusionFrame block = Frame(builder);
        foreach (Vector2 light in new Vector2[] { new(2.5f, 2.5f), new(2.5f, 4.6f), new(4.6f, 2.2f) })
        {
            foreach (Vector2 target in CellTexels(4, 4))
                Equal(1f, Transmission(block, light, target));
            foreach (Vector2 target in CellTexels(5, 5))
                Equal(0f, Transmission(block, light, target));
        }
    }

    [LumenFact]
    public static void DoorInAWallPassesLightOnlyWhenOpenAndItsLeafFacesTheLamp()
    {
        // East-west wall z = 5 with a door at (5, 5), leaves sliding along x; lamp north of it, ground target south.
        var builder = new OcclusionBuilder(12, 12);
        builder.Wall(new CellBox(2, 5, 4, 5));
        builder.Wall(new CellBox(6, 5, 8, 5));
        builder.Blocker(new CellBox(5, 5, 5, 5), 1f, DoorAxis.X);
        OcclusionMap map = Publish(builder);
        var closed = new byte[12 * 12 * 4];
        closed[(5 * 12 + 5) * 4 + 2] = 1;
        byte[] open = (byte[])closed.Clone();
        open[(5 * 12 + 5) * 4] = 255;
        var lamp = new Vector2(5.5f, 7.5f);
        OcclusionFrame shut = new(map, new DoorStates(12, 12, closed)), clear = new(map, new DoorStates(12, 12, open));
        Equal(0f, Transmission(shut, lamp, new(5.5f, 2.5f)));
        Equal(1f, Transmission(clear, lamp, new(5.5f, 2.5f)));
        foreach (Vector2 target in CellTexels(5, 5))
            Equal(1f, Transmission(shut, lamp, target));
    }

    [LumenFact]
    public static void DoorOpeningAffectsNeighbourCellGather()
    {
        var builder = new OcclusionBuilder(12, 12);
        builder.Blocker(new CellBox(5, 5, 5, 5), 1f, DoorAxis.Z);
        OcclusionMap map = Publish(builder);
        var closed = new byte[12 * 12 * 4];
        int i = (5 * 12 + 5) * 4;
        closed[i + 2] = 1;
        byte[] open = (byte[])closed.Clone();
        open[i] = 255;
        // z = 5.01 lies inside the door's ramp; its 2 x 2 gather takes the south row from cell z = 4.
        float dark = Transmission(new OcclusionFrame(map, new DoorStates(12, 12, closed)), new(3.5f, 5.01f), new(7.5f, 5.01f));
        float clear = Transmission(new OcclusionFrame(map, new DoorStates(12, 12, open)), new(3.5f, 5.01f), new(7.5f, 5.01f));
        Require(dark < 1f && clear == 1f, $"Door-edge gather should respect state; got closed={dark}, open={clear}.");
    }

    [LumenFact]
    public static void StampsTakeMaximumAndClearResets()
    {
        var builder = new OcclusionBuilder(12, 12);
        var at = new MaskPlacement(5.5f, 5.5f, 1f, 1f);
        builder.Cover(Full, at, 0.25f);
        builder.Cover(Full, at, 0.5f);
        builder.Cover(Full, at, 0.25f);
        OcclusionMap before = Publish(builder);
        int i = ((5 * 4 + 2) * 48 + 5 * 4 + 2) * 4;
        Require(before.Texels[i] == 128, $"Expected maximum opacity 128, got {before.Texels[i]}.");
        builder.ClearCells(new CellBox(5, 5, 5, 5));
        OcclusionMap after = Publish(builder);
        Require(after.Texels[i] == 0 && before.Texels[i] == 128, "Clear must reset the new snapshot without changing the old one.");
    }

    [LumenFact]
    public static void HeightStampRaisesGreenAndItsFlagUnderTheSpriteAndLeavesR()
    {
        var builder = new OcclusionBuilder(3, 1);
        var footprint = new CellBox(0, 0, 1, 0);
        builder.Cover(Full, new MaskPlacement(0.5f, 0.5f, 1f, 1f), 0.5f);
        builder.Wall(new CellBox(1, 0, 1, 0), 1.5f);
        builder.Height(Full, new MaskPlacement(1.5f, 0.5f, 1f, 1f), 0.5f, fade: true);
        builder.RecomputeDerived(footprint);
        OcclusionMap before = builder.Publish();

        builder.Height(Full, OcclusionStamps.Over(footprint), 1f, fade: false);
        OcclusionMap after = builder.Publish();
        for (int i = 0; i < after.Texels.Length; i += 4)
        {
            int x = i / 4 % after.TexelWidth;
            byte expectedGreen = x < 4 ? (byte)128 : x < 8 ? (byte)191 : (byte)0;
            Equal(expectedGreen, after.Texels[i + 1]);
            Equal(before.Texels[i], after.Texels[i]);
            Require(after.Texels[i + 2] >= before.Texels[i + 2], $"Texel {x}: B fell from {before.Texels[i + 2]} to {after.Texels[i + 2]}.");
            Equal((byte)(before.Texels[i + 3] | (x < 8 ? OcclusionLayout.FlagPointHeight : 0)), after.Texels[i + 3]);
        }
    }

    [LumenFact]
    public static void MatureTreeHeightFootprintIsItsCoverFootprint()
    {
        // TreeBase at growth 1 (Plants_Bases.xml:70-129): a 2 x 2 canopy (visualSizeRange 2.0) printed at the south clamp,
        // shadowData height 0.35 * 2, fillPercent 0.25. Its G covers exactly the texels its cover would, and no R.
        var canopy = new Pattern(8, 8, Enumerable.Range(0, 64)
            .Select(i => (i % 8 - 3.5f) * (i % 8 - 3.5f) + (i / 8 - 3.5f) * (i / 8 - 3.5f) <= 12f ? (byte)255 : (byte)0).ToArray());
        var at = new MaskPlacement(5.5f, 6f, 2f, 2f);
        var tree = new OcclusionBuilder(12, 12);
        tree.StampOccluder([new SpritePiece(OcclusionStamps.Fit(canopy, at), at)], new Occluder(MathF.Floor(255f * 0.7f) / 255f, Fade: true, Cover: 0.25f));
        var cover = new OcclusionBuilder(12, 12);
        cover.Cover(canopy, at, 0.25f);
        OcclusionMap height = Publish(tree), reference = Publish(cover);
        int stamped = 0;
        for (int i = 0; i < height.Texels.Length; i += 4)
        {
            Require((height.Texels[i + 1] > 0) == (reference.Texels[i] > 0) && height.Texels[i] == 0,
                $"Texel ({i / 4 % height.TexelWidth}, {i / 4 / height.TexelWidth}): G {height.Texels[i + 1]}, R {height.Texels[i]}, cover R {reference.Texels[i]}.");
            if (height.Texels[i + 1] > 0) stamped++;
        }
        Require(stamped > 0, "The canopy stamped no texel.");
    }

    [LumenFact]
    public static void IncrementalUpdateMatchesFullRebuildWhenArtOverhangsTwoCells()
    {
        // A one-cell tree whose canopy reaches two cells out, and a wall two cells east of it that is then removed.
        var tree = new FakeThing(new CellBox(5, 5, 5, 5), new MaskPlacement(5.5f, 5.5f, 5f, 5f), Solid: false);
        var wall = new FakeThing(new CellBox(7, 5, 7, 5), new MaskPlacement(7.5f, 5.5f, 1f, 1f), Solid: true);
        var map = new FakeMap(tree, wall);
        var builder = new OcclusionBuilder(12, 12);
        var ledger = new StampLedger<FakeThing>();
        ledger.BuildAll([tree, wall], map, builder);
        builder.RecomputeDerived(new CellBox(0, 0, 11, 11));
        map.Alive.Remove(wall);
        ledger.Update([(7, 5)], map, builder);
        OcclusionMap incremental = builder.Publish();

        var full = new OcclusionBuilder(12, 12);
        new StampLedger<FakeThing>().BuildAll([tree], map, full);
        OcclusionMap expected = Publish(full);
        int texel = incremental.Texels.AsSpan().CommonPrefixLength(expected.Texels);
        if (texel < expected.Texels.Length)
            throw new InvalidOperationException($"Texel ({texel / 4 % expected.TexelWidth}, {texel / 4 / expected.TexelWidth}) channel {texel % 4} differs: " +
                $"incremental {incremental.Texels[texel]}, full rebuild {expected.Texels[texel]}.");
        Require(incremental.Coarse.AsSpan().SequenceEqual(expected.Coarse), "Coarse texels differ from a full rebuild.");
    }

    [LumenFact]
    public static void RemovingCentralRockMatchesFullRebuildIncludingDeepWallTexels()
    {
        FakeThing[] rocks = (from z in Enumerable.Range(1, 9) from x in Enumerable.Range(1, 9)
            select new FakeThing(new CellBox(x, z, x, z), new MaskPlacement(x + 0.5f, z + 0.5f, 1f, 1f), Solid: true)).ToArray();
        var map = new FakeMap(rocks);
        var incremental = new OcclusionBuilder(12, 12);
        var ledger = new StampLedger<FakeThing>();
        ledger.BuildAll(rocks, map, incremental);
        incremental.RecomputeDerived(new CellBox(0, 0, 11, 11));
        map.Alive.Remove(rocks.Single(r => r.Occupied.MinX == 5 && r.Occupied.MinZ == 5));
        ledger.Update([(5, 5)], map, incremental);
        var full = new OcclusionBuilder(12, 12);
        new StampLedger<FakeThing>().BuildAll(map.Alive, map, full);
        OcclusionMap actual = incremental.Publish(), expected = Publish(full);
        int b = (22 * actual.TexelWidth + 28) * 4 + 2;
        Equal(expected.Texels[b], actual.Texels[b]);
        Require(actual.Texels.AsSpan().SequenceEqual(expected.Texels) && actual.Coarse.AsSpan().SequenceEqual(expected.Coarse),
            "Removing a rock from the middle of a mountain must reproduce every byte of a full rebuild.");
    }

    [LumenFact]
    public static void SpawningLinkedNeighbourRestampsAdjacentLinkedArt()
    {
        // Like Graphic_Linked, a linked cover thing's art (sandbags) depends on its neighbour: alone it covers its west half,
        // joined the whole cell.
        var west = new FakeThing(new CellBox(5, 5, 5, 5), new MaskPlacement(5.5f, 5.5f, 1f, 1f), Solid: true, Linked: true);
        var east = new FakeThing(new CellBox(6, 5, 6, 5), new MaskPlacement(6.5f, 5.5f, 1f, 1f), Solid: true, Linked: true);
        var map = new FakeMap(west);
        var builder = new OcclusionBuilder(12, 12);
        var ledger = new StampLedger<FakeThing>();
        ledger.BuildAll([west], map, builder);
        builder.RecomputeDerived(new CellBox(0, 0, 11, 11));
        map.Alive.Add(east);
        // Spawning a building dirties its cell with the Buildings flag, so vanilla passes regenAdjacentCells = true.
        ledger.Update(MeshDirtyCells.Of(6, 5, regenAdjacentCells: true, 12, 12).Select(i => (i % 12, i / 12)).ToList(), map, builder);
        OcclusionMap incremental = builder.Publish();

        var full = new OcclusionBuilder(12, 12);
        new StampLedger<FakeThing>().BuildAll(map.Alive, map, full);
        OcclusionMap expected = Publish(full);
        int texel = incremental.Texels.AsSpan().CommonPrefixLength(expected.Texels);
        if (texel < expected.Texels.Length)
            throw new InvalidOperationException($"Texel ({texel / 4 % expected.TexelWidth}, {texel / 4 / expected.TexelWidth}) channel {texel % 4} differs: " +
                $"incremental {incremental.Texels[texel]}, full rebuild {expected.Texels[texel]}.");
        Require(incremental.Coarse.AsSpan().SequenceEqual(expected.Coarse), "Coarse texels differ from a full rebuild.");
    }

    private sealed record FakeThing(CellBox Occupied, MaskPlacement Art, bool Solid, bool Linked = false);

    private sealed class FakeMap(params FakeThing[] things) : IOcclusionSource<FakeThing>
    {
        internal readonly HashSet<FakeThing> Alive = new(things);
        public IEnumerable<FakeThing> OccupantsOf(CellBox box) => Alive.Where(thing => thing.Occupied.Overlaps(box));
        public bool IsAlive(FakeThing thing) => Alive.Contains(thing);
        public CellBox? Stamp(FakeThing thing, OcclusionBuilder builder) =>
            thing.Linked ? builder.Cover(Alive.Any(other => other.Linked && other.Occupied.MinZ == thing.Occupied.MinZ &&
                    other.Occupied.MinX == thing.Occupied.MaxX + 1) ? Full : WestHalf, thing.Art, 0.5f)
            : thing.Solid ? builder.Blocker(thing.Occupied, 1f, null) : builder.Cover(Full, thing.Art, 0.5f);
    }

    private static OcclusionMap Publish(OcclusionBuilder builder)
    {
        builder.RecomputeDerived(new CellBox(0, 0, 11, 11));
        return builder.Publish();
    }

    private static OcclusionFrame Room()
    {
        var builder = new OcclusionBuilder(12, 12);
        foreach (CellBox rock in new[] { new CellBox(0, 0, 11, 4), new CellBox(0, 9, 11, 11), new CellBox(0, 5, 4, 8), new CellBox(9, 5, 11, 8) }) builder.Wall(rock);
        return Frame(builder);
    }

    // The centres of the 16 texels of cell (x, z).
    private static IEnumerable<Vector2> CellTexels(int x, int z) =>
        from j in Enumerable.Range(0, 4) from i in Enumerable.Range(0, 4) select new Vector2(x + (i + 0.5f) / 4f, z + (j + 0.5f) / 4f);

    private static OcclusionFrame Frame(OcclusionBuilder builder) => new(Publish(builder), new DoorStates(12, 12, new byte[12 * 12 * 4]));
    private static float Transmission(OcclusionFrame frame, Vector2 light, Vector2 target) => LightTransport.Visibility(frame, light, target, out float cover) * cover;
    private static void Equal(float expected, float actual) => Require(expected == actual, $"Expected {expected}; got {actual}.");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
