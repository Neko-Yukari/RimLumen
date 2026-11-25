using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using RimLumen.Core.Contracts;
using RimLumen.Core.Rendering;
using Waylith.Rendering;

namespace RimLumen.Core.Compat;

// Compiles and caches alpha-clamped fragment shader variants for third-party CoverageAlpha (AlphaBlend / Premultiplied)
// draws entering RimLumen's RGBA16F HDR layer. Vulkan RGBA16F does not clamp fragment alpha to [0,1] before blend
// evaluation; unclamped a > 1 or a < 0 makes OneMinusSrcAlpha negative or > 1 and corrupts or punches holes in the HDR
// buffer. Any shader whose fragment source cannot be verified and safely wrapped is rejected so it stays on MapMain.
internal static class ExternalHdrAlphaClampShaders
{
    internal const string LinearProgramMarker = "// @rimunity-linear-program 1";
    private const string LinearClampedKeySuffix = "_RimLumenHdrAlphaClamp";
    private const string LegacyClampedKeySuffix = "_RimLumenLegacyAlphaClamp";

    // SPIR-V binary constants for verifying fragment output interface after preprocessor expansion.
    private const uint SpirvMagic = 0x07230203u;
    private const ushort OpTypeFloat = 22;
    private const ushort OpTypeVector = 23;
    private const ushort OpTypePointer = 32;
    private const ushort OpVariable = 59;
    private const ushort OpDecorate = 71;
    private const uint StorageClassOutput = 3u;
    private const uint DecorationBuiltIn = 11u;
    private const uint DecorationLocation = 30u;
    private const uint DecorationIndex = 32u;

    private static readonly Regex LayoutOutDeclRegex = new(
        @"\blayout\s*\(([^)]*)\)\s*(?:(?:flat|noperspective|smooth|centroid|sample|invariant|precise|highp|mediump|lowp)\s+)*out\s+(\w+)\s+(\w+)\s*(\[[^\]]*\])?\s*;",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FragmentOutputBlockRegex = new(
        @"\blayout\s*\([^)]*\)\s*out\s+\w+\s*\{",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LocationZeroRegex = new(
        @"\blocation\s*=\s*0\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex IndexAttrRegex = new(
        @"\bindex\s*=",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LinearMainWrapperRegex = new(
        @"\bvoid\s+main\s*\(\s*\)\s*\{\s*RU_SourceMain\s*\(\s*\)\s*;\s*(\w+)\.rgb\s*=\s*RU_Encode\s*\(\s*\1\.rgb\s*\)\s*;\s*\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PlainMainDefRegex = new(
        @"(\bvoid\s+)main(\s*\(\s*(?:void\s*)?\)\s*\{)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AnyMainCallOrDeclRegex = new(
        @"\bmain\s*\(",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PreprocessorConditionalRegex = new(
        @"(?m)^\s*#\s*(?:if|ifdef|ifndef|elif|else|include)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SuspiciousMacroDirectiveRegex = new(
        @"(?m)^\s*#\s*(?:undef\b|define\b[^\r\n]*(?:\\|\b(?:out|layout|main)\b|##))",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private sealed class ClampedSlot(
        Shader? shader,
        MaterialLayout sourceLayout,
        ShaderVertexInputLayout? sourceVertexInputLayout)
    {
        public readonly Shader? Shader = shader;
        public readonly MaterialLayout SourceLayout = sourceLayout;
        public readonly ShaderVertexInputLayout? SourceVertexInputLayout = sourceVertexInputLayout;
    }

    private sealed class ClampedSlots
    {
        public ClampedSlot? Legacy;
        public ClampedSlot? Linear;
    }

    private static readonly ConditionalWeakTable<Shader, ClampedSlots> Cache = new();
    private static readonly HashSet<string> LoggedFailures = new(StringComparer.Ordinal);

    // Invalidates cached positive and negative results for a reloaded Waylith Shader instance (Shader.ReloadLayouts).
    internal static void Invalidate(Shader? shader)
    {
        if (shader is null) return;
        Cache.Remove(shader);
        lock (LoggedFailures)
        {
            LoggedFailures.Remove($"{shader.Key}|{LumenColorBlendSpace.Legacy}");
            LoggedFailures.Remove($"{shader.Key}|{LumenColorBlendSpace.Linear}");
        }
    }

    internal static void InvalidateAll()
    {
        Cache.Clear();
        lock (LoggedFailures)
            LoggedFailures.Clear();
    }

    // Ensures: returns true iff key belongs to an in-memory transformed RimUnity shader variant (MappedColorSpaceShader
    //          or SkinnedShaderVariants) whose Program.fragmentShaderFileName still points to an untransformed disk file.
    internal static bool IsInMemoryTransformedShaderKey(string? key) =>
        !string.IsNullOrEmpty(key)
        && (key.EndsWith("_Linear", StringComparison.Ordinal)
            || key.EndsWith("_Linear_HdrOutput", StringComparison.Ordinal)
            || key.EndsWith("_GpuSkin", StringComparison.Ordinal)
            || key.EndsWith("_GpuSkin_HdrOutput", StringComparison.Ordinal));

    // Ensures: returns true with a cached immutable alpha-clamped Shader matching sourceShader's MaterialLayout and
    //          VertexInputLayout, or false (cached negatively) when the shader cannot be safely alpha-clamped.
    internal static bool TryResolve(Shader sourceShader, LumenColorBlendSpace blendSpace, out Shader targetShader)
    {
        ArgumentNullException.ThrowIfNull(sourceShader);
        if (!ExternalHdrMaterialRules.TryResolveShaderForBlendSpace(sourceShader, blendSpace, out Shader baseTarget))
        {
            targetShader = null!;
            return false;
        }

        MaterialLayout liveLayout = baseTarget.MaterialLayout;
        ShaderVertexInputLayout? liveVertexLayout = baseTarget.VertexInputLayout;
        ClampedSlots slots = Cache.GetValue(baseTarget, static _ => new ClampedSlots());
        ClampedSlot? slot = blendSpace == LumenColorBlendSpace.Linear
            ? Volatile.Read(ref slots.Linear)
            : Volatile.Read(ref slots.Legacy);
        if (slot is null
            || !ReferenceEquals(slot.SourceLayout, liveLayout)
            || !ReferenceEquals(slot.SourceVertexInputLayout, liveVertexLayout))
        {
            lock (slots)
            {
                slot = blendSpace == LumenColorBlendSpace.Linear ? slots.Linear : slots.Legacy;
                if (slot is null
                    || !ReferenceEquals(slot.SourceLayout, liveLayout)
                    || !ReferenceEquals(slot.SourceVertexInputLayout, liveVertexLayout))
                {
                    Shader? built = TryBuildClampedShader(baseTarget, blendSpace);
                    slot = new ClampedSlot(built, liveLayout, liveVertexLayout);
                    if (blendSpace == LumenColorBlendSpace.Linear) Volatile.Write(ref slots.Linear, slot);
                    else Volatile.Write(ref slots.Legacy, slot);
                }
            }
        }

        if (slot.Shader is null)
        {
            targetShader = null!;
            return false;
        }

        targetShader = slot.Shader;
        return true;
    }

    // Pure GLSL fragment rewriter for offline unit tests and live runtime compilation.
    // Ensures: returns true iff fragmentSource has a unique location=0 vec4 output and a uniquely verifiable main()
    //          entry point, producing rewrittenSource that clamps outVar.a to [0,1] (zeroing NaN) without altering
    //          any uniform, sampler, vertex-input, or RGB math (except stripping RU_Encode in Linear mode).
    internal static bool TryRewriteFragmentForAlphaClamp(
        string fragmentSource,
        LumenColorBlendSpace blendSpace,
        bool allowPlainDiskRewrite,
        out string rewrittenSource)
    {
        rewrittenSource = string.Empty;
        if (string.IsNullOrWhiteSpace(fragmentSource)
            || blendSpace is not (LumenColorBlendSpace.Legacy or LumenColorBlendSpace.Linear))
        {
            return false;
        }

        bool hasLinearMarker = fragmentSource.Contains(LinearProgramMarker, StringComparison.Ordinal);
        string masked = MaskComments(fragmentSource);

        if (!TryGetUniqueSingleColorOutput(masked, out string outVar))
            return false;

        if (hasLinearMarker)
        {
            if (SuspiciousMacroDirectiveRegex.IsMatch(masked)
                || masked.Contains("_lumenAlpha", StringComparison.Ordinal))
            {
                return false;
            }

            MatchCollection wrappers = LinearMainWrapperRegex.Matches(masked);
            if (wrappers.Count != 1 || AnyMainCallOrDeclRegex.Matches(masked).Count != 1)
                return false;

            Match wrapper = wrappers[0];
            if (!string.Equals(wrapper.Groups[1].Value, outVar, StringComparison.Ordinal))
                return false;

            string clampStmt = $"float _lumenAlpha = {outVar}.a; {outVar}.a = isnan(_lumenAlpha) ? 0.0 : clamp(_lumenAlpha, 0.0, 1.0);";
            string replacement = blendSpace == LumenColorBlendSpace.Linear
                ? $"void main() {{ RU_SourceMain(); {clampStmt} }}"
                : $"void main() {{ RU_SourceMain(); {outVar}.rgb = RU_Encode({outVar}.rgb); {clampStmt} }}";

            rewrittenSource = string.Concat(
                fragmentSource.AsSpan(0, wrapper.Index),
                replacement.AsSpan(),
                fragmentSource.AsSpan(wrapper.Index + wrapper.Length));
            return true;
        }

        // Plain disk rewrite only applies to Legacy/Gamma shaders whose disk source matches the live program.
        // In Linear mode, a shader lacking LinearProgramMarker is not a disk-backed Linear program (e.g. an in-memory
        // MappedColorSpaceShader _Linear_HdrOutput whose disk file is still Gamma) and must not be rewritten from disk.
        if (!allowPlainDiskRewrite || blendSpace != LumenColorBlendSpace.Legacy)
            return false;

        if (PreprocessorConditionalRegex.IsMatch(masked)
            || SuspiciousMacroDirectiveRegex.IsMatch(masked)
            || masked.Contains("_lumenSourceMain", StringComparison.Ordinal)
            || masked.Contains("_lumenAlpha", StringComparison.Ordinal))
        {
            return false;
        }

        MatchCollection mainDefs = PlainMainDefRegex.Matches(masked);
        if (mainDefs.Count != 1 || AnyMainCallOrDeclRegex.Matches(masked).Count != 1)
            return false;

        Match mainDef = mainDefs[0];
        string renamedHeader = mainDef.Groups[1].Value + "_lumenSourceMain" + mainDef.Groups[2].Value;
        string clampFooter = $"\nvoid main() {{ _lumenSourceMain(); float _lumenAlpha = {outVar}.a; {outVar}.a = isnan(_lumenAlpha) ? 0.0 : clamp(_lumenAlpha, 0.0, 1.0); }}\n";
        rewrittenSource = string.Concat(
            fragmentSource.AsSpan(0, mainDef.Index),
            renamedHeader.AsSpan(),
            fragmentSource.AsSpan(mainDef.Index + mainDef.Length),
            clampFooter.AsSpan());
        return true;
    }

    internal static bool HasPreprocessorConditional(string source) =>
        PreprocessorConditionalRegex.IsMatch(MaskComments(source));

    // Verifies on compiled SPIR-V binary (after all preprocessor macros have expanded) that the fragment shader
    // declares exactly one non-BuiltIn Output variable, at Location 0, with no dual-source Index decoration and type vec4.
    internal static bool SpirvHasUniqueLocation0Vec4Output(ReadOnlySpan<byte> spirv)
    {
        if (spirv.Length < 20 || (spirv.Length & 3) != 0)
            return false;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(spirv);
        if (magic != SpirvMagic)
            return false;

        var float32Types = new HashSet<uint>();
        var vec4CompTypes = new Dictionary<uint, uint>();
        var outputPtrTypes = new Dictionary<uint, uint>();
        var outputVars = new List<(uint Id, uint PointerTypeId)>();
        var builtInIds = new HashSet<uint>();
        var indexIds = new HashSet<uint>();
        var locations = new Dictionary<uint, uint>();

        int wordCount = spirv.Length >> 2;
        int pos = 5;
        while (pos < wordCount)
        {
            uint header = BinaryPrimitives.ReadUInt32LittleEndian(spirv.Slice(pos << 2, 4));
            int len = (int)(header >> 16);
            ushort op = (ushort)(header & 0xFFFFu);
            if (len <= 0 || pos + len > wordCount)
                return false;

            switch (op)
            {
                case OpTypeFloat when len >= 3:
                {
                    uint resultId = BinaryPrimitives.ReadUInt32LittleEndian(spirv.Slice((pos + 1) << 2, 4));
                    uint width = BinaryPrimitives.ReadUInt32LittleEndian(spirv.Slice((pos + 2) << 2, 4));
                    if (width == 32u) float32Types.Add(resultId);
                    break;
                }
                case OpTypeVector when len >= 4:
                {
                    uint resultId = BinaryPrimitives.ReadUInt32LittleEndian(spirv.Slice((pos + 1) << 2, 4));
                    uint compType = BinaryPrimitives.ReadUInt32LittleEndian(spirv.Slice((pos + 2) << 2, 4));
                    uint count = BinaryPrimitives.ReadUInt32LittleEndian(spirv.Slice((pos + 3) << 2, 4));
                    if (count == 4u) vec4CompTypes[resultId] = compType;
                    break;
                }
                case OpTypePointer when len >= 4:
                {
                    uint resultId = BinaryPrimitives.ReadUInt32LittleEndian(spirv.Slice((pos + 1) << 2, 4));
                    uint storageClass = BinaryPrimitives.ReadUInt32LittleEndian(spirv.Slice((pos + 2) << 2, 4));
                    uint pointee = BinaryPrimitives.ReadUInt32LittleEndian(spirv.Slice((pos + 3) << 2, 4));
                    if (storageClass == StorageClassOutput) outputPtrTypes[resultId] = pointee;
                    break;
                }
                case OpVariable when len >= 4:
                {
                    uint resultType = BinaryPrimitives.ReadUInt32LittleEndian(spirv.Slice((pos + 1) << 2, 4));
                    uint resultId = BinaryPrimitives.ReadUInt32LittleEndian(spirv.Slice((pos + 2) << 2, 4));
                    uint storageClass = BinaryPrimitives.ReadUInt32LittleEndian(spirv.Slice((pos + 3) << 2, 4));
                    if (storageClass == StorageClassOutput) outputVars.Add((resultId, resultType));
                    break;
                }
                case OpDecorate when len >= 3:
                {
                    uint target = BinaryPrimitives.ReadUInt32LittleEndian(spirv.Slice((pos + 1) << 2, 4));
                    uint decoration = BinaryPrimitives.ReadUInt32LittleEndian(spirv.Slice((pos + 2) << 2, 4));
                    if (decoration == DecorationBuiltIn)
                        builtInIds.Add(target);
                    else if (decoration == DecorationIndex)
                        indexIds.Add(target);
                    else if (decoration == DecorationLocation && len >= 4)
                        locations[target] = BinaryPrimitives.ReadUInt32LittleEndian(spirv.Slice((pos + 3) << 2, 4));
                    break;
                }
            }

            pos += len;
        }

        int colorOutputs = 0;
        uint uniqueColorId = 0;
        uint uniquePtrType = 0;
        for (int i = 0; i < outputVars.Count; i++)
        {
            (uint id, uint ptrType) = outputVars[i];
            if (builtInIds.Contains(id))
                continue;
            colorOutputs++;
            uniqueColorId = id;
            uniquePtrType = ptrType;
        }

        return colorOutputs == 1
            && !indexIds.Contains(uniqueColorId)
            && locations.TryGetValue(uniqueColorId, out uint loc)
            && loc == 0u
            && outputPtrTypes.TryGetValue(uniquePtrType, out uint pointeeType)
            && vec4CompTypes.TryGetValue(pointeeType, out uint elementType)
            && float32Types.Contains(elementType);
    }

    private static bool TryGetUniqueSingleColorOutput(string maskedSource, out string outputVar)
    {
        outputVar = string.Empty;
        if (FragmentOutputBlockRegex.IsMatch(maskedSource))
            return false;

        MatchCollection matches = LayoutOutDeclRegex.Matches(maskedSource);
        if (matches.Count != 1)
            return false;

        Match match = matches[0];
        string attrs = match.Groups[1].Value;
        string typeName = match.Groups[2].Value;
        string varName = match.Groups[3].Value;
        string arraySuffix = match.Groups[4].Value;

        if (!LocationZeroRegex.IsMatch(attrs)
            || IndexAttrRegex.IsMatch(attrs)
            || !string.Equals(typeName, "vec4", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(arraySuffix))
        {
            return false;
        }

        outputVar = varName;
        return true;
    }

    // Index-preserving comment masking: replaces // and /* */ comment bodies with spaces while keeping newlines
    // intact so regex match indices on the masked copy correspond 1:1 to offsets in the original source.
    private static string MaskComments(string source)
    {
        char[] buffer = source.ToCharArray();
        int i = 0;
        int n = buffer.Length;
        while (i < n)
        {
            if (i + 1 < n && buffer[i] == '/' && buffer[i + 1] == '/')
            {
                buffer[i] = ' ';
                buffer[i + 1] = ' ';
                i += 2;
                while (i < n && buffer[i] != '\r' && buffer[i] != '\n')
                    buffer[i++] = ' ';
            }
            else if (i + 1 < n && buffer[i] == '/' && buffer[i + 1] == '*')
            {
                buffer[i] = ' ';
                buffer[i + 1] = ' ';
                i += 2;
                while (i < n)
                {
                    if (i + 1 < n && buffer[i] == '*' && buffer[i + 1] == '/')
                    {
                        buffer[i] = ' ';
                        buffer[i + 1] = ' ';
                        i += 2;
                        break;
                    }
                    if (buffer[i] != '\r' && buffer[i] != '\n')
                        buffer[i] = ' ';
                    i++;
                }
            }
            else
            {
                i++;
            }
        }
        return new string(buffer);
    }

    private static Shader? TryBuildClampedShader(Shader baseTarget, LumenColorBlendSpace blendSpace)
    {
        if (baseTarget.MaterialLayout.UniformBlocks.Length != 0)
            return null;

        if (!TryReadShaderSource(baseTarget.FragmentShaderFileName, baseTarget.Key, blendSpace, out string fragmentSource))
            return null;

        // RimUnity's CustomShaderLoader always attaches PrecompiledShaderStages even when compiling raw GLSL files
        // directly from rooted paths on disk. Allow plain disk rewrite for non-precompiled shaders AND rooted custom
        // disk shaders, while still rejecting in-memory transformed variants (_Linear / _GpuSkin) whose disk file
        // differs from the live SPIR-V.
        bool allowPlainDiskRewrite = baseTarget.PrecompiledStages is null
            || (Path.IsPathRooted(baseTarget.FragmentShaderFileName)
                && !IsInMemoryTransformedShaderKey(baseTarget.Key));

        if (!TryRewriteFragmentForAlphaClamp(fragmentSource, blendSpace, allowPlainDiskRewrite, out string rewrittenFragment))
            return null;

        string clampedKey = baseTarget.Key + (blendSpace == LumenColorBlendSpace.Linear
            ? LinearClampedKeySuffix
            : LegacyClampedKeySuffix);

        try
        {
            // Verify the disk-rewritten fragment matches the live MaterialLayout (rejects in-memory transformed
            // shaders such as GPU-skinned variants whose disk source lacks injected set-2 storage buffers).
            MaterialLayout rewrittenLayout = ShaderMetadataParser.ExtractMaterialLayout(rewrittenFragment, clampedKey + ".frag.glsl", 2);
            if (!LayoutMatchesSource(rewrittenLayout, baseTarget.MaterialLayout))
            {
                LogFailureOnce(baseTarget.Key, blendSpace, "Rewritten fragment layout differs from the live shader's MaterialLayout.");
                return null;
            }

            byte[] vertexSpirV;
            bool scaleOffsetTextureST;
            if (baseTarget.PrecompiledStages is { } precompiled)
            {
                vertexSpirV = precompiled.Vertex;
                scaleOffsetTextureST = precompiled.ScaleOffsetTextureST;
            }
            else
            {
                if (!TryReadShaderSource(baseTarget.VertexShaderFileName, baseTarget.Key, blendSpace, out string vertexSource)
                    || HasPreprocessorConditional(vertexSource))
                {
                    return null;
                }
                vertexSpirV = RuntimeShaderCompiler.Compile(
                    vertexSource,
                    clampedKey + ".vert.glsl",
                    "main",
                    RuntimeShaderLanguage.Glsl,
                    RuntimeShaderStage.Vertex);
                scaleOffsetTextureST = false;
            }

            byte[] fragmentSpirV = RuntimeShaderCompiler.Compile(
                rewrittenFragment,
                clampedKey + ".frag.glsl",
                "main",
                RuntimeShaderLanguage.Glsl,
                RuntimeShaderStage.Fragment);

            if (!SpirvHasUniqueLocation0Vec4Output(fragmentSpirV))
            {
                LogFailureOnce(baseTarget.Key, blendSpace, "Compiled fragment SPIR-V does not expose a unique location=0 vec4 color output.");
                return null;
            }

            var stages = new PrecompiledShaderStages(vertexSpirV, fragmentSpirV, scaleOffsetTextureST);
            var clamped = new Shader(
                clampedKey,
                baseTarget.ShaderPath,
                baseTarget.Program,
                baseTarget.Keywords.ToArray(),
                baseTarget.MaterialLayout,
                baseTarget.PipelineState,
                iconShaderKey: baseTarget.IconShaderKey,
                vertexInputLayout: baseTarget.VertexInputLayout,
                precompiledStages: stages);

            if (clamped.HasErrors)
            {
                LogFailureOnce(baseTarget.Key, blendSpace, "Waylith rejected the alpha-clamped shader metadata.");
                return null;
            }

            return clamped;
        }
        catch (Exception ex)
        {
            LogFailureOnce(baseTarget.Key, blendSpace, ex.Message);
            return null;
        }
    }

    internal static bool LayoutMatchesSource(MaterialLayout parsed, MaterialLayout expected)
    {
        if (parsed.HasErrors || expected.HasErrors
            || parsed.UniformSize != expected.UniformSize
            || parsed.UniformSlots.Length != expected.UniformSlots.Length
            || parsed.TextureSlots.Length != expected.TextureSlots.Length
            || parsed.StorageBufferSlots.Length != expected.StorageBufferSlots.Length)
        {
            return false;
        }

        for (int i = 0; i < parsed.UniformSlots.Length; i++)
        {
            MaterialUniformSlot a = parsed.UniformSlots[i];
            MaterialUniformSlot b = expected.UniformSlots[i];
            if (a.PropertyId != b.PropertyId || a.Offset != b.Offset || a.Type != b.Type)
                return false;
        }

        for (int i = 0; i < parsed.TextureSlots.Length; i++)
        {
            MaterialTextureSlot a = parsed.TextureSlots[i];
            MaterialTextureSlot b = expected.TextureSlots[i];
            if (a.PropertyId != b.PropertyId || a.Binding != b.Binding || a.Kind != b.Kind || a.ResourceSlot != b.ResourceSlot)
                return false;
        }

        for (int i = 0; i < parsed.StorageBufferSlots.Length; i++)
        {
            MaterialStorageBufferSlot a = parsed.StorageBufferSlots[i];
            MaterialStorageBufferSlot b = expected.StorageBufferSlots[i];
            if (a.PropertyId != b.PropertyId || a.Binding != b.Binding)
                return false;
        }

        return true;
    }

    private static bool TryReadShaderSource(string? fileName, string shaderKey, LumenColorBlendSpace blendSpace, out string source)
    {
        source = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        try
        {
            if (Path.IsPathRooted(fileName))
            {
                if (!File.Exists(fileName))
                    return false;
                source = File.ReadAllText(fileName);
                return true;
            }

            if (!ShaderSourceLoader.TryResolveShaderPath(fileName, out _))
                return false;
            source = ShaderSourceLoader.ReadShaderSource(fileName);
            return true;
        }
        catch (Exception ex)
        {
            LogFailureOnce(shaderKey, blendSpace, $"Could not read shader source '{fileName}': {ex.Message}");
            return false;
        }
    }

    private static void LogFailureOnce(string shaderKey, LumenColorBlendSpace blendSpace, string reason)
    {
        string key = $"{shaderKey}|{blendSpace}";
        bool shouldLog;
        lock (LoggedFailures)
            shouldLog = LoggedFailures.Add(key);
        if (shouldLog)
            RimLumenRenderHost.LogWarning($"[RimLumen] Alpha-clamped HDR shader unavailable for '{shaderKey}' ({blendSpace}); leaving CoverageAlpha draws on MapMain: {reason}");
    }
}
