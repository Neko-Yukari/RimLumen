using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLumen.Core.Lighting.Occlusion;

// Host boundary of the occlusion map (design 1.2-1.4): scans a map's things into an OcclusionBuilder, keeps it current
// from MapDrawer.MapMeshDirty and per-frame door polling, and publishes snapshots. Everything the offline tests cannot
// reach (Verse things, graphics, textures, Harmony) stays in this file and OcclusionMasks.
internal static class OcclusionField
{
    private sealed class Field : IOcclusionSource<Thing>
    {
        internal readonly Map Map;
        internal readonly OcclusionBuilder Builder;
        internal readonly HashSet<Building_Door> Doors = new();
        internal readonly HashSet<int> Dirty = new();
        private readonly bool sunDisabled;
        private readonly StampLedger<Thing> ledger = new();
        internal OcclusionMap Snapshot;
        internal DoorStates DoorSnapshot;
        private byte[]? dirtyDoorCells;

        internal Field(Map map)
        {
            Map = map;
            Builder = new OcclusionBuilder(map.Size.x, map.Size.z);
            Snapshot = null!;
            DoorSnapshot = new DoorStates(map.Size.x, map.Size.z, new byte[map.Size.x * map.Size.z * 4]);
            sunDisabled = map.info.disableSunShadows || map.Biome.disableShadows;
            ledger.BuildAll(map.listerThings.AllThings, this, Builder);
            Builder.RecomputeDerived(new CellBox(0, 0, map.Size.x - 1, map.Size.z - 1));
            Snapshot = Builder.Publish();
            UpdateDoors();
        }

        public IEnumerable<Thing> OccupantsOf(CellBox box)
        {
            for (int z = box.MinZ; z <= box.MaxZ; z++)
                for (int x = box.MinX; x <= box.MaxX; x++)
                    foreach (Thing thing in Map.thingGrid.ThingsListAt(new IntVec3(x, 0, z))) yield return thing;
        }

        public bool IsAlive(Thing thing) => thing.Spawned && thing.Map == Map;

        public CellBox? Stamp(Thing thing, OcclusionBuilder builder) => OcclusionField.Stamp(thing, builder, Doors, sunDisabled);

        internal OcclusionFrame Capture()
        {
            bool changed = false;
            if (Dirty.Count > 0)
            {
                var cells = new List<(int X, int Z)>(Dirty.Count);
                foreach (int index in Dirty)
                {
                    IntVec3 cell = Map.cellIndices.IndexToCell(index);
                    cells.Add((cell.x, cell.z));
                }
                Dirty.Clear();
                // Every spawned door is rewritten into a cleared copy, so a despawned or moved door leaves no stale cell.
                Doors.RemoveWhere(door => !IsAlive(door));
                dirtyDoorCells = new byte[DoorSnapshot.Cells.Length];
                changed = ledger.Update(cells, this, Builder);
            }
            if (changed) Snapshot = Builder.Publish();
            UpdateDoors();
            return new OcclusionFrame(Snapshot, DoorSnapshot);
        }

        private void UpdateDoors()
        {
            byte[]? cells = dirtyDoorCells;
            dirtyDoorCells = null;
            foreach (Building_Door door in Doors)
            {
                bool multi = door is Building_MultiTileDoor;
                float p = MathF.Round(openPct(door) * 16f) / 16f;
                float gap = multi && stuckOpen(door) || !multi && !canDrawMovers(door) ? 0.5f : (multi ? 0.35f : 0.45f) * p;
                byte encoded = (byte)MathF.Round(gap * 510f);
                bool axisZ = door.Rotation.IsHorizontal;
                CellRect rect = door.OccupiedRect();
                int width = axisZ ? rect.maxZ - rect.minZ + 1 : rect.maxX - rect.minX + 1;
                int first = (rect.minZ * Map.Size.x + rect.minX) * 4;
                if (cells is null && DoorSnapshot.Cells[first] == encoded && DoorSnapshot.Cells[first + 2] == width) continue;
                cells ??= (byte[])DoorSnapshot.Cells.Clone();
                for (int z = rect.minZ; z <= rect.maxZ; z++)
                    for (int x = rect.minX; x <= rect.maxX; x++)
                    {
                        int i = (z * Map.Size.x + x) * 4;
                        cells[i] = encoded;
                        cells[i + 1] = (byte)(axisZ ? z - rect.minZ : x - rect.minX);
                        cells[i + 2] = (byte)width;
                    }
            }
            if (cells is not null && !cells.AsSpan().SequenceEqual(DoorSnapshot.Cells)) DoorSnapshot = new DoorStates(Map.Size.x, Map.Size.z, cells);
        }
    }

    private static readonly Dictionary<int, Field> Fields = new();
    private static readonly Dictionary<MapDrawer, Field> Drawers = new();
    private static readonly HashSet<ThingDef> Warned = new();
    private static Func<Building_Door, float> openPct = null!;
    private static Func<Building_Door, bool> stuckOpen = null!;
    private static Func<Building_Door, bool> canDrawMovers = null!;

    // Requires: main thread, install time (RimLumenMapHooks.Arm), before any capture.
    // Ensures: patches MapDrawer.MapMeshDirty(IntVec3, ulong, bool, bool) with a postfix that marks the cell (and, with
    //          regenAdjacentCells, its 8 neighbours: linked art follows them) dirty when the flags hold Things or
    //          Buildings (MapMeshFlagDefOf); binds Building_Door.OpenPct's getter and
    //          Graphic_Linked.LinkedDrawMatFrom by exact signature. Throws when any member is missing or changed.
    internal static void Arm()
    {
        MethodInfo dirty = typeof(MapDrawer).GetMethod(nameof(MapDrawer.MapMeshDirty), [typeof(IntVec3), typeof(ulong), typeof(bool), typeof(bool)])
            ?? throw new MissingMethodException(typeof(MapDrawer).FullName, nameof(MapDrawer.MapMeshDirty));
        openPct = Getter<float>("OpenPct");
        stuckOpen = Getter<bool>("StuckOpen");
        canDrawMovers = Getter<bool>("CanDrawMovers");
        OcclusionMasks.Arm();
        new Harmony("NekoYukari.RimLumen.Occlusion").Patch(dirty, postfix: new HarmonyMethod(typeof(OcclusionField), nameof(MapMeshDirty)));
    }

    private static Func<Building_Door, T> Getter<T>(string name)
    {
        MethodInfo getter = typeof(Building_Door).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetMethod
            ?? throw new MissingMemberException(typeof(Building_Door).FullName, name);
        if (getter.ReturnType != typeof(T) || getter.GetParameters().Length != 0) throw new InvalidOperationException($"Building_Door.{name} signature changed.");
        return getter.CreateDelegate<Func<Building_Door, T>>();
    }

    private static void MapMeshDirty(MapDrawer __instance, IntVec3 loc, ulong dirtyFlags, bool regenAdjacentCells)
    {
        if (!Drawers.TryGetValue(__instance, out Field? field) || !loc.InBounds(field.Map)) return;
        if ((dirtyFlags & ((ulong)MapMeshFlagDefOf.Things | (ulong)MapMeshFlagDefOf.Buildings)) != 0)
            field.Dirty.UnionWith(MeshDirtyCells.Of(loc.x, loc.z, regenAdjacentCells, field.Map.Size.x, field.Map.Size.z));
    }

    // Requires: main thread, once per capture (LightPoolRenderer.AppendDraws).
    // Ensures: the map's frame: first call builds the whole map; later calls restamp overlapping ledger entries
    //          and recompute derived data, publishing a new OcclusionMap only when something changed, and republish
    //          DoorStates only when a door's quantised OpenPct changed. Unchanged frames return the same instances,
    //          so callers can skip uploads by reference.
    internal static OcclusionFrame Capture(Map map)
    {
        if (!Fields.TryGetValue(map.uniqueID, out Field? field))
        {
            field = new Field(map);
            Fields.Add(map.uniqueID, field);
            Drawers.Add(map.mapDrawer, field);
        }
        return field.Capture();
    }

    // Requires: main thread. Ensures: drops the map's builder, dirty set and registered doors.
    internal static void Forget(int mapId)
    {
        if (Fields.Remove(mapId, out Field? field)) Drawers.Remove(field.Map.mapDrawer);
    }

    // IOcclusionSource.Stamp of Field. The sprite is the footprint of every channel (OcclusionBuilder.StampOccluder);
    // the def only picks the values. Height is vanilla's: staticSunShadowHeight for edifices, which vanilla prints only
    // on maps with sun shadows (SectionLayer_SunShadows, Section.cs:46-50), else the shadowData height, which
    // Printer_Shadow prints on every map (Printer_Shadow.cs:26-28) except for RealtimeOnly things it never prints into
    // the map mesh (ACS:83086), whose shadow is the dynamic SunShadowFade.
    private static CellBox? Stamp(Thing thing, OcclusionBuilder builder, HashSet<Building_Door> doors, bool sunDisabled)
    {
        ThingDef def = thing.def;
        if (def.category != ThingCategory.Building && def.category != ThingCategory.Plant && def.category != ThingCategory.Item) return null;
        bool blocker = def.blockLight;
        float staticHeight = sunDisabled ? 0f : def.staticSunShadowHeight;
        ShadowData? shadow = def.drawerType == DrawerType.RealtimeOnly ? null : def.graphicData?.shadowData;
        float height = 0f;
        bool fade = false;
        if (staticHeight > 0f && def.IsEdifice()) height = staticHeight;
        else if (shadow is not null)
        {
            float scale = thing is Plant plant ? plant.def.plant.visualSizeRange.LerpThroughRange(plant.Growth) : 1f;
            // Printer_Shadow.PrintShadow encodes the extrusion as vertex alpha (byte)min(255 * y, 255), so vanilla caps it at 1.
            height = MathF.Floor(MathF.Min(255f * shadow.volume.y * scale, 255f)) / 255f;
            fade = true;
        }
        float cover = !blocker && def.Fillage != FillCategory.None ? def.fillPercent : 0f;
        if (!blocker && height == 0f && cover == 0f) return null;
        if (thing is Building_Door door) doors.Add(door);
        if (!OcclusionMasks.TryGet(thing, out SpritePiece[] sprite))
        {
            if (!blocker && height == 0f) return null;
            if (Warned.Add(def)) Log.Warning($"[RimLumen] Cannot read occlusion texture for {def.defName}; using solid footprint.");
            CellRect rect = thing.OccupiedRect();
            sprite = [new SpritePiece(AlphaMask.Opaque(rect.Width * OcclusionLayout.TexelsPerCell, rect.Height * OcclusionLayout.TexelsPerCell), new MaskPlacement((rect.minX + rect.maxX + 1) * 0.5f,
                (rect.minZ + rect.maxZ + 1) * 0.5f, rect.maxX - rect.minX + 1, rect.maxZ - rect.minZ + 1))];
        }
        DoorAxis? axis = thing is Building_Door ? (thing.Rotation.IsHorizontal ? DoorAxis.Z : DoorAxis.X) : null;
        return builder.StampOccluder(sprite, new Occluder(blocker, axis, height, fade, cover));
    }
}
