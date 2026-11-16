using System;
using System.Collections.Generic;
using System.Numerics;

namespace RimLumen.Core.Rendering.Logic;

public static class ShaderPackGraphValidator
{
    private const string PrevPrefix = "prev:";
    private static readonly string[] ReservedSourceNames =
    [
        "hdr", "bloom", "scene", "depth", "mapLightField", "backbuffer",
    ];

    internal enum InputKind { Hdr, Bloom, Scene, Depth, MapLightField, Target }

    internal readonly record struct PlannedInput(int Binding, InputKind Kind, int TargetIndex = -1, int HistoryFramesAgo = 0);

    internal sealed record PlannedPass(
        string RuntimeName,
        string FragmentShaderPath,
        int OutputTargetIndex,
        IReadOnlyList<PlannedInput> Inputs,
        ShaderPackBlendMode BlendMode,
        Vector2 ExtraParams,
        bool UsesBuiltInComposite);

    // The only constructor is private; obtaining a plan always runs graph validation.
    internal sealed class ExecutionPlan
    {
        internal IReadOnlyList<ShaderPackTargetSpec> Targets { get; }
        internal IReadOnlyList<PlannedPass> Passes { get; }
        internal int BloomMipLevels { get; }
        internal string BloomPrefilterShaderPath { get; }
        internal string BloomDownShaderPath { get; }
        internal string BloomUpShaderPath { get; }
        internal bool HasCustomBloomShader { get; }

        private ExecutionPlan(ShaderPackSpec spec, ShaderPackTargetSpec[] targets, PlannedPass[] passes)
        {
            Targets = Array.AsReadOnly(targets);
            Passes = Array.AsReadOnly(passes);
            BloomMipLevels = spec.BloomMipLevels;
            HasCustomBloomShader = !string.IsNullOrWhiteSpace(spec.BloomPrefilterShaderPath)
                || !string.IsNullOrWhiteSpace(spec.BloomDownShaderPath)
                || !string.IsNullOrWhiteSpace(spec.BloomUpShaderPath);
            BloomPrefilterShaderPath = ShaderPath(spec.BloomPrefilterShaderPath, "RimLumenBloomPrefilter.frag.glsl");
            BloomDownShaderPath = ShaderPath(spec.BloomDownShaderPath, "RimLumenBloomDown.frag.glsl");
            BloomUpShaderPath = ShaderPath(spec.BloomUpShaderPath, "RimLumenBloomUp.frag.glsl");
        }

        internal static ExecutionPlan Compile(ShaderPackSpec spec)
        {
            ArgumentNullException.ThrowIfNull(spec);
            if (spec.BloomMipLevels is < 0 or > 7)
                throw new ArgumentException($"BloomMipLevels must be in [0, 7]; got {spec.BloomMipLevels}.", nameof(spec));
            if (spec.Targets is null)
                throw new ArgumentException("ShaderPackSpec.Targets cannot be null.", nameof(spec));
            if (spec.Passes is null)
                throw new ArgumentException("ShaderPackSpec.Passes cannot be null.", nameof(spec));

            var targets = new ShaderPackTargetSpec[spec.Targets.Count];
            var targetsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targets.Length; i++)
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
                if (!targetsByName.TryAdd(target.Name, i))
                    throw new ArgumentException($"Duplicate target name '{target.Name}'.", nameof(spec));
                targets[i] = target;
            }

            string compositePath = ShaderPath(spec.CompositeShaderPath, "RimLumenComposite.frag.glsl");
            if (spec.Passes.Count == 0)
                return new ExecutionPlan(spec, targets,
                [new PlannedPass("RimLumenComposite", compositePath, -1, DefaultInputs(), ShaderPackBlendMode.PremultipliedAlpha, default, true)]);

            var passes = new PlannedPass[spec.Passes.Count];
            var writtenTargets = new HashSet<int>();
            int lastIndex = passes.Length - 1;
            for (int i = 0; i < passes.Length; i++)
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
                int outputIndex = -1;
                if (i == lastIndex)
                {
                    if (!isBackbuffer)
                        throw new ArgumentException($"Final pass '{pass.Name}' must target 'backbuffer'; got '{pass.OutputTarget}'.", nameof(spec));
                }
                else
                {
                    if (isBackbuffer)
                        throw new ArgumentException($"Only the final pass may target 'backbuffer'; pass '{pass.Name}' is at index {i}.", nameof(spec));
                    if (!targetsByName.TryGetValue(pass.OutputTarget, out outputIndex))
                        throw new ArgumentException($"Pass '{pass.Name}' targets undeclared render target '{pass.OutputTarget}'.", nameof(spec));
                    if (string.IsNullOrWhiteSpace(pass.FragmentShaderPath))
                        throw new ArgumentException($"Offscreen pass '{pass.Name}' must specify a non-empty FragmentShaderPath.", nameof(spec));
                    if (pass.BlendMode != ShaderPackBlendMode.Opaque)
                        throw new ArgumentException($"Offscreen pass '{pass.Name}' must use BlendMode.Opaque; got {pass.BlendMode}.", nameof(spec));
                }

                if (pass.Inputs is null)
                    throw new ArgumentException($"Pass '{pass.Name}' Inputs cannot be null.", nameof(spec));

                var inputs = new PlannedInput[pass.Inputs.Count];
                var usedBindings = new HashSet<int>();
                for (int b = 0; b < inputs.Length; b++)
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
                    inputs[b] = CompileInput(pass.Name, pass.OutputTarget, input, targetsByName, targets, writtenTargets);
                }

                bool builtInComposite = isBackbuffer && string.IsNullOrWhiteSpace(pass.FragmentShaderPath);
                passes[i] = new PlannedPass(
                    isBackbuffer ? "RimLumenPackComposite" : $"RimLumenPackPass.{i}",
                    isBackbuffer ? ShaderPath(pass.FragmentShaderPath, compositePath) : pass.FragmentShaderPath!,
                    outputIndex,
                    builtInComposite && inputs.Length == 0 ? DefaultInputs() : Array.AsReadOnly(inputs),
                    pass.BlendMode, pass.ExtraParams, builtInComposite);
                if (!isBackbuffer)
                    writtenTargets.Add(outputIndex);
            }
            return new ExecutionPlan(spec, targets, passes);
        }
    }

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

    public static void Validate(ShaderPackSpec spec) => ExecutionPlan.Compile(spec);

    private static PlannedInput CompileInput(
        string passName,
        string outputTarget,
        ShaderPackInputBinding input,
        Dictionary<string, int> targetsByName,
        ShaderPackTargetSpec[] targets,
        HashSet<int> writtenTargets)
    {
        string source = input.Source;
        if (string.Equals(source, "backbuffer", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Pass '{passName}' cannot sample 'backbuffer' as an input.");

        InputKind? kind = source switch
        {
            _ when string.Equals(source, "hdr", StringComparison.OrdinalIgnoreCase) => InputKind.Hdr,
            _ when string.Equals(source, "bloom", StringComparison.OrdinalIgnoreCase) => InputKind.Bloom,
            _ when string.Equals(source, "scene", StringComparison.OrdinalIgnoreCase) => InputKind.Scene,
            _ when string.Equals(source, "depth", StringComparison.OrdinalIgnoreCase) => InputKind.Depth,
            _ when string.Equals(source, "mapLightField", StringComparison.OrdinalIgnoreCase) => InputKind.MapLightField,
            _ => null,
        };
        if (kind is { } reservedKind)
            return new PlannedInput(input.Binding, reservedKind);

        if (source.StartsWith(PrevPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string targetName = source[PrevPrefix.Length..];
            if (string.IsNullOrWhiteSpace(targetName) || !targetsByName.TryGetValue(targetName, out int prevIndex))
                throw new ArgumentException($"Pass '{passName}' references unknown previous-frame target '{source}'.");
            if (!targets[prevIndex].Persistent)
                throw new ArgumentException($"Pass '{passName}' references '{source}', but target '{targetName}' is not marked Persistent.");
            return new PlannedInput(input.Binding, InputKind.Target, prevIndex, 1);
        }

        if (string.Equals(source, outputTarget, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Pass '{passName}' cannot read its own OutputTarget '{outputTarget}' in the same pass (use 'prev:{outputTarget}' for history).");
        if (!targetsByName.TryGetValue(source, out int targetIndex))
            throw new ArgumentException($"Pass '{passName}' references unknown input source '{source}'.");
        if (!writtenTargets.Contains(targetIndex))
            throw new ArgumentException($"Pass '{passName}' reads target '{source}' before any earlier pass has written it.");
        return new PlannedInput(input.Binding, InputKind.Target, targetIndex);
    }

    private static IReadOnlyList<PlannedInput> DefaultInputs() => Array.AsReadOnly<PlannedInput>(
    [
        new(0, InputKind.Hdr), new(1, InputKind.Bloom), new(2, InputKind.Scene),
        new(3, InputKind.Depth), new(4, InputKind.MapLightField),
    ]);

    private static string ShaderPath(string? path, string fallback) => string.IsNullOrWhiteSpace(path) ? fallback : path;

    private static bool IsReservedName(string name)
    {
        foreach (string reserved in ReservedSourceNames)
            if (string.Equals(name, reserved, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
