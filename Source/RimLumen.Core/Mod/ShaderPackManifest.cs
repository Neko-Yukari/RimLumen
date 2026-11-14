using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimLumen.Core.Rendering;

namespace RimLumen.Core.Mod;

public enum ShaderPackOptionTarget
{
    BloomIntensity,
    BloomThreshold,
    BloomKnee,
    BloomScatter,
    PackParam0,
    PackParam1,
    PackParam2,
    PackParam3,
    PackParam4,
    PackParam5,
    PackParam6,
    PackParam7,
    PackParam8,
    PackParam9,
    PackParam10,
    PackParam11,
    PackParam12,
    PackParam13,
    PackParam14,
    PackParam15,
}

public sealed record ShaderPackOptionDefinition(
    string Key,
    string Label,
    ShaderPackOptionTarget Target,
    float DefaultValue,
    float MinValue,
    float MaxValue,
    float Step,
    string? Tooltip);

public sealed record ShaderPackEntry(
    string Id,
    string Name,
    string Author,
    string Description,
    string SourceLabel,
    string? DirectoryPath,
    ShaderPackSpec Spec,
    IReadOnlyList<ShaderPackOptionDefinition> Options,
    string? LoadError = null);

public static class ShaderPackManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    internal sealed class ManifestDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("author")] public string? Author { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("runOnEmptyFrames")] public bool RunOnEmptyFrames { get; set; }
        [JsonPropertyName("requiresSceneDepth")] public bool RequiresSceneDepth { get; set; }
        [JsonPropertyName("requiresMapLightField")] public bool RequiresMapLightField { get; set; }
        [JsonPropertyName("bloomMipLevels")] public int? BloomMipLevels { get; set; }
        [JsonPropertyName("compositeShader")] public string? CompositeShader { get; set; }
        [JsonPropertyName("bloomPrefilterShader")] public string? BloomPrefilterShader { get; set; }
        [JsonPropertyName("bloomDownShader")] public string? BloomDownShader { get; set; }
        [JsonPropertyName("bloomUpShader")] public string? BloomUpShader { get; set; }
        [JsonPropertyName("targets")] public List<TargetDto>? Targets { get; set; }
        [JsonPropertyName("passes")] public List<PassDto>? Passes { get; set; }
        [JsonPropertyName("options")] public List<OptionDto>? Options { get; set; }
    }

    internal sealed class TargetDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("scale")] public float? Scale { get; set; }
        [JsonPropertyName("filter")] public string? Filter { get; set; }
        [JsonPropertyName("persistent")] public bool Persistent { get; set; }
    }

    internal sealed class InputDto
    {
        [JsonPropertyName("binding")] public int Binding { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
    }

    internal sealed class PassDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("shader")] public string? Shader { get; set; }
        [JsonPropertyName("output")] public string? Output { get; set; }
        [JsonPropertyName("blend")] public string? Blend { get; set; }
        [JsonPropertyName("extraParams")] public float[]? ExtraParams { get; set; }
        [JsonPropertyName("inputs")] public List<InputDto>? Inputs { get; set; }
    }

    internal sealed class OptionDto
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("target")] public string? Target { get; set; }
        [JsonPropertyName("default")] public float Default { get; set; }
        [JsonPropertyName("min")] public float Min { get; set; }
        [JsonPropertyName("max")] public float Max { get; set; } = 1.0f;
        [JsonPropertyName("step")] public float Step { get; set; }
        [JsonPropertyName("tooltip")] public string? Tooltip { get; set; }
    }

    public static ShaderPackEntry LoadFromDirectory(string packDirectory, string sourceLabel)
    {
        string fullDir = Path.GetFullPath(packDirectory);
        string folderName = Path.GetFileName(fullDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string manifestPath = Path.Combine(fullDir, "pack.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Missing pack.json in '{fullDir}'.", manifestPath);

        string json = File.ReadAllText(manifestPath);
        ManifestDto dto = JsonSerializer.Deserialize<ManifestDto>(json, JsonOptions)
            ?? throw new InvalidDataException($"Empty or invalid JSON in '{manifestPath}'.");

        string id = string.IsNullOrWhiteSpace(dto.Id) ? folderName : dto.Id.Trim();
        if (string.Equals(id, "default", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Shader pack id 'default' is reserved for the built-in Standard baseline.");

        string name = string.IsNullOrWhiteSpace(dto.Name) ? id : dto.Name.Trim();
        string author = string.IsNullOrWhiteSpace(dto.Author) ? "Unknown" : dto.Author.Trim();
        string description = dto.Description?.Trim() ?? string.Empty;

        var targets = new List<ShaderPackTargetSpec>();
        if (dto.Targets is not null)
        {
            foreach (TargetDto t in dto.Targets)
            {
                targets.Add(new ShaderPackTargetSpec(
                    t.Name ?? string.Empty,
                    ParseEnum(t.Format, ShaderPackTargetFormat.R16G16B16A16SFloat, "target format"),
                    t.Scale ?? 1.0f,
                    ParseEnum(t.Filter, ShaderPackTargetFilter.Linear, "target filter"),
                    t.Persistent));
            }
        }

        var passes = new List<ShaderPackPassSpec>();
        if (dto.Passes is not null)
        {
            foreach (PassDto p in dto.Passes)
            {
                var inputs = new List<ShaderPackInputBinding>();
                if (p.Inputs is not null)
                {
                    foreach (InputDto input in p.Inputs)
                        inputs.Add(new ShaderPackInputBinding(input.Binding, input.Source ?? string.Empty));
                }

                Vector2 extra = Vector2.Zero;
                if (p.ExtraParams is not null)
                {
                    if (p.ExtraParams.Length > 2)
                        throw new InvalidDataException($"Pass '{p.Name}' extraParams supports at most 2 floats.");
                    extra = new Vector2(
                        p.ExtraParams.Length > 0 ? p.ExtraParams[0] : 0f,
                        p.ExtraParams.Length > 1 ? p.ExtraParams[1] : 0f);
                }

                passes.Add(new ShaderPackPassSpec(
                    p.Name ?? string.Empty,
                    ResolvePackShaderPath(fullDir, p.Shader),
                    p.Output ?? string.Empty,
                    inputs,
                    ParseEnum(p.Blend, ShaderPackBlendMode.Opaque, "blend mode"),
                    extra));
            }
        }

        var options = new List<ShaderPackOptionDefinition>();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedTargets = new HashSet<ShaderPackOptionTarget>();
        if (dto.Options is not null)
        {
            foreach (OptionDto o in dto.Options)
            {
                string key = string.IsNullOrWhiteSpace(o.Key)
                    ? throw new InvalidDataException("Option key cannot be empty.")
                    : o.Key.Trim();
                if (!usedKeys.Add(key))
                    throw new InvalidDataException($"Duplicate option key '{key}'.");

                ShaderPackOptionTarget target = ParseOptionTarget(o.Target);
                if (!usedTargets.Add(target))
                    throw new InvalidDataException($"Duplicate option target '{target}'.");

                if (!float.IsFinite(o.Min) || !float.IsFinite(o.Max) || !float.IsFinite(o.Default) || o.Min > o.Max)
                    throw new InvalidDataException($"Option '{key}' has invalid numeric range [{o.Min}, {o.Max}].");
                if (!float.IsFinite(o.Step) || o.Step < 0f)
                    throw new InvalidDataException($"Option '{key}' step must be >= 0.");

                float clampedDefault = Math.Clamp(o.Default, o.Min, o.Max);
                string label = string.IsNullOrWhiteSpace(o.Label) ? key : o.Label.Trim();
                options.Add(new ShaderPackOptionDefinition(
                    key, label, target, clampedDefault, o.Min, o.Max, o.Step, o.Tooltip?.Trim()));
            }
        }

        var spec = new ShaderPackSpec
        {
            Id = id,
            CompositeShaderPath = ResolvePackShaderPath(fullDir, dto.CompositeShader),
            BloomPrefilterShaderPath = ResolvePackShaderPath(fullDir, dto.BloomPrefilterShader),
            BloomDownShaderPath = ResolvePackShaderPath(fullDir, dto.BloomDownShader),
            BloomUpShaderPath = ResolvePackShaderPath(fullDir, dto.BloomUpShader),
            RunOnEmptyFrames = dto.RunOnEmptyFrames,
            RequiresSceneDepth = dto.RequiresSceneDepth,
            RequiresMapLightField = dto.RequiresMapLightField,
            BloomMipLevels = dto.BloomMipLevels ?? 5,
            Targets = targets,
            Passes = passes,
        };

        return new ShaderPackEntry(id, name, author, description, sourceLabel, fullDir, spec, options);
    }

    internal static string? ResolvePackShaderPath(string packDirectory, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        string trimmed = relativePath.Trim();
        if (Path.IsPathRooted(trimmed))
            throw new InvalidDataException($"Shader path '{trimmed}' must be relative to the pack directory.");

        string packRoot = Path.GetFullPath(packDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string combined = Path.GetFullPath(Path.Combine(packDirectory, trimmed));
        if (!combined.StartsWith(packRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Shader path '{trimmed}' escapes the pack directory.");

        // A symlink/junction inside the pack can lead anywhere, so compare physical locations. The root is resolved
        // too: the mod itself may be mounted through a junction (Mods\RimLumen), which must keep its packs valid.
        string physicalRoot = ResolvePhysicalPath(packRoot);
        if (!ResolvePhysicalPath(combined).StartsWith(physicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Shader path '{trimmed}' escapes the pack directory through a symlink or junction.");

        return combined;
    }

    // Ensures: every symlink/junction in the existing segments of an absolute path is replaced by its final target;
    //   a missing tail is kept as written.
    private static string ResolvePhysicalPath(string fullPath)
    {
        const int maxLinkHops = 32;
        string current = fullPath;
        for (int hop = 0; hop < maxLinkHops; hop++)
        {
            string? redirected = RedirectFirstLink(current);
            if (redirected is null)
                return current;
            current = redirected;
        }
        throw new InvalidDataException($"Too many nested symlinks or junctions while resolving '{fullPath}'.");
    }

    private static string? RedirectFirstLink(string fullPath)
    {
        string root = Path.GetPathRoot(fullPath)!;
        string[] segments = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        string prefix = root;
        for (int i = 0; i < segments.Length; i++)
        {
            prefix = Path.Combine(prefix, segments[i]);
            // ResolveLinkTarget throws for a missing path, and nothing below a missing segment can be a link.
            if (!File.Exists(prefix) && !Directory.Exists(prefix))
                return null;
            FileSystemInfo? target = new DirectoryInfo(prefix).ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
                return Path.GetFullPath(Path.Combine([target.FullName, .. segments[(i + 1)..]]));
        }
        return null;
    }

    private static TEnum ParseEnum<TEnum>(string? raw, TEnum fallback, string label) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (Enum.TryParse<TEnum>(raw.Trim(), ignoreCase: true, out TEnum value) && Enum.IsDefined(value))
            return value;
        throw new InvalidDataException($"Unknown {label} '{raw}'.");
    }

    private static ShaderPackOptionTarget ParseOptionTarget(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidDataException("Option target cannot be empty.");
        if (Enum.TryParse<ShaderPackOptionTarget>(raw.Trim(), ignoreCase: true, out ShaderPackOptionTarget target)
            && Enum.IsDefined(target))
            return target;
        throw new InvalidDataException($"Unknown option target '{raw}'.");
    }
}
