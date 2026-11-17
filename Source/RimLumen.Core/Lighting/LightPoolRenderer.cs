using System;
using System.Collections.Generic;
using System.Numerics;
using Verse;
using Waylith.Rendering;
using Shader = Waylith.Rendering.Shader;

namespace RimLumen.Core.Lighting;

// Effect point lights as purely visual light pools. Each light is one plane10 quad scaled to 2R x 2R at the
// LightingOverlay altitude, submitted to the vanilla map pass through Graphics.DrawMesh and sorted at queue 3101:
// right after LightOverlay (3100), before the glows (3151), fog (3169/3175) and Darkness (3180), so everything that
// hides vanilla lighting hides these pools too. Blend DstColor, One gives scene * (1 + L); the 8-bit scene target
// clamps the shader output to [0,1] first, so a pool brightens a pixel at most 2x. Nothing here touches GlowGrid.
internal sealed class LightPoolRenderer : IDisposable
{
    internal const int RenderQueue = 3101;
    internal const string VertexFile = "SceneDefault.vert.glsl";
    internal const string FragmentFile = "RimLumen/RimLumenLightPool.frag.glsl";
    private static readonly int LightPropertyId = Shader.PropertyToID("_LumenLight");

    private readonly DrawProperty[] property = new DrawProperty[1];
    private Material? material;
    private bool disposed;

    // Requires: main thread, inside FrameRenderer.BeginMainThreadFrame (RimLumenPipeline.BeginCapture), so the draws
    //           land in this frame's MeshDrawSubmission snapshot that MapRenderFeature.CaptureFrame takes next.
    // Ensures: one map-pass draw per light; an empty list submits nothing.
    internal void Submit(IReadOnlyList<VisualPointLight> lights)
    {
        if (disposed || lights.Count == 0) return;
        material ??= new Material(CreateShader(), "RimLumen.LightPool");
        float altitude = AltitudeLayer.LightingOverlay.AltitudeFor();
        foreach (VisualPointLight light in lights)
        {
            property[0] = DrawProperty.Vector(LightPropertyId, new Vector4(light.Light, 0f));
            float diameter = 2f * light.Radius;
            var matrix = UnityEngine.Matrix4x4.TRS(new UnityEngine.Vector3(light.X, altitude, light.Z),
                UnityEngine.Quaternion.identity, new UnityEngine.Vector3(diameter, 1f, diameter));
            UnityEngine.Graphics.DrawMesh(MeshPool.plane10, matrix, material, 0, null, 0, new DrawPropertyBlock(property),
                UnityEngine.Rendering.ShadowCastingMode.Off, receiveShadows: false);
        }
    }

    internal static ShaderProgramDefinition CreateDefinition() => new()
    {
        vertexShaderFileName = VertexFile,
        fragmentShaderFileName = FragmentFile,
        useTexture = false,
        transparent = true,
        depthTest = true,
        depthWrite = false,
        cullMode = FaceCull.None,
        blendMode = ShaderBlendMode.Custom,
        blendSrcFactor = BlendFactor.DstColor,
        blendDstFactor = BlendFactor.One,
        renderQueue = new RenderQueueValue(RenderQueue)
    };

    // Ensures: throws InvalidOperationException when Waylith rejects the material, vertex-input or stage-interface
    //          metadata instead of handing a broken shader to the pipeline cache.
    private static Shader CreateShader()
    {
        string vertexSource = ShaderSourceLoader.ReadShaderSource(VertexFile);
        string fragmentSource = ShaderSourceLoader.ReadShaderSource(FragmentFile);
        MaterialLayout materialLayout = ShaderMetadataParser.ExtractMaterialLayout(fragmentSource, "RimLumenLightPool.frag.glsl");
        ShaderVertexInputLayout vertexInput = ShaderSourceLoader.CreateVertexInputLayout(VertexFile);
        bool stageInterfaceBroken = ShaderMetadataParser.ValidateStageInterface(vertexSource, fragmentSource,
            VertexFile, "RimLumenLightPool.frag.glsl");
        if (materialLayout.HasErrors || vertexInput.HasErrors || stageInterfaceBroken)
        {
            throw new InvalidOperationException(
                $"[RimLumen] Light pool shader metadata rejected by Waylith: {VertexFile} + {FragmentFile} " +
                $"(material errors: {materialLayout.HasErrors}, vertex input errors: {vertexInput.HasErrors}, " +
                $"stage interface broken: {stageInterfaceBroken}).");
        }
        ShaderProgramDefinition definition = CreateDefinition();
        return new Shader("RimLumen.LightPool", VertexFile, definition, keywords: null, materialLayout,
            definition.ToPipelineState(), iconShaderKey: null, vertexInputLayout: vertexInput);
    }

    // Requires: render thread and GPU idle (RimLumenPipeline.Dispose after Quiesce).
    // Ensures: unregisters the material binding; idempotent.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        material?.Dispose();
        material = null;
    }
}
