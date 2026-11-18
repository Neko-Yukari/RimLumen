using System;
using System.Collections.Generic;

namespace RimLumen.Core.Lighting.Occlusion;

// What OcclusionField needs from the map to apply a dirty update; Verse implements it, the offline tests fake it.
internal interface IOcclusionSource<T> where T : notnull
{
    // Ensures: every thing whose occupied cells intersect box (thingGrid order irrelevant).
    IEnumerable<T> OccupantsOf(CellBox box);

    // Ensures: true while the thing is still spawned on this map.
    bool IsAlive(T thing);

    // Ensures: stamps the thing into builder (R, G, A as design 1.2) and returns the cell box covering every texel it
    //          touched (mask, solid footprint, sun footprint and shadowData box together); null when it stamped nothing.
    CellBox? Stamp(T thing, OcclusionBuilder builder);
}

// Per-map stamped-bounds ledger (design 1.4, lead 2026-11-19): thing -> the cell box it was last stamped into. Pure
// logic; a dictionary plus one overlap pass over the cleared boxes, no spatial index.
internal sealed class StampLedger<T> where T : notnull
{
    private readonly Dictionary<T, CellBox> boxes = new();

    // Requires: builder is empty and matches the map. Ensures: every thing stamped, its box recorded.
    internal void BuildAll(IEnumerable<T> things, IOcclusionSource<T> source, OcclusionBuilder builder)
    {
        foreach (T thing in things)
            if (source.Stamp(thing, builder) is { } box) boxes[thing] = box;
    }

    // Requires: dirty = cells marked by MapMeshDirty since the last update (non-empty).
    // Ensures: 1. changed = occupants of dirty cells plus ledger entries that are no longer alive; each changed entry's
    //             old box is cleared (OcclusionBuilder.ClearCells) and removed from the ledger.
    //          2. every still-alive changed thing, and every remaining entry whose box intersects a cleared box, is
    //             restamped and its new box recorded; neighbours are not cleared again, stamps max-combine.
    //          3. RecomputeDerived runs over the union of cleared and restamped boxes.
    //          The texels afterwards equal a full rebuild of the same things. Returns false when nothing changed.
    internal bool Update(IReadOnlyCollection<(int X, int Z)> dirty, IOcclusionSource<T> source, OcclusionBuilder builder)
    {
        var changed = new HashSet<T>();
        foreach ((int x, int z) in dirty) changed.UnionWith(source.OccupantsOf(new CellBox(x, z, x, z)));
        foreach (T thing in boxes.Keys)
            if (!source.IsAlive(thing)) changed.Add(thing);
        var cleared = new List<CellBox>();
        CellBox? touched = null;
        foreach (T thing in changed)
        {
            if (!boxes.Remove(thing, out CellBox old)) continue;
            builder.ClearCells(old);
            cleared.Add(old);
            touched = touched.Union(old);
        }
        var restamp = new List<T>();
        foreach (T thing in changed)
            if (source.IsAlive(thing)) restamp.Add(thing);
        foreach ((T thing, CellBox box) in boxes)
            foreach (CellBox old in cleared)
                if (old.Overlaps(box)) { restamp.Add(thing); break; }
        foreach (T thing in restamp)
        {
            if (source.Stamp(thing, builder) is not { } box) continue;
            boxes[thing] = box;
            touched = touched.Union(box);
        }
        if (touched is not { } area) return false;
        builder.RecomputeDerived(area);
        return true;
    }
}

// The cells one MapDrawer.MapMeshDirty call dirties (ACS:79184-79202), as row-major indices of a width x height map.
internal static class MeshDirtyCells
{
    // Requires: (x, z) inside the map.
    internal static IEnumerable<int> Of(int x, int z, bool regenAdjacentCells, int width, int height)
    {
        yield return z * width + x;
        if (!regenAdjacentCells) yield break;
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                if ((dx != 0 || dz != 0) && (uint)(x + dx) < (uint)width && (uint)(z + dz) < (uint)height)
                    yield return (z + dz) * width + x + dx;
    }
}

internal static class CellBoxes
{
    internal static bool Overlaps(this CellBox a, CellBox b) =>
        a.MinX <= b.MaxX && a.MaxX >= b.MinX && a.MinZ <= b.MaxZ && a.MaxZ >= b.MinZ;

    internal static CellBox Union(this CellBox a, CellBox b) =>
        new(Math.Min(a.MinX, b.MinX), Math.Min(a.MinZ, b.MinZ), Math.Max(a.MaxX, b.MaxX), Math.Max(a.MaxZ, b.MaxZ));

    internal static CellBox? Union(this CellBox? a, CellBox? b) => a is null ? b : b is null ? a : a.Value.Union(b.Value);
}
