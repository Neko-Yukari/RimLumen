using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RimLumen.Core.ParticlesGpu;
using Waylith.Graphics.Meshes;
using Waylith.Rendering;

namespace RimLumen.Core.Tests.ParticlesGpu;

[LumenTestSuite]
public static class ParticleShaderTests
{
    [LumenFact]
    public static void ShaderStorageMembersMatchFixedCpuByteLayout()
    {
        string source = Read("ParticleTypes.glsl");
        CheckStruct<GpuParticle>(source, 64);
        CheckStruct<GpuEmitterParams>(source, 208);
        CheckStruct<GpuForceField>(source, 48);
        CheckStruct<GpuCurveSample>(source, 32);
        Require(Marshal.SizeOf<GpuSimPushConstants>() == 16, "Compute push constants must be 16 bytes.");
    }

    [LumenFact]
    public static void ComputeDescriptorsAndPushConstantsFitHostLimits()
    {
        string source = Regex.Replace(Read("ParticleSim.comp.glsl"), @"//[^\r\n]*|/\*[\s\S]*?\*/", "");
        var bindings = new HashSet<int>();
        MatchCollection declarations = Regex.Matches(source,
            @"layout\s*\((?<layout>[^)]*)\)\s*(?:(?:readonly|writeonly|coherent|restrict|volatile)\s+)*(?<storage>buffer|uniform)\s+\w+\s*(?<block>\{)?");
        int pushBytes = 0;
        int pushBlocks = 0;
        foreach (Match declaration in declarations)
        {
            string layout = declaration.Groups["layout"].Value;
            if (Regex.IsMatch(layout, @"\bpush_constant\b"))
            {
                pushBlocks++;
                int bodyStart = declaration.Index + declaration.Length;
                int bodyEnd = source.IndexOf('}', bodyStart);
                Require(declaration.Groups["block"].Success && bodyEnd >= bodyStart, "Invalid compute push block.");
                string body = source.Substring(bodyStart, bodyEnd - bodyStart);
                foreach (string member in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    // This fixed protocol is scalar-only. Reject unknown/vector/array members rather than undercounting them.
                    Match scalar = Regex.Match(member, @"^(?<type>uint|int|float|double)\s+\w+$");
                    Require(scalar.Success, $"Unsupported compute push member: {member}");
                    int bytes = scalar.Groups["type"].Value == "double" ? 8 : 4;
                    pushBytes = (pushBytes + bytes - 1) / bytes * bytes + bytes;
                }
                continue;
            }
            Require(declaration.Groups["storage"].Value != "uniform" || !declaration.Groups["block"].Success,
                "Compute descriptors cannot contain uniform buffers.");
            Match set = Regex.Match(layout, @"\bset\s*=\s*(\d+)\b");
            Match binding = Regex.Match(layout, @"\bbinding\s*=\s*(\d+)\b");
            Require(set.Success && set.Groups[1].Value == "0" && binding.Success,
                "Every compute descriptor must explicitly use set0 and a binding.");
            int index = int.Parse(binding.Groups[1].Value);
            Require(index < 16 && bindings.Add(index), "Compute bindings must be unique and in [0,15].");
        }
        Require(bindings.SetEquals(new[] { 0, 1, 2, 3 }), "Compute must expose exactly particle/emitter/field/dead-block bindings 0/1/2/3.");
        Require(pushBlocks == 1 && pushBytes <= 128 && pushBytes == Marshal.SizeOf<GpuSimPushConstants>(),
            $"Compute push layout is {pushBytes} bytes; expected fixed CPU layout within host 128-byte limit.");
    }

    [LumenFact]
    public static void PositiveCoverageCannotBeDiscardedBeforeHdrBlending()
    {
        string fragment = Read("ParticleBillboard.frag.glsl");
        Match cutoff = Regex.Match(fragment, @"if\s*\(color\.a\s*<=\s*(?<value>[0-9.]+)\)\s*discard;");
        Require(cutoff.Success, "Expected explicit nonpositive-coverage rejection.");
        float threshold = float.Parse(cutoff.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture);
        Require(threshold == 0f, "Positive coverage can carry arbitrarily bright HDR radiance and must not be culled.");
        Require(0.0005f > threshold, "Coverage 0.0005 with HDR 10000 must retain its radiance contribution of 5.");
    }

    [LumenFact]
    public static void WaylithMetadataAcceptsGraphicsDescriptorAndStageContract()
    {
        string vertex = Read("ParticleBillboard.vert.glsl");
        string fragment = Read("ParticleBillboard.frag.glsl");
        // Parse each stage separately: concatenating repeats the material block and is not the engine merge path.
        MaterialLayout vertexLayout = ShaderMetadataParser.ExtractMaterialLayout(vertex, "LumenParticlesVertex");
        MaterialLayout fragmentLayout = ShaderMetadataParser.ExtractMaterialLayout(fragment, "LumenParticlesFragment");
        Require(!vertexLayout.HasErrors && !fragmentLayout.HasErrors, "Waylith rejected material metadata.");
        CheckUniforms(vertexLayout);
        CheckUniforms(fragmentLayout);
        Require(vertexLayout.StorageBufferSlots.Select(x => (x.Binding, x.PropertyName)).SequenceEqual(new[]
        {
            (1, GpuParticleProperties.Particles), (2, GpuParticleProperties.Emitters), (3, GpuParticleProperties.Curves)
        }), "Graphics storage properties/bindings differ from C# upload contract.");
        Require(fragmentLayout.TextureSlots.Select(x => (x.Binding, x.PropertyName)).SequenceEqual(new[]
        {
            (4, GpuParticleProperties.Texture), (5, GpuParticleProperties.SceneDepth)
        }), "Graphics texture properties/bindings differ from C# binding contract.");
        Require(!ShaderMetadataParser.ValidateStageInterface(vertex, fragment, "particle.vert", "particle.frag"),
            "Waylith reports mismatched vertex/fragment varyings.");
        // L2 draws one static quad mesh per instance: the vertex shader reads only the quad corner
        // (its xy is the billboard UV) and gl_InstanceIndex picks the particle.
        MatchCollection inputs = Regex.Matches(vertex,
            @"layout\s*\(\s*location\s*=\s*(?<location>\d+)\s*\)\s*in\s+(?<type>vec[234])\s+(?<name>\w+)\s*;");
        Require(inputs.Count == 1 && inputs[0].Groups["location"].Value == "0" && inputs[0].Groups["type"].Value == "vec3",
            "L2 billboards expect exactly one vec3 quad-corner attribute at location 0.");
        Require(!Regex.IsMatch(vertex + fragment, @"\bset\s*=\s*1\b|\bpush_constant\b"),
            "Graphics may not declare reserved set1 or fragment push constants.");
    }

    [LumenFact]
    public static void L1MaterialAndVertexLayoutsMatchTheDynamicQuadMesh()
    {
        // The L1 mesh carries exactly these three attributes; a mismatch silently drops the attribute.
        string vertex = Read("L1ParticleQuad.vert.glsl");
        string fragment = Read("L1ParticleQuad.frag.glsl");
        MaterialLayout layout = ShaderMetadataParser.ExtractMaterialLayout(fragment, "L1ParticleQuad.frag");
        Require(!layout.HasErrors, "Waylith rejected the L1 material metadata.");
        Require(layout.UniformSize == 48, $"L1 material UBO must be 48 bytes; got {layout.UniformSize}.");
        Require(layout.UniformSlots.Select(x => (x.Offset, x.PropertyName)).SequenceEqual(new[]
        {
            (0, "_LumenDraw"), (16, "_LumenDepth"), (32, "_LumenProjection")
        }), "L1 material UBO offsets differ from the runtime packing.");
        CheckOverrideSlots(layout, ("_LumenDraw", 0), ("_LumenDepth", 16), ("_LumenProjection", 32));
        Require(layout.TextureSlots.Select(x => (x.Binding, x.PropertyName)).SequenceEqual(new[]
        {
            (1, "_LumenParticleTexture"), (2, "_LumenSceneDepth")
        }), "L1 texture bindings differ from the material contract.");
        Require(layout.StorageBufferSlots.Length == 0, "L1 quads are not instanced and declare no storage buffers.");
        Require(!ShaderMetadataParser.ValidateStageInterface(vertex, fragment, "L1ParticleQuad.vert", "L1ParticleQuad.frag"),
            "Waylith reports mismatched L1 vertex/fragment varyings.");
        ShaderVertexInputLayout input = ShaderMetadataParser.ExtractVertexInputLayout(vertex, "L1ParticleQuad.vert");
        Require(!input.HasErrors, "Waylith rejected the L1 vertex input layout.");
        Require(input.Slots.Select(x => (x.location, x.semantic, x.required)).SequenceEqual(new[]
        {
            (0, VertexAttributeSemantic.Position, true),
            (1, VertexAttributeSemantic.TexCoord0, true),
            (2, VertexAttributeSemantic.Color, true)
        }), "L1 vertex input must require Position/TexCoord0/Color at locations 0..2.");
    }

    [LumenFact]
    public static void RuntimeSlangCompilesAllStagesAndPreservesStorageStrides()
    {
        // Brutal.Slang.PInvoke explicitly calls NativeLibrary.Load, then installs its own DllImportResolver.
        // The load-context event handles that explicit load without competing with its resolver or copying locked DLLs.
        string buildProperties = Path.GetFullPath(Path.Combine(ShaderDirectory(), "..", "..", "..", "Directory.Build.props"));
        string gameRoot = Environment.GetEnvironmentVariable("GameRoot")
            ?? XDocument.Load(buildProperties).Descendants("GameRoot").Single().Value;
        string nativePath = Path.Combine(gameRoot, "slang.dll");
        nint ResolveSlang(System.Reflection.Assembly assembly, string name)
        {
            if (assembly.GetName().Name != "Brutal.Slang" || name != "slang")
                return 0;
            Console.WriteLine($"[SLANG] resolve explicit host load: {nativePath}");
            // This reference belongs to PInvoke's process-lifetime resolver, not to the test.
            return NativeLibrary.Load(nativePath, assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
        }
        // Slang loads its SPIR-V optimizer plugin by bare name, outside the managed load-context event.
        string optimizerPath = Path.Combine(gameRoot, "slang-glslang.dll");
        nint optimizer = NativeLibrary.Load(optimizerPath, typeof(ParticleShaderTests).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
        Console.WriteLine($"[SLANG] optimizer plugin: {optimizerPath}");
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveSlang;
        try
        {
            CompileAndCheckStorageStrides();
        }
        finally
        {
            AssemblyLoadContext.Default.ResolvingUnmanagedDll -= ResolveSlang;
            NativeLibrary.Free(optimizer);
        }
    }

    private static void CompileAndCheckStorageStrides()
    {
        // The real host compiler only; no Vulkan instance/device/game startup or compiler fallback.
        byte[] compute = Compile("ParticleSim.comp.glsl", RuntimeShaderStage.Compute);
        byte[] vertex = Compile("ParticleBillboard.vert.glsl", RuntimeShaderStage.Vertex);
        byte[] fragment = Compile("ParticleBillboard.frag.glsl", RuntimeShaderStage.Fragment);
        var computeInstructions = Instructions(compute);
        var vertexInstructions = Instructions(vertex);
        foreach (uint stride in new uint[] { 64, 208, 48, 4 })
            Require(HasArrayStride(computeInstructions, stride), $"Compute SPIR-V missing {stride}-byte storage array stride.");
        foreach (uint stride in new uint[] { 64, 208, 32 })
            Require(HasArrayStride(vertexInstructions, stride), $"Vertex SPIR-V missing {stride}-byte storage array stride.");
        Require(computeInstructions.Any(x => x[0] == 16 && x.Length >= 6 && x[2] == 17 && x[3] == 64 && x[4] == 1 && x[5] == 1),
            "SPIR-V workgroup must be LocalSize 64,1,1.");
        Require(fragment.Length > 20, "Fragment compiler returned no program.");
        // The L1 quads and the depth-copy pass are separate entry points from the L2 billboards.
        CompileFile("Particles/L1ParticleQuad.vert.glsl", RuntimeShaderStage.Vertex);
        CompileFile("Particles/L1ParticleQuad.frag.glsl", RuntimeShaderStage.Fragment);
        CompileFile("PostFx/RimLumenDepthCopy.frag.glsl", RuntimeShaderStage.Fragment);
        CompileFile("PostFx/RimLumenBloomPrefilter.frag.glsl", RuntimeShaderStage.Fragment);
        CompileFile("PostFx/RimLumenBloomDown.frag.glsl", RuntimeShaderStage.Fragment);
        CompileFile("PostFx/RimLumenBloomUp.frag.glsl", RuntimeShaderStage.Fragment);
        CompileFile("PostFx/RimLumenComposite.frag.glsl", RuntimeShaderStage.Fragment);
        CompileFile("PostFx/RimLumenFogOfWarMask.frag.glsl", RuntimeShaderStage.Fragment);
        CompileFile("PostFx/RimLumenDarknessMask.frag.glsl", RuntimeShaderStage.Fragment);
        Console.WriteLine("[SPIR-V] compute strides=64,208,48,4; vertex strides=64,208,32; LocalSize=64,1,1");
    }

    // Overlay masks receive vanilla's material copy and per-draw property block (darkness strength in _ColorTwo,
    // its clock in _AgeSecsPausable), so every vanilla name must resolve to a slot of the mask's own block.
    [LumenFact]
    public static void OverlayMaskPropertiesResolveToMaskUniformSlots()
    {
        MaterialLayout darkness = ShaderMetadataParser.ExtractMaterialLayout(ReadPostFx("RimLumenDarknessMask.frag.glsl"), "RimLumenDarknessMask");
        Require(!darkness.HasErrors, "Darkness mask material layout rejected.");
        CheckOverrideSlots(darkness, ("_Color", 0), ("_ColorTwo", 16), ("_AgeSecsPausable", 32));
        MaterialLayout fog = ShaderMetadataParser.ExtractMaterialLayout(ReadPostFx("RimLumenFogOfWarMask.frag.glsl"), "RimLumenFogOfWarMask");
        Require(!fog.HasErrors, "Fog of war mask material layout rejected.");
        CheckOverrideSlots(fog, ("_Color", 0));
    }

    private static void CheckUniforms(MaterialLayout layout)
    {
        Require(layout.UniformSize == 96, "Material UBO must be 96 bytes.");
        Require(layout.UniformSlots.Select(x => (x.Offset, x.PropertyName)).SequenceEqual(new[]
        {
            (0, "_LumenDraw"), (16, "_LumenCameraRight"), (32, "_LumenCameraUp"),
            (48, "_LumenProjection"), (64, "_LumenDepthUv"), (80, "_LumenVertical")
        }), "Material UBO offsets differ from runtime packing.");
        CheckOverrideSlots(layout, ("_LumenDraw", 0), ("_LumenCameraRight", 16), ("_LumenCameraUp", 32),
            ("_LumenProjection", 48), ("_LumenDepthUv", 64), ("_LumenVertical", 80));
    }

    // Per-draw vectors travel through DrawPropertyArena and only reach a uniform when the id the renderer uses
    // (Shader.PropertyToID) resolves to the parsed slot; a renamed uniform would silently fall back to the
    // material's stored defaults (all zero), which is exactly the state the arena path replaced.
    private static void CheckOverrideSlots(MaterialLayout layout, params (string Name, int Offset)[] expected)
    {
        foreach ((string name, int offset) in expected)
        {
            int propertyId = Shader.PropertyToID(name);
            Require(layout.TryGetUniformSlot(propertyId, out int index),
                $"Material override '{name}' does not resolve to a uniform slot.");
            Require(layout.UniformSlots[index].Offset == offset,
                $"Material override '{name}' resolves to offset {layout.UniformSlots[index].Offset}, not {offset}.");
        }
    }

    private static void CheckStruct<T>(string source, int expectedSize) where T : struct
    {
        Match body = Regex.Match(source, @"struct\s+" + typeof(T).Name + @"\s*\{(?<body>[^}]+)\}");
        Require(body.Success, $"Missing shader struct {typeof(T).Name}.");
        MatchCollection fields = Regex.Matches(body.Groups["body"].Value, @"\b(vec4|mat4)\s+(\w+)\s*;");
        int offset = 0;
        foreach (Match field in fields)
        {
            string name = field.Groups[2].Value;
            Require(Marshal.OffsetOf<T>(name).ToInt32() == offset, $"{typeof(T).Name}.{name} byte offset mismatch.");
            offset += field.Groups[1].Value == "mat4" ? 64 : 16;
        }
        Require(offset == expectedSize && Marshal.SizeOf<T>() == expectedSize, $"{typeof(T).Name} byte size mismatch.");
    }

    private static byte[] Compile(string file, RuntimeShaderStage stage)
    {
        return CompileFile("Particles/" + file, stage, Path.Combine(ShaderDirectory(), file));
    }

    private static byte[] CompileFile(string shaderRelativePath, RuntimeShaderStage stage)
    {
        string path = Path.GetFullPath(Path.Combine(ShaderDirectory(), "..", shaderRelativePath));
        return CompileFile(shaderRelativePath, stage, path);
    }

    private static byte[] CompileFile(string label, RuntimeShaderStage stage, string path)
    {
        byte[] spirv;
        try
        {
            spirv = RuntimeShaderCompiler.Compile(ShaderSourceLoader.ReadShaderSource(path), path, "main", RuntimeShaderLanguage.Glsl, stage);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.ToString());
            throw;
        }
        Require(spirv.Length >= 20 && BitConverter.ToUInt32(spirv) == 0x07230203u, $"{label}: invalid SPIR-V.");
        Console.WriteLine($"[SLANG] {label}: {spirv.Length} bytes");
        return spirv;
    }

    private static List<uint[]> Instructions(byte[] spirv)
    {
        uint[] words = new uint[spirv.Length / 4];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        var instructions = new List<uint[]>();
        for (int offset = 5; offset < words.Length;)
        {
            int count = (int)(words[offset] >> 16);
            Require(count > 0 && offset + count <= words.Length, "Invalid SPIR-V instruction length.");
            uint[] instruction = words.AsSpan(offset, count).ToArray();
            instruction[0] &= 0xffff;
            instructions.Add(instruction);
            offset += count;
        }
        return instructions;
    }

    private static bool HasArrayStride(List<uint[]> instructions, uint stride) =>
        instructions.Any(x => x[0] == 71 && x.Length == 4 && x[2] == 6 && x[3] == stride);

    private static string Read(string file)
    {
        string path = Path.Combine(ShaderDirectory(), file);
        return ShaderSourceLoader.ReadShaderSource(path);
    }

    private static string ReadPostFx(string file)
        => ShaderSourceLoader.ReadShaderSource(Path.GetFullPath(Path.Combine(ShaderDirectory(), "..", "PostFx", file)));

    private static string ShaderDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "1.7", "Shaders", "Particles");
            if (Directory.Exists(path))
                return path;
        }
        throw new DirectoryNotFoundException("Cannot locate repository particle shaders above test output.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
