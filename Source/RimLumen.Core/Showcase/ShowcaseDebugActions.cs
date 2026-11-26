using LudeonTK;
using RimLumen.Core.Rendering;
using Verse;

namespace RimLumen.Core.Showcase;

public static class ShowcaseDebugActions
{
    [DebugAction("RimLumen", "Build / Reset Left-to-Right Showcase", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void BuildOrResetShowcase()
    {
        Map? map = Find.CurrentMap;
        if (map is null) return;
        ShowcaseStage.BuildOrRebuild(map);
    }

    [DebugAction("RimLumen", "Clear Showcase", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void ClearShowcase()
    {
        Map? map = Find.CurrentMap;
        if (map is null) return;
        ShowcaseStage.Clear(map);
    }

    // Storyboard: ShowcaseTrailerTimeline. Rebuilds the showcase, so it can be re-shot any number of times.
    [DebugAction("RimLumen", "Play Trailer Take", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void PlayTrailerTake()
    {
        Map? map = Find.CurrentMap;
        if (map is null) return;
        ShowcaseTrailerSession.TryStart(map);
    }

    [DebugAction("RimLumen", "Toggle L2 Force Arrows", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void ToggleForceArrows() => ShowcaseForceArrows.SetEnabled(!ShowcaseForceArrows.Enabled);

    [DebugAction("RimLumen", "Toggle Vanilla Additive HDR", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void ToggleVanillaAdditiveHdr()
    {
        bool next = !RimLumenRenderHost.EnableVanillaAdditiveBloom;
        RimLumenRenderHost.EnableVanillaAdditiveBloom = next;
        Log.Message($"[RimLumen] EnableVanillaAdditiveBloom = {next}");
    }

    [DebugAction("RimLumen", "Toggle Midnight / Noon", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void ToggleMidnightNoon()
    {
        Map? map = Find.CurrentMap;
        if (map is null) return;
        bool isNoon = ShowcaseStage.ToggleMidnightNoon(map);
        Log.Message($"[RimLumen] Showcase sky toggled to {(isNoon ? "Noon" : "Midnight")}");
    }

    [DebugAction("RimLumen", "Toggle Station Labels", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void ToggleStationLabels()
    {
        Map? map = Find.CurrentMap;
        if (map is null) return;
        bool visible = ShowcaseStage.ToggleLabels(map);
        Log.Message($"[RimLumen] Showcase station labels = {visible}");
    }
}
