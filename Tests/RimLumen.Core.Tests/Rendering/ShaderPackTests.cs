using System;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Xml.Linq;
using RimLumen.Core.Lighting;
using RimLumen.Core.Mod;
using RimLumen.Core.Rendering;
using RimLumen.Core.Rendering.Logic;
using UnityStubs.Rendering;
using Waylith.Rendering;

namespace RimLumen.Core.Tests.Rendering;

[LumenTestSuite]
public static class ShaderPackTests
{
    [LumenFact]
    public static void ShaderPackUniformsMatchesStd140Layout()
    {
        Require(Marshal.SizeOf<ShaderPackUniforms>() == 224,
            $"Expected 224 bytes, got {Marshal.SizeOf<ShaderPackUniforms>()}.");
        Equal(0, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.InvViewProjection)).ToInt32());
        Equal(64, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.ScreenAndMapSize)).ToInt32());
        Equal(80, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.GameSeconds)).ToInt32());
        Equal(84, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.RealSeconds)).ToInt32());
        Equal(88, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.SkyGlow)).ToInt32());
        Equal(92, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.ZoomRootSize)).ToInt32());
        Equal(96, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.HasHdrDraws)).ToInt32());
        Equal(100, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.HasSceneDepth)).ToInt32());
        Equal(104, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.HasMapLightField)).ToInt32());
        Equal(108, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.IsPaused)).ToInt32());
        Equal(112, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.DepthValid)).ToInt32());
        Equal(116, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.GameDeltaSeconds)).ToInt32());
        Equal(120, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.RealDeltaSeconds)).ToInt32());
        Equal(124, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.ColorBlendSpace)).ToInt32());
        Equal(128, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.BloomParams)).ToInt32());
        Equal(144, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.PackParam0)).ToInt32());
        Equal(160, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.PackParam1)).ToInt32());
        Equal(176, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.PackParam2)).ToInt32());
        Equal(192, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.PackParam3)).ToInt32());
        Equal(208, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.LayerFlags)).ToInt32());
        Equal(16, Marshal.SizeOf<Vector4>());
        WithSlangLoaded(() =>
        {
            CheckCompiledPackParams("Atmospheric/FogPass.frag.glsl");
            CheckCompiledPackParams("Atmospheric/Composite.frag.glsl");
            CheckCompiledPackParams("Cinematic/Composite.frag.glsl");
            CheckCompiledPackParams("Crisp/Composite.frag.glsl");
            CheckCompiledPackParams("Vibrant/Composite.frag.glsl");
        });
    }

    [LumenFact]
    public static void AtmosphericFogPassSamplesLightSumAtItsManifestBinding()
    {
        string packsRoot = Path.Combine(RepoRoot(), "1.7", "ShaderPacks");
        ShaderPackEntry entry = ShaderPackCatalog.ScanRoots([(packsRoot, "built-in")]).Single(p => p.Id == "atmospheric");
        ShaderPackGraphValidator.PlannedPass fog = ShaderPackGraphValidator.ExecutionPlan.Compile(entry.Spec).Passes[0];
        Equal("FogPass.frag.glsl", Path.GetFileName(fog.FragmentShaderPath));
        uint binding = (uint)fog.Inputs.Single(input => input.Kind == ShaderPackGraphValidator.InputKind.LightSum).Binding;
        WithSlangLoaded(() =>
        {
            List<uint[]> instructions = CompileInstructions("Atmospheric/FogPass.frag.glsl");
            // OpDecorate Binding / DescriptorSet 0 -> OpVariable -> OpTypePointer -> OpTypeSampledImage.
            uint variable = instructions.Single(x => x[0] == 71 && x[2] == 33 && x[3] == binding &&
                instructions.Any(set => set[0] == 71 && set[1] == x[1] && set[2] == 34 && set[3] == 0))[1];
            uint pointer = instructions.Single(x => x[0] == 59 && x[2] == variable)[1];
            uint pointee = instructions.Single(x => x[0] == 32 && x[1] == pointer)[3];
            Require(instructions.Any(x => x[0] == 27 && x[1] == pointee), $"binding {binding} must be a combined image sampler.");
        });
    }

    [LumenFact]
    public static void AtmosphericValidatesCompilesAndReadsNoDepth()
    {
        // Vanilla Cutout sprites write depth, so a depth test in Atmospheric fired on every wall, rock and plant pixel.
        string packsRoot = Path.Combine(RepoRoot(), "1.7", "ShaderPacks");
        ShaderPackEntry entry = ShaderPackCatalog.ScanRoots([(packsRoot, "built-in")]).Single(p => p.Id == "atmospheric");
        IReadOnlyList<ShaderPackGraphValidator.PlannedPass> passes = ShaderPackGraphValidator.ExecutionPlan.Compile(entry.Spec).Passes;
        Require(!entry.Spec.RequiresSceneDepth, "Atmospheric must not request scene depth.");
        Require(passes.All(pass => pass.Inputs.All(input => input.Kind != ShaderPackGraphValidator.InputKind.Depth)),
            "No Atmospheric pass may declare a depth input.");
        WithSlangLoaded(() =>
        {
            foreach (ShaderPackGraphValidator.PlannedPass pass in passes)
                CompileInstructions("Atmospheric/" + Path.GetFileName(pass.FragmentShaderPath));
        });
    }

    private static List<uint[]> CompileInstructions(string relativePath)
    {
        string path = Path.Combine(RepoRoot(), "1.7", "ShaderPacks", relativePath);
        byte[] spirv = RuntimeShaderCompiler.Compile(ShaderSourceLoader.ReadShaderSource(path), path, "main",
            RuntimeShaderLanguage.Glsl, RuntimeShaderStage.Fragment);
        uint[] words = MemoryMarshal.Cast<byte, uint>(spirv).ToArray();
        var instructions = new List<uint[]>();
        for (int offset = 5; offset < words.Length;)
        {
            int count = (int)(words[offset] >> 16);
            uint[] instruction = words.AsSpan(offset, count).ToArray();
            instruction[0] &= 0xffff;
            instructions.Add(instruction);
            offset += count;
        }
        return instructions;
    }

    private static void CheckCompiledPackParams(string relativePath)
    {
        string path = Path.Combine(RepoRoot(), "1.7", "ShaderPacks", relativePath);
        string source = ShaderSourceLoader.ReadShaderSource(path);
        Require(Regex.IsMatch(source, @"\bvec4\s+PackParams\s*\[\s*4\s*\]\s*;"), $"{relativePath}: missing vec4 PackParams[4].");
        Require(!Regex.IsMatch(source, @"\bPackParam[0-3]\b"), $"{relativePath}: old GLSL vector alias remains.");
        List<uint[]> instructions = CompileInstructions(relativePath);
        // Follow set=0/binding=5 through OpVariable/OpTypePointer to its actual compiled block and its last two members.
        uint uniform = instructions.Single(x => x[0] == 71 && x[2] == 33 && x[3] == 5 &&
            instructions.Any(set => set[0] == 71 && set[1] == x[1] && set[2] == 34 && set[3] == 0))[1];
        uint pointer = instructions.Single(x => x[0] == 59 && x[2] == uniform)[1];
        uint block = instructions.Single(x => x[0] == 32 && x[1] == pointer)[3];
        uint[] members = instructions.Single(x => x[0] == 30 && x[1] == block);
        Equal(19, members.Length); // result id + 17 members + opcode
        // Slang lowers a GLSL array member to a one-member struct; the array itself starts at wrapper offset 0.
        uint wrapper = members[17];
        uint[] wrapperType = instructions.Single(x => x[0] == 30 && x[1] == wrapper);
        Equal(3, wrapperType.Length);
        Equal(0u, instructions.Single(x => x[0] == 72 && x[1] == wrapper && x[2] == 0 && x[3] == 35)[4]);
        uint array = wrapperType[2];
        uint[] arrayType = instructions.Single(x => x[0] == 28 && x[1] == array);
        uint length = instructions.Single(x => x[0] == 43 && x[2] == arrayType[3])[3];
        uint stride = instructions.Single(x => x[0] == 71 && x[1] == array && x[2] == 6)[3];
        uint baseOffset = instructions.Single(x => x[0] == 72 && x[1] == block && x[2] == 15 && x[3] == 35)[4];
        uint[] vector = instructions.Single(x => x[0] == 23 && x[1] == arrayType[2]);
        Equal(4u, vector[3]);
        Equal(32u, instructions.Single(x => x[0] == 22 && x[1] == vector[2])[2]);
        Equal(144u, baseOffset);
        Equal(16u, stride);
        Equal(4u, length);
        Equal(64u, stride * length);
        uint layerFlagsOffset = instructions.Single(x => x[0] == 72 && x[1] == block && x[2] == 16 && x[3] == 35)[4];
        Equal(baseOffset + stride * length, layerFlagsOffset);
        uint[] layerFlags = instructions.Single(x => x[0] == 23 && x[1] == members[18]);
        Equal(4u, layerFlags[3]);
        Equal(32u, instructions.Single(x => x[0] == 22 && x[1] == layerFlags[2])[2]);
        Equal(208u, layerFlagsOffset);
        Console.WriteLine($"[PACK ABI] {relativePath}: base={baseOffset}, stride={stride}, count={length}, bytes={stride * length}, LayerFlags={layerFlagsOffset}, UBO={layerFlagsOffset + 16u}");
    }

    [LumenFact]
    public static void RuntimeParamsIndexesAll16PackParamsAndValidatesBounds()
    {
        var p = new ShaderPackRuntimeParams();
        for (int i = 0; i < 16; i++)
        {
            p.SetPackParam(i, i * 0.5f + 1.25f);
        }
        for (int i = 0; i < 16; i++)
        {
            Equal(i * 0.5f + 1.25f, p.GetPackParam(i));
        }
        Equal(new Vector4(1.25f, 1.75f, 2.25f, 2.75f), p.PackParam0);
        Equal(new Vector4(7.25f, 7.75f, 8.25f, 8.75f), p.PackParam3);
        ShaderPackUniformBlockSnapshot snapshot = p.CaptureSnapshot();
        ShaderPackUniforms uniforms = ShaderPackUniforms.Build(Matrix4x4.Identity, 1, 1, 1, 1,
            0f, 0f, 0f, 0f, 0f, 0f, false, false, false, false, false, false, in snapshot);
        ReadOnlySpan<float> indexed = MemoryMarshal.Cast<byte, float>(
            MemoryMarshal.AsBytes(new[] { uniforms }.AsSpan()).Slice(144, 64));
        Require(indexed.SequenceEqual(new float[]
        {
            1.25f, 1.75f, 2.25f, 2.75f, 3.25f, 3.75f, 4.25f, 4.75f,
            5.25f, 5.75f, 6.25f, 6.75f, 7.25f, 7.75f, 8.25f, 8.75f
        }), "packParamN must occupy PackParams[N/4][N%4] in the uploaded bytes.");
        Throws<ArgumentOutOfRangeException>(() => p.GetPackParam(-1));
        Throws<ArgumentOutOfRangeException>(() => p.GetPackParam(16));
        Throws<ArgumentOutOfRangeException>(() => p.SetPackParam(-1, 0f));
        Throws<ArgumentOutOfRangeException>(() => p.SetPackParam(16, 0f));
    }

    [LumenFact]
    public static void GraphCompilerResolvesInputKindsTargetsAndPersistentHistory()
    {
        var valid = new ShaderPackSpec
        {
            Id = "valid-multipass",
            BloomMipLevels = 0,
            RequiresSceneDepth = true,
            RequiresMapLightField = true,
            Targets =
            [
                new ShaderPackTargetSpec("history", ShaderPackTargetFormat.R16G16B16A16SFloat, 1.0f, ShaderPackTargetFilter.Linear, Persistent: true),
                new ShaderPackTargetSpec("blur", ShaderPackTargetFormat.R16SFloat, 0.5f, ShaderPackTargetFilter.Point, Persistent: false),
            ],
            Passes =
            [
                new ShaderPackPassSpec(
                    "accum",
                    "accum.frag.glsl",
                    "history",
                    [
                        new ShaderPackInputBinding(0, "scene"),
                        new ShaderPackInputBinding(1, "PrEv:HISTORY"),
                        new ShaderPackInputBinding(2, "depth"),
                        new ShaderPackInputBinding(3, "mapLightField"),
                    ],
                    ShaderPackBlendMode.Opaque),
                new ShaderPackPassSpec(
                    "blur",
                    "blur.frag.glsl",
                    "blur",
                    [new ShaderPackInputBinding(0, "HISTORY")],
                    ShaderPackBlendMode.Opaque),
                new ShaderPackPassSpec(
                    "composite",
                    null,
                    "backbuffer",
                    [
                        new ShaderPackInputBinding(0, "hdr"),
                        new ShaderPackInputBinding(1, "bloom"),
                        new ShaderPackInputBinding(2, "scene"),
                        new ShaderPackInputBinding(3, "blur"),
                        new ShaderPackInputBinding(4, "GLOW"),
                    ],
                    ShaderPackBlendMode.PremultipliedAlpha),
            ],
        };
        ShaderPackGraphValidator.ExecutionPlan plan = ShaderPackGraphValidator.ExecutionPlan.Compile(valid);
        Equal(0, plan.Passes[0].OutputTargetIndex);
        Equal(1, plan.Passes[1].OutputTargetIndex);
        Equal(-1, plan.Passes[2].OutputTargetIndex);
        Equal(new ShaderPackGraphValidator.PlannedInput(0, ShaderPackGraphValidator.InputKind.Scene), plan.Passes[0].Inputs[0]);
        Equal(new ShaderPackGraphValidator.PlannedInput(1, ShaderPackGraphValidator.InputKind.Target, 0, 1), plan.Passes[0].Inputs[1]);
        Equal(new ShaderPackGraphValidator.PlannedInput(2, ShaderPackGraphValidator.InputKind.Depth), plan.Passes[0].Inputs[2]);
        Equal(new ShaderPackGraphValidator.PlannedInput(3, ShaderPackGraphValidator.InputKind.MapLightField), plan.Passes[0].Inputs[3]);
        Equal(new ShaderPackGraphValidator.PlannedInput(0, ShaderPackGraphValidator.InputKind.Target, 0), plan.Passes[1].Inputs[0]);
        Equal(new ShaderPackGraphValidator.PlannedInput(0, ShaderPackGraphValidator.InputKind.Hdr), plan.Passes[2].Inputs[0]);
        Equal(new ShaderPackGraphValidator.PlannedInput(1, ShaderPackGraphValidator.InputKind.Bloom), plan.Passes[2].Inputs[1]);
        Equal(new ShaderPackGraphValidator.PlannedInput(3, ShaderPackGraphValidator.InputKind.Target, 1), plan.Passes[2].Inputs[3]);
        Equal(new ShaderPackGraphValidator.PlannedInput(4, ShaderPackGraphValidator.InputKind.Glow), plan.Passes[2].Inputs[4]);
    }

    [LumenFact]
    public static void GraphCompilerNormalizesShaderSelectionAndImplicitCompositeInputs()
    {
        var spec = new ShaderPackSpec
        {
            CompositeShaderPath = "custom-composite.glsl",
            BloomPrefilterShaderPath = " ",
            BloomDownShaderPath = "custom-down.glsl",
            BloomUpShaderPath = "\t",
            Passes = [new ShaderPackPassSpec("final", " ", "BACKBUFFER", [])],
        };
        ShaderPackGraphValidator.ExecutionPlan plan = ShaderPackGraphValidator.ExecutionPlan.Compile(spec);
        Equal("custom-composite.glsl", plan.Passes[0].FragmentShaderPath);
        Equal(true, plan.Passes[0].UsesBuiltInComposite);
        Equal("RimLumenBloomPrefilter.frag.glsl", plan.BloomPrefilterShaderPath);
        Equal("custom-down.glsl", plan.BloomDownShaderPath);
        Equal("RimLumenBloomUp.frag.glsl", plan.BloomUpShaderPath);
        Equal(true, plan.HasCustomBloomShader);
        Equal(6, plan.Passes[0].Inputs.Count);
        Equal(new ShaderPackGraphValidator.PlannedInput(4, ShaderPackGraphValidator.InputKind.MapLightField), plan.Passes[0].Inputs[4]);

        ShaderPackGraphValidator.ExecutionPlan custom = ShaderPackGraphValidator.ExecutionPlan.Compile(new ShaderPackSpec
        {
            CompositeShaderPath = "unused.glsl",
            Passes = [new ShaderPackPassSpec("final", "explicit.glsl", "backbuffer", [])],
        });
        Equal("explicit.glsl", custom.Passes[0].FragmentShaderPath);
        Equal(false, custom.Passes[0].UsesBuiltInComposite);
        Equal(0, custom.Passes[0].Inputs.Count);

        ShaderPackGraphValidator.ExecutionPlan implicitComposite = ShaderPackGraphValidator.ExecutionPlan.Compile(new ShaderPackSpec
        {
            CompositeShaderPath = "\t",
        });
        Equal("RimLumenComposite.frag.glsl", implicitComposite.Passes[0].FragmentShaderPath);
        Equal(6, implicitComposite.Passes[0].Inputs.Count);
        Equal(ShaderPackBlendMode.PremultipliedAlpha, implicitComposite.Passes[0].BlendMode);
    }

    [LumenFact]
    public static void GraphCompilerSnapshotsListsAndRevalidatesEachCompilation()
    {
        var inputs = new System.Collections.Generic.List<ShaderPackInputBinding> { new(0, "scene") };
        var passes = new System.Collections.Generic.List<ShaderPackPassSpec>
        {
            new("final", "custom.glsl", "backbuffer", inputs),
        };
        var spec = new ShaderPackSpec { Passes = passes };
        ShaderPackGraphValidator.ExecutionPlan plan = ShaderPackGraphValidator.ExecutionPlan.Compile(spec);
        inputs[0] = new ShaderPackInputBinding(0, "unknown");
        Equal(ShaderPackGraphValidator.InputKind.Scene, plan.Passes[0].Inputs[0].Kind);
        Throws<ArgumentException>(() => ShaderPackGraphValidator.ExecutionPlan.Compile(spec));
        passes.Clear();
        Equal("custom.glsl", plan.Passes[0].FragmentShaderPath);
        Equal("RimLumenComposite.frag.glsl", ShaderPackGraphValidator.ExecutionPlan.Compile(spec).Passes[0].FragmentShaderPath);
    }

    [LumenFact]
    public static void GraphValidatorRejectsCyclesSelfReadsReservedTargetsAndBadBindings()
    {
        // Bad mip levels
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec { BloomMipLevels = -1 }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec { BloomMipLevels = 8 }));

        // Reserved target names and prefix
        foreach (string badName in new[] { "hdr", "glow", "bloom", "scene", "depth", "mapLightField", "lightSum", "backbuffer", "prev:foo", "" })
        {
            Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
            {
                Targets = [new ShaderPackTargetSpec(badName)],
            }));
        }

        // Duplicate target names & invalid scale
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a"), new ShaderPackTargetSpec("A")],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a", Scale: 0.05f)],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a", Scale: 1.01f)],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a", Scale: float.NaN)],
        }));

        // Last pass must be backbuffer; earlier passes must be opaque with non-empty shader
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a")],
            Passes = [new ShaderPackPassSpec("p0", "a.frag.glsl", "a", [])],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a")],
            Passes =
            [
                new ShaderPackPassSpec("p0", "a.frag.glsl", "backbuffer", []),
                new ShaderPackPassSpec("p1", "b.frag.glsl", "a", []),
            ],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a")],
            Passes =
            [
                new ShaderPackPassSpec("p0", "a.frag.glsl", "a", [], ShaderPackBlendMode.Additive),
                new ShaderPackPassSpec("p1", null, "backbuffer", []),
            ],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a")],
            Passes =
            [
                new ShaderPackPassSpec("p0", null, "a", []),
                new ShaderPackPassSpec("p1", null, "backbuffer", []),
            ],
        }));

        // Binding 5 is reserved for UBO; out-of-range and duplicate bindings rejected
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Passes = [new ShaderPackPassSpec("p0", "c.frag.glsl", "backbuffer", [new ShaderPackInputBinding(5, "scene")])],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Passes = [new ShaderPackPassSpec("p0", "c.frag.glsl", "backbuffer", [new ShaderPackInputBinding(16, "scene")])],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Passes = [new ShaderPackPassSpec("p0", "c.frag.glsl", "backbuffer", [new ShaderPackInputBinding(0, "scene"), new ShaderPackInputBinding(0, "hdr")])],
        }));

        // Forward reference / self-read / cycle / non-persistent prev
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a"), new ShaderPackTargetSpec("b")],
            Passes =
            [
                new ShaderPackPassSpec("p0", "a.frag.glsl", "a", [new ShaderPackInputBinding(0, "b")]),
                new ShaderPackPassSpec("p1", "b.frag.glsl", "b", [new ShaderPackInputBinding(0, "a")]),
                new ShaderPackPassSpec("p2", null, "backbuffer", []),
            ],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a")],
            Passes =
            [
                new ShaderPackPassSpec("p0", "a.frag.glsl", "a", [new ShaderPackInputBinding(0, "a")]),
                new ShaderPackPassSpec("p1", null, "backbuffer", []),
            ],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a", Persistent: false)],
            Passes =
            [
                new ShaderPackPassSpec("p0", "a.frag.glsl", "a", [new ShaderPackInputBinding(0, "prev:a")]),
                new ShaderPackPassSpec("p1", null, "backbuffer", []),
            ],
        }));
    }

    [LumenFact]
    public static void MapLightFieldEncoderMatchesRoofArtificialLightOutdoorRoomAndFog()
    {
        Span<byte> rgba = stackalloc byte[4];

        // Unroofed cell with an outdoor-temperature Room and independent artificial light.
        MapLightFieldEncoder.EncodeCell(roofed: false, 10, 20, 30, hasOutdoorTemperatureRoom: true, fogged: false, rgba);
        Equal((byte)0, rgba[0]);
        Equal((byte)108, rgba[1]);
        Equal((byte)255, rgba[2]);
        Equal((byte)0, rgba[3]);

        // Roofed outdoor (porch) with dark glow, fogged
        MapLightFieldEncoder.EncodeCell(roofed: true, 0, 0, 0, hasOutdoorTemperatureRoom: true, fogged: true, rgba);
        Equal((byte)255, rgba[0]);
        Equal((byte)0, rgba[1]);
        Equal((byte)255, rgba[2]);
        Equal((byte)255, rgba[3]);

        // Indoor-temperature Room or no Room: B=false, independent of roof and artificial-light cap.
        MapLightFieldEncoder.EncodeCell(roofed: true, 255, 128, 0, hasOutdoorTemperatureRoom: false, fogged: false, rgba);
        Equal((byte)255, rgba[0]);
        Equal((byte)128, rgba[1]);
        Equal((byte)0, rgba[2]);
        Equal((byte)0, rgba[3]);

        // Mid torch glow: r = 20 => 20/255 * 3.6 = 0.28235 -> round(0.28235 * 255) = 72
        MapLightFieldEncoder.EncodeCell(roofed: false, 20, 0, 0, hasOutdoorTemperatureRoom: false, fogged: false, rgba);
        Equal((byte)0, rgba[0]);
        Equal((byte)72, rgba[1]);
        Equal((byte)0, rgba[2]);
        Equal((byte)0, rgba[3]);
    }

    [LumenFact]
    public static void HdrFrameGateHonorsRunOnEmptyFrames()
    {
        Equal(true, HdrFrameGate.ShouldRenderHdr(0, false, true, 0));
        Equal(HdrFrameMode.Render, HdrFrameGate.Decide(false, 0, 0, true));
    }

    [LumenFact]
    public static void UniformsBuildEncodesInverseScreenSizeFlagsAndLiveParams()
    {
        var runtime = new ShaderPackRuntimeParams { BloomIntensity = 0.3f, BloomThreshold = 1.2f, BloomKnee = 0.4f, BloomScatter = 0.7f };
        runtime.SetPackParam(2, 9f);
        runtime.SetPackParam(15, -3f);
        ShaderPackUniformBlockSnapshot snapshot = runtime.CaptureSnapshot();
        Matrix4x4 vp = Matrix4x4.CreateOrthographic(40f, 22.5f, 0.3f, 200f);

        ShaderPackUniforms u = ShaderPackUniforms.Build(
            in vp, 1920, 1080, 250, 200, 12.5f, 20f, 0.05f, 0.016f, 0.75f, 24f,
            hasHdrDraws: true, hasSceneDepth: true, hasMapLightField: false, isPaused: false, linearBlendSpace: false, refractLayer: true, in snapshot);

        Equal(1f / 1920f, u.ScreenAndMapSize.X);
        Equal(1f / 1080f, u.ScreenAndMapSize.Y);
        Equal(250f, u.ScreenAndMapSize.Z);
        Equal(200f, u.ScreenAndMapSize.W);
        Equal(12.5f, u.GameSeconds);
        Equal(0.75f, u.SkyGlow);
        Equal(24f, u.ZoomRootSize);
        Equal(1f, u.HasHdrDraws);
        Equal(1f, u.DepthValid);
        Equal(0f, u.HasMapLightField);
        Require(Matrix4x4.Invert(vp, out Matrix4x4 inv), "ViewProjection must be invertible.");
        Equal(inv, u.InvViewProjection);
        Equal(new Vector4(0.3f, 1.2f, 0.4f, 0.7f), u.BloomParams);
        Equal(new Vector4(0f, 0f, 9f, 0f), u.PackParam0);
        Equal(new Vector4(0f, 0f, 0f, -3f), u.PackParam3);

        // Named float flags are independently available to shader consumers.
        ShaderPackUniforms all = ShaderPackUniforms.Build(
            in vp, 800, 600, 1, 1, 0f, 0f, 0f, 0f, 1f, 24f,
            hasHdrDraws: true, hasSceneDepth: true, hasMapLightField: true, isPaused: true, linearBlendSpace: true, refractLayer: true, in snapshot);
        Equal(1f, all.HasSceneDepth);
        Equal(1f, all.HasMapLightField);
        Equal(1f, all.IsPaused);
        Equal(1f, all.ColorBlendSpace);
        Equal(new Vector4(1f, 0f, 0f, 0f), all.LayerFlags);
        ShaderPackUniforms none = ShaderPackUniforms.Build(
            in vp, 0, 0, 0, 0, 0f, 0f, 0f, 0f, 1f, 24f,
            hasHdrDraws: false, hasSceneDepth: false, hasMapLightField: false, isPaused: false, linearBlendSpace: false, refractLayer: false, in snapshot);
        Equal(0f, none.DepthValid);
        Equal(0f, none.ColorBlendSpace);
        Equal(Vector4.Zero, none.LayerFlags);
        Equal(0f, none.ScreenAndMapSize.X);
    }

    [LumenFact]
    public static void InvViewProjectionUnprojectsScreenUvOntoGroundPlane()
    {
        // Waylith composes camera matrices column-vector style and Slang stores a UBO mat4 RowMajor,
        // so a shader's `M * v` is dot(row, v). Both conventions are pinned against Build() here.
        VerifyGroundPlaneUnproject(Matrix4x4.CreateOrthographic(40f, 22.5f, 0.3f, 200f));

        // IsoRim-style camera: the shim squashes the vertical axis and tilts the view.
        Matrix4x4 squashed = Matrix4x4.CreateOrthographic(40f, 22.5f, 0.3f, 200f);
        squashed.M22 *= 0.62f;
        VerifyGroundPlaneUnproject(squashed);
    }

    [LumenFact]
    public static void ShaderPackFailurePolicyIsolatesCustomPacks()
    {
        var custom = new ShaderPackSpec { Id = "custom" };
        Require(RimLumenRenderHost.ShouldRevertPackOnFailure(custom), "Custom pack failures must revert the pack.");
        Require(!RimLumenRenderHost.ShouldRevertPackOnFailure(ShaderPackSpec.Default),
            "Default pack failures trip the fail-safe instead of reverting.");

        // A failure report for a pack that is no longer active must not touch the live pack.
        Equal(false, RimLumenRenderHost.RevertFailedPackToDefault(custom));
        Equal("default", RimLumenRenderHost.ActiveShaderPack.Id);
    }

    private static void VerifyGroundPlaneUnproject(Matrix4x4 projection)
    {
        // Column-vector camera: view = rotation with the translation in the fourth column, so the shader's
        // `InvViewProjection * v` (dot(row, v)) is the same algebra the C# side composed.
        Vector3 eye = new(51.25f, 30f, 60f);
        Vector3 target = new(51.25f, 0f, 40f);
        Vector3 zAxis = Vector3.Normalize(eye - target);
        Vector3 xAxis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, zAxis));
        Vector3 yAxis = Vector3.Cross(zAxis, xAxis);
        Matrix4x4 view = new(
            xAxis.X, xAxis.Y, xAxis.Z, -Vector3.Dot(xAxis, eye),
            yAxis.X, yAxis.Y, yAxis.Z, -Vector3.Dot(yAxis, eye),
            zAxis.X, zAxis.Y, zAxis.Z, -Vector3.Dot(zAxis, eye),
            0f, 0f, 0f, 1f);
        Matrix4x4 viewProjection = projection * view;

        var runtime = new ShaderPackRuntimeParams();
        runtime.BloomIntensity = 0.3f;
        runtime.SetPackParam(0, 0.375f);
        ShaderPackUniforms uniforms = ShaderPackUniforms.Build(viewProjection, 1920, 1080, 250, 250, 7.5f, 10f, 0.05f, 0.016f, 0.8f, 24f,
            hasHdrDraws: true, hasSceneDepth: true, hasMapLightField: true, isPaused: false, linearBlendSpace: false, refractLayer: true, runtime.CaptureSnapshot());

        Equal(1f / 1920f, uniforms.ScreenAndMapSize.X);
        Equal(250f, uniforms.ScreenAndMapSize.Z);
        Equal(0.3f, uniforms.BloomParams.X);
        Equal(0.375f, uniforms.PackParam0.X);
        Equal(7.5f, uniforms.GameSeconds);

        Vector3 ground = new(51.25f, 0f, 36.75f);
        Vector4 clip = SlangMultiply(viewProjection, new Vector4(ground, 1f));
        Vector2 ndc = new(clip.X / clip.W, clip.Y / clip.W);

        Vector3 nearPoint = SlangUnproject(uniforms.InvViewProjection, ndc, 0f);
        Vector3 farPoint = SlangUnproject(uniforms.InvViewProjection, ndc, 1f);
        float span = farPoint.Y - nearPoint.Y;
        float t = MathF.Abs(span) < 1e-6f ? 0f : -nearPoint.Y / span;
        Vector3 recovered = Vector3.Lerp(nearPoint, farPoint, Math.Clamp(t, 0f, 1f));
        Require(Vector3.Distance(ground, recovered) < 5e-3f, $"Expected {ground}; recovered {recovered}.");

        // An accidental transpose moves the translation out of the rows the shader dots with.
        Vector3 transposed = SlangUnproject(Matrix4x4.Transpose(uniforms.InvViewProjection), ndc, 0f);
        float transposedError = float.IsFinite(transposed.X) && float.IsFinite(transposed.Y) && float.IsFinite(transposed.Z)
            ? Vector3.Distance(ground, transposed)
            : float.PositiveInfinity;
        Require(transposedError > 1f, $"Transposed InvViewProjection must not unproject the ground plane (error {transposedError}).");
    }

    // Slang RowMajor: result_i = dot(matrix row i, v).
    private static Vector4 SlangMultiply(Matrix4x4 m, Vector4 v) => new(
        m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z + m.M14 * v.W,
        m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z + m.M24 * v.W,
        m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z + m.M34 * v.W,
        m.M41 * v.X + m.M42 * v.Y + m.M43 * v.Z + m.M44 * v.W);

    private static Vector3 SlangUnproject(Matrix4x4 inverseViewProjection, Vector2 ndc, float z)
    {
        Vector4 p = SlangMultiply(inverseViewProjection, new Vector4(ndc.X, ndc.Y, z, 1f));
        return new Vector3(p.X / p.W, p.Y / p.W, p.Z / p.W);
    }

    [LumenFact]
    public static void ReloadShaderPackFailsSafelyAndCompilesValidSlangPack()
    {
        // Bad DAG fails cleanly without tripping fail-safe
        Equal(false, RimLumenRenderHost.ReloadShaderPack(new ShaderPackSpec { BloomMipLevels = 99 }));
        Require(RimLumenRenderHost.LastShaderPackError != null, "Expected LastShaderPackError after bad DAG.");
        Equal(false, RimLumenRenderHost.FailSafeTripped);

        // Missing shader file fails cleanly without tripping fail-safe
        Equal(false, RimLumenRenderHost.ReloadShaderPack(new ShaderPackSpec
        {
            CompositeShaderPath = "NonExistentShaderPackPass_12345.frag.glsl",
        }));
        Require(RimLumenRenderHost.LastShaderPackError != null, "Expected LastShaderPackError after missing shader.");
        Equal(false, RimLumenRenderHost.FailSafeTripped);

        // Slang compilation tests (valid + broken GLSL)
        WithSlangLoaded(() =>
        {
            string bloomAdd = Path.Combine(RepoRoot(), "1.7", "Shaders", "PostFx", "RimLumenBloomAdd.frag.glsl");
            RuntimeShaderCompiler.Compile(File.ReadAllText(bloomAdd), bloomAdd, "main",
                RuntimeShaderLanguage.Glsl, RuntimeShaderStage.Fragment, out _);
            string tempDir = Path.Combine(Path.GetTempPath(), "opencode", "rimlumen-shaderpack-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string goodFrag = Path.Combine(tempDir, "TestComposite.frag.glsl");
                File.WriteAllText(goodFrag, """
                    #version 450
                    layout(location = 0) in vec2 vUv;
                    layout(set = 0, binding = 0) uniform sampler2D uHdr;
                    layout(set = 0, binding = 1) uniform sampler2D uBloom;
                    layout(set = 0, binding = 2) uniform sampler2D uScene;
                    layout(set = 0, binding = 3) uniform sampler2D uDepth;
                    layout(set = 0, binding = 4) uniform sampler2D uLightField;
                    layout(std140, set = 0, binding = 5) uniform ShaderPackUniforms
                    {
                        mat4 InvViewProjection;
                        vec4 ScreenAndMapSize;
                        float GameSeconds;
                        float RealSeconds;
                        float SkyGlow;
                        float ZoomRootSize;
                        float HasHdrDraws;
                        float HasSceneDepth;
                        float HasMapLightField;
                        float IsPaused;
                        float DepthValid;
                        float GameDeltaSeconds;
                        float RealDeltaSeconds;
                        float ColorBlendSpace;
                        vec4 BloomParams;
                        vec4 PackParams[4];
                    } ubo;
                    layout(push_constant) uniform Push { vec4 value; } pc;
                    layout(location = 0) out vec4 outColor;
                    void main()
                    {
                        vec4 wp = ubo.InvViewProjection * vec4(vUv * 2.0 - 1.0, texture(uDepth, vUv).r, 1.0);
                        vec3 lf = texture(uLightField, wp.xz / max(ubo.ScreenAndMapSize.zw, vec2(1.0))).rgb;
                        vec3 hdr = texture(uHdr, vUv).rgb + texture(uBloom, vUv).rgb * ubo.BloomParams.x;
                        vec3 scene = texture(uScene, vUv).rgb;
                        outColor = vec4(scene + hdr + lf * 0.01 + pc.value.xxx * 0.0 + ubo.PackParams[0].xyz * 0.0, 1.0);
                    }
                    """);

                var goodSpec = new ShaderPackSpec
                {
                    Id = "test-valid",
                    CompositeShaderPath = goodFrag,
                    RunOnEmptyFrames = true,
                    RequiresSceneDepth = true,
                    RequiresMapLightField = true,
                    BloomMipLevels = 4,
                };
                Equal(true, RimLumenRenderHost.ReloadShaderPack(goodSpec));
                Equal<string?>(null, RimLumenRenderHost.LastShaderPackError);
                Equal("test-valid", RimLumenRenderHost.ActiveShaderPack.Id);
                Equal(true, RimLumenRenderHost.IsMapLightFieldActive);

                // Bad GLSL must fail cleanly, preserve previous valid pack, and not trip fail-safe
                string badFrag = Path.Combine(tempDir, "Broken.frag.glsl");
                File.WriteAllText(badFrag, "#version 450\nvoid main() { outColor = undefined_symbol; }\n");
                Equal(false, RimLumenRenderHost.ReloadShaderPack(new ShaderPackSpec
                {
                    Id = "test-broken",
                    CompositeShaderPath = badFrag,
                }));
                Require(RimLumenRenderHost.LastShaderPackError != null, "Expected LastShaderPackError after Slang failure.");
                Equal(false, RimLumenRenderHost.FailSafeTripped);
                Equal("test-valid", RimLumenRenderHost.ActiveShaderPack.Id);

                // Reset back to Default
                Equal(true, RimLumenRenderHost.ReloadShaderPack(ShaderPackSpec.Default));
                Equal<string?>(null, RimLumenRenderHost.LastShaderPackError);
                Equal("default", RimLumenRenderHost.ActiveShaderPack.Id);
                Equal(false, RimLumenRenderHost.IsMapLightFieldActive);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        });
    }

    [LumenFact]
    public static void BorrowedProxyAndCollectorReleaseSurviveTeardown()
    {
        // ReplaceShaderPack drops the proxy before the next HDR record rebinds it; Clear must null the borrowed
        // pointer without touching ownership.
        var proxy = new BorrowedRenderTexture();
        proxy.Clear();
        Require(proxy.GpuResource is null, "Clear must null the borrowed GpuResource.");
        Equal(0, proxy.width);
        Equal(0, proxy.height);

        // Faulted-render-thread release path: CPU mirror drops with no device waits and no throw.
        MapLightFieldCollector.Release(null, waitIdle: false);
        Equal(false, MapLightFieldCollector.TryGetDescriptor(out _, out _, out _, out _));
    }

    private static void WithSlangLoaded(Action action)
    {
        string repoRoot = RepoRoot();
        string buildProperties = Path.Combine(repoRoot, "Directory.Build.props");
        string gameRoot = Environment.GetEnvironmentVariable("GameRoot")
            ?? XDocument.Load(buildProperties).Descendants("GameRoot").Single().Value;
        string nativePath = Path.Combine(gameRoot, "slang.dll");
        nint ResolveSlang(Assembly assembly, string name)
        {
            if (assembly.GetName().Name != "Brutal.Slang" || name != "slang")
                return 0;
            return NativeLibrary.Load(nativePath, assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
        }
        string optimizerPath = Path.Combine(gameRoot, "slang-glslang.dll");
        nint optimizer = NativeLibrary.Load(optimizerPath, typeof(ShaderPackTests).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveSlang;
        try
        {
            action();
        }
        finally
        {
            AssemblyLoadContext.Default.ResolvingUnmanagedDll -= ResolveSlang;
            NativeLibrary.Free(optimizer);
        }
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Cannot locate repository root above test output.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; actual {actual}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
