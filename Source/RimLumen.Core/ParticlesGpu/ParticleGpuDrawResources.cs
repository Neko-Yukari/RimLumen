using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RimLumen.Core.Contracts;
using RimLumen.Core.Rendering;
using Waylith;
using Waylith.Graphics.Meshes;
using Waylith.Rendering;
using Waylith.Rendering.Compute;
using Shader = Waylith.Rendering.Shader;

namespace RimLumen.Core.ParticlesGpu;

// L2 GPU particles are one static quad Mesh drawn once per instance through MeshBatchRenderer. Every
// draw shares a cached Material per (texture, blend): per-draw arena vectors carry flags, camera axes
// and material colour; emitter/particle/curve buffers arrive as InstancedDrawInfo storage bindings.
// Main-thread only: it builds Mesh/Shader/Material objects and never touches a command buffer.
internal sealed class ParticleGpuDrawResources : IDisposable
{
    private static readonly int TintPropertyId = Shader.PropertyToID("_LumenMaterialTint");
    private static readonly int ColorPropertyId = Shader.PropertyToID("_LumenColor");
    private static readonly int DrawPropertyId = Shader.PropertyToID("_LumenDraw");
    private static readonly int CameraRightPropertyId = Shader.PropertyToID("_LumenCameraRight");
    private static readonly int CameraUpPropertyId = Shader.PropertyToID("_LumenCameraUp");
    private static readonly int ProjectionPropertyId = Shader.PropertyToID("_LumenProjection");
    private static readonly int DepthUvPropertyId = Shader.PropertyToID("_LumenDepthUv");
    private static readonly int VerticalPropertyId = Shader.PropertyToID("_LumenVertical");
    private static readonly int TexturePropertyId = Shader.PropertyToID("_LumenParticleTexture");
    private static readonly int SceneDepthPropertyId = Shader.PropertyToID("_LumenSceneDepth");
    private static readonly int ParticlesPropertyId = Shader.PropertyToID("_LumenParticles");
    private static readonly int EmittersPropertyId = Shader.PropertyToID("_LumenEmitters");
    private static readonly int CurvesPropertyId = Shader.PropertyToID("_LumenCurves");

    private const int MaxActiveMaterials = 128;

    private readonly Mesh quad;
    private readonly ParticleMeshShaders shaders;
    private readonly int retirementAge;
    // Cached materials keep only static state (shader + textures); the emitter index is a per-draw
    // DrawProperty, so two emitters sharing a texture and blend share one material.
    private readonly Dictionary<(LumenBlendMode Blend, Texture Texture), CachedMaterial> materials = new();
    private readonly HashSet<(LumenBlendMode Blend, Texture Texture)> frameKeys = new();
    private readonly List<(LumenBlendMode Blend, Texture Texture)> retired = new();
    private bool overflowLogged;
    private bool disposed;

    internal ParticleGpuDrawResources(int framesInFlight)
    {
        retirementAge = 2 * Math.Max(1, framesInFlight) + 2;
        quad = CreateQuad();
        // The occlusion test lives in the fragment shader, so the pipeline runs without a depth test.
        shaders = ParticleMeshShaders.Create("RimLumen.L2", "Particles/ParticleBillboard.vert.glsl",
            "Particles/ParticleBillboard.frag.glsl", depthTest: false);
    }

    internal static VkDescriptorSetLayoutBinding Binding(int binding, VkDescriptorType type, VkShaderStageFlags stages) => new()
    {
        Binding = binding, DescriptorCount = 1, DescriptorType = type, StageFlags = stages
    };

    internal static VkDescriptorSetLayout CreateLayout(DeviceEx device, VkDescriptorSetLayoutBinding[] bindings)
    {
        return device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo { Bindings = bindings }, device.VkAllocator);
    }

    // One compute set per frame: particle/emitter/field/dead-block storage buffers.
    internal static unsafe VkDescriptorPool CreateComputePool(DeviceEx device)
    {
        VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[1];
        sizes[0] = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 10 };
        return device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo
        {
            MaxSets = 1, PoolSizeCount = 1, PoolSizes = sizes
        }, device.VkAllocator);
    }

    internal static unsafe void WriteBuffers(DeviceEx device, VkDescriptorSet set,
        GpuComputeBuffer[] buffers, VkDescriptorType type)
    {
        VkDescriptorBufferInfo* infos = stackalloc VkDescriptorBufferInfo[buffers.Length];
        VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[buffers.Length];
        for (int i = 0; i < buffers.Length; i++)
        {
            infos[i] = new VkDescriptorBufferInfo { Buffer = buffers[i].Buffer, Offset = ByteSize.Zero, Range = buffers[i].Size };
            writes[i] = new VkWriteDescriptorSet
            {
                DstSet = set, DstBinding = i, DescriptorCount = 1, DescriptorType = type, BufferInfo = infos + i
            };
        }
        device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(writes, buffers.Length), default(ReadOnlySpan<VkCopyDescriptorSet>));
    }

    // Requires: main thread, after MeshDrawSubmission flushed this frame's uploads; `particles` and the
    //           frame's emitter/curve buffers were written by this frame's enqueue.
    // Ensures: appends one instanced draw per live emitter to the pass-0 list, carrying this frame's
    //   _LumenDraw/_LumenCameraRight/_LumenCameraUp/_LumenProjection/_LumenDepthUv/_LumenVertical through the arena (never by
    //   mutating the cached Material); drops draws after one Log.Error when the capture needs more than
    //   MaxActiveMaterials distinct materials.
    internal void AppendDraws(ParticleFrame snapshot, ParticleGpuFrame resources,
        Texture sceneDepth, Texture[] textures, bool[] texturePresent, bool[] textureSrgb, List<FinalDrawItem> draws,
        DrawItemFactory maker, DrawPropertyArena arena, int queueOrder, ulong frameIndex)
    {
        if (disposed || snapshot.Emitters.Length == 0) return;
        CameraBillboardBasis.Extract(snapshot.Context.ViewMatrix, out Vector3 right, out Vector3 up, out float yScaleRatio);
        Matrix4x4 projection = snapshot.Context.ProjectionMatrix;
        var depthUv = new Vector4(1f / snapshot.Context.ViewportWidth, 1f / snapshot.Context.ViewportHeight, 0, 0);
        var cameraRight = new Vector4(right, 0);
        var cameraUp = new Vector4(up, yScaleRatio);
        var projectionPacked = new Vector4(projection.M33, projection.M34, projection.M43, projection.M44);
        float verticalMode = CameraBillboardBasis.IsStraightTopDown2D(up, yScaleRatio) ? 2f : 1f;
        float vanillaMoteY = Verse.Altitudes.AltitudeFor(Verse.AltitudeLayer.MoteOverhead);
        RetireStale(frameIndex);
        frameKeys.Clear();
        for (int i = 0; i < snapshot.Emitters.Length; i++)
        {
            ParticleEmitterFrame emitter = snapshot.Emitters[i];
            if (!emitter.IsVisibleOnMap(snapshot.Context.CurrentMapIndex)) continue;
            int instances = (int)emitter.Parameters.Slots.Y;
            if (instances <= 0) continue;
            Texture texture = textures[i];
            bool present = texturePresent[i];
            Material? material = MaterialFor(emitter.Blend, present ? texture : Texture2D.whiteTexture, sceneDepth, frameIndex);
            if (material is null) continue;
            Span<DrawProperty> properties = arena.Allocate(8, out DrawPropertyRange range);
            properties[0] = DrawProperty.Vector(DrawPropertyId, new Vector4(i, present ? 1f : 0f, 1f,
                emitter.Blend == LumenBlendMode.AlphaBlend ? 0f : 1f));
            properties[1] = DrawProperty.Vector(CameraRightPropertyId, cameraRight);
            properties[2] = DrawProperty.Vector(CameraUpPropertyId, cameraUp);
            properties[3] = DrawProperty.Vector(ProjectionPropertyId, projectionPacked);
            properties[4] = DrawProperty.Vector(DepthUvPropertyId, depthUv);
            properties[5] = DrawProperty.Vector(VerticalPropertyId, emitter.IsVertical3D
                ? new Vector4(verticalMode, 0f, vanillaMoteY, 0f) : Vector4.Zero);
            properties[6] = DrawProperty.Vector(TintPropertyId, emitter.MaterialTint);
            properties[7] = DrawProperty.Vector(ColorPropertyId,
                new Vector4(emitter.ColorSpace == LumenColorSpace.Linear ? 1f : 0f, textureSrgb[i] ? 1f : 0f,
                    snapshot.Context.ColorBlendSpace == LumenColorBlendSpace.Linear ? 1f : 0f, 0f));
            InstancedDrawInfo instancing = resources.DrawInstances(i, instances);
            var command = new MeshDrawCommand(quad, material, range, Matrix4x4.Identity, 0,
                RimLumenRenderHost.LumenRenderPassId, instancing);
            FinalDrawItem? draw = maker.TryMake(in command, arena, UnityEngine.Shader.GlobalKeywordKey, queueOrder + i);
            if (draw is null) continue;
            draws.Add(draw);
        }
    }

    internal static InstanceStorageBinding[] CreateStorageBindings(GpuComputeBuffer particles,
        GpuComputeBuffer emitters, GpuComputeBuffer curves) => new[]
    {
        new InstanceStorageBinding(ParticlesPropertyId, particles),
        new InstanceStorageBinding(EmittersPropertyId, emitters),
        new InstanceStorageBinding(CurvesPropertyId, curves)
    };

    // Static state only: emitter index, camera vectors and depth UVs are per-draw DrawProperties.
    private Material? MaterialFor(LumenBlendMode blend, Texture texture, Texture sceneDepth, ulong frameIndex)
    {
        var key = (blend, texture);
        if (materials.TryGetValue(key, out CachedMaterial cached))
        {
            materials[key] = cached with { LastUsedFrame = frameIndex };
            frameKeys.Add(key);
            return cached.Material;
        }
        if (frameKeys.Count >= MaxActiveMaterials)
        {
            if (!overflowLogged)
            {
                overflowLogged = true;
                Verse.Log.Error($"[RimLumen] L2 particles need more than {MaxActiveMaterials} distinct materials in one frame; the remaining emitters are not drawn.");
            }
            return null;
        }
        Shader shader = blend switch
        {
            LumenBlendMode.Additive => shaders.Additive,
            LumenBlendMode.AlphaBlend => shaders.Alpha,
            LumenBlendMode.Premultiplied => shaders.Premultiplied,
            _ => throw new ArgumentOutOfRangeException(nameof(blend), blend, "Unknown L2 blend mode.")
        };
        var material = new Material(shader, $"RimLumen.L2.{blend}.{texture.Name}");
        material.SetTexture(TexturePropertyId, texture);
        // The proxy mirrors the current submission slot's R32_SFLOAT snapshot; its backing image follows
        // target rebuilds, and the engine re-resolves the descriptor when the allocation changes.
        material.SetTexture(SceneDepthPropertyId, sceneDepth);
        materials[key] = new CachedMaterial(material, frameIndex);
        frameKeys.Add(key);
        return material;
    }

    // Frame-safety: a capture's draws are consumed by at most FramesInFlight submitted frames, so a material
    // unused for more than 2*FramesInFlight+2 capture indices cannot be referenced by an in-flight frame.
    // Material.Dispose only unregisters the binding (Waylith Material.cs:1273-1280 → MaterialBindingCache),
    // it frees no GPU object, so even a straggler reference would stay valid.
    private void RetireStale(ulong frameIndex)
    {
        if (materials.Count == 0) return;
        retired.Clear();
        foreach (KeyValuePair<(LumenBlendMode Blend, Texture Texture), CachedMaterial> pair in materials)
            if (frameIndex >= pair.Value.LastUsedFrame && frameIndex - pair.Value.LastUsedFrame > (ulong)retirementAge)
                retired.Add(pair.Key);
        foreach ((LumenBlendMode Blend, Texture Texture) key in retired)
        {
            materials[key].Material.Dispose();
            materials.Remove(key);
        }
    }

    private static Mesh CreateQuad()
    {
        Mesh mesh = new() { DebugName = "RimLumen.L2BillboardQuad" };
        MeshDataArray data = Mesh.AllocateWritableMeshData(1);
        bool applied = false;
        try
        {
            MeshData writable = data[0];
            writable.SetVertexBufferParams(4, new[]
            {
                new VertexAttribute(VertexAttributeSemantic.Position, VertexAttributeFormat.Float32, 3, 0, 0)
            });
            Span<Vector3> positions = writable.GetVertexData<Vector3>().AsSpan();
            positions[0] = new Vector3(0f, 0f, 0f);
            positions[1] = new Vector3(1f, 0f, 0f);
            positions[2] = new Vector3(0f, 1f, 0f);
            positions[3] = new Vector3(1f, 1f, 0f);
            writable.SetIndexBufferParams(6, MeshIndexFormat.UInt32);
            Span<uint> indices = writable.GetIndexData<uint>().AsSpan();
            indices[0] = 0; indices[1] = 1; indices[2] = 2;
            indices[3] = 1; indices[4] = 3; indices[5] = 2;
            writable.subMeshCount = 1;
            writable.SetSubMesh(0, new SubMesh(0, 6));
            Mesh.ApplyAndDisposeWritableMeshData(data, mesh);
            applied = true;
        }
        finally
        {
            if (!applied) data.Dispose();
        }
        return mesh;
    }

    // Requires: main thread after the render thread went idle (LumenGpuParticleEngine.DisposeGpu).
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (CachedMaterial cached in materials.Values) cached.Material.Dispose();
        materials.Clear();
        frameKeys.Clear();
        retired.Clear();
        quad.Dispose();
    }

    // Per-capture vector state is never stored here: see AppendDraws/DrawPropertyArena.
    private readonly record struct CachedMaterial(Material Material, ulong LastUsedFrame);
}

internal sealed class ParticleGpuFrame : IDisposable
{
    private readonly DeviceEx device;
    internal readonly VkDescriptorPool Pool;
    internal readonly GpuComputeBuffer Emitters, Fields, Curves, MapCells, Events, SubLinks, ExplicitBursts;
    internal readonly int EmitterCapacity, FieldCapacity, CellCapacity, EventCapacity, LinkCapacity, BurstCapacity;
    internal readonly ParticleEmitterFrame[] SimulationEmitters;
    internal readonly Texture[] Textures;
    internal readonly bool[] TexturePresent;
    internal readonly bool[] TextureSrgb;
    internal readonly GpuComputeBuffer[] ComputeBuffers;
    internal GpuComputeBuffer BurstStates => ComputeBuffers[6];
    private readonly GpuEmitterParams[] parameters;
    private RimLumen.Core.Scene.ParticleMapGrid[] uploadedMaps = Array.Empty<RimLumen.Core.Scene.ParticleMapGrid>();
    private readonly int[] curveInstanceIds;
    private readonly InstanceStorageBinding[] storageBindings;
    private readonly InstancedDrawInfo?[] instancing;

    internal static long EmitterBufferBytes(int emitterCapacity) => (long)emitterCapacity * Marshal.SizeOf<GpuEmitterParams>();

    internal ParticleGpuFrame(EngineApplication app, ParticleFrame snapshot, int emitterCount,
        GpuComputeBuffer particles, GpuComputeBuffer deadBlocks, GpuComputeBuffer burstStates)
    {
        device = app.Device;
        EmitterCapacity = Math.Max(1, emitterCount);
        FieldCapacity = Math.Max(1, snapshot.Fields.Length);
        CellCapacity = Math.Max(1, snapshot.CellCount);
        EventCapacity = Math.Max(1, snapshot.EventCapacity);
        LinkCapacity = Math.Max(1, snapshot.SubLinks.Length);
        BurstCapacity = Math.Max(1, snapshot.ExplicitBursts.Length);
        Pool = ParticleGpuDrawResources.CreateComputePool(device);
        Emitters = GpuComputeBuffer.Create(device, app.GraphicsDevice!.Allocator, "Lumen.Emitters", EmitterBufferBytes(EmitterCapacity));
        Fields = GpuComputeBuffer.Create(device, app.GraphicsDevice.Allocator, "Lumen.Fields", (long)FieldCapacity * 48);
        Curves = GpuComputeBuffer.Create(device, app.GraphicsDevice.Allocator, "Lumen.Curves", (long)EmitterCapacity * 64 * 32);
        MapCells = GpuComputeBuffer.Create(device, app.GraphicsDevice.Allocator, "Lumen.CollisionCells", (long)CellCapacity * 4);
        Events = GpuComputeBuffer.Create(device, app.GraphicsDevice.Allocator, "Lumen.SubEvents", (long)EventCapacity * 48, deviceLocal: true);
        SubLinks = GpuComputeBuffer.Create(device, app.GraphicsDevice.Allocator, "Lumen.SubLinks", (long)LinkCapacity * 32);
        ExplicitBursts = GpuComputeBuffer.Create(device, app.GraphicsDevice.Allocator, "Lumen.ExplicitBursts", (long)BurstCapacity * 16);
        SimulationEmitters = new ParticleEmitterFrame[EmitterCapacity];
        Textures = new Texture[EmitterCapacity];
        TexturePresent = new bool[EmitterCapacity];
        TextureSrgb = new bool[EmitterCapacity];
        parameters = new GpuEmitterParams[EmitterCapacity];
        curveInstanceIds = new int[EmitterCapacity];
        Array.Fill(curveInstanceIds, -1);
        ComputeBuffers = new[] { particles, Emitters, Fields, deadBlocks, MapCells, Events, burstStates, SubLinks, Curves, ExplicitBursts };
        storageBindings = ParticleGpuDrawResources.CreateStorageBindings(particles, Emitters, Curves);
        instancing = new InstancedDrawInfo?[EmitterCapacity];
    }

    internal InstancedDrawInfo DrawInstances(int row, int count)
    {
        InstancedDrawInfo? draw = instancing[row];
        if (draw is null) instancing[row] = draw = InstancedDrawInfo.Direct(count, storageBindings);
        else draw.ReconfigureDirect(count, storageBindings);
        return draw;
    }

    internal void Upload(ParticleFrame frame, ReadOnlySpan<ParticleEmitterFrame> emitters)
    {
        device.ResetDescriptorPool(Pool, 0);
        for (int i = 0; i < emitters.Length; i++)
        {
            ParticleEmitterFrame emitter = emitters[i];
            parameters[i] = emitter.Parameters;
            if (curveInstanceIds[i] != emitter.InstanceId)
            {
                if (emitter.InstanceId >= 0)
                    Curves.Upload<GpuCurveSample>(emitter.Curves, (long)i * 64 * 32);
                curveInstanceIds[i] = emitter.InstanceId;
            }
        }
        Emitters.Upload<GpuEmitterParams>(parameters.AsSpan(0, emitters.Length));
        if (frame.Fields.Length > 0) Fields.Upload<GpuForceField>(frame.Fields);
        bool mapsChanged = uploadedMaps.Length != frame.Maps.Length;
        for (int i = 0; !mapsChanged && i < frame.Maps.Length; i++) mapsChanged = !ReferenceEquals(uploadedMaps[i], frame.Maps[i]);
        if (mapsChanged)
        {
            long offset = 0;
            foreach (var map in frame.Maps)
            {
                MapCells.Upload<uint>(map.Cells, offset);
                offset += (long)map.Cells.Length * 4;
            }
            uploadedMaps = frame.Maps;
        }
        if (frame.SubLinks.Length > 0) SubLinks.Upload<GpuSubLink>(frame.SubLinks);
        if (frame.ExplicitBursts.Length > 0) ExplicitBursts.Upload<GpuExplicitBurst>(frame.ExplicitBursts);
    }

    public void Dispose()
    {
        device.DestroyDescriptorPool(Pool, device.VkAllocator);
        Emitters.Dispose(); Fields.Dispose(); Curves.Dispose();
        MapCells.Dispose(); Events.Dispose(); SubLinks.Dispose(); ExplicitBursts.Dispose();
    }
}
