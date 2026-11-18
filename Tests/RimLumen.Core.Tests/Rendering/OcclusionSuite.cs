using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RimLumen.Core.Lighting.Occlusion;

namespace RimLumen.Core.Tests.Rendering;

[LumenTestSuite]
public static class OcclusionSuite
{
    private static readonly AlphaMask Full = new(1, 1, [255]);
    private static readonly AlphaMask WestHalf = new(2, 1, [255, 0]);

    [LumenFact]
    public static void TwoCellWallHasDarkCore()
    {
        var builder = new OcclusionBuilder(12, 12);
        builder.StampSolidFootprint(new CellBox(5, 1, 6, 10), 1f);
        OcclusionFrame frame = Frame(builder);
        Equal(0f, LightTransport.Visibility(frame, new(3.5f, 5.5f), new(6.0f, 5.5f)));
    }

    [LumenFact]
    public static void OneCellCoverHasExpectedTransmittance()
    {
        var builder = new OcclusionBuilder(12, 12);
        builder.StampCover(Full, new MaskPlacement(5.5f, 5.5f, 1f, 1f), 0.5f);
        OcclusionFrame frame = Frame(builder);
        float value = LightTransport.Visibility(frame, new(3.5f, 5.5f), new(7.5f, 5.5f));
        float shifted = LightTransport.Visibility(frame, new(3.8f, 5.5f), new(7.1f, 5.5f));
        Require(MathF.Abs(value - (1f - 128f / 255f)) < 0.002f && MathF.Abs(value - shifted) < 0.002f,
            $"Expected one-cell cover transmittance independent of endpoints; got {value}, {shifted}.");
    }

    [LumenFact]
    public static void WallCornerHasMonotonicOneTexelEdge()
    {
        var builder = new OcclusionBuilder(12, 12);
        builder.StampSolidFootprint(new CellBox(5, 4, 5, 4), 1f);
        OcclusionFrame frame = Frame(builder);
        float inside = LightTransport.Visibility(frame, new(3.5f, 4.875f), new(7.5f, 4.875f));
        float edge = LightTransport.Visibility(frame, new(3.5f, 5f), new(7.5f, 5f));
        float outside = LightTransport.Visibility(frame, new(3.5f, 5.125f), new(7.5f, 5.125f));
        Require(inside == 0f && edge > inside && edge < outside && outside == 1f, $"Expected smooth wall edge; got {inside}, {edge}, {outside}.");
    }

    [LumenFact]
    public static void DoorOpeningAffectsNeighbourCellGather()
    {
        var builder = new OcclusionBuilder(12, 12);
        builder.StampBlocker(Full, new MaskPlacement(5.5f, 5.5f, 1f, 1f), 1f, DoorAxis.Z);
        OcclusionMap map = Publish(builder);
        var closed = new byte[12 * 12 * 4];
        int i = (5 * 12 + 5) * 4;
        closed[i + 2] = 1;
        byte[] open = (byte[])closed.Clone();
        open[i] = 255;
        float dark = LightTransport.Visibility(new OcclusionFrame(map, new DoorStates(12, 12, closed)), new(3.5f, 4.95f), new(7.5f, 4.95f));
        float clear = LightTransport.Visibility(new OcclusionFrame(map, new DoorStates(12, 12, open)), new(3.5f, 4.95f), new(7.5f, 4.95f));
        Require(dark < 1f && clear == 1f, $"Door-edge gather should respect state; got closed={dark}, open={clear}.");
    }

    [LumenFact]
    public static void DiagonalConnectedWallTargetsShareVisibility()
    {
        var builder = new OcclusionBuilder(12, 12);
        builder.StampSolidFootprint(new CellBox(5, 4, 5, 4), 1f);
        builder.StampSolidFootprint(new CellBox(6, 5, 6, 5), 1f);
        OcclusionFrame frame = Frame(builder);
        float a = LightTransport.Visibility(frame, new(3.5f, 2.5f), new(5.5f, 4.5f));
        float b = LightTransport.Visibility(frame, new(3.5f, 2.5f), new(6.5f, 5.5f));
        Equal(a, b);
    }

    [LumenFact]
    public static void StampsTakeMaximumAndClearResets()
    {
        var builder = new OcclusionBuilder(12, 12);
        var at = new MaskPlacement(5.5f, 5.5f, 1f, 1f);
        builder.StampCover(Full, at, 0.25f);
        builder.StampCover(Full, at, 0.5f);
        builder.StampCover(Full, at, 0.25f);
        OcclusionMap before = Publish(builder);
        int i = ((5 * 4 + 2) * 48 + 5 * 4 + 2) * 4;
        Require(before.Texels[i] == 128, $"Expected maximum opacity 128, got {before.Texels[i]}.");
        builder.ClearCells(new CellBox(5, 5, 5, 5));
        OcclusionMap after = Publish(builder);
        Require(after.Texels[i] == 0 && before.Texels[i] == 128, "Clear must reset the new snapshot without changing the old one.");
    }

    [LumenFact]
    public static void SunFootprintRaisesOnlyGreenAcrossOccupiedCells()
    {
        var builder = new OcclusionBuilder(3, 1);
        var footprint = new CellBox(0, 0, 1, 0);
        builder.StampCover(Full, new MaskPlacement(0.5f, 0.5f, 1f, 1f), 0.5f);
        builder.StampSolidFootprint(new CellBox(1, 0, 1, 0), 1.5f);
        builder.StampSunBox(new MaskPlacement(1.5f, 0.5f, 1f, 1f), 0.5f);
        builder.RecomputeDerived(footprint);
        OcclusionMap before = builder.Publish();

        builder.StampSunFootprint(footprint, 1f);
        OcclusionMap after = builder.Publish();
        for (int i = 0; i < after.Texels.Length; i += 4)
        {
            int x = i / 4 % after.TexelWidth;
            byte expectedGreen = x < 4 ? (byte)128 : x < 8 ? (byte)191 : (byte)0;
            Equal(expectedGreen, after.Texels[i + 1]);
            Equal(before.Texels[i], after.Texels[i]);
            Equal(before.Texels[i + 2], after.Texels[i + 2]);
            Equal(before.Texels[i + 3], after.Texels[i + 3]);
        }
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
        // Like Graphic_Linked, a linked wall's art depends on its neighbour: alone it covers its west half, joined the whole cell.
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
            thing.Linked ? builder.StampBlocker(Alive.Any(other => other.Linked && other.Occupied.MinZ == thing.Occupied.MinZ &&
                    other.Occupied.MinX == thing.Occupied.MaxX + 1) ? Full : WestHalf, thing.Art, 1f, null)
            : thing.Solid ? builder.StampBlocker(Full, thing.Art, 1f, null) : builder.StampCover(Full, thing.Art, 0.5f);
    }

    private static OcclusionMap Publish(OcclusionBuilder builder)
    {
        builder.RecomputeDerived(new CellBox(0, 0, 11, 11));
        return builder.Publish();
    }

    private static OcclusionFrame Frame(OcclusionBuilder builder) => new(Publish(builder), new DoorStates(12, 12, new byte[12 * 12 * 4]));
    private static void Equal(float expected, float actual) => Require(expected == actual, $"Expected {expected}; got {actual}.");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
