using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LudeonTK;
using RimLumen.Core.Api;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;
using RimWorld;
using Verse;

namespace RimLumen.Core.Debug;

public static class LumenEffectDebugActions
{
    private static readonly List<ILumenEffectHandle> PlayedHandles = new();

    [DebugAction("RimLumen", "Play native effects at mouse...", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void PlayEffectAtMouse()
    {
        LumenSceneBootstrap.EnsureInitialized();
        var options = new List<DebugMenuOption>();
        foreach (EffectAsset asset in LumenSceneBootstrap.Catalog.GetSnapshot().OrderBy(asset => asset.EffectId, StringComparer.Ordinal))
        {
            string effectId = asset.EffectId;
            options.Add(new DebugMenuOption(effectId, DebugMenuOptionMode.Tool, () =>
            {
                Map map = Find.CurrentMap;
                IntVec3 cell = UI.MouseCell();
                if (!cell.InBounds(map)) return;
                PlayedHandles.RemoveAll(handle => !handle.IsAlive);
                PlayedHandles.Add(RimLumenApi.PlayAtCell(effectId, map, cell));
            }));
        }
        Find.WindowStack.Add(new Dialog_DebugOptionListLister(options));
    }

    [DebugAction("RimLumen", "Stop all debug-played effects", allowedGameStates = AllowedGameStates.Playing)]
    public static void StopAllDebugPlayedEffects()
    {
        foreach (ILumenEffectHandle handle in PlayedHandles)
            handle.Stop(true);
        PlayedHandles.Clear();
    }

    [DebugAction("RimLumen", "Reload effect assets", allowedGameStates = AllowedGameStates.Playing)]
    public static void ReloadEffectAssets()
    {
        LumenSceneBootstrap.EnsureInitialized();
        var refreshed = new InMemoryAssetCatalog();
        int loaded = 0;
        int failed = 0;
        foreach (ModContentPack mod in LoadedModManager.RunningMods)
        {
            string packageId = string.IsNullOrEmpty(mod.PackageIdPlayerFacing) ? mod.Name : mod.PackageIdPlayerFacing;
            Action<string, Exception> report = (file, error) =>
            {
                failed++;
                Log.Error($"[RimLumen] Failed to load effect asset '{file}' from {packageId}: {error.Message}");
            };
            loaded += LumenModAssetScanner.ScanDirectory(refreshed, packageId, Path.Combine(mod.RootDir, "1.7", "Data", "RimLumen"), report);
            loaded += LumenModAssetScanner.ScanDirectory(refreshed, packageId, Path.Combine(mod.RootDir, "Data", "RimLumen"), report);
        }

        // Rescanning in a fresh catalog preserves first-writer short names across mods, then refreshes those names too.
        foreach (EffectAsset asset in refreshed.GetSnapshot())
            LumenSceneBootstrap.Catalog.Register(asset);
        Messages.Message("RimLumen_EffectAssetsReloaded".Translate(loaded, failed), MessageTypeDefOf.TaskCompletion, historical: false);
    }
}
