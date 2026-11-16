using System;
using System.Collections.Generic;
using System.Linq;
using RimLumen.Core.Demo;
using RimLumen.Core.Rendering;
using UnityEngine;
using Verse;

namespace RimLumen.Core.Mod;

public sealed class RimLumenSettings : ModSettings
{
    public bool EnhanceVanillaAdditiveGlow = true;
    public float ParticleDensityScale = 1.0f;
    public bool EnableDemoBuildings = false;
    public string SelectedShaderPackId = ShaderPackCatalog.DefaultPackId;
    public Dictionary<string, float> PackParamOverrides = new(StringComparer.OrdinalIgnoreCase);

    private string? appliedPackId;

    // Ensures: persists base settings, SelectedShaderPackId and per-pack PackParamOverrides.
    public override void ExposeData()
    {
        Scribe_Values.Look(ref EnhanceVanillaAdditiveGlow, "enhanceVanillaAdditiveGlow", true);
        Scribe_Values.Look(ref ParticleDensityScale, "particleDensityScale", 1.0f);
        Scribe_Values.Look(ref EnableDemoBuildings, "enableDemoBuildings", false);
        Scribe_Values.Look(ref SelectedShaderPackId, "selectedShaderPackId", ShaderPackCatalog.DefaultPackId);
        Scribe_Collections.Look(ref PackParamOverrides, "packParamOverrides", LookMode.Value, LookMode.Value);

        if (string.IsNullOrWhiteSpace(SelectedShaderPackId))
            SelectedShaderPackId = ShaderPackCatalog.DefaultPackId;
        PackParamOverrides = PackParamOverrides is null
            ? new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, float>(PackParamOverrides, StringComparer.OrdinalIgnoreCase);
        ParticleDensityScale = Mathf.Clamp(ParticleDensityScale, 0.1f, 1.0f);

        // Startup loads settings on the async PlayDataLoader thread before Install; RimLumenMod's ctor defers that first apply.
        if (UnityData.IsInMainThread && RimLumenRenderHost.LumenRenderPassId != 0)
            ApplyToRuntime();
    }

    // Ensures: pushes the scalar settings (glow toggle, density, demo buildings) to the host; never touches shader packs.
    internal void ApplyScalarsToRuntime()
    {
        RimLumenRenderHost.EnableVanillaAdditiveBloom = EnhanceVanillaAdditiveGlow;
        RimLumenRenderHost.ParticleDensityScale = ParticleDensityScale;
        DemoBuildingRegistry.SyncWithSettings(EnableDemoBuildings);
    }

    // Ensures: pushes current settings and runtime parameters to RimLumenRenderHost; reloads the
    //          active ShaderPack pipeline only when the selected pack id changed (or forced).
    public void ApplyToRuntime(bool forceReloadPack = false)
    {
        ApplyScalarsToRuntime();

        IReadOnlyList<ShaderPackEntry> packs = ShaderPackCatalog.GetAvailablePacks(rescan: forceReloadPack);
        ShaderPackEntry entry = ShaderPackCatalog.FindOrDefault(SelectedShaderPackId, packs);
        if (!string.Equals(SelectedShaderPackId, entry.Id, StringComparison.OrdinalIgnoreCase))
            SelectedShaderPackId = entry.Id;

        if (forceReloadPack || !string.Equals(appliedPackId, entry.Id, StringComparison.OrdinalIgnoreCase))
        {
            appliedPackId = entry.Id;
            RimLumenRenderHost.ReloadShaderPack(entry.Spec);
            entry = ShaderPackCatalog.FindOrDefault(RimLumenRenderHost.ActiveShaderPack.Id, packs);
            SelectedShaderPackId = entry.Id;
            appliedPackId = entry.Id;
        }

        ShaderPackCatalog.ApplyOptionsToRuntime(entry, PackParamOverrides, RimLumenRenderHost.ShaderPackParams);
    }

    public void ResetPackOverrides(string packId)
    {
        string prefix = packId + ":";
        foreach (string key in PackParamOverrides.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            PackParamOverrides.Remove(key);
    }

    public void ResetAll()
    {
        EnhanceVanillaAdditiveGlow = true;
        ParticleDensityScale = 1.0f;
        EnableDemoBuildings = false;
        SelectedShaderPackId = ShaderPackCatalog.DefaultPackId;
        PackParamOverrides.Clear();
        ApplyToRuntime(forceReloadPack: true);
    }
}

public sealed class RimLumenMod : Verse.Mod
{
    public static RimLumenSettings Settings { get; private set; } = new();

    private Vector2 scrollPosition;
    private float lastContentHeight = 640f;

    public RimLumenMod(ModContentPack content) : base(content)
    {
        Settings = GetSettings<RimLumenSettings>();
    }

    public override string SettingsCategory() => "RimLumen";

    public override void DoSettingsWindowContents(Rect inRect)
    {
        IReadOnlyList<ShaderPackEntry> packs = ShaderPackCatalog.GetAvailablePacks();
        ShaderPackEntry activeEntry = ShaderPackCatalog.FindOrDefault(Settings.SelectedShaderPackId, packs);
        bool glowBefore = Settings.EnhanceVanillaAdditiveGlow;
        float densityBefore = Settings.ParticleDensityScale;
        bool demoBefore = Settings.EnableDemoBuildings;

        Rect viewRect = new(0f, 0f, inRect.width - 20f, Mathf.Max(inRect.height, lastContentHeight));
        Widgets.BeginScrollView(inRect, ref scrollPosition, viewRect);

        var listing = new Listing_Standard();
        listing.Begin(viewRect);

        listing.CheckboxLabeled(
            "RimLumen_EnhanceAdditiveGlow".Translate(),
            ref Settings.EnhanceVanillaAdditiveGlow,
            "RimLumen_EnhanceAdditiveGlowTooltip".Translate());
        listing.Gap(6f);

        listing.Label("RimLumen_ParticleDensity".Translate((int)Mathf.Round(Settings.ParticleDensityScale * 100f)));
        Settings.ParticleDensityScale = listing.Slider(Settings.ParticleDensityScale, 0.10f, 1.00f);
        listing.Gap(6f);

        listing.CheckboxLabeled(
            "RimLumen_EnableDemoBuildings".Translate(),
            ref Settings.EnableDemoBuildings,
            "RimLumen_EnableDemoBuildingsTooltip".Translate());
        bool settingsChanged = Settings.EnhanceVanillaAdditiveGlow != glowBefore
            || Settings.ParticleDensityScale != densityBefore
            || Settings.EnableDemoBuildings != demoBefore;

        listing.GapLine(12f);

        Text.Font = GameFont.Medium;
        listing.Label("RimLumen_ShaderPacks".Translate());
        Text.Font = GameFont.Small;
        listing.Gap(4f);

        DrawShaderPackSelector(listing, packs, activeEntry);
        listing.Gap(4f);
        DrawActionRow(listing, activeEntry);
        listing.Gap(6f);
        DrawPackSummary(listing, activeEntry);

        string? runtimeError = RimLumenRenderHost.LastShaderPackError;
        if (!string.IsNullOrWhiteSpace(runtimeError))
        {
            listing.Gap(4f);
            Color prev = GUI.color;
            GUI.color = new Color(1f, 0.45f, 0.45f);
            bool wordWrap = Text.WordWrap;
            Text.WordWrap = true;
            listing.Label("RimLumen_ShaderPackError".Translate(runtimeError));
            Text.WordWrap = wordWrap;
            GUI.color = prev;
            if (listing.ButtonText("RimLumen_CopyError".Translate()))
                GUIUtility.systemCopyBuffer = runtimeError;
        }

        if (activeEntry.Options.Count > 0)
        {
            listing.GapLine(10f);
            listing.Label("RimLumen_PackOptions".Translate(PackDisplayName(activeEntry)));
            listing.Gap(4f);
            foreach (ShaderPackOptionDefinition option in activeEntry.Options)
                settingsChanged |= DrawOptionSlider(listing, activeEntry, option);
        }

        listing.GapLine(12f);
        if (listing.ButtonText("RimLumen_ResetAll".Translate()))
            Settings.ResetAll();

        listing.End();
        lastContentHeight = listing.CurHeight + 24f;
        Widgets.EndScrollView();

        // Push only what the player actually changed; a pipeline rebuild stays tied to the pack id.
        if (settingsChanged)
            Settings.ApplyToRuntime();
    }

    private static void DrawShaderPackSelector(Listing_Standard listing, IReadOnlyList<ShaderPackEntry> packs, ShaderPackEntry activeEntry)
    {
        Rect row = listing.GetRect(30f);
        float labelWidth = Mathf.Min(230f, row.width * 0.42f);
        Rect labelRect = new(row.x, row.y, labelWidth, row.height);
        Rect buttonRect = new(row.x + labelWidth + 8f, row.y, row.width - labelWidth - 8f, row.height);

        TextAnchor oldAnchor = Text.Anchor;
        Text.Anchor = TextAnchor.MiddleLeft;
        Widgets.Label(labelRect, "RimLumen_ActivePack".Translate());
        Text.Anchor = oldAnchor;

        string buttonText = $"{PackDisplayName(activeEntry)}  [{PackSourceLabel(activeEntry)}]";
        if (Widgets.ButtonText(buttonRect, buttonText))
        {
            var menu = new List<FloatMenuOption>(packs.Count);
            foreach (ShaderPackEntry pack in packs)
            {
                if (pack.LoadError is not null)
                {
                    menu.Add(new FloatMenuOption("RimLumen_BrokenPack".Translate(PackDisplayName(pack), PackSourceLabel(pack), TrimSingleLine(pack.LoadError, 60)), null));
                    continue;
                }
                ShaderPackEntry captured = pack;
                menu.Add(new FloatMenuOption($"{PackDisplayName(captured)}  [{PackSourceLabel(captured)}]", () =>
                {
                    Settings.SelectedShaderPackId = captured.Id;
                    Settings.ApplyToRuntime();
                }));
            }
            Find.WindowStack.Add(new FloatMenu(menu));
        }
    }

    private static void DrawActionRow(Listing_Standard listing, ShaderPackEntry activeEntry)
    {
        Rect row = listing.GetRect(28f);
        float gap = 8f;
        float w = (row.width - gap * 2f) / 3f;
        if (Widgets.ButtonText(new Rect(row.x, row.y, w, row.height), "RimLumen_ReloadPack".Translate()))
            Settings.ApplyToRuntime(forceReloadPack: true);
        if (Widgets.ButtonText(new Rect(row.x + w + gap, row.y, w, row.height), "RimLumen_RescanPacks".Translate()))
        {
            ShaderPackCatalog.GetAvailablePacks(rescan: true);
            Settings.ApplyToRuntime();
        }
        if (Widgets.ButtonText(new Rect(row.x + (w + gap) * 2f, row.y, w, row.height), "RimLumen_ResetPack".Translate()))
        {
            Settings.ResetPackOverrides(activeEntry.Id);
            Settings.ApplyToRuntime();
        }
    }

    private static void DrawPackSummary(Listing_Standard listing, ShaderPackEntry activeEntry)
    {
        var tags = new List<string>();
        if (activeEntry.Spec.RunOnEmptyFrames) tags.Add("RimLumen_FeatureFullscreen".Translate());
        if (activeEntry.Spec.RequiresMapLightField) tags.Add("RimLumen_FeatureMapLight".Translate());
        if (activeEntry.Spec.RequiresSceneDepth) tags.Add("RimLumen_FeatureSceneDepth".Translate());
        tags.Add(activeEntry.Spec.BloomMipLevels > 0
            ? "RimLumen_FeatureBloomLevels".Translate(activeEntry.Spec.BloomMipLevels)
            : "RimLumen_FeatureNoBloom".Translate());
        if (activeEntry.Spec.Passes.Count > 0) tags.Add("RimLumen_FeaturePasses".Translate(activeEntry.Spec.Passes.Count));

        Color prev = GUI.color;
        GUI.color = new Color(0.8f, 0.85f, 0.9f);
        listing.Label("RimLumen_PackSummary".Translate(activeEntry.Author, string.Join(" / ", tags)));
        if (!string.IsNullOrWhiteSpace(activeEntry.Description))
            listing.Label(activeEntry.Id == ShaderPackCatalog.DefaultPackId
                ? activeEntry.Description.Translate().ToString()
                : activeEntry.Description);

        string? localDir = ShaderPackCatalog.TryGetLocalUserPacksDirectory();
        if (!string.IsNullOrWhiteSpace(localDir))
        {
            GUI.color = new Color(0.68f, 0.72f, 0.78f);
            listing.Label("RimLumen_PackDirectory".Translate(localDir));
        }
        GUI.color = prev;
    }

    private static bool DrawOptionSlider(Listing_Standard listing, ShaderPackEntry entry, ShaderPackOptionDefinition option)
    {
        float current = ShaderPackCatalog.GetOptionValue(entry, option, Settings.PackParamOverrides);
        string label = entry.Id == ShaderPackCatalog.DefaultPackId ? option.Label.Translate().ToString() : option.Label;
        if (!string.IsNullOrWhiteSpace(option.Tooltip))
        {
            Rect tipRect = listing.GetRect(22f);
            Widgets.Label(tipRect, $"{label}: {current:F2}");
            TooltipHandler.TipRegion(tipRect, entry.Id == ShaderPackCatalog.DefaultPackId
                ? option.Tooltip.Translate().ToString()
                : option.Tooltip);
        }
        else
        {
            listing.Label($"{label}: {current:F2}");
        }

        float next = listing.Slider(current, option.MinValue, option.MaxValue);
        float snapped = ShaderPackCatalog.SnapAndClamp(next, option);
        bool changed = Mathf.Abs(snapped - current) > 1e-5f;
        if (changed)
            Settings.PackParamOverrides[ShaderPackCatalog.MakeOverrideKey(entry.Id, option.Key)] = snapped;
        listing.Gap(2f);
        return changed;
    }

    private static string PackDisplayName(ShaderPackEntry entry) => entry.Id == ShaderPackCatalog.DefaultPackId
        ? entry.Name.Translate().ToString()
        : entry.Name;

    private static string PackSourceLabel(ShaderPackEntry entry)
    {
        const string modPrefix = "RimLumen_SourceMod:";
        if (entry.SourceLabel.StartsWith(modPrefix, StringComparison.Ordinal))
            return "RimLumen_SourceMod".Translate(entry.SourceLabel[modPrefix.Length..]);
        return entry.SourceLabel is "RimLumen_SourceBuiltIn" or "RimLumen_SourceLocal"
            ? entry.SourceLabel.Translate().ToString()
            : entry.SourceLabel;
    }

    private static string TrimSingleLine(string text, int maxChars)
    {
        string single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= maxChars ? single : single[..maxChars] + "...";
    }
}

// Queues initial pack and setting application from CallAll so it runs on the main thread after
// RimLumenStartup.Install has registered 1.7/Shaders and created the pipeline.
[StaticConstructorOnStartup]
internal static class RimLumenModStartupHook
{
    static RimLumenModStartupHook()
    {
        LongEventHandler.ExecuteWhenFinished(() =>
        {
            if (RimLumenRenderHost.LumenRenderPassId != 0)
                RimLumenMod.Settings.ApplyToRuntime();
        });
    }
}
