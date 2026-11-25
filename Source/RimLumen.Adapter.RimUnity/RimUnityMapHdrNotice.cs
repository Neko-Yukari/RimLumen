using System;
using HarmonyLib;
using RimLumen.Core.Mod;
using RimWorld;
using Verse;

namespace RimLumen.Adapter.RimUnity;

/// <summary>
/// Session rule for the RimUnity Map HDR notice. The combination is active when RimUnity's Map HDR is on and
/// RimLumen's HDR glow or lighting takeover is on. Notify the first time it becomes active, again when the player
/// switches to a different Map HDR mode, and again when it becomes active after being inactive.
/// </summary>
internal sealed class MapHdrNoticeRule
{
    public const string Off = "None";

    private string? notifiedMode;

    public bool ShouldNotify(string mode, bool rimLumenMapHdrOn)
    {
        if (mode == Off || !rimLumenMapHdrOn)
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
/// Tells the player that RimUnity's Map HDR and RimLumen's map HDR are both on. Notification only: neither mod's
/// settings are changed.
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
        RimLumenSettings settings = RimLumenMod.Settings;
        if (!Rule.ShouldNotify(mode, settings.EnhanceVanillaAdditiveGlow || settings.LightingTakeover)) return;
        string provider = runtime.Provider.Name;
        Log.Warning($"[RimLumen.Adapter.RimUnity] RimUnity {runtime.Version} (provided by {provider}) Map HDR mode {mode} is on " +
            "together with RimLumen HDR glow or lighting takeover; this combination has crashed without a known cause on the author's machine.");
        Find.WindowStack.Add(new Dialog_MessageBox(
            "RimLumen_RimUnityMapHdrConflict".Translate(provider, mode),
            title: "RimLumen_RimUnityMapHdrConflictTitle".Translate()));
    }
}
