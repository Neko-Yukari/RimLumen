using System;
using System.Collections.Generic;
using Waylith.Rendering;

namespace RimLumen.Core.Rendering;

// Vanilla fog of war and Anomaly darkness draw over glows in the 8-bit scene (queues 3175 / 3180, Shaders.Map.xml),
// but the glow target reaches bloom after them. Each overlay command is redrawn into the glow target with
// vanilla's vertex stage, geometry and properties, through a fragment that rebuilds only the overlay's effect on
// light underneath. Only the purely additive glow target is masked: the hdr layer's coverage alpha would need a
// different blend (fog's grey term scales with coverage), so it is not covered here.
internal sealed class OverlayMaskMaterials
{
    // Top of DrawSortKey's 13 queue bits; with MaskHostOrder every mask sorts after every glow copy (host order 0).
    internal const int MaskRenderQueue = 8191;
    internal const ushort MaskHostOrder = ushort.MaxValue;

    // dst.rgb * src.rgb + dst.rgb * (1 - src.a): the linear part of vanilla's overlay; glow.a is unused and untouched.
    private static readonly BlendState MaskBlend = new(Enabled: true, BlendFactor.DstColor, BlendFactor.OneMinusSrcAlpha,
        BlendOp.Add, BlendFactor.Zero, BlendFactor.One, BlendOp.Add);

    // Main thread only. A mask material is never written after creation (the render thread may read it for
    // FramesInFlight frames); the source's current properties travel with each draw instead.
    private readonly Dictionary<Shader, Shader> shaders = new();
    private readonly Dictionary<Material, Material> materials = new();

    // Ensures: the mask fragment for a vanilla overlay shader key (bare ShaderDef defName), or null when the key
    //   is not an overlay that covers glows.
    internal static string? MaskFragmentFor(string shaderKey) => shaderKey switch
    {
        "FogOfWar" => "PostFx/RimLumenFogOfWarMask.frag.glsl",
        "Darkness" => "PostFx/RimLumenDarknessMask.frag.glsl",
        _ => null
    };

    // Requires: main thread; MaskFragmentFor(source.Shader.Key) == maskFragment.
    // Ensures: a material drawing source's coverage with the mask blend; per-draw properties supply its values; throws
    //   InvalidOperationException when Waylith rejects the mask shader metadata.
    internal Material Get(Material source, string maskFragment)
    {
        if (materials.TryGetValue(source, out Material? mask)) return mask;
        Shader sourceShader = source.Shader;
        if (!shaders.TryGetValue(sourceShader, out Shader? maskShader))
        {
            maskShader = CreateShader(sourceShader, maskFragment);
            shaders.Add(sourceShader, maskShader);
        }
        mask = new Material(maskShader, "RimLumen.OverlayMask." + source.DebugName);
        mask.WrapModeOverride = source.WrapModeOverride;
        materials.Add(source, mask);
        return mask;
    }

    private static Shader CreateShader(Shader source, string maskFragment)
    {
        string vertexFile = source.VertexShaderFileName;
        string vertexSource = ShaderSourceLoader.ReadShaderSource(vertexFile);
        string fragmentSource = ShaderSourceLoader.ReadShaderSource(maskFragment);
        MaterialLayout materialLayout = ShaderMetadataParser.ExtractMaterialLayout(fragmentSource, maskFragment);
        ShaderVertexInputLayout vertexInput = ShaderSourceLoader.CreateVertexInputLayout(vertexFile);
        bool stageInterfaceBroken = ShaderMetadataParser.ValidateStageInterface(vertexSource, fragmentSource, vertexFile, maskFragment);
        if (materialLayout.HasErrors || vertexInput.HasErrors || stageInterfaceBroken)
        {
            throw new InvalidOperationException(
                $"[RimLumen] Overlay mask shader metadata rejected by Waylith: {vertexFile} + {maskFragment} " +
                $"(material errors: {materialLayout.HasErrors}, vertex input errors: {vertexInput.HasErrors}, " +
                $"stage interface broken: {stageInterfaceBroken}).");
        }
        ShaderProgramDefinition program = source.Program;
        ShaderProgramDefinition definition = new()
        {
            vertexShaderFileName = vertexFile,
            fragmentShaderFileName = maskFragment,
            useTexture = program.useTexture,
            flipTextureST = program.flipTextureST,
            transparent = true,
            depthTest = source.PipelineState.Depth.Test,
            depthWrite = false,
            cullMode = source.PipelineState.Cull,
            renderQueue = new RenderQueueValue(MaskRenderQueue),
            textureWrapOverrides = program.textureWrapOverrides
        };
        PipelineState state = new(MaskBlend, new DepthState(source.PipelineState.Depth.Test, Write: false), source.PipelineState.Cull);
        return new Shader("RimLumen.OverlayMask." + source.Key, vertexFile, definition, keywords: null, materialLayout, state,
            iconShaderKey: null, vertexInputLayout: vertexInput);
    }
}
