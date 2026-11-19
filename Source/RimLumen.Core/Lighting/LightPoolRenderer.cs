using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Lighting.Casters;
using RimLumen.Core.Lighting.Occlusion;
using RimLumen.Core.Rendering;
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
// In takeover mode vanilla glowers join the effect lights; the queue-3100 split only accumulates, and LightOverlay
// reads the sum. Both paths sample the current map's independent sprite-based occlusion map.
// Object shadows (design object-shadows): ShadowChannels gives each light a mask channel of one round. Before a round's
// pools accumulate, its casters (CasterChunks, RimLumenObjectShadow.{vert,frag}.glsl) draw every light's silhouette
// shadows into that round's RGBA8 mask with blend Max, and each pool reads its own channel. A round's draws, casters
// and pools alike, sit at queue RenderQueue + round. Round 0's mask holds the takeover sun too and stays bound until the
// sun shade (3170) and the dynamic SunShadowFade (3175) have read it; every later round reuses the second mask.
internal sealed class LightPoolRenderer : IDisposable
{
    internal const int RenderQueue = 3101;
    internal const string VertexFile = "SceneDefault.vert.glsl";
    internal const string AccumulateFile = "RimLumen/RimLumenLightPoolAccumulate.frag.glsl";
    internal const string CompositeFile = "RimLumen/RimLumenLightPool.frag.glsl";
    internal const string CasterVertexFile = "RimLumen/RimLumenObjectShadow.vert.glsl";
    internal const string CasterFragmentFile = "RimLumen/RimLumenObjectShadow.frag.glsl";
    private static readonly int LightPropertyId = Shader.PropertyToID("_LumenLight");
    private static readonly int LightAtPropertyId = Shader.PropertyToID("_LumenLightAt");
    private static readonly int ShadowChannelPropertyId = Shader.PropertyToID("_LumenShadowChannel");
    private static readonly int ObjectShadowPropertyId = Shader.PropertyToID("_LumenObjectShadow");
    private static readonly int CasterLightPropertyId = Shader.PropertyToID("_LumenCasterLight");
    private static readonly int CasterSunPropertyId = Shader.PropertyToID("_LumenCasterSun");
    private static readonly int CasterChannelPropertyId = Shader.PropertyToID("_LumenCasterChannel");
    private static readonly int OcclusionPropertyId = Shader.PropertyToID("_LumenOcclusion");
    private static readonly int CoarsePropertyId = Shader.PropertyToID("_LumenOcclusionCoarse");
    private static readonly int DoorPropertyId = Shader.PropertyToID("_LumenDoorOpen");

    // The masks of round 0 and of every later round. RimLumenMapHooks clears both before each map record with pools and
    // RimLumenMapChain.RecordLightPools binds each once its round's casters are drawn; cleared, a slot reads black.
    internal readonly BorrowedRenderTexture FirstMask = new(), LaterMask = new();
    private readonly List<VisualPointLight> staged = new();
    private readonly List<Vector3> boxes = new();
    private ulong stagedFrameIndex = ulong.MaxValue;
    private ulong preparedFrameIndex = ulong.MaxValue;
    private OcclusionFrame prepared;
    private CasterChunks casters = null!;
    private bool objectShadows;
    // [0] reads FirstMask, [1] LaterMask.
    private Material[]? materials;
    private Material? casterMaterial;
    private Texture2D? occlusion, coarse, doors;
    private OcclusionMap? uploadedMap;
    private DoorStates? uploadedDoors;
    private bool disposed;

    // Requires: main thread, in RimLumenPipeline.BeginCapture for frameIndex.
    // Ensures: copies this frame's lights; the service list is rewritten by the next capture.
    internal void Stage(ulong frameIndex, IReadOnlyList<VisualPointLight> lights)
    {
        staged.Clear();
        stagedFrameIndex = frameIndex;
        for (int i = 0; i < lights.Count; i++) staged.Add(lights[i]);
    }

    internal void StageVanilla(ulong frameIndex, Map map)
    {
        if (stagedFrameIndex != frameIndex) Stage(frameIndex, Array.Empty<VisualPointLight>());
        LightingTakeover.CollectVanillaPools(map, staged);
    }

    // Texture resource indices are validated by LightPoolShadows.LayoutMatches before rebinding the draw.
    internal void BindOcclusion(ref MaterialTextureResourceSet<Texture?> resources)
    {
        resources.Set(0, occlusion);
        resources.Set(1, coarse);
        resources.Set(2, doors);
    }

    // Requires: main thread, in RimLumenPipeline.BeginCapture after this frame's Stage/StageVanilla, before the frame's
    //           MeshDrawSubmission snapshot.
    // Ensures: on a frame with pools (takeover, or lights staged for frameIndex) captures map's occlusion frame and
    //          casters, whose changed chunk meshes the snapshot then uploads; other frames prepare nothing.
    //          FrameSunChannel holds this frame's sun selector.
    internal void Prepare(ulong frameIndex, Map map, bool takeover, bool objectShadows)
    {
        this.objectShadows = objectShadows;
        FrameSunChannel = takeover && objectShadows ? SunChannel : Vector4.Zero;
        if (!takeover && (stagedFrameIndex != frameIndex || staged.Count == 0)) return;
        prepared = OcclusionField.Capture(map, out casters);
        preparedFrameIndex = frameIndex;
    }

    // ShadowSlot.Selector of the takeover sun on this frame (LightingTakeover.Rebind); zero without object shadows.
    internal Vector4 FrameSunChannel { get; private set; }

    // Requires: main thread, in RimLumenPipeline.Capture after MeshDrawSubmission.CaptureFlushedSnapshot.
    // Ensures: on a prepared frame, one accumulation draw per staged light into draws, each carrying its _LumenLight,
    //          _LumenLightAt and _LumenShadowChannel through the arena. With object shadows, into shadows: one caster draw
    //          per light and caster chunk reaching its pool box, and with takeover one sun caster draw per chunk whose
    //          shadow reaches the view. Returns the number of rounds: one past the last light's round, at least 1 with the
    //          sun. Lights staged for another frame are dropped.
    internal int AppendDraws(List<FinalDrawItem> draws, List<FinalDrawItem> shadows, DrawItemFactory maker, DrawPropertyArena arena,
        ulong frameIndex, bool takeover)
    {
        if (stagedFrameIndex != frameIndex) staged.Clear();
        if (disposed || preparedFrameIndex != frameIndex)
        {
            staged.Clear();
            return 0;
        }
        materials ??= CreatePoolMaterials();
        UploadOcclusion(prepared);
        boxes.Clear();
        foreach (VisualPointLight light in staged) boxes.Add(new Vector3(light.X, light.Z, light.Radius));
        bool sun = takeover && objectShadows;
        ShadowSlot[] slots = objectShadows ? ShadowChannels.Assign(boxes, sun) : [];
        float altitude = AltitudeLayer.LightingOverlay.AltitudeFor();
        int rounds = 0;
        for (int i = 0; i < staged.Count; i++)
        {
            VisualPointLight light = staged[i];
            ShadowSlot slot = objectShadows ? slots[i] : default;
            Vector4 channel = objectShadows ? slot.Selector : Vector4.Zero;
            int queue = RenderQueue + slot.Round;
            Span<DrawProperty> properties = arena.Allocate(3, out DrawPropertyRange range);
            properties[0] = DrawProperty.Vector(LightPropertyId, new Vector4(light.Light, 0f));
            properties[1] = DrawProperty.Vector(LightAtPropertyId, new Vector4(light.X, light.Z, (byte)light.Kind, light.Radius));
            properties[2] = DrawProperty.Vector(ShadowChannelPropertyId, channel);
            float diameter = 2f * light.Radius;
            Matrix4x4 matrix = UnityEngine.Matrix4x4.ToNumerics(UnityEngine.Matrix4x4.TRS(
                new UnityEngine.Vector3(light.X, altitude, light.Z), UnityEngine.Quaternion.identity,
                new UnityEngine.Vector3(diameter, 1f, diameter)));
            var command = new MeshDrawCommand(MeshPool.plane10.EngineMesh, materials[Math.Min(slot.Round, 1)], range, matrix, 0,
                Rendering.RimLumenRenderHost.LumenRenderPassId, renderQueueOverride: queue);
            if (maker.TryMake(in command, arena, UnityEngine.Shader.GlobalKeywordKey, i) is { } draw) draws.Add(draw);
            rounds = Math.Max(rounds, slot.Round + 1);
            if (!objectShadows) continue;
            foreach (CasterChunks.Chunk chunk in casters.Filled)
                if (chunk.Overlaps(light.X - light.Radius, light.Z - light.Radius, light.X + light.Radius, light.Z + light.Radius))
                    AppendCaster(shadows, maker, arena, chunk, new Vector4(light.X, light.Z, light.Y, light.Radius), Vector4.Zero, channel, queue);
        }
        if (sun)
        {
            UnityEngine.Vector4 cast = UnityEngine.Shader.GetGlobalVector(ShaderPropertyIDs.MapSunLightDirection);
            CellRect view = Find.CameraDriver.CurrentViewRect;
            rounds = Math.Max(rounds, 1);
            foreach (CasterChunks.Chunk chunk in casters.Filled)
            {
                // The chunk's shadows reach its box moved by up to cast * MaxHeight.
                float dx = cast.x * chunk.MaxHeight, dz = cast.z * chunk.MaxHeight;
                if (chunk.MinX + MathF.Min(0f, dx) <= view.maxX + 1 && chunk.MaxX + MathF.Max(0f, dx) >= view.minX
                    && chunk.MinZ + MathF.Min(0f, dz) <= view.maxZ + 1 && chunk.MaxZ + MathF.Max(0f, dz) >= view.minZ)
                    AppendCaster(shadows, maker, arena, chunk, Vector4.Zero, new Vector4(cast.x, cast.z, 1f, 0f),
                        SunChannel, RenderQueue);
            }
        }
        staged.Clear();
        return rounds;
    }

    // ShadowSlot.Selector of the takeover sun (round 0, ShadowChannels.SunChannel).
    private static Vector4 SunChannel => new ShadowSlot(0, ShadowChannels.SunChannel).Selector;

    private void AppendCaster(List<FinalDrawItem> shadows, DrawItemFactory maker, DrawPropertyArena arena, CasterChunks.Chunk chunk,
        Vector4 light, Vector4 sun, Vector4 channel, int queue)
    {
        Span<DrawProperty> properties = arena.Allocate(3, out DrawPropertyRange range);
        properties[0] = DrawProperty.Vector(CasterLightPropertyId, light);
        properties[1] = DrawProperty.Vector(CasterSunPropertyId, sun);
        properties[2] = DrawProperty.Vector(CasterChannelPropertyId, channel);
        casterMaterial ??= new Material(CreateCasterShader(), "RimLumen.ObjectShadow");
        var command = new MeshDrawCommand(chunk.Mesh, casterMaterial, range, Matrix4x4.Identity, 0,
            Rendering.RimLumenRenderHost.LumenRenderPassId, renderQueueOverride: queue);
        if (maker.TryMake(in command, arena, UnityEngine.Shader.GlobalKeywordKey, shadows.Count) is { } draw) shadows.Add(draw);
    }

    // One program, one material per mask: round 0 reads FirstMask, later rounds LaterMask.
    private Material[] CreatePoolMaterials()
    {
        ShaderProgramDefinition definition = CreateDefinition();
        Shader shader = CreateShader("RimLumen.LightPool", definition, definition.ToPipelineState());
        var first = new Material(shader, "RimLumen.LightPool.0");
        var later = new Material(shader, "RimLumen.LightPool.1");
        first.SetTexture(ObjectShadowPropertyId, FirstMask);
        later.SetTexture(ObjectShadowPropertyId, LaterMask);
        return [first, later];
    }

    // Casters write their strength with blend Max, depth-tested against the scene without writing it.
    private static Shader CreateCasterShader()
    {
        var definition = new ShaderProgramDefinition
        {
            vertexShaderFileName = CasterVertexFile,
            fragmentShaderFileName = CasterFragmentFile,
            useTexture = false,
            transparent = true,
            depthTest = true,
            depthWrite = false,
            cullMode = FaceCull.None,
            renderQueue = new RenderQueueValue(RenderQueue)
        };
        return CreateShader("RimLumen.ObjectShadow", definition, definition.ToPipelineState() with
        {
            Blend = new BlendState(true, BlendFactor.One, BlendFactor.One, BlendOp.Max, BlendFactor.One, BlendFactor.One, BlendOp.Max)
        });
    }

    private void UploadOcclusion(OcclusionFrame frame)
    {
        if (!ReferenceEquals(frame.Map, uploadedMap))
        {
            UploadTexture(ref occlusion, OcclusionPropertyId, frame.Map.TexelWidth, frame.Map.TexelHeight, frame.Map.Texels);
            UploadTexture(ref coarse, CoarsePropertyId, frame.Map.Width, frame.Map.Height, frame.Map.Coarse);
            uploadedMap = frame.Map;
        }
        if (!ReferenceEquals(frame.Doors, uploadedDoors))
        {
            UploadTexture(ref doors, DoorPropertyId, frame.Doors.Width, frame.Doors.Height, frame.Doors.Cells);
            uploadedDoors = frame.Doors;
        }
    }

    private void UploadTexture(ref Texture2D? texture, int property, int width, int height, byte[] data)
    {
        if (texture is null || texture.width != width || texture.height != height)
        {
            Texture2D? replaced = texture;
            texture = new Texture2D(width, height, LoadedTextureFormat.Rgba32, linear: true, mipCount: 1, createUninitialized: true)
            {
                Name = "RimLumen.Occlusion", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
            };
            foreach (Material target in materials!) target.SetTexture(property, texture);
            replaced?.Dispose();
        }
        texture.ReplaceCpuData(width, height, LoadedTextureFormat.Rgba32, [(byte[])data.Clone()], 1, linear: true, upload: true);
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

    // Requires: definition names the vertex and fragment files. Also builds LightPoolShadows' lifted sun-shadow shaders.
    // Ensures: throws InvalidOperationException when Waylith rejects the material, vertex-input or stage-interface
    //          metadata instead of handing a broken shader to the pipeline cache.
    internal static Shader CreateShader(string key, ShaderProgramDefinition definition, PipelineState pipelineState)
    {
        string vertexFile = definition.vertexShaderFileName;
        string fragmentFile = definition.fragmentShaderFileName;
        string fragmentName = System.IO.Path.GetFileName(fragmentFile);
        string vertexSource = ShaderSourceLoader.ReadShaderSource(vertexFile);
        string fragmentSource = ShaderSourceLoader.ReadShaderSource(fragmentFile);
        MaterialLayout materialLayout = ShaderMetadataParser.ExtractMaterialLayout(fragmentSource, fragmentName);
        ShaderVertexInputLayout vertexInput = ShaderSourceLoader.CreateVertexInputLayout(vertexFile);
        List<string> stageErrors = ShaderMetadataParser.ValidateStageInterface(vertexSource, fragmentSource,
            vertexFile, fragmentName);
        if (materialLayout.HasErrors || vertexInput.HasErrors || stageErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"[RimLumen] Shader metadata rejected by Waylith: {vertexFile} + {fragmentFile} " +
                $"(material errors: {materialLayout.HasErrors}, vertex input errors: {vertexInput.HasErrors}, " +
                $"stage interface: {string.Join(" ", stageErrors)}).");
        }
        return new Shader(key, vertexFile, definition, keywords: null, materialLayout,
            pipelineState, iconShaderKey: null, vertexInputLayout: vertexInput);
    }

    // Requires: render thread and GPU idle (RimLumenPipeline.Dispose after Quiesce).
    // Ensures: unregisters the material binding; idempotent.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (materials is not null)
            foreach (Material material in materials) material.Dispose();
        materials = null;
        casterMaterial?.Dispose();
        casterMaterial = null;
        FirstMask.Dispose();
        LaterMask.Dispose();
        occlusion?.Dispose();
        coarse?.Dispose();
        doors?.Dispose();
        occlusion = coarse = doors = null;
        uploadedMap = null;
        uploadedDoors = null;
    }
}
