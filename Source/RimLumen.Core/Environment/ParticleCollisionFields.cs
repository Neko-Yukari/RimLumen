using System.Collections.Generic;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;
using Verse;

namespace RimLumen.Core.ParticleWorld;

// Lazy per-map grid. Events mark cells dirty; a capture publishes a new immutable array only after a change,
// so an in-flight GPU frame never observes a main-thread edit. Unused maps allocate no collision grid.
internal static class ParticleCollisionFields
{
    private sealed class Field
    {
        internal readonly Map Map;
        internal ParticleMapGrid Grid;
        private readonly HashSet<int> dirty = new();

        internal Field(Map map)
        {
            Map = map;
            uint[] cells = new uint[map.cellIndices.NumGridCells];
            for (int i = 0; i < cells.Length; i++) cells[i] = BitsAt(map, map.cellIndices.IndexToCell(i));
            Grid = new(map.uniqueID, map.Size.x, map.Size.z, cells);
            map.events.PathCostRecalculate += Changed;
            map.events.RoofChanged += Changed;
        }

        private void Changed(IntVec3 cell) => dirty.Add(Map.cellIndices.CellToIndex(cell));
        internal ParticleMapGrid Capture()
        {
            if (dirty.Count == 0) return Grid;
            uint[] cells = (uint[])Grid.Cells.Clone();
            foreach (int i in dirty) cells[i] = BitsAt(Map, Map.cellIndices.IndexToCell(i));
            dirty.Clear();
            return Grid = Grid with { Cells = cells };
        }

        internal void Dispose()
        {
            Map.events.PathCostRecalculate -= Changed;
            Map.events.RoofChanged -= Changed;
        }
    }

    private static readonly Dictionary<int, Field> Fields = new();

    internal static ParticleMapGrid? Capture(int mapId, CollisionSpec spec)
    {
        if (mapId < 0 || (spec.Mode == LumenCollisionMode.None && !spec.KillUnderRoof)) return null;
        if (Fields.TryGetValue(mapId, out Field? field)) return field.Capture();
        foreach (Map map in Find.Maps)
            if (map.uniqueID == mapId) return Capture(map);
        return null; // A removed map has no collidable geometry while its effects drain.
    }

    private static ParticleMapGrid Capture(Map map)
    {
        if (Fields.TryGetValue(map.uniqueID, out Field? field)) return field.Capture();
        field = new Field(map);
        Fields.Add(map.uniqueID, field);
        return field.Grid;
    }

    internal static void Forget(int mapId)
    {
        if (Fields.Remove(mapId, out Field? field)) field.Dispose();
        ParticleEnvironment.ForgetMap(mapId);
    }

    private static uint BitsAt(Map map, IntVec3 cell)
    {
        // GenGrid.Impassable (Assembly-CSharp:211520-211530): any ThingDef.passability == Impassable.
        // Unlike pawn path costs, water and terrain are not walls. Height [0,2] is RimLumen's visual model.
        return (cell.Impassable(map) ? 1u : 0u) | (map.roofGrid.Roofed(cell) ? 2u : 0u);
    }
}
