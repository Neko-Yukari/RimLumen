using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RimLumen.Core.ParticlesGpu;
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
        Require(bindings.SetEquals(new[] { 0, 1, 2 }), "Compute must expose exactly particle/emitter/field bindings 0/1/2.");
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
        Require(!Regex.IsMatch(vertex, @"layout\s*\(\s*location\s*=\s*\d+\s*\)\s*in\b"),
            "Draw(6,capacity,0,0) requires no vertex attributes.");
        Require(!Regex.IsMatch(vertex + fragment, @"\bset\s*=\s*1\b|\bpush_constant\b"),
            "Graphics may not declare reserved set1 or fragment push constants.");
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
        foreach (uint stride in new uint[] { 64, 208, 48 })
            Require(HasArrayStride(computeInstructions, stride), $"Compute SPIR-V missing {stride}-byte storage array stride.");
        foreach (uint stride in new uint[] { 64, 208, 32 })
            Require(HasArrayStride(vertexInstructions, stride), $"Vertex SPIR-V missing {stride}-byte storage array stride.");
        Require(computeInstructions.Any(x => x[0] == 16 && x.Length >= 6 && x[2] == 17 && x[3] == 64 && x[4] == 1 && x[5] == 1),
            "SPIR-V workgroup must be LocalSize 64,1,1.");
        Require(fragment.Length > 20, "Fragment compiler returned no program.");
        Console.WriteLine("[SPIR-V] compute strides=64,208,48; vertex strides=64,208,32; LocalSize=64,1,1");
    }

    private static void CheckUniforms(MaterialLayout layout)
    {
        Require(layout.UniformSize == 80, "Material UBO must be 80 bytes.");
        Require(layout.UniformSlots.Select(x => (x.Offset, x.PropertyName)).SequenceEqual(new[]
        {
            (0, "_LumenDraw"), (16, "_LumenCameraRight"), (32, "_LumenCameraUp"),
            (48, "_LumenProjection"), (64, "_LumenDepthUv")
        }), "Material UBO offsets differ from runtime packing.");
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
        string path = Path.Combine(ShaderDirectory(), file);
        byte[] spirv;
        try
        {
            spirv = RuntimeShaderCompiler.Compile(Read(file), path, "main", RuntimeShaderLanguage.Glsl, stage);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.ToString());
            throw;
        }
        Require(spirv.Length >= 20 && BitConverter.ToUInt32(spirv) == 0x07230203u, $"{file}: invalid SPIR-V.");
        Console.WriteLine($"[SLANG] {file}: {spirv.Length} bytes");
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
