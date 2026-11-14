using System;
using System.Collections.Generic;

namespace RimLumen.Core.Rendering.Logic;

public static class ShaderPackGraphValidator
{
    private const string PrevPrefix = "prev:";
    private static readonly string[] ReservedSourceNames =
    [
        "hdr",
        "bloom",
        "scene",
        "depth",
        "mapLightField",
        "backbuffer",
    ];

    public static bool TryValidate(ShaderPackSpec? spec, out string? error)
    {
        try
        {
            if (spec is null)
            {
                error = "ShaderPackSpec cannot be null.";
                return false;
            }
            Validate(spec);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static void Validate(ShaderPackSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.BloomMipLevels is < 0 or > 7)
            throw new ArgumentException($"BloomMipLevels must be in [0, 7]; got {spec.BloomMipLevels}.", nameof(spec));
        if (spec.Targets is null)
            throw new ArgumentException("ShaderPackSpec.Targets cannot be null.", nameof(spec));
        if (spec.Passes is null)
            throw new ArgumentException("ShaderPackSpec.Passes cannot be null.", nameof(spec));

        var targetsByName = new Dictionary<string, ShaderPackTargetSpec>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < spec.Targets.Count; i++)
        {
            ShaderPackTargetSpec target = spec.Targets[i]
                ?? throw new ArgumentException($"Targets[{i}] cannot be null.", nameof(spec));
            if (string.IsNullOrWhiteSpace(target.Name))
                throw new ArgumentException($"Targets[{i}].Name cannot be empty.", nameof(spec));
            if (IsReservedName(target.Name))
                throw new ArgumentException($"Target name '{target.Name}' is reserved.", nameof(spec));
            if (target.Name.StartsWith(PrevPrefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Target name '{target.Name}' cannot start with '{PrevPrefix}'.", nameof(spec));
            if (!Enum.IsDefined(target.Format))
                throw new ArgumentException($"Targets[{i}] has unknown format {target.Format}.", nameof(spec));
            if (!Enum.IsDefined(target.Filter))
                throw new ArgumentException($"Targets[{i}] has unknown filter {target.Filter}.", nameof(spec));
            if (!float.IsFinite(target.Scale) || target.Scale <= 0.05f || target.Scale > 1.0f)
                throw new ArgumentException($"Target '{target.Name}' Scale must be in (0.05, 1.0]; got {target.Scale}.", nameof(spec));
            if (!targetsByName.TryAdd(target.Name, target))
                throw new ArgumentException($"Duplicate target name '{target.Name}'.", nameof(spec));
        }

        if (spec.Passes.Count == 0)
            return;

        var writtenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int lastIndex = spec.Passes.Count - 1;
        for (int i = 0; i < spec.Passes.Count; i++)
        {
            ShaderPackPassSpec pass = spec.Passes[i]
                ?? throw new ArgumentException($"Passes[{i}] cannot be null.", nameof(spec));
            if (string.IsNullOrWhiteSpace(pass.Name))
                throw new ArgumentException($"Passes[{i}].Name cannot be empty.", nameof(spec));
            if (string.IsNullOrWhiteSpace(pass.OutputTarget))
                throw new ArgumentException($"Pass '{pass.Name}' OutputTarget cannot be empty.", nameof(spec));
            if (!Enum.IsDefined(pass.BlendMode))
                throw new ArgumentException($"Pass '{pass.Name}' has unknown BlendMode {pass.BlendMode}.", nameof(spec));

            bool isBackbuffer = string.Equals(pass.OutputTarget, "backbuffer", StringComparison.OrdinalIgnoreCase);
            if (i == lastIndex)
            {
                if (!isBackbuffer)
                    throw new ArgumentException($"Final pass '{pass.Name}' must target 'backbuffer'; got '{pass.OutputTarget}'.", nameof(spec));
            }
            else
            {
                if (isBackbuffer)
                    throw new ArgumentException($"Only the final pass may target 'backbuffer'; pass '{pass.Name}' is at index {i}.", nameof(spec));
                if (!targetsByName.ContainsKey(pass.OutputTarget))
                    throw new ArgumentException($"Pass '{pass.Name}' targets undeclared render target '{pass.OutputTarget}'.", nameof(spec));
                if (string.IsNullOrWhiteSpace(pass.FragmentShaderPath))
                    throw new ArgumentException($"Offscreen pass '{pass.Name}' must specify a non-empty FragmentShaderPath.", nameof(spec));
                if (pass.BlendMode != ShaderPackBlendMode.Opaque)
                    throw new ArgumentException($"Offscreen pass '{pass.Name}' must use BlendMode.Opaque; got {pass.BlendMode}.", nameof(spec));
            }

            if (pass.Inputs is null)
                throw new ArgumentException($"Pass '{pass.Name}' Inputs cannot be null.", nameof(spec));

            var usedBindings = new HashSet<int>();
            for (int b = 0; b < pass.Inputs.Count; b++)
            {
                ShaderPackInputBinding input = pass.Inputs[b]
                    ?? throw new ArgumentException($"Pass '{pass.Name}' Inputs[{b}] cannot be null.", nameof(spec));
                if (input.Binding is < 0 or > 15)
                    throw new ArgumentException($"Pass '{pass.Name}' binding {input.Binding} is out of range [0, 15].", nameof(spec));
                if (input.Binding == 5)
                    throw new ArgumentException($"Pass '{pass.Name}' cannot bind input to reserved UBO slot 5.", nameof(spec));
                if (!usedBindings.Add(input.Binding))
                    throw new ArgumentException($"Pass '{pass.Name}' declares duplicate input binding {input.Binding}.", nameof(spec));
                if (string.IsNullOrWhiteSpace(input.Source))
                    throw new ArgumentException($"Pass '{pass.Name}' binding {input.Binding} has empty Source.", nameof(spec));

                ValidateInputSource(pass.Name, pass.OutputTarget, input.Source, targetsByName, writtenTargets);
            }

            if (!isBackbuffer)
                writtenTargets.Add(pass.OutputTarget);
        }
    }

    private static void ValidateInputSource(
        string passName,
        string outputTarget,
        string source,
        Dictionary<string, ShaderPackTargetSpec> targetsByName,
        HashSet<string> writtenTargets)
    {
        if (string.Equals(source, "backbuffer", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Pass '{passName}' cannot sample 'backbuffer' as an input.");

        if (IsReservedInputSource(source))
            return;

        if (source.StartsWith(PrevPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string targetName = source[PrevPrefix.Length..];
            if (string.IsNullOrWhiteSpace(targetName) || !targetsByName.TryGetValue(targetName, out ShaderPackTargetSpec? prevTarget))
                throw new ArgumentException($"Pass '{passName}' references unknown previous-frame target '{source}'.");
            if (!prevTarget.Persistent)
                throw new ArgumentException($"Pass '{passName}' references '{source}', but target '{targetName}' is not marked Persistent.");
            return;
        }

        if (string.Equals(source, outputTarget, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Pass '{passName}' cannot read its own OutputTarget '{outputTarget}' in the same pass (use 'prev:{outputTarget}' for history).");

        if (!targetsByName.ContainsKey(source))
            throw new ArgumentException($"Pass '{passName}' references unknown input source '{source}'.");

        if (!writtenTargets.Contains(source))
            throw new ArgumentException($"Pass '{passName}' reads target '{source}' before any earlier pass has written it.");
    }

    private static bool IsReservedName(string name)
    {
        foreach (string reserved in ReservedSourceNames)
            if (string.Equals(name, reserved, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool IsReservedInputSource(string source)
    {
        return string.Equals(source, "hdr", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "bloom", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "scene", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "depth", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "mapLightField", StringComparison.OrdinalIgnoreCase);
    }
}
