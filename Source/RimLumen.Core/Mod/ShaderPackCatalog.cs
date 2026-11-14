using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using RimLumen.Core.Rendering;
using RimLumen.Core.Rendering.Logic;
using Verse;

namespace RimLumen.Core.Mod;

public static class ShaderPackCatalog
{
    public const string DefaultPackId = "default";

    private static readonly IReadOnlyList<ShaderPackOptionDefinition> StandardOptions =
    [
        new("bloomIntensity", "泛光强度 (Bloom intensity)", ShaderPackOptionTarget.BloomIntensity, 0.16f, 0.0f, 0.8f, 0.01f,
            "控制 HDR 发光体与粒子泛光的整体叠加强度。"),
        new("bloomThreshold", "起辉阈值 (Bloom threshold)", ShaderPackOptionTarget.BloomThreshold, 1.0f, 0.2f, 3.0f, 0.05f,
            "亮度超过该阈值的像素才会进入泛光金字塔。"),
        new("bloomKnee", "高光过渡膝宽 (Bloom knee)", ShaderPackOptionTarget.BloomKnee, 0.5f, 0.05f, 1.0f, 0.01f,
            "控制阈值附近的平滑起辉过渡宽度。"),
        new("bloomScatter", "泛光扩散权重 (Bloom scatter)", ShaderPackOptionTarget.BloomScatter, 0.65f, 0.2f, 0.95f, 0.01f,
            "控制多级升采样时低频光晕向四周扩散的权重。"),
    ];

    public static ShaderPackEntry StandardEntry { get; } = new(
        DefaultPackId,
        "Standard (默认 · 零侵入基线)",
        "RimLumen",
        "RimLumen 内置默认 HDR 与 5 级 Bloom 管线；关闭原版加色增强且无活动特效时保持逐像素零差。",
        "内置",
        null,
        ShaderPackSpec.Default,
        StandardOptions);

    private static IReadOnlyList<ShaderPackEntry>? cachedEntries;

    public static IReadOnlyList<ShaderPackEntry> GetAvailablePacks(bool rescan = false)
    {
        if (rescan || cachedEntries is null)
            cachedEntries = ScanLiveSources();
        return cachedEntries;
    }

    public static ShaderPackEntry FindOrDefault(string? id, IReadOnlyList<ShaderPackEntry>? entries = null)
    {
        IReadOnlyList<ShaderPackEntry> list = entries ?? GetAvailablePacks();
        if (!string.IsNullOrWhiteSpace(id))
        {
            foreach (ShaderPackEntry entry in list)
            {
                if (entry.LoadError is null && string.Equals(entry.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
        }
        return StandardEntry;
    }

    public static string? TryGetLocalUserPacksDirectory()
    {
        try
        {
            string configDir = GenFilePaths.ConfigFolderPath;
            if (string.IsNullOrWhiteSpace(configDir))
                return null;
            return Path.Combine(configDir, "RimLumen", "ShaderPacks");
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<ShaderPackEntry> ScanRoots(IEnumerable<(string RootPath, string SourceLabel)> roots)
    {
        var byId = new Dictionary<string, ShaderPackEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [StandardEntry.Id] = StandardEntry,
        };
        var orderedIds = new List<string> { StandardEntry.Id };

        foreach ((string rootPath, string sourceLabel) in roots)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                continue;

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(rootPath);
                Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                TryLogWarning($"[RimLumen] Failed to enumerate shader pack directory '{rootPath}': {ex.Message}");
                continue;
            }

            foreach (string packDir in subDirs)
            {
                string manifestPath = Path.Combine(packDir, "pack.json");
                if (!File.Exists(manifestPath))
                    continue;

                ShaderPackEntry entry = LoadEntrySafe(packDir, sourceLabel);
                if (!byId.ContainsKey(entry.Id))
                    orderedIds.Add(entry.Id);
                byId[entry.Id] = entry;
            }
        }

        var result = new List<ShaderPackEntry>(orderedIds.Count);
        foreach (string id in orderedIds)
            result.Add(byId[id]);
        return result;
    }

    public static string MakeOverrideKey(string packId, string optionKey) => $"{packId}:{optionKey}";

    public static float GetOptionValue(
        ShaderPackEntry entry,
        ShaderPackOptionDefinition option,
        IReadOnlyDictionary<string, float>? overrides)
    {
        if (overrides is not null
            && overrides.TryGetValue(MakeOverrideKey(entry.Id, option.Key), out float raw)
            && float.IsFinite(raw))
        {
            return SnapAndClamp(raw, option);
        }
        return SnapAndClamp(option.DefaultValue, option);
    }

    public static void ApplyOptionsToRuntime(
        ShaderPackEntry entry,
        IReadOnlyDictionary<string, float>? overrides,
        ShaderPackRuntimeParams target)
    {
        // Indexed by ShaderPackOptionTarget (0..3 bloom, 4..19 PackParam0..15) and committed by one SetAll,
        // so the render thread never observes the reset values without the pack's overrides.
        Span<float> slots = stackalloc float[(int)ShaderPackOptionTarget.PackParam15 + 1];
        slots[(int)ShaderPackOptionTarget.BloomIntensity] = 0.16f;
        slots[(int)ShaderPackOptionTarget.BloomThreshold] = 1.0f;
        slots[(int)ShaderPackOptionTarget.BloomKnee] = 0.5f;
        slots[(int)ShaderPackOptionTarget.BloomScatter] = 0.65f;

        foreach (ShaderPackOptionDefinition option in entry.Options)
            slots[(int)option.Target] = GetOptionValue(entry, option, overrides);

        const int p = (int)ShaderPackOptionTarget.PackParam0;
        target.SetAll(
            slots[(int)ShaderPackOptionTarget.BloomIntensity],
            slots[(int)ShaderPackOptionTarget.BloomThreshold],
            slots[(int)ShaderPackOptionTarget.BloomKnee],
            slots[(int)ShaderPackOptionTarget.BloomScatter],
            new Vector4(slots[p], slots[p + 1], slots[p + 2], slots[p + 3]),
            new Vector4(slots[p + 4], slots[p + 5], slots[p + 6], slots[p + 7]),
            new Vector4(slots[p + 8], slots[p + 9], slots[p + 10], slots[p + 11]),
            new Vector4(slots[p + 12], slots[p + 13], slots[p + 14], slots[p + 15]));
    }

    public static float SnapAndClamp(float value, ShaderPackOptionDefinition option)
    {
        float clamped = Math.Clamp(value, option.MinValue, option.MaxValue);
        if (option.Step > 0f)
        {
            double steps = Math.Round((clamped - (double)option.MinValue) / option.Step);
            double snapped = option.MinValue + steps * option.Step;
            clamped = Math.Clamp((float)Math.Round(snapped, 6), option.MinValue, option.MaxValue);
        }
        return clamped;
    }

    private static IReadOnlyList<ShaderPackEntry> ScanLiveSources()
    {
        var roots = new List<(string, string)>();
        try
        {
            List<ModContentPack> mods = LoadedModManager.RunningMods?.ToList() ?? new List<ModContentPack>();
            ModContentPack? self = mods.FirstOrDefault(m =>
                string.Equals(m.PackageIdPlayerFacing, "NekoYukari.RimLumen", StringComparison.OrdinalIgnoreCase));
            if (self is not null)
            {
                roots.Add((Path.Combine(self.RootDir, "1.7", "ShaderPacks"), "内置"));
                roots.Add((Path.Combine(self.RootDir, "ShaderPacks"), "内置"));
            }

            foreach (ModContentPack mod in mods)
            {
                if (ReferenceEquals(mod, self))
                    continue;
                string label = string.IsNullOrWhiteSpace(mod.Name) ? mod.PackageIdPlayerFacing : mod.Name;
                roots.Add((Path.Combine(mod.RootDir, "1.7", "ShaderPacks"), $"模组: {label}"));
                roots.Add((Path.Combine(mod.RootDir, "ShaderPacks"), $"模组: {label}"));
            }
        }
        catch
        {
            // Headless / offline test environments have no LoadedModManager state.
        }

        string? localDir = TryGetLocalUserPacksDirectory();
        if (!string.IsNullOrWhiteSpace(localDir))
            roots.Add((localDir, "本地"));

        return ScanRoots(roots);
    }

    private static ShaderPackEntry LoadEntrySafe(string packDir, string sourceLabel)
    {
        string folderName = Path.GetFileName(packDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        try
        {
            ShaderPackEntry entry = ShaderPackManifest.LoadFromDirectory(packDir, sourceLabel);
            if (!ShaderPackGraphValidator.TryValidate(entry.Spec, out string? validationError))
            {
                return entry with { LoadError = validationError ?? "Invalid render graph." };
            }
            return entry;
        }
        catch (Exception ex)
        {
            TryLogWarning($"[RimLumen] Failed to load shader pack '{packDir}': {ex.Message}");
            return new ShaderPackEntry(
                $"invalid:{folderName}",
                folderName,
                "Unknown",
                string.Empty,
                sourceLabel,
                packDir,
                ShaderPackSpec.Default,
                Array.Empty<ShaderPackOptionDefinition>(),
                ex.Message);
        }
    }

    private static void TryLogWarning(string message)
    {
        try { Log.Warning(message); }
        catch { }
    }
}
