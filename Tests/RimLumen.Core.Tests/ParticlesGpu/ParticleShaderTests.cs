using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RimLumen.Core.ParticlesGpu;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Lighting;
using RimLumen.Core.Rendering;
using RimLumen.Core.Scene;
using Waylith.Assets;
using Waylith.Graphics.Meshes;
using Waylith.Rendering;
using Waylith.Rendering.Compute;

namespace RimLumen.Core.Tests.ParticlesGpu;

[LumenTestSuite]
public static class ParticleShaderTests
{
    [LumenFact]
    public static void PhaseATextureMetadataDistinguishesEncodingAndRejectsUnknownLinearSampling()
    {
        using var stored = new Texture2D(1, 1, LoadedTextureFormat.Rgba32, linear: false, mipCount: 1, createUninitialized: true);
        using var linear = new Texture2D(1, 1, LoadedTextureFormat.Rgba32, linear: true, mipCount: 1, createUninitialized: true);
        Require(LumenTextureRegistry.TextureIsSrgb("stored", stored, LumenColorSpace.Linear)
            && !LumenTextureRegistry.TextureIsSrgb("linear", linear, LumenColorSpace.Linear),
            "Loaded CpuData.Linear must be the actual texture-decode flag.");
        using var unbacked = Texture2D.CreateUnbacked(1, 1);
        using var volume = new Texture3D(1, 1, 1);
        Require(!LumenTextureRegistry.TextureIsSrgb("unbacked", unbacked, LumenColorSpace.Stored)
            && !LumenTextureRegistry.TextureIsSrgb("volume", volume, LumenColorSpace.Stored),
            "Stored sampling does not require or guess colour metadata.");
        RequireMissingMetadata("unbacked", unbacked);
        RequireMissingMetadata("volume", volume);
    }

    private static void RequireMissingMetadata(string key, Texture texture)
    {
        try { LumenTextureRegistry.TextureIsSrgb(key, texture, LumenColorSpace.Linear); }
        catch (InvalidOperationException error)
        {
            Require(error.Message.Contains(key, StringComparison.Ordinal)
                && error.Message.Contains("colour metadata missing", StringComparison.Ordinal),
                "Metadata rejection must name the texture key and missing colour metadata.");
            return;
        }
        throw new InvalidOperationException("Linear sampling must reject textures without colour metadata: " + key);
    }

    [LumenFact]
    public static void PhaseASharedCompositionIsWiredBeforeCoverageAndPremultiplication()
    {
        string l1 = Read("L1ParticleQuad.frag.glsl"), l2 = Read("ParticleBillboard.frag.glsl");
        Require(l1.Contains("lumenParticleStoredColor(color, lumen._LumenMaterialTint, texel", StringComparison.Ordinal)
            && l2.Contains("lumenParticleStoredColor(vColor, material._LumenMaterialTint, texel", StringComparison.Ordinal),
            "Both real fragment entry points must call the shared straight stored composition with raw vertex and separate tint.");
        string helper = Read("ParticleColor.glsl");
        Require(helper.Contains("vertex.rgb * lumenDecodeStored(tint.rgb)", StringComparison.Ordinal)
            && helper.Contains("textureSrgb ? lumenDecodeStored(texel.rgb) : texel.rgb", StringComparison.Ordinal)
            && helper.Contains("vertex.a * tint.a * texel.a", StringComparison.Ordinal)
            && !helper.Contains("clamp(", StringComparison.Ordinal),
            "Linear materials decode only tint/sRGB texels, preserve alpha multiplication and extend HDR without clamping.");
        Require(helper.Contains("pow((storedRgb + 0.055) / 1.055, vec3(2.4))", StringComparison.Ordinal)
            && helper.Contains("storedRgb / 12.92", StringComparison.Ordinal)
            && helper.Contains("lessThanEqual(storedRgb, vec3(0.04045))", StringComparison.Ordinal)
            && helper.Contains("1.055 * pow(linearRgb, vec3(1.0 / 2.4)) - 0.055", StringComparison.Ordinal)
            && helper.Contains("linearRgb * 12.92", StringComparison.Ordinal)
            && helper.Contains("lessThanEqual(linearRgb, vec3(0.0031308))", StringComparison.Ordinal),
            "Shared shader must use the pinned extended piecewise sRGB equations, not a gamma approximation.");
        Require(l1.IndexOf("lumenParticleStoredColor(", StringComparison.Ordinal) < l1.IndexOf("c.a *=", StringComparison.Ordinal)
            && l1.IndexOf("c.a *=", StringComparison.Ordinal) < l1.IndexOf("c.rgb *= c.a", StringComparison.Ordinal)
            && l2.IndexOf("lumenParticleStoredColor(", StringComparison.Ordinal) < l2.IndexOf("color.a *=", StringComparison.Ordinal)
            && l2.IndexOf("color.a *=", StringComparison.Ordinal) < l2.IndexOf("color.rgb *= color.a", StringComparison.Ordinal),
            "Straight composition must precede soft coverage and output premultiplication in both real entry points.");
        string root = Path.GetFullPath(Path.Combine(ShaderDirectory(), "..", "..", "..", "Source", "RimLumen.Core"));
        string cpu = File.ReadAllText(Path.Combine(root, "Rendering", "L1ParticleBatchRenderer.cs"));
        string gpu = File.ReadAllText(Path.Combine(root, "ParticlesGpu", "ParticleGpuDrawResources.cs"));
        string enqueue = File.ReadAllText(Path.Combine(root, "ParticlesGpu", "LumenGpuParticleEngine.Gpu.cs"));
        Require(cpu.Contains("DrawProperty.Vector(TintPropertyId, batch.MaterialTint)", StringComparison.Ordinal)
            && cpu.Contains("batch.TextureSrgb ? 1f : 0f", StringComparison.Ordinal)
            && gpu.Contains("DrawProperty.Vector(TintPropertyId, emitter.MaterialTint)", StringComparison.Ordinal)
            && gpu.Contains("textureSrgb[i] ? 1f : 0f", StringComparison.Ordinal)
            && enqueue.Contains("LumenTextureRegistry.TextureIsSrgb(key, texture, emitter.ColorSpace)", StringComparison.Ordinal)
            && enqueue.Contains("gpu.TextureSrgb[i] = texture is not null && textureSrgb", StringComparison.Ordinal)
            && enqueue.Contains("frame.Gpu.TextureSrgb", StringComparison.Ordinal),
            "Main-thread texture flags and captured tint must reach per-draw arena vectors, not cached material mutation.");
    }

    [LumenFact]
    public static void PhaseBConvertsStraightColorThenGainsAndCompositesInLinearLight()
    {
        string l1 = Read("L1ParticleQuad.frag.glsl"), l2 = Read("ParticleBillboard.frag.glsl");
        int l1Convert = l1.IndexOf("c = lumenColorForBlendSpace(c, lumen._LumenColor.z > 0.5)", StringComparison.Ordinal);
        int l2Convert = l2.IndexOf("color = lumenColorForBlendSpace(color, material._LumenColor.z > 0.5)", StringComparison.Ordinal);
        int l1Gain = l1.IndexOf("c.rgb *= hdrGain;", StringComparison.Ordinal);
        int l2Gain = l2.IndexOf("color.rgb *= vSoftAndGain.y;", StringComparison.Ordinal);
        Require(l1Convert >= 0 && l1Convert < l1Gain && l1Gain < l1.IndexOf("c.rgb *= c.a", StringComparison.Ordinal)
            && l2Convert >= 0 && l2Convert < l2Gain && l2Gain < l2.IndexOf("color.rgb *= color.a", StringComparison.Ordinal),
            "Both shaders must convert straight colour, then apply the scalar gain, then premultiply.");
        Require(!Read("ParticleBillboard.vert.glsl").Contains("vColor.rgb *=", StringComparison.Ordinal),
            "L2 must not fold the radiance gain into the vertex colour before the sRGB transfer.");
        string postFx = Path.GetFullPath(Path.Combine(ShaderDirectory(), "..", "PostFx"));
        Require(!File.Exists(Path.Combine(postFx, "RimLumenEncodeStored.frag.glsl")),
            "The pre-encoded E(L*a) layer is retired; composites blend in linear light instead.");
        string composite = ShaderSourceLoader.ReadShaderSource(Path.Combine(postFx, "RimLumenComposite.frag.glsl"));
        Require(composite.Contains("if (hdr == vec4(0.0) && bloom == vec3(0.0)", StringComparison.Ordinal)
            && composite.Contains("float a = clamp(hdr.a, 0.0, 1.0);", StringComparison.Ordinal)
            && composite.Contains("scene * (1.0 - a) + hdr.rgb + bloom", StringComparison.Ordinal)
            && composite.Contains("vec4(lumenEncodeStored(compressHighlights(linear)), 1.0)", StringComparison.Ordinal),
            "Linear composite must skip empty pixels and encode D(scene)(1-a)+hdr+bloom once, opaquely.");
        string prefilter = ShaderSourceLoader.ReadShaderSource(Path.Combine(postFx, "RimLumenBloomPrefilter.frag.glsl"));
        Require(prefilter.Contains("if (ubo.ColorBlendSpace > 0.5) glow = lumenDecodeStored(glow);", StringComparison.Ordinal),
            "Linear bloom must decode the stored glow copies into linear radiance.");
        string root = Path.GetFullPath(Path.Combine(ShaderDirectory(), "..", "..", "..", "Source", "RimLumen.Core"));
        string pipeline = File.ReadAllText(Path.Combine(root, "Rendering", "RimLumenPipeline.cs"));
        string chain = File.ReadAllText(Path.Combine(root, "Rendering", "RimLumenMapChain.cs"));
        string gpu = File.ReadAllText(Path.Combine(root, "ParticlesGpu", "ParticleGpuDrawResources.cs"));
        Require(pipeline.Contains("colorBlendSpace = RimLumenRenderHost.CurrentFrameColorBlendSpace", StringComparison.Ordinal)
            && pipeline.Contains("ColorBlendSpace = colorBlendSpace", StringComparison.Ordinal)
            && pipeline.Contains("earlyWork, colorBlendSpace, takeoverFrame)", StringComparison.Ordinal)
            && chain.Contains("ColorBlendSpace = payload.ColorBlendSpace", StringComparison.Ordinal)
            && gpu.Contains("snapshot.Context.ColorBlendSpace == LumenColorBlendSpace.Linear", StringComparison.Ordinal),
            "Capture, draw context, and GPU properties must carry the same frame latch.");
    }

    [LumenFact]
    public static void P2A4aFlipbookStartsAtTopRow()
    {
        string vertex = Read("ParticleBillboard.vert.glsl");
        Require(vertex.Contains("1.0 - float(frame / columns + 1u) / float(rows)", StringComparison.Ordinal),
            "Frame zero must start at v=1-1/rows, not the bottom row.");
    }

    [LumenFact]
    public static void WorldSizeScaleIsBakedOrAppliedOncePerSpace()
    {
        string compute = Read("ParticleSim.comp.glsl");
        string vertex = Read("ParticleBillboard.vert.glsl");
        Require(compute.Contains("emitter.SizeCone.w > 0.5 ? emitter.Appearance.w : 1.0", StringComparison.Ordinal),
            "World-space size scale must bake cbrt(|det|) into the start size at birth.");
        Require(vertex.Contains("if (emitter.SizeCone.w < 0.5) size *= emitter.Appearance.w;", StringComparison.Ordinal),
            "Local-space size scale must multiply at draw, never both.");
    }

    [LumenFact]
    public static void P2A4bProceduralCoverageUsesSquaredDisc()
    {
        string disc = Read("ParticleDisc.glsl");
        string l1 = File.ReadAllText(Path.Combine(ShaderDirectory(), "L1ParticleQuad.frag.glsl"));
        string l2 = File.ReadAllText(Path.Combine(ShaderDirectory(), "ParticleBillboard.frag.glsl"));
        Require(disc.Contains("max(0.0, 1.0 - dot(radial, radial))", StringComparison.Ordinal)
            && disc.Contains("return coverage * coverage;", StringComparison.Ordinal)
            && l1.Contains("#include \"ParticleDisc.glsl\"", StringComparison.Ordinal)
            && l2.Contains("#include \"ParticleDisc.glsl\"", StringComparison.Ordinal)
            && l1.Contains("lumenParticleDiscCoverage(radialUv)", StringComparison.Ordinal)
            && l2.Contains("lumenParticleDiscCoverage(vRadialUv)", StringComparison.Ordinal),
            "At squared radius 0.5 coverage must be (1-0.5)^2=0.25, matching L2.");
    }

    [LumenFact]
    public static void P2A4cProceduralDiscIgnoresAtlasUvs()
    {
        string vertex = Read("L1ParticleQuad.vert.glsl"), fragment = Read("L1ParticleQuad.frag.glsl");
        Require(fragment.Contains("lumenParticleDiscCoverage(radialUv)", StringComparison.Ordinal)
            && vertex.Contains("radialUv = inRadialUvGain.xy;", StringComparison.Ordinal)
            && !vertex.Contains("gl_VertexIndex", StringComparison.Ordinal),
            "Procedural coordinates must come from the quad corner, not the animated atlas tile.");
        Require(Read("ParticleBillboard.frag.glsl").Contains("lumenParticleDiscCoverage(vRadialUv)", StringComparison.Ordinal),
            "L2 must also feed its independent quad coordinates, never flipbook UVs, into the shared disc.");
        Require(ShaderMetadataParser.ValidateStageInterface(vertex, fragment, "L1.vert", "L1.frag").Count == 0,
            "Quad-local coordinates must cross the vertex/fragment interface.");
    }

    [LumenFact]
    public static void ParityForceTwinPinsGpuAccelerationAndAnalyticCurl()
    {
        string shader = Read("ParticleSim.comp.glsl");
        string twin = File.ReadAllText(Path.Combine(ShaderDirectory(), "..", "..", "..", "Source", "RimLumen.Core", "Scene", "ParticleForceMath.cs"));
        Require(shader.Contains("total += axisOrCenter * strength", StringComparison.Ordinal)
            && shader.Contains("drag += max(0.0, parameters.x)", StringComparison.Ordinal)
            && shader.Contains("cross(axis, radialDirection) * strength - radialDirection * parameters.x", StringComparison.Ordinal)
            && shader.Contains("+ axis * parameters.y) * weight", StringComparison.Ordinal)
            && shader.Contains("radius > 0.0 ? max(0.0, 1.0 - distanceToCenter / radius) : 1.0", StringComparison.Ordinal),
            "GPU force equations differ from the CPU twin's canonical gravity/vortex/attractor equations.");
        Require(shader.Contains("vec3 relative = position - center", StringComparison.Ordinal)
            && shader.Contains("emitter.World * vec4(axisOrCenter, 1.0)", StringComparison.Ordinal),
            "World-space fields must follow the current node origin/centre.");
        Require(shader.Contains("vec3 q = (position - parameters) * frequency", StringComparison.Ordinal)
            && twin.Contains("(position - field.Parameters) * field.Frequency", StringComparison.Ordinal)
            && shader.Contains("vec3(dz.y - dy.z, dx.z - dz.x, dy.x - dx.y)", StringComparison.Ordinal)
            && twin.Contains("new(dz.Y - dy.Z, dx.Z - dz.X, dy.X - dx.Y)", StringComparison.Ordinal),
            "CPU/GPU must use the same curl orientation, offset and frequency chain rule.");
        string[] sharedNoiseWords = { "0x8da6b343u", "0xd8163841u", "0xcb1ab31fu", "0x9e3779b9u", "0x85ebca6bu", "0xc2b2ae35u", "0x68bc21ebu", "0x02e5be93u", "0x967a889bu" };
        Require(sharedNoiseWords.All(word => shader.Contains(word, StringComparison.Ordinal) && twin.Contains(word, StringComparison.Ordinal)),
            "Noise hash, lattice or potential channels drifted between CPU and GPU.");
    }

    [LumenFact]
    public static void ParityShapeAndStretchPinGpuCanonicalEquations()
    {
        string sim = Read("ParticleSim.comp.glsl"), draw = Read("ParticleBillboard.vert.glsl");
        Require(sim.Contains("vec3 direction = vec3(0.0, 1.0, 0.0)", StringComparison.Ordinal)
            && sim.Contains("position = radial * radius * extents", StringComparison.Ordinal)
            && sim.Contains("velocity = (emitter.World * vec4(velocity, 0.0)).xyz", StringComparison.Ordinal)
            && sim.Contains("rotateShape(emitter.ShapeRotation, position * emitter.ShapeScale.xyz) + emitter.ShapePosition.xyz", StringComparison.Ordinal)
            && sim.Contains("direction = rotateShape(emitter.ShapeRotation, direction)", StringComparison.Ordinal),
            "Canonical emission direction, elliptic disc or scaled velocity changed.");
        Require(draw.Contains("height = size * emitter.Appearance.y + length(velocity) * emitter.Appearance.x", StringComparison.Ordinal)
            && draw.Contains("right = vec3(1.0, 0.0, 0.0)", StringComparison.Ordinal)
            && draw.Contains("up = vec3(0.0, 0.0, 1.0)", StringComparison.Ordinal),
            "Canonical stretch units or horizontal axes changed.");
        string twin = File.ReadAllText(Path.Combine(ShaderDirectory(), "..", "..", "..", "Source", "RimLumen.Core", "Rendering", "CameraBillboardBasis.cs"));
        Require(draw.Contains("(along * (0.8660254 - 1.0) + value.y * 0.5) * groundUp", StringComparison.Ordinal)
            && twin.Contains("(along * (SinPitch - 1f) + value.Y * CosPitch) * groundUp", StringComparison.Ordinal)
            && twin.Contains("SinPitch = 0.8660254f", StringComparison.Ordinal) && twin.Contains("CosPitch = 0.5f", StringComparison.Ordinal)
            && draw.Contains("emitter.SizeCone.w > 0.5 ? lumenUnpackAnchor(particle.SpawnAnchorXZ) : lumenNodeGroundXZ(emitter.World)", StringComparison.Ordinal)
            && sim.Contains("particle.SpawnAnchorXZ = lumenPackAnchor(lumenNodeGroundXZ(emitter.World))", StringComparison.Ordinal)
            && sim.Contains("child.SpawnAnchorXZ = lumenPackAnchor(ev.PositionAndTrigger.xz)", StringComparison.Ordinal),
            "Vertical3D top-down pitch or the node ground anchor drifted between CPU and GPU.");
    }

    [LumenFact]
    public static void Vertical3DAndKillBelowFloorUseFixedGroundInGpuStages()
    {
        string draw = Read("ParticleBillboard.vert.glsl"), sim = Read("ParticleSim.comp.glsl");
        bool absoluteProjection = draw.Contains("projectTopDownPitch(center - vec3(anchor.x, LUMEN_GROUND_Y, anchor.y), groundUp)", StringComparison.Ordinal)
            && draw.Contains("center.y /= yScale;", StringComparison.Ordinal);
        bool descendingBelowGround = sim.Contains("if (worldY < LUMEN_GROUND_Y && worldVelY < 0.0)", StringComparison.Ordinal);
        // GroundXZ under the straight-down 2D camera draws on the mote layer, as CameraBillboardBasis.ProjectToDisplay.
        // Every corner shares it: the billboard axes lose their Y before the corners are placed.
        bool groundDrawLayer = Regex.IsMatch(draw, @"else\s+center\.y = material\._LumenVertical\.z;")
            && Regex.IsMatch(draw, @"if \(topDown2D\)\s*\{[^}]*right\.y = 0\.0;\s*up\.y = 0\.0;\s*\}\s*vec3 worldPosition = center \+")
            && draw.Contains("bool topDown2D = material._LumenVertical.y > 0.5;", StringComparison.Ordinal);
        Require(groundDrawLayer, "L2 must draw every corner on the mote layer under the straight-down 2D camera.");
        Require(absoluteProjection && descendingBelowGround,
            $"L2 fixed-ground formulas: absolute projection={absoluteProjection}, descending below ground={descendingBelowGround}.");

        Match ground = Regex.Match(sim, @"\bconst\s+float\s+LUMEN_GROUND_Y\s*=\s*(?<y>[-.\d]+)\s*;");
        Require(ground.Success, "L2 ground-plane include must define LUMEN_GROUND_Y.");
        float shaderGroundY = float.Parse(ground.Groups["y"].Value, System.Globalization.CultureInfo.InvariantCulture);
        Require(shaderGroundY == SpacePlaneTransform.GroundY,
            $"L1/L2 ground-plane constants differ: CPU={SpacePlaneTransform.GroundY}, GPU={shaderGroundY}.");
    }

    [LumenFact]
    public static void DepthMaskDiscardsNorthFragmentsWithinTheLiftInL1AndL2()
    {
        string l2Vertex = Read("ParticleBillboard.vert.glsl"), l2Fragment = Read("ParticleBillboard.frag.glsl");
        string l1Vertex = Read("L1ParticleQuad.vert.glsl"), l1Fragment = Read("L1ParticleQuad.frag.glsl");
        // The vertex input slot (TexCoord2, location 4) is checked by the L1 layout test through Waylith's parser.
        bool l2 = Regex.IsMatch(l2Vertex, @"vDepthMask\s*=\s*vec2\(\s*material\._LumenVertical\.w\s*,\s*worldPosition\.z\s*-\s*emitter\.ShapeScale\.w\s*\)")
            && Regex.IsMatch(l2Fragment, @"vDepthMask\.y\s*>\s*0\.0\s*&&\s*separation\s*<\s*vDepthMask\.x\s*\)\s*discard");
        bool l1 = Regex.IsMatch(l1Vertex, @"depthMask\s*=\s*vec2\(\s*inDepthMask\.x\s*,\s*inPosition\.z\s*-\s*inDepthMask\.y\s*\)")
            && Regex.IsMatch(l1Fragment, @"depthMask\.y\s*>\s*0\.0\s*&&\s*separation\s*<\s*depthMask\.x\s*\)\s*discard");
        Require(l1 && l2, $"Depth mask north discard: L1={l1}, L2={l2}.");
    }

    [LumenFact]
    public static void EmitterBufferAllocationDerivesStrideFromUploadedStruct()
    {
        string shader = Read("ParticleTypes.glsl");
        Match body = Regex.Match(shader, @"struct\s+GpuEmitterParams\s*\{(?<body>[^}]+)\}");
        // Scalar tail groups are padded to 16 bytes; check each actual member below as well as the row stride.
        int shaderStride = Regex.Matches(body.Groups["body"].Value, @"\b(vec4|mat4|uint)\s+\w+\s*;")
            .Cast<Match>().Sum(member => member.Groups[1].Value switch { "mat4" => 64, "vec4" => 16, _ => 4 });
        CheckStruct<GpuEmitterParams>(shader, shaderStride);
        int structStride = Marshal.SizeOf<GpuEmitterParams>();
        int uploadedBytes = MemoryMarshal.AsBytes(new GpuEmitterParams[3].AsSpan()).Length;
        long one = ParticleGpuFrame.EmitterBufferBytes(1), three = ParticleGpuFrame.EmitterBufferBytes(3);
        Console.WriteLine($"Production emitter allocation: capacity1={one}, capacity3={three}; uploaded struct={structStride}, std140/std430={shaderStride}, upload3={uploadedBytes} bytes.");
        Require(one == structStride && one == shaderStride && three == uploadedBytes && three == (long)shaderStride * 3,
            "Production emitter allocation must match the actual uploaded struct bytes and shader row layout for each capacity.");
        string path = Path.Combine(ShaderDirectory(), "..", "..", "..", "Source", "RimLumen.Core", "ParticlesGpu", "ParticleGpuDrawResources.cs");
        string source = Regex.Replace(File.ReadAllText(path), @"//[^\r\n]*|/\*[\s\S]*?\*/", "");
        Require(Regex.IsMatch(source, @"Emitters\s*=\s*GpuComputeBuffer\.Create\([^;]+EmitterBufferBytes\(EmitterCapacity\)\s*\)\s*;")
            && Regex.IsMatch(source, @"Emitters\.Upload<GpuEmitterParams>\(parameters\.AsSpan\(0,\s*emitters\.Length\)\)"),
            "The constructor must allocate using the exercised production byte calculation for the typed emitter upload.");
    }

    [LumenFact]
    public static void ShaderStorageMembersMatchFixedCpuByteLayout()
    {
        string source = Read("ParticleTypes.glsl");
        CheckStruct<GpuParticle>(source, 64);
        CheckStruct<GpuEmitterParams>(source, 480);
        CheckStruct<GpuSubEvent>(source, 48);
        CheckStruct<GpuSubLink>(source, 32);
        CheckStruct<GpuForceField>(source, 48);
        CheckStruct<GpuCurveSample>(source, 32);
        string trailTypes = Read("ParticleTrailTypes.glsl");
        CheckStruct<GpuTrailHeader>(trailTypes, 80);
        CheckStruct<GpuTrailPoint>(trailTypes, 32);
        CheckStruct<GpuTrailEmitterParams>(trailTypes, 160);
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
        Require(bindings.SetEquals(Enumerable.Range(0, 10)), "Compute bindings must match the ten host-bound particle/world/event buffers.");
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
        Require(ShaderMetadataParser.ValidateStageInterface(vertex, fragment, "particle.vert", "particle.frag").Count == 0,
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
        Require(layout.UniformSize == 80, $"L1 material UBO must be 80 bytes; got {layout.UniformSize}.");
        Require(layout.UniformSlots.Select(x => (x.Offset, x.PropertyName)).SequenceEqual(new[]
        {
            (0, "_LumenDraw"), (16, "_LumenDepth"), (32, "_LumenProjection"),
            (48, "_LumenMaterialTint"), (64, "_LumenColor")
        }), "L1 material UBO offsets differ from the runtime packing.");
        CheckOverrideSlots(layout, ("_LumenDraw", 0), ("_LumenDepth", 16), ("_LumenProjection", 32),
            ("_LumenMaterialTint", 48), ("_LumenColor", 64));
        Require(layout.TextureSlots.Select(x => (x.Binding, x.PropertyName)).SequenceEqual(new[]
        {
            (1, "_LumenParticleTexture"), (2, "_LumenSceneDepth")
        }), "L1 texture bindings differ from the material contract.");
        Require(layout.StorageBufferSlots.Length == 0, "L1 quads are not instanced and declare no storage buffers.");
        Require(ShaderMetadataParser.ValidateStageInterface(vertex, fragment, "L1ParticleQuad.vert", "L1ParticleQuad.frag").Count == 0,
            "Waylith reports mismatched L1 vertex/fragment varyings.");
        ShaderVertexInputLayout input = ShaderMetadataParser.ExtractVertexInputLayout(vertex, "L1ParticleQuad.vert");
        Require(!input.HasErrors, "Waylith rejected the L1 vertex input layout.");
        Require(input.Slots.Select(x => (x.location, x.semantic, x.required)).SequenceEqual(new[]
        {
            (0, VertexAttributeSemantic.Position, true),
            (1, VertexAttributeSemantic.TexCoord0, true),
            (2, VertexAttributeSemantic.Color, true),
            (3, VertexAttributeSemantic.TexCoord1, true),
            (4, VertexAttributeSemantic.TexCoord2, true)
        }), "L1 vertex input must require Position/TexCoord0/Color/TexCoord1/TexCoord2 at locations 0..4.");
    }

    [LumenFact]
    public static void LightPoolShaderMatchesSceneDefaultStageAndMaterialContract()
    {
        string gameRoot = Environment.GetEnvironmentVariable("RIMWORLD_DIR")
            ?? throw new InvalidOperationException("RIMWORLD_DIR is not set; run Tools/test-offline.ps1.");
        string vertex = File.ReadAllText(Path.Combine(gameRoot, "Rendering", "Shaders", LightPoolRenderer.VertexFile));
        string fragmentPath = Path.GetFullPath(Path.Combine(ShaderDirectory(), "..", LightPoolRenderer.AccumulateFile));
        string fragment = ShaderSourceLoader.ReadShaderSource(fragmentPath);

        MaterialLayout layout = ShaderMetadataParser.ExtractMaterialLayout(fragment, "RimLumenLightPoolAccumulate.frag.glsl");
        Require(!layout.HasErrors, "Waylith rejected the light-pool material metadata.");
        Require(layout.UniformSize == 48, $"Light-pool material UBO must be 48 bytes; got {layout.UniformSize}.");
        CheckOverrideSlots(layout, ("_LumenLight", 0), ("_LumenLightAt", 16), ("_LumenShadowChannel", 32));
        Require(layout.TextureSlots.Select(x => (x.Binding, x.PropertyName)).SequenceEqual(new[]
            { (1, "_LumenOcclusion"), (2, "_LumenOcclusionCoarse"), (3, "_LumenDoorOpen"), (4, "_LumenObjectShadow") })
            && layout.StorageBufferSlots.Length == 0,
            "Light-pool shader declares the occlusion, coarse and door textures and the object-shadow mask at bindings 1 through 4.");
        Require(ShaderMetadataParser.ValidateStageInterface(vertex, fragment, LightPoolRenderer.VertexFile, "RimLumenLightPoolAccumulate.frag.glsl").Count == 0,
            "Waylith reports mismatched SceneDefault/RimLumenLightPoolAccumulate varyings.");
        Require(fragment.Contains("vec2 d = vUv * 2.0 - 1.0;", StringComparison.Ordinal)
            && fragment.Contains("float q = max(0.0, 1.0 - dot(d, d));", StringComparison.Ordinal)
            && fragment.Contains("falloff = q * q;", StringComparison.Ordinal)
            && fragment.Contains("rgb = uMaterial._LumenLight.rgb * falloff;", StringComparison.Ordinal)
            && fragment.Contains("if (uMaterial._LumenLight.a > 0.5)", StringComparison.Ordinal)
            && fragment.Contains("visibility = 1.0;", StringComparison.Ordinal)
            && fragment.Contains("outColor = vec4(rgb * visibility, 0.0);", StringComparison.Ordinal),
            "Light-pool accumulation must evaluate (1 - (d/R)^2)^2 times wall visibility (or 1 when castShadows=0) with zero alpha.");
        string composite = ShaderSourceLoader.ReadShaderSource(
            Path.GetFullPath(Path.Combine(ShaderDirectory(), "..", LightPoolRenderer.CompositeFile)));
        Require(composite.Contains("if (peak <= 1.0) return color;", StringComparison.Ordinal)
            && composite.Contains("if (any(isnan(sum))) sum = vec3(0.0);", StringComparison.Ordinal)
            && composite.Contains("sum = clamp(sum, vec3(0.0), vec3(1024.0));", StringComparison.Ordinal)
            && composite.Contains("return compressHighlights(sum);", StringComparison.Ordinal)
            && composite.Contains("vec4(lumenLightPoolTerm(lightSum, ivec2(gl_FragCoord.xy)), 0.0)", StringComparison.Ordinal),
            "Light-pool composite must apply the linear sum once, identity up to 1 and compressed above.");
    }

    [LumenFact]
    public static void ObjectShadowCasterShaderMatchesItsMaterialAndVaryings()
    {
        string vertex = ShaderSourceLoader.ReadShaderSource(Path.GetFullPath(Path.Combine(ShaderDirectory(), "..", LightPoolRenderer.CasterVertexFile)));
        string fragment = ShaderSourceLoader.ReadShaderSource(Path.GetFullPath(Path.Combine(ShaderDirectory(), "..", LightPoolRenderer.CasterFragmentFile)));
        MaterialLayout layout = ShaderMetadataParser.ExtractMaterialLayout(fragment, "RimLumenObjectShadow.frag.glsl");
        Require(!layout.HasErrors && layout.UniformSize == 48 && layout.TextureSlots.Length == 0,
            $"Caster material must be three vectors and no texture; got {layout.UniformSize} bytes.");
        CheckOverrideSlots(layout, ("_LumenCasterLight", 0), ("_LumenCasterSun", 16), ("_LumenCasterChannel", 32));
        Require(ShaderMetadataParser.ValidateStageInterface(vertex, fragment, LightPoolRenderer.CasterVertexFile, "RimLumenObjectShadow.frag.glsl").Count == 0,
            "Waylith reports mismatched RimLumenObjectShadow varyings.");
    }

    [LumenFact]
    public static void LiftedSunShadowsKeepVanillaMaterialLayoutAndVaryings()
    {
        string gameRoot = Environment.GetEnvironmentVariable("RIMWORLD_DIR")
            ?? throw new InvalidOperationException("RIMWORLD_DIR is not set; run Tools/test-offline.ps1.");
        string vanillaShaders = Path.Combine(gameRoot, "Data", "Core", "Shaders", "Map");
        MaterialLayout Layout(string path) =>
            ShaderMetadataParser.ExtractMaterialLayout(ShaderSourceLoader.ReadShaderSource(path), Path.GetFileName(path));
        string Lifted(string file) => Path.GetFullPath(Path.Combine(ShaderDirectory(), "..", "RimLumen", file));

        MaterialLayout shadow = Layout(Path.Combine(vanillaShaders, "SunShadow.frag.glsl"));
        MaterialLayout fade = Layout(Path.Combine(vanillaShaders, "SunShadowFade.frag.glsl"));
        MaterialLayout liftedShadow = Layout(Lifted("RimLumenSunShadow.frag.glsl"));
        MaterialLayout liftedFade = Layout(Lifted("RimLumenSunShadowFade.frag.glsl"));
        Require(!liftedShadow.HasErrors && !liftedFade.HasErrors, "Waylith rejected the lifted sun-shadow material metadata.");
        string[] lightSum = [LightPoolShadows.LightSumTexture];
        Require(LightPoolShadows.LayoutMatches(liftedShadow, shadow, lightSum) && LightPoolShadows.LayoutMatches(liftedFade, fade, lightSum),
            "Lifted sun shadows must keep vanilla's uniform block byte for byte and add only _LumenLightSum.");
        Require(!LightPoolShadows.LayoutMatches(liftedShadow, fade, lightSum),
            "The layout check must reject a lifted shader whose uniform block differs from vanilla's.");
        Require(liftedShadow.TextureSlots[0].Default == MaterialTextureDefault.Black &&
                liftedFade.TextureSlots[0].Default == MaterialTextureDefault.Black,
            "A cleared light-sum slot must resolve to black, or an unbound shadow would differ from vanilla.");

        string Varyings(string vertex, string lifted) => string.Join(" ", ShaderMetadataParser.ValidateStageInterface(
            File.ReadAllText(Path.Combine(vanillaShaders, vertex)), ShaderSourceLoader.ReadShaderSource(Lifted(lifted)), vertex, lifted));
        MaterialLayout overlay = Layout(Path.Combine(vanillaShaders, "LightOverlay.frag.glsl"));
        Require(LightPoolShadows.LayoutMatches(Layout(Lifted("RimLumenLightTakeover.frag.glsl")), overlay, lightSum),
            "Takeover LightOverlay must keep vanilla uniforms and bind only the light sum.");
        Require(LightPoolShadows.LayoutMatches(Layout(Lifted("RimLumenSunShadowTakeover.frag.glsl")), overlay, LightPoolShadows.ShadeTextures, LightPoolShadows.ShadeUniforms),
            "Takeover sun shade must prefix vanilla LightOverlay uniforms, append exactly three vectors and bind only the five RimLumen textures.");
        Require(LightPoolShadows.LayoutMatches(Layout(Lifted("RimLumenSunShadowFadeTakeover.frag.glsl")), fade, LightPoolShadows.FadeTextures, LightPoolShadows.FadeUniforms),
            "Takeover SunShadowFade must keep vanilla uniforms, append the sun channel and bind the occlusion textures and the object-shadow mask.");
        string shadowErrors = Varyings("SunShadow.vert.glsl", "RimLumenSunShadow.frag.glsl");
        string fadeErrors = Varyings("SunShadowFade.vert.glsl", "RimLumenSunShadowFade.frag.glsl");
        foreach ((string vert, string frag) in new[]
        {
            (Path.Combine(gameRoot, "Rendering", "Shaders", LightPoolRenderer.VertexFile), Lifted("RimLumenLightTakeover.frag.glsl")),
            (Path.Combine(gameRoot, "Rendering", "Shaders", LightPoolRenderer.VertexFile), Lifted("RimLumenSunShadowTakeover.frag.glsl")),
            (Lifted("RimLumenSunShadowFadeTakeover.vert.glsl"), Lifted("RimLumenSunShadowFadeTakeover.frag.glsl"))
        })
        {
            var errors = ShaderMetadataParser.ValidateStageInterface(ShaderSourceLoader.ReadShaderSource(vert), ShaderSourceLoader.ReadShaderSource(frag), vert, frag);
            Require(errors.Count == 0, $"{Path.GetFileName(frag)} stage interface: {string.Join(" ", errors)}");
        }
        Require(shadowErrors.Length == 0 && fadeErrors.Length == 0,
            $"Waylith reports mismatched vanilla-vertex/lifted-fragment varyings: '{shadowErrors}' '{fadeErrors}'.");
    }

    [LumenFact]
    public static void LiftedSunShadowWaylithMembersBindAtInstall()
    {
        // The install-time binding in RimLumenMapHooks.Arm fails loudly when Waylith renames these members; this is
        // the offline copy of that check against the shipped Waylith.
        LightPoolShadows.BindEngineMembers();
    }

    [LumenFact]
    public static void PrecompiledShadowWithVanillaNamesIsAnOverride() =>
        CheckPrecompiledShadow(nameof(PrecompiledShadowWithVanillaNamesIsAnOverride), "SunShadow.frag.glsl");

    [LumenFact]
    public static void PrecompiledShadowWithoutFragmentSourceIsAnOverride() =>
        CheckPrecompiledShadow(nameof(PrecompiledShadowWithoutFragmentSourceIsAnOverride), "RimLumenTest.Missing.frag.glsl");

    private static void CheckPrecompiledShadow(string key, string fragmentName) => WithRuntimeSlang(gameRoot =>
    {
        string core = Path.GetFullPath(Path.Combine(gameRoot, "Data", "Core", "Shaders"));
        string map = Path.Combine(core, "Map");
        ShaderSourceLoader.RegisterShaderSourceDirectory(Path.Combine(gameRoot, "Rendering", "Shaders"));
        ShaderSourceLoader.RegisterShaderSourceDirectory(map);
        // Register a different compiled program under this test's own key. Both shader filenames are only metadata.
        var stages = new PrecompiledShaderStages(
            CompileFile("SunShadowFade.vert.glsl", RuntimeShaderStage.Vertex, Path.Combine(map, "SunShadowFade.vert.glsl")),
            CompileFile("SunShadowFade.frag.glsl", RuntimeShaderStage.Fragment, Path.Combine(map, "SunShadowFade.frag.glsl")));
        var definition = new ShaderProgramDefinition { vertexShaderFileName = "SunShadow.vert.glsl", fragmentShaderFileName = fragmentName };
        var shader = ShaderRegistry.Register(new Shader(key, key, definition,
            materialLayout: ShaderSourceLoader.CreateMaterialLayout("SunShadowFade.frag.glsl"),
            vertexInputLayout: ShaderSourceLoader.CreateVertexInputLayout("SunShadowFade.vert.glsl"), precompiledStages: stages));
        var material = new UnityEngine.Material(UnityEngine.Shader.FromEngineShader(key, shader));
        var kind = new LightPoolShadows.Kind(key, "SunShadow.vert.glsl", "SunShadow.frag.glsl", null, [], material, -1);
        string? reason = kind.Resolve(core + Path.DirectorySeparatorChar);
        Require(!kind.OriginalProgram && reason != null && reason.Contains(shader.Key, StringComparison.Ordinal)
            && reason.Contains("vertex", StringComparison.OrdinalIgnoreCase) && reason.Contains("fragment", StringComparison.OrdinalIgnoreCase),
            $"Expected a precompiled override naming the registry key and both stages; OriginalProgram={kind.OriginalProgram}, reason={reason ?? "<null>"}.");
    });

    [LumenFact]
    public static void RuntimeSlangCompilesAllStagesAndPreservesStorageStrides() => WithRuntimeSlang(_ => CompileAndCheckStorageStrides());

    [LumenFact]
    public static void TrailGpuShadersCompileWithMatchingSpirvStrides() => WithRuntimeSlang(_ =>
    {
        byte[] compute = Compile("ParticleTrailSim.comp.glsl", RuntimeShaderStage.Compute);
        byte[] vertex = Compile("ParticleTrail.vert.glsl", RuntimeShaderStage.Vertex);
        byte[] fragment = Compile("ParticleTrail.frag.glsl", RuntimeShaderStage.Fragment);
        var computeInstructions = Instructions(compute);
        var vertexInstructions = Instructions(vertex);
        foreach (uint stride in new uint[] { 160, 80, 32, 4 })
            Require(HasArrayStride(computeInstructions, stride), $"Trail compute SPIR-V missing {stride}-byte storage array stride.");
        foreach (uint stride in new uint[] { 160, 80, 32 })
            Require(HasArrayStride(vertexInstructions, stride), $"Trail vertex SPIR-V missing {stride}-byte storage array stride.");
        Require(computeInstructions.Any(x => x[0] == 16 && x.Length >= 6 && x[2] == 17 && x[3] == 64 && x[4] == 1 && x[5] == 1),
            "Trail compute SPIR-V workgroup must be LocalSize 64,1,1.");
        Require(fragment.Length > 20, "Trail fragment compiler returned no program.");

        string vertSource = Read("ParticleTrail.vert.glsl");
        string fragSource = Read("ParticleTrail.frag.glsl");
        MaterialLayout vertLayout = ShaderMetadataParser.ExtractMaterialLayout(vertSource, "ParticleTrailVertex");
        MaterialLayout fragLayout = ShaderMetadataParser.ExtractMaterialLayout(fragSource, "ParticleTrailFragment");
        Require(!vertLayout.HasErrors && !fragLayout.HasErrors, "Waylith rejected trail material metadata.");
        CheckUniforms(vertLayout);
        CheckUniforms(fragLayout);
        Require(fragLayout.StorageBufferSlots.Select(x => (x.Binding, x.PropertyName)).SequenceEqual(new[]
        {
            (1, GpuTrailProperties.Headers), (2, GpuTrailProperties.Points),
            (3, GpuTrailProperties.Emitters), (4, GpuParticleProperties.Curves)
        }), "Trail graphics storage properties/bindings differ from C# upload contract.");
        Require(fragLayout.TextureSlots.Select(x => (x.Binding, x.PropertyName)).SequenceEqual(new[]
        {
            (5, GpuParticleProperties.Texture), (6, GpuParticleProperties.SceneDepth)
        }), "Trail graphics texture properties/bindings differ from C# binding contract.");
        Require(ShaderMetadataParser.ValidateStageInterface(vertSource, fragSource, "ParticleTrail.vert", "ParticleTrail.frag").Count == 0,
            "Waylith reports mismatched trail vertex/fragment varyings.");
        ShaderVertexInputLayout input = ShaderMetadataParser.ExtractVertexInputLayout(vertSource, "ParticleTrail.vert");
        Require(!input.HasErrors && input.Slots.Select(x => (x.location, x.semantic, x.required)).SequenceEqual(new[]
        {
            (0, VertexAttributeSemantic.Position, true)
        }), "Trail graphics expects one required Position attribute at location 0.");
    });

    [LumenFact]
    public static void ParticleShadersRegisterOnceAndFeedWaylithWarmupPlan()
    {
        EnsureTestShaderDirectories();
        ParticleMeshShaders.EnsureParticleShadersRegistered();
        ParticleMeshShaders l1 = ParticleMeshShaders.GetOrCreateL1();
        ParticleMeshShaders l2 = ParticleMeshShaders.GetOrCreateL2Billboard();
        ParticleMeshShaders l2Trail = ParticleMeshShaders.GetOrCreateL2Trail();
        Require(ReferenceEquals(l1, ParticleMeshShaders.GetOrCreateL1()), "L1 shaders must reuse one singleton set.");
        Require(ReferenceEquals(l2, ParticleMeshShaders.GetOrCreateL2Billboard()), "L2 billboard shaders must reuse one singleton set.");
        Require(ReferenceEquals(l2Trail, ParticleMeshShaders.GetOrCreateL2Trail()), "L2 trail shaders must reuse one singleton set.");

        (string Key, Shader Expected)[] expectedShaders =
        {
            ("RimLumen.L1-additive", l1.Additive),
            ("RimLumen.L1-alpha", l1.Alpha),
            ("RimLumen.L1-premultiplied", l1.Premultiplied),
            ("RimLumen.L2-additive", l2.Additive),
            ("RimLumen.L2-alpha", l2.Alpha),
            ("RimLumen.L2-premultiplied", l2.Premultiplied),
            ("RimLumen.L2Trail-additive", l2Trail.Additive),
            ("RimLumen.L2Trail-alpha", l2Trail.Alpha),
            ("RimLumen.L2Trail-premultiplied", l2Trail.Premultiplied),
        };
        foreach ((string key, Shader expected) in expectedShaders)
        {
            Require(ShaderRegistry.TryResolve(key, out Shader? resolved) && ReferenceEquals(resolved, expected),
                $"ShaderRegistry must resolve '{key}' to the singleton instance.");
            Require(resolved!.ShaderPath == string.Empty,
                $"Particle shader '{key}' must leave ShaderPath empty so blend modes do not collide in ShadersByPath.");
        }

        ShaderWarmupPlan plan = ShaderVariantEnumerator.Enumerate(
            Array.Empty<ShaderKeywordContribution>(), Array.Empty<string>());
        foreach ((string file, RuntimeShaderStage stage) in new[]
        {
            ("Particles/L1ParticleQuad.vert.glsl", RuntimeShaderStage.Vertex),
            ("Particles/L1ParticleQuad.frag.glsl", RuntimeShaderStage.Fragment),
            ("Particles/ParticleBillboard.vert.glsl", RuntimeShaderStage.Vertex),
            ("Particles/ParticleBillboard.frag.glsl", RuntimeShaderStage.Fragment),
            ("Particles/ParticleTrail.vert.glsl", RuntimeShaderStage.Vertex),
            ("Particles/ParticleTrail.frag.glsl", RuntimeShaderStage.Fragment),
        })
        {
            Require(plan.Graphics.Any(x => string.Equals(x.SourceFile, file, StringComparison.Ordinal) && x.Stage == stage),
                $"Waylith warmup plan must include '{file}' ({stage}).");
        }
    }

    [LumenFact]
    public static void ParticleGpuDrawResourcesPrebuildsAllTrailStripMeshesBeforeFirstDraw()
    {
        EnsureTestShaderDirectories();
        int uploadCount = 0;
        void OnUpload(Mesh _) => uploadCount++;
        Mesh.UploadRequested += OnUpload;
        try
        {
            using var drawResources = new ParticleGpuDrawResources(3);
            Require(uploadCount == 1 + ParticleTrailMath.StripBuckets.Length,
                $"ParticleGpuDrawResources construction must prebuild quad + {ParticleTrailMath.StripBuckets.Length} strip meshes (got {uploadCount} uploads).");

            uploadCount = 0;
            foreach (int maxPoints in new[] { 2, 8, 9, 16, 32, 64, 128, 256 })
            {
                int bucket = ParticleTrailMath.StripBuckets[ParticleTrailMath.StripBucketIndex(maxPoints)];
                Mesh strip = drawResources.MeshForStrip(maxPoints);
                Require(strip.VertexCount == (bucket + 1) * 2 && strip.IndexCount == bucket * 6,
                    $"Strip bucket {bucket} geometry count mismatch.");
            }
            Require(uploadCount == 0, "First trail MeshForStrip lookup must not trigger lazy mesh creation/upload.");

            Mesh beforeClear = drawResources.MeshForStrip(64);
            drawResources.ClearMaterials();
            Require(uploadCount == 0 && ReferenceEquals(beforeClear, drawResources.MeshForStrip(64)),
                "ClearMaterials must retain prebuilt trail strip meshes without re-uploading.");
        }
        finally
        {
            Mesh.UploadRequested -= OnUpload;
        }
    }

    [LumenFact]
    public static void TrailComputeDescriptorsAndPushConstantsMatchHostSpec()
    {
        EnsureTestShaderDirectories();
        CheckComputeShaderContract(LumenGpuParticleEngine.SimShaderPath, LumenGpuParticleEngine.SimBindings, "ParticleSim");
        CheckComputeShaderContract(LumenGpuParticleEngine.TrailSimShaderPath, LumenGpuParticleEngine.TrailSimBindings, "ParticleTrailSim");
    }

    private static void CheckComputeShaderContract(string shaderRelativePath, ComputeBinding[] bindings, string label)
    {
        string source = ShaderSourceLoader.ReadShaderSource(shaderRelativePath);
        int[] shaderBindings = Regex.Matches(source, @"layout\s*\([^)]*\bbinding\s*=\s*(\d+)\b[^)]*\)\s*(?:readonly\s+|writeonly\s+)?buffer\b")
            .Select(m => int.Parse(m.Groups[1].Value))
            .OrderBy(x => x)
            .ToArray();
        int[] hostBindings = bindings.Select(b => (int)b.Binding).OrderBy(x => x).ToArray();
        Require(shaderBindings.Length == hostBindings.Length && shaderBindings.SequenceEqual(hostBindings)
            && bindings.All(b => b.Type == ComputeBindingType.StorageBuffer),
            $"{label} C# ComputeBinding spec does not match GLSL storage buffer bindings.");

        Match pushBlock = Regex.Match(source, @"layout\s*\(\s*push_constant\s*\)\s*uniform\s+\w+\s*\{(?<body>[^}]+)\}");
        Require(pushBlock.Success, $"{label} missing layout(push_constant) uniform block.");
        int pushBytes = Regex.Matches(pushBlock.Groups["body"].Value, @"\b(vec4|uvec4|ivec4|mat4|float|uint|int)\s+\w+\s*;")
            .Sum(m => m.Groups[1].Value switch { "mat4" => 64, "vec4" or "uvec4" or "ivec4" => 16, _ => 4 });
        Require(pushBytes == LumenGpuParticleEngine.ComputePushConstantBytes,
            $"{label} GLSL push_constant size ({pushBytes} B) does not match host ComputePushConstantBytes ({LumenGpuParticleEngine.ComputePushConstantBytes} B).");
    }

    [LumenFact]
    public static void L1ParticleBatchRendererAndRibbonShareSingletonL1ShadersWithoutLazyDrawCreation()
    {
        EnsureTestShaderDirectories();
        ParticleMeshShaders singleton = ParticleMeshShaders.GetOrCreateL1();
        using var depth = new BorrowedRenderTexture();
        using var r1 = new L1ParticleBatchRenderer(depth, 3);
        using var r2 = new L1ParticleBatchRenderer(depth, 3);
        var shadersField = typeof(L1ParticleBatchRenderer).GetField("shaders",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Require(shadersField is not null, "L1ParticleBatchRenderer must keep a shaders field initialized at construction.");
        object? r1Shaders = shadersField!.GetValue(r1);
        object? r2Shaders = shadersField.GetValue(r2);
        Require(ReferenceEquals(r1Shaders, singleton) && ReferenceEquals(r2Shaders, singleton),
            "Separate L1ParticleBatchRenderer instances (main L1 + Ribbon) must share the singleton L1 ParticleMeshShaders at construction.");
        Require(ShaderRegistry.AllShaders.Count(x => x.Key.StartsWith("RimLumen.L1-", StringComparison.Ordinal)) == 3,
            "ShaderRegistry must hold exactly 3 singleton RimLumen.L1 blend shaders.");

        string root = Path.GetFullPath(Path.Combine(ShaderDirectory(), "..", "..", "..", "Source", "RimLumen.Core"));
        string l1Source = File.ReadAllText(Path.Combine(root, "Rendering", "L1ParticleBatchRenderer.cs"));
        string drawSource = File.ReadAllText(Path.Combine(root, "ParticlesGpu", "ParticleGpuDrawResources.cs"));
        string gpuSource = File.ReadAllText(Path.Combine(root, "ParticlesGpu", "LumenGpuParticleEngine.Gpu.cs"));
        Require(!l1Source.Contains("shaders ??=", StringComparison.Ordinal),
            "L1ParticleBatchRenderer must initialize singleton shaders at construction, not lazily in AppendDraws.");
        Require(!drawSource.Contains("trailShaders ??=", StringComparison.Ordinal)
            && !drawSource.Contains("trailMeshes[bucket] ??=", StringComparison.Ordinal),
            "ParticleGpuDrawResources must not lazily create trail shaders or strip meshes during draw.");
        Match releaseMethod = Regex.Match(gpuSource, @"private void ReleaseGpu\([^\)]*\)\s*\{[\s\S]*?\r?\n    \}");
        Require(releaseMethod.Success, "Expected ReleaseGpu method.");
        string releaseBody = releaseMethod.Value;
        Require(releaseBody.Contains("drawResources?.ClearMaterials();", StringComparison.Ordinal)
            && !releaseBody.Contains("drawResources?.Dispose()", StringComparison.Ordinal)
            && !Regex.IsMatch(releaseBody, @"\btrailKernel\s*=\s*null\s*;"),
            "ReleaseGpu must free VRAM pools/materials while retaining prebuilt static drawResources and compute kernels.");
    }

    private static void EnsureTestShaderDirectories()
    {
        string shaderRoot = Path.GetFullPath(Path.Combine(ShaderDirectory(), ".."));
        ShaderSourceLoader.RegisterShaderSourceDirectory(shaderRoot);
        string gameRoot = Environment.GetEnvironmentVariable("RIMWORLD_DIR")
            ?? throw new InvalidOperationException("RIMWORLD_DIR is not set; run Tools/test-offline.ps1.");
        ShaderSourceLoader.RegisterShaderSourceDirectory(Path.Combine(gameRoot, "Rendering", "Shaders"));
    }

    private static void WithRuntimeSlang(Action<string> check)
    {
        // Brutal.Slang.PInvoke explicitly calls NativeLibrary.Load, then installs its own DllImportResolver.
        // The load-context event handles that explicit load without competing with its resolver or copying locked DLLs.
        string gameRoot = Environment.GetEnvironmentVariable("RIMWORLD_DIR")
            ?? throw new InvalidOperationException("RIMWORLD_DIR is not set; run Tools/test-offline.ps1.");
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
            check(gameRoot);
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
        foreach (uint stride in new uint[] { 64, 480, 48, 4, 16, 32 })
            Require(HasArrayStride(computeInstructions, stride), $"Compute SPIR-V missing {stride}-byte storage array stride.");
        foreach (uint stride in new uint[] { 64, 480, 32 })
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
        CompileFile("RimLumen/RimLumenLightPool.frag.glsl", RuntimeShaderStage.Fragment);
        CompileFile("RimLumen/RimLumenLightPoolAccumulate.frag.glsl", RuntimeShaderStage.Fragment);
        CompileFile("RimLumen/RimLumenSunShadow.frag.glsl", RuntimeShaderStage.Fragment);
        CompileFile("RimLumen/RimLumenSunShadowFade.frag.glsl", RuntimeShaderStage.Fragment);
        CompileFile("RimLumen/RimLumenLightTakeover.frag.glsl", RuntimeShaderStage.Fragment);
        CompileFile("RimLumen/RimLumenSunShadowTakeover.frag.glsl", RuntimeShaderStage.Fragment);
        CompileFile("RimLumen/RimLumenSunShadowFadeTakeover.vert.glsl", RuntimeShaderStage.Vertex);
        CompileFile("RimLumen/RimLumenSunShadowFadeTakeover.frag.glsl", RuntimeShaderStage.Fragment);
        CompileFile(LightPoolRenderer.CasterVertexFile, RuntimeShaderStage.Vertex);
        CompileFile(LightPoolRenderer.CasterFragmentFile, RuntimeShaderStage.Fragment);
        Console.WriteLine("[SPIR-V] compute strides=64,480,48,4,16,32; vertex strides=64,480,32; LocalSize=64,1,1");
    }

    private static void CheckUniforms(MaterialLayout layout)
    {
        Require(layout.UniformSize == 128, "Material UBO must be 128 bytes.");
        Require(layout.UniformSlots.Select(x => (x.Offset, x.PropertyName)).SequenceEqual(new[]
        {
            (0, "_LumenDraw"), (16, "_LumenCameraRight"), (32, "_LumenCameraUp"),
            (48, "_LumenProjection"), (64, "_LumenDepthUv"), (80, "_LumenVertical"),
            (96, "_LumenMaterialTint"), (112, "_LumenColor")
        }), "Material UBO offsets differ from runtime packing.");
        CheckOverrideSlots(layout, ("_LumenDraw", 0), ("_LumenCameraRight", 16), ("_LumenCameraUp", 32),
            ("_LumenProjection", 48), ("_LumenDepthUv", 64), ("_LumenVertical", 80),
            ("_LumenMaterialTint", 96), ("_LumenColor", 112));
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
        MatchCollection fields = Regex.Matches(body.Groups["body"].Value, @"\b(vec4|mat4|float|uint)\s+(\w+)\s*;");
        int offset = 0;
        foreach (Match field in fields)
        {
            string name = field.Groups[2].Value;
            Require(Marshal.OffsetOf<T>(name).ToInt32() == offset, $"{typeof(T).Name}.{name} byte offset mismatch.");
            offset += field.Groups[1].Value switch { "mat4" => 64, "vec4" => 16, _ => 4 };
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
