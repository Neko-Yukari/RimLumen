using System;
using System.Collections.Generic;
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

    [DebugAction("RimLumen", "Effect preview window...", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void OpenEffectPreviewWindow()
    {
        if (!Prefs.DevMode) return;
        if (Find.CurrentMap is null)
        {
            Messages.Message("RimLumen_PreviewRequiresMap".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        LumenSceneBootstrap.EnsureInitialized();
        LumenEffectPreviewWindow? existing = Find.WindowStack.WindowOfType<LumenEffectPreviewWindow>();
        if (existing is not null)
        {
            Find.WindowStack.Notify_ClickedInsideWindow(existing);
            return;
        }

        Find.WindowStack.Add(new LumenEffectPreviewWindow());
    }

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
        LumenAssetReloadResult result = LumenModAssetScanner.ReloadRunningModAssets(LumenSceneBootstrap.Catalog);
        Messages.Message("RimLumen_EffectAssetsReloaded".Translate(result.Loaded, result.Failed), MessageTypeDefOf.TaskCompletion, historical: false);
    }
}
