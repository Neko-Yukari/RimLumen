using System;
using System.Collections.Generic;
using Waylith.Rendering;

namespace RimLumen.Core.Rendering;

// Material set for the RimLumen particle shaders. Waylith's MeshBatchRenderer binds set 0 to engine
// globals (camera) and set 2 to the material; there is no per-draw push constant. The layout is
// resolved explicitly from the expanded sources because ShaderSourceLoader parses only the fragment
// stage, and the instance storage buffers live in a shared vertex/fragment header.
internal sealed class ParticleMeshShaders
{
    private const int ParticleRenderQueue = 3200;

    internal ParticleMeshShaders(Shader additive, Shader alpha, Shader premultiplied)
    {
        Additive = additive;
        Alpha = alpha;
        Premultiplied = premultiplied;
    }

    internal Shader Additive { get; }
    internal Shader Alpha { get; }
    internal Shader Premultiplied { get; }

    // Requires: shader paths relative to a registered shader directory; every blend mode gets the same
    //           stages and pipeline state and differs only in BlendState.
    // Ensures: throws InvalidOperationException when Waylith rejects the material, vertex-input or
    //          stage-interface metadata instead of handing a broken shader to the pipeline cache.
    internal static ParticleMeshShaders Create(string keyPrefix, string vertexFile, string fragmentFile,
        bool depthTest, int renderQueue = ParticleRenderQueue)
    {
        (MaterialLayout materialLayout, ShaderVertexInputLayout vertexInput) = ReadLayouts(vertexFile, fragmentFile);
        return new ParticleMeshShaders(
            CreateShader(keyPrefix + "-additive", vertexFile, fragmentFile, depthTest, renderQueue, materialLayout, vertexInput,
                new BlendState(Enabled: true, BlendFactor.One, BlendFactor.One, BlendOp.Add, BlendFactor.Zero, BlendFactor.One, BlendOp.Add)),
            CreateShader(keyPrefix + "-alpha", vertexFile, fragmentFile, depthTest, renderQueue, materialLayout, vertexInput,
                BlendState.Alpha),
            CreateShader(keyPrefix + "-premultiplied", vertexFile, fragmentFile, depthTest, renderQueue, materialLayout, vertexInput,
                new BlendState(Enabled: true, BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add,
                    BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add)));
    }

    // Requires and Ensures as Create, for one shader with the given blend.
    internal static Shader CreateSingle(string key, string vertexFile, string fragmentFile, bool depthTest, int renderQueue,
        BlendState blend)
    {
        (MaterialLayout materialLayout, ShaderVertexInputLayout vertexInput) = ReadLayouts(vertexFile, fragmentFile);
        return CreateShader(key, vertexFile, fragmentFile, depthTest, renderQueue, materialLayout, vertexInput, blend);
    }

    private static (MaterialLayout, ShaderVertexInputLayout) ReadLayouts(string vertexFile, string fragmentFile)
    {
        string vertexSource = ShaderSourceLoader.ReadShaderSource(vertexFile);
        string fragmentSource = ShaderSourceLoader.ReadShaderSource(fragmentFile);
        // Waylith derives the material (set 2) layout from the fragment stage only. ParticleMaterial.glsl is
        // included by both stages, so that parse already carries the storage buffers the vertex stage reads.
        MaterialLayout materialLayout = ShaderMetadataParser.ExtractMaterialLayout(fragmentSource, StageName(fragmentFile));
        ShaderVertexInputLayout vertexInput = ShaderSourceLoader.CreateVertexInputLayout(vertexFile);
        bool stageInterfaceBroken = ShaderMetadataParser.ValidateStageInterface(vertexSource, fragmentSource,
            StageName(vertexFile), StageName(fragmentFile));
        if (materialLayout.HasErrors || vertexInput.HasErrors || stageInterfaceBroken)
        {
            throw new InvalidOperationException(
                $"[RimLumen] Particle shader metadata rejected by Waylith: {vertexFile} + {fragmentFile} " +
                $"(material errors: {materialLayout.HasErrors}, vertex input errors: {vertexInput.HasErrors}, " +
                $"stage interface broken: {stageInterfaceBroken}).");
        }

        return (materialLayout, vertexInput);
    }

    private static Shader CreateShader(string name, string vertexFile, string fragmentFile, bool depthTest, int renderQueue,
        MaterialLayout materialLayout, ShaderVertexInputLayout vertexInput, BlendState blend)
    {
        ShaderProgramDefinition definition = new()
        {
            vertexShaderFileName = vertexFile,
            fragmentShaderFileName = fragmentFile,
            useTexture = true,
            flipTextureST = false,
            transparent = true,
            depthTest = depthTest,
            depthWrite = false,
            cullMode = FaceCull.None,
            renderQueue = new RenderQueueValue(renderQueue)
        };
        // The explicit PipelineState is authoritative; MeshPipelineCache hardcodes GreaterOrEqual for
        // every mesh pipeline, matching the reverse-Z depth the particle shaders were written against.
        PipelineState state = new(blend, new DepthState(depthTest, Write: false), FaceCull.None);
        return new Shader(name, vertexFile, definition, keywords: null, materialLayout, state,
            iconShaderKey: null, vertexInputLayout: vertexInput);
    }

    private static string StageName(string file)
    {
        int slash = file.LastIndexOf('/');
        return slash < 0 ? file : file[(slash + 1)..];
    }
}
