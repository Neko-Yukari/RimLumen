using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace RimLumen.Core.Lighting;

// Attaches one LogicalGlowGrid to every GlowGrid and folds it into GroundGlowAt. VisualGlowAt, the vanilla
// lighting overlay and the accumulated glow arrays are never touched.
internal static class LogicalLightHooks
{
    private const string HarmonyId = "NekoYukari.RimLumen.LogicalLight";
    private const float MaxNonOverlitGroundGlow = 0.5f;
    private static readonly ConditionalWeakTable<GlowGrid, LogicalGlowGrid> Grids = new();
    private static readonly List<int> ChangedCells = new();
    private static bool armed;

    // Requires: main thread, before any map is constructed.
    // Ensures: idempotent; throws (with every patch of this id removed) when a GlowGrid target is missing.
    internal static void Arm()
    {
        if (armed) return;
        Type grid = typeof(GlowGrid);
        ConstructorInfo ctor = grid.GetConstructor(new[] { typeof(Map) })
            ?? throw new MissingMethodException(grid.FullName, ".ctor(Map)");
        var harmony = new Harmony(HarmonyId);
        try
        {
            harmony.Patch(ctor, postfix: Patch(nameof(ConstructorPostfix)));
            harmony.Patch(RequireMethod(grid, nameof(GlowGrid.GroundGlowAt), typeof(IntVec3), typeof(bool), typeof(bool)),
                postfix: Patch(nameof(GroundGlowAtPostfix)));
            harmony.Patch(RequireMethod(grid, nameof(GlowGrid.LightBlockerAdded), typeof(IntVec3)),
                postfix: Patch(nameof(LightBlockerAddedPostfix)));
            harmony.Patch(RequireMethod(grid, nameof(GlowGrid.LightBlockerRemoved), typeof(IntVec3)),
                postfix: Patch(nameof(LightBlockerRemovedPostfix)));
            harmony.Patch(RequireMethod(grid, nameof(GlowGrid.GlowGridUpdate_First)),
                postfix: Patch(nameof(UpdatePostfix)));
            armed = true;
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(HarmonyId);
            throw new InvalidOperationException("Could not patch GlowGrid for RimLumen logical lights.", ex);
        }
    }

    // Requires: Arm has run before the map was constructed.
    internal static LogicalGlowGrid GridOf(Map map) =>
        Grids.TryGetValue(map.glowGrid, out LogicalGlowGrid? grid)
            ? grid
            : throw new InvalidOperationException($"Map {map.uniqueID} has no RimLumen logical light field; GlowGrid hooks are not armed.");

    private static HarmonyMethod Patch(string name) =>
        new(typeof(LogicalLightHooks).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!);

    private static MethodInfo RequireMethod(Type type, string name, params Type[] parameters) =>
        type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, parameters, null)
            ?? throw new MissingMethodException(type.FullName, name);

    private static void ConstructorPostfix(GlowGrid __instance, Map map) =>
        Grids.Add(__instance, new LogicalGlowGrid(map.Size.x, map.Size.z));

    // GroundGlowAt returns at least the sky glow and otherwise caps non-overlit ground light at 0.5, so a
    // logical light only matters below 0.5; vacuum-exposed terrain has no ground glow (GetAccumulatedGlowAt).
    private static void GroundGlowAtPostfix(GlowGrid __instance, Map ___map, IntVec3 c, ref float __result)
    {
        if (__result >= MaxNonOverlitGroundGlow || !Grids.TryGetValue(__instance, out LogicalGlowGrid? grid) ||
            grid.Glow is not { } glow) return;
        int index = ___map.cellIndices.CellToIndex(c);
        if (glow[index] <= __result || ___map.terrainGrid.TerrainAt(index).exposesToVacuum) return;
        __result = glow[index];
    }

    private static void LightBlockerAddedPostfix(GlowGrid __instance, Map ___map, IntVec3 cell)
    {
        if (Grids.TryGetValue(__instance, out LogicalGlowGrid? grid)) grid.SetBlocker(___map.cellIndices.CellToIndex(cell), true);
    }

    private static void LightBlockerRemovedPostfix(GlowGrid __instance, Map ___map, IntVec3 cell)
    {
        if (Grids.TryGetValue(__instance, out LogicalGlowGrid? grid)) grid.SetBlocker(___map.cellIndices.CellToIndex(cell), false);
    }

    // Rebuilds where vanilla recomputes its own glow, once per map update, and raises GlowChanged for each
    // cell whose logical glow changed. No map mesh is dirtied: nothing visual reads this field.
    private static void UpdatePostfix(GlowGrid __instance, Map ___map)
    {
        if (!Grids.TryGetValue(__instance, out LogicalGlowGrid? grid)) return;
        ChangedCells.Clear();
        grid.Rebuild(ChangedCells);
        foreach (int index in ChangedCells) ___map.events.Notify_GlowChanged(___map.cellIndices.IndexToCell(index));
    }
}
