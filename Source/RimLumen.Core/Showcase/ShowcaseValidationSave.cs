using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using Verse;
using Waylith;
using Waylith.UnityCompat;

namespace RimLumen.Core.Showcase;

internal static class ShowcaseValidationSave
{
    private const string ArgumentPrefix = "-rimlumen-validation-save=";
    private const int SettleFrames = 90;

    internal static bool TryStart(IReadOnlyList<string> args)
    {
        string[] requests = args.Where(arg => arg.StartsWith(ArgumentPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (requests.Length == 0) return false;

        try
        {
            string saveName = requests[0].Substring(ArgumentPrefix.Length);
            if (requests.Length != 1 || string.IsNullOrWhiteSpace(saveName) || !GenText.IsValidFilename(saveName) ||
                saveName.EndsWith('.') || saveName != saveName.Trim())
                throw new ArgumentException("Validation save requires one valid save name (1-40 characters, no path or trailing dot/space).");
            if (args.Any(arg => arg.StartsWith("-rimlumen-showcase-capture=", StringComparison.OrdinalIgnoreCase) ||
                                arg.StartsWith("-rimlumen-showcase-bench=", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Validation save cannot be combined with capture or benchmark.");

            EngineApplication app = EngineApplication.Instance;
            void UpdateValidationSave()
            {
                if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || LongEventHandler.AnyEventNowOrWaiting)
                    return;
                app.OnUpdate -= UpdateValidationSave;
                try
                {
                    Map map = Find.CurrentMap;
                    BuildScene(map);
                    Current.CameraDriver.StartCoroutine(SaveAndShutdown(saveName));
                }
                catch (Exception ex)
                {
                    Log.Error("[RimLumen] Validation scene setup failed: " + ex);
                    Shutdown(1);
                }
            }
            app.OnUpdate += UpdateValidationSave;
        }
        catch (Exception ex)
        {
            Log.Error("[RimLumen] Validation save arguments failed: " + ex);
            Shutdown(1);
        }
        return true;
    }

    private static void BuildScene(Map map)
    {
        IntVec3 center = map.Center;
        IntVec3 Cell(int x, int z) => new(center.x + x, 0, center.z + z);
        CellRect Rect(int x1, int z1, int x2, int z2) => CellRect.FromLimits(Cell(x1, z1), Cell(x2, z2));
        CellRect arena = Rect(-28, -18, 27, 17);
        if (!arena.Cells.All(cell => cell.InBounds(map)))
            throw new InvalidOperationException("Validation scene needs a clearable 56x36 area at map centre.");

        IntVec3 pawnCell = Cell(0, -17);
        foreach (IntVec3 cell in arena) map.roofGrid.SetRoof(cell, null);
        foreach (IntVec3 cell in arena)
        {
            foreach (Thing thing in cell.GetThingList(map).ToArray())
            {
                if (thing is Pawn pawn)
                {
                    pawn.Position = pawnCell;
                    pawn.Notify_Teleported(endCurrentJob: true, resetTweenedPos: true);
                }
                else if (thing.Spawned && !thing.Destroyed && thing.def.destroyable)
                {
                    thing.Destroy(DestroyMode.Vanish);
                }
            }
            map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
            map.fogGrid.Unfog(cell);
        }
        map.roofCollapseBufferResolver.CollapseRoofsMarkedToCollapse();
        if (!pawnCell.Standable(map))
            throw new InvalidOperationException("Validation pawn holding cell is obstructed.");

        SpawnFueled(map, ThingDefOf.TorchLamp, Cell(-24, -10));
        SpawnFueled(map, ThingDefOf.TorchLamp, Cell(-19, -10));
        SpawnFueled(map, ThingDefOf.Campfire, Cell(-22, -6));
        Report("open ground: TorchLamp x2, Campfire x1", Rect(-25, -13, -17, -4));

        CellRect fogRoom = Rect(-10, -14, -2, -6);
        BuildRoom(map, fogRoom);
        SpawnFueled(map, ThingDefOf.TorchLamp, Cell(-8, -10));
        SpawnFueled(map, ThingDefOf.TorchLamp, Cell(-4, -10));

        CellRect mountain = Rect(3, -14, 11, -6);
        CellRect pocket = Rect(6, -11, 8, -9);
        foreach (IntVec3 cell in mountain)
        {
            if (!pocket.Contains(cell)) Spawn(map, ThingDefOf.Granite, cell);
            map.roofGrid.SetRoof(cell, RoofDefOf.RoofRockThick);
        }
        SpawnFueled(map, ThingDefOf.TorchLamp, Cell(7, -10));
        Report("mountain: Granite + RoofRockThick", mountain);
        Report("mined cave: TorchLamp x1", pocket);

        CellRect outdoorRoom = Rect(-24, 3, -17, 10);
        BuildRoom(map, outdoorRoom);
        CellRect openRoof = Rect(-23, 4, -22, 9);
        foreach (IntVec3 cell in openRoof) map.roofGrid.SetRoof(cell, null);
        SpawnFueled(map, ThingDefOf.TorchLamp, Cell(-19, 7));
        Report("outdoor-temperature room: TorchLamp x1, 6x6 interior", outdoorRoom);
        Report("open roof: 12/36 interior cells", openRoof);

        CellRect indoorRoom = Rect(-10, 3, -4, 9);
        BuildRoom(map, indoorRoom);
        SpawnFueled(map, ThingDefOf.TorchLamp, Cell(-7, 6));
        Report("indoor-temperature room: TorchLamp x1, 5x5 interior", indoorRoom);

        Spawn(map, DefDatabase<ThingDef>.GetNamed("RimLumen_FireworkLauncher"), Cell(4, 6));
        Spawn(map, DefDatabase<ThingDef>.GetNamed("RimLumen_ParticleFountain"), Cell(12, 6));
        Report("RimLumen_FireworkLauncher (auto-launch on)", Rect(4, 6, 4, 6));
        Report("RimLumen_ParticleFountain (active)", Rect(12, 6, 12, 6));

        map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
        Room outdoor = Cell(-19, 7).GetRoom(map);
        Room indoor = Cell(-7, 6).GetRoom(map);
        Room cave = Cell(7, -10).GetRoom(map);
        if (outdoor == null || outdoor.CellCount != 36 || outdoor.OpenRoofCount != 12 || !outdoor.UsesOutdoorTemperature ||
            indoor == null || indoor.CellCount != 25 || indoor.OpenRoofCount != 0 || indoor.UsesOutdoorTemperature ||
            cave == null || cave.CellCount != 9 || cave.OpenRoofCount != 0 || cave.TouchesMapEdge ||
            Cell(3, -14).GetRoom(map) != null)
            throw new InvalidOperationException("Validation scene room/roof contracts were not established.");

        // Refog last: subsequent wall spawning/removal can trigger vanilla unfogging.
        map.fogGrid.Refog(fogRoom.ContractedBy(1));
        if (!fogRoom.ContractedBy(1).Cells.All(cell => cell.Fogged(map)))
            throw new InvalidOperationException("Validation fog room interior was not fully refogged.");
        Report("fogged built room: TorchLamp x2, 7x7 interior", fogRoom);

        float delta = 22f / 24f - GenLocalDate.DayPercent(map);
        if (delta < 0f) delta += 1f;
        Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + (int)MathF.Round(delta * GenDate.TicksPerDay));
        map.skyManager.SkyManagerUpdate();
        map.mapDrawer.WholeMapChanged((ulong)MapMeshFlagDefOf.GroundGlow | (ulong)MapMeshFlagDefOf.Things | (ulong)MapMeshFlagDefOf.Terrain);
        Current.CameraDriver.JumpToCurrentMapLoc(center.ToVector3Shifted());
        Current.CameraDriver.SetRootSize(32f);
        Log.Message($"[RimLumen] Validation scene ready: map={map.uniqueID}, center={center}, arena={arena}, localHour={GenLocalDate.DayPercent(map) * 24f:F3}, paused={Find.TickManager.Paused}. Showcase stage omitted: its effect handles are not saved.");
    }

    private static void BuildRoom(Map map, CellRect rect)
    {
        foreach (IntVec3 cell in rect)
        {
            if (cell.x == rect.minX || cell.x == rect.maxX || cell.z == rect.minZ || cell.z == rect.maxZ)
                Spawn(map, ThingDefOf.Wall, cell, ThingDefOf.BlocksGranite);
            map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
        }
    }

    private static Thing Spawn(Map map, ThingDef def, IntVec3 cell, ThingDef? stuff = null)
    {
        Thing thing = ThingMaker.MakeThing(def, stuff);
        if (def.CanHaveFaction) thing.SetFaction(Faction.OfPlayer);
        GenSpawn.Spawn(thing, cell, map);
        if (!thing.Spawned || thing.Position != cell)
            throw new InvalidOperationException($"Could not spawn {def.defName} at {cell}.");
        return thing;
    }

    private static void SpawnFueled(Map map, ThingDef def, IntVec3 cell)
    {
        Thing thing = Spawn(map, def, cell);
        CompRefuelable fuel = thing.TryGetComp<CompRefuelable>() ??
            throw new InvalidOperationException(def.defName + " has no refuelable comp.");
        fuel.Refuel(fuel.Props.fuelCapacity / fuel.Props.FuelMultiplierCurrentDifficulty);
        if (!fuel.HasFuel || fuel.FuelPercentOfMax < 1f || !thing.TryGetComp<CompGlower>().Glows)
            throw new InvalidOperationException($"{def.defName} at {cell} is not fully fuelled and glowing.");
    }

    private static void Report(string label, CellRect rect) =>
        Log.Message($"[RimLumen] Validation scene {label}: ({rect.minX},{rect.minZ})..({rect.maxX},{rect.maxZ})");

    private static IEnumerator SaveAndShutdown(string saveName)
    {
        for (int i = 0; i < SettleFrames; i++)
            yield return new UnityEngine.WaitForEndOfFrame();
        while (LongEventHandler.AnyEventNowOrWaiting)
            yield return new UnityEngine.WaitForEndOfFrame();

        int exitCode = 1;
        try
        {
            string path = GenFilePaths.FilePathForSavedGame(saveName);
            // Same payload as GameDataSaveLoader.SaveGame, without its exception-swallowing wrapper.
            SafeSaver.Save(path, "savegame", () =>
            {
                ScribeMetaHeaderUtility.WriteMetaHeader();
                Game game = Current.Game;
                Scribe_Deep.Look(ref game, "game");
            }, Find.GameInfo.permadeathMode);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new IOException("Validation save was not created or is empty: " + path);
            Log.Message("[RimLumen] Validation save written: " + path);
            exitCode = 0;
        }
        catch (Exception ex)
        {
            Log.Error("[RimLumen] Validation save failed: " + ex);
        }
        finally
        {
            Shutdown(exitCode);
        }
    }

    private static void Shutdown(int exitCode)
    {
        try
        {
            EngineApplication.Instance?.WaitForRenderThreadIdle();
        }
        catch (Exception ex)
        {
            Log.Error("[RimLumen] Validation save shutdown failed: " + ex);
            exitCode = 1;
        }
        if (UnityRuntime.Current is { } runtime) runtime.App.ExitCode = exitCode;
        Root.Shutdown();
    }
}
