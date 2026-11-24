using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimLumen.Adapter.RimUnity;

/// <summary>
/// Session rule for the RimUnity Map HDR conflict notice: notify the first time an enabled mode is seen,
/// again when the player switches to a different enabled mode, and again when it is re-enabled after being off.
/// </summary>
internal sealed class MapHdrNoticeRule
{
    public const string Off = "None";

    private string? notifiedMode;

    public bool ShouldNotify(string mode)
    {
        if (mode == Off)
        {
            notifiedMode = null;
            return false;
        }
        if (mode == notifiedMode) return false;
        notifiedMode = mode;
        return true;
    }
}

/// <summary>
/// Tells the player to turn RimUnity's Map HDR off themselves. Notification only: RimUnity's settings are only
/// read, and RimLumen's own rendering is left unchanged.
/// Checked after a game finishes loading and whenever a mod settings window closes (RimUnity applies the mode
/// from the next frame, so a change made in-game matters immediately).
/// </summary>
internal static class RimUnityMapHdrNotice
{
    private static readonly MapHdrNoticeRule Rule = new();
    private static RimUnityRuntime runtime = null!;

    public static void Install(RimUnityRuntime rimUnity, Harmony harmony)
    {
        runtime = rimUnity;
        var postfix = new HarmonyMethod(typeof(RimUnityMapHdrNotice), nameof(Check));
        harmony.Patch(typeof(Game).GetMethod(nameof(Game.FinalizeInit), Type.EmptyTypes)
            ?? throw new MissingMethodException(typeof(Game).FullName, nameof(Game.FinalizeInit)), postfix: postfix);
        harmony.Patch(typeof(Dialog_ModSettings).GetMethod(nameof(Dialog_ModSettings.PreClose), Type.EmptyTypes)
            ?? throw new MissingMethodException(typeof(Dialog_ModSettings).FullName, nameof(Dialog_ModSettings.PreClose)), postfix: postfix);
    }

    private static void Check() => LongEventHandler.ExecuteWhenFinished(Evaluate);

    private static void Evaluate()
    {
        string mode = runtime.ReadMapHdrMode();
        if (!Rule.ShouldNotify(mode)) return;
        string provider = runtime.Provider.Name;
        Log.Warning($"[RimLumen.Adapter.RimUnity] RimUnity {runtime.Version} (provided by {provider}) has Map HDR mode {mode}; " +
            "it conflicts with RimLumen's lighting and can crash the game. Turn it off in RimUnity's mod settings.");
        Find.WindowStack.Add(new Dialog_MessageBox(
            "RimLumen_RimUnityMapHdrConflict".Translate(provider, mode),
            title: "RimLumen_RimUnityMapHdrConflictTitle".Translate()));
    }
}
