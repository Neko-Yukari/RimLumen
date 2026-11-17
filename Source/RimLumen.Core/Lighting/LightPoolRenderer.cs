using System;
using System.Collections.Generic;
using System.Numerics;
using Verse;
using Waylith.Rendering;
using Shader = Waylith.Rendering.Shader;

namespace RimLumen.Core.Lighting;

// Effect point lights as purely visual light pools, in two steps. Accumulation: each light is one plane10 quad
// scaled to 2R x 2R at the LightingOverlay altitude, routed to the RimLumen pass and drawn with blend One, One into a
// cleared RGBA16F target, depth-tested against the scene, so overlapping pools add linearly. Composite: at the
// queue-3101 map split (RimLumenMapChain.RecordLightPools), right after LightOverlay (3100) and before the glows
// (3151), fog (3169/3175) and Darkness (3180), one full-screen pass with blend DstColor, One turns the scene into
// scene * (1 + compress(sum)), so everything that hides vanilla lighting hides these pools too. compress is identity up
// to a peak of 1, so a pixel brightens at most 2x however many pools overlap. Nothing here touches GlowGrid.
internal sealed class LightPoolRenderer : IDisposable
{
    internal const int RenderQueue = 3101;
    internal const string VertexFile = "SceneDefault.vert.glsl";
    internal const string AccumulateFile = "RimLumen/RimLumenLightPoolAccumulate.frag.glsl";
    internal const string CompositeFile = "RimLumen/RimLumenLightPool.frag.glsl";
    private static readonly int LightPropertyId = Shader.PropertyToID("_LumenLight");

    private readonly List<VisualPointLight> staged = new();
    private ulong stagedFrameIndex = ulong.MaxValue;
    private Material? material;
    private bool disposed;

    // Requires: main thread, in RimLumenPipeline.BeginCapture for frameIndex.
    // Ensures: copies this frame's lights; the service list is rewritten by the next capture.
    internal void Stage(ulong frameIndex, IReadOnlyList<VisualPointLight> lights)
    {
        staged.Clear();
        stagedFrameIndex = frameIndex;
        for (int i = 0; i < lights.Count; i++) staged.Add(lights[i]);
    }

    // Requires: main thread, in RimLumenPipeline.Capture after MeshDrawSubmission.CaptureFlushedSnapshot.
    // Ensures: one accumulation draw per staged light, each carrying its _LumenLight through the arena; lights staged
    //          for another frame are dropped.
    internal void AppendDraws(List<FinalDrawItem> draws, DrawItemFactory maker, DrawPropertyArena arena, ulong frameIndex)
    {
        if (disposed || staged.Count == 0) return;
        if (stagedFrameIndex != frameIndex)
        {
            staged.Clear();
            return;
        }
        material ??= new Material(CreateShader(), "RimLumen.LightPool");
        float altitude = AltitudeLayer.LightingOverlay.AltitudeFor();
        for (int i = 0; i < staged.Count; i++)
        {
            VisualPointLight light = staged[i];
            Span<DrawProperty> properties = arena.Allocate(1, out DrawPropertyRange range);
            properties[0] = DrawProperty.Vector(LightPropertyId, new Vector4(light.Light, 0f));
            float diameter = 2f * light.Radius;
            Matrix4x4 matrix = UnityEngine.Matrix4x4.ToNumerics(UnityEngine.Matrix4x4.TRS(
                new UnityEngine.Vector3(light.X, altitude, light.Z), UnityEngine.Quaternion.identity,
                new UnityEngine.Vector3(diameter, 1f, diameter)));
            var command = new MeshDrawCommand(MeshPool.plane10.EngineMesh, material, range, matrix, 0,
                Rendering.RimLumenRenderHost.LumenRenderPassId);
            FinalDrawItem? draw = maker.TryMake(in command, arena, UnityEngine.Shader.GlobalKeywordKey, i);
            if (draw is not null) draws.Add(draw);
        }
        staged.Clear();
    }

    internal static ShaderProgramDefinition CreateDefinition() => new()
    {
        vertexShaderFileName = VertexFile,
        fragmentShaderFileName = AccumulateFile,
        useTexture = false,
        transparent = true,
        depthTest = true,
        depthWrite = false,
        cullMode = FaceCull.None,
        blendMode = ShaderBlendMode.Custom,
        blendSrcFactor = BlendFactor.One,
        blendDstFactor = BlendFactor.One,
        renderQueue = new RenderQueueValue(RenderQueue)
    };

    // Ensures: throws InvalidOperationException when Waylith rejects the material, vertex-input or stage-interface
    //          metadata instead of handing a broken shader to the pipeline cache.
    private static Shader CreateShader()
    {
        string vertexSource = ShaderSourceLoader.ReadShaderSource(VertexFile);
        string fragmentSource = ShaderSourceLoader.ReadShaderSource(AccumulateFile);
        MaterialLayout materialLayout = ShaderMetadataParser.ExtractMaterialLayout(fragmentSource, "RimLumenLightPoolAccumulate.frag.glsl");
        ShaderVertexInputLayout vertexInput = ShaderSourceLoader.CreateVertexInputLayout(VertexFile);
        bool stageInterfaceBroken = ShaderMetadataParser.ValidateStageInterface(vertexSource, fragmentSource,
            VertexFile, "RimLumenLightPoolAccumulate.frag.glsl");
        if (materialLayout.HasErrors || vertexInput.HasErrors || stageInterfaceBroken)
        {
            throw new InvalidOperationException(
                $"[RimLumen] Light pool shader metadata rejected by Waylith: {VertexFile} + {AccumulateFile} " +
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
