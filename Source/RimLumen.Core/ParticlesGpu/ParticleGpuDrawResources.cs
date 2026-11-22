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
    private static readonly int ParticlesPropertyId = Shader.PropertyToID(GpuParticleProperties.Particles);
    private static readonly int EmittersPropertyId = Shader.PropertyToID(GpuParticleProperties.Emitters);
    private static readonly int CurvesPropertyId = Shader.PropertyToID(GpuParticleProperties.Curves);
    private static readonly int TrailHeadersPropertyId = Shader.PropertyToID(GpuTrailProperties.Headers);
    private static readonly int TrailPointsPropertyId = Shader.PropertyToID(GpuTrailProperties.Points);
    private static readonly int TrailEmittersPropertyId = Shader.PropertyToID(GpuTrailProperties.Emitters);

    private const int MaxActiveMaterials = 128;

    private readonly Mesh quad;
    private readonly ParticleMeshShaders shaders;
    private readonly ParticleMeshShaders trailShaders;
    private readonly Mesh[] trailMeshes;
    private readonly int retirementAge;
    // Cached materials keep only static state (shader + textures); the emitter index is a per-draw
    // DrawProperty, so two emitters sharing a texture and blend share one material.
    private readonly Dictionary<(LumenBlendMode Blend, Texture Texture), CachedMaterial> materials = new();
    private readonly Dictionary<(LumenBlendMode Blend, Texture Texture), CachedMaterial> trailMaterials = new();
    private readonly HashSet<(LumenBlendMode Blend, Texture Texture)> frameKeys = new();
    private readonly List<(LumenBlendMode Blend, Texture Texture)> retired = new();
    private bool overflowLogged;
    private bool disposed;

    internal ParticleGpuDrawResources(int framesInFlight)
    {
        retirementAge = 2 * Math.Max(1, framesInFlight) + 2;
        quad = CreateQuad();
        trailMeshes = CreateAllStripMeshes();
        // The occlusion test lives in the fragment shader, so the pipeline runs without a depth test.
        shaders = ParticleMeshShaders.GetOrCreateL2Billboard();
        trailShaders = ParticleMeshShaders.GetOrCreateL2Trail();
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
    internal static VkDescriptorPool CreateComputePool(DeviceEx device, int descriptorCount = 10)
        => CreatePool(device, descriptorCount);

    private static unsafe VkDescriptorPool CreatePool(DeviceEx device, int descriptorCount)
    {
        VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[1];
        sizes[0] = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = descriptorCount };
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
        if (disposed || (snapshot.Emitters.Length == 0 && snapshot.TrailEmitters.Length == 0)) return;
        CameraBillboardBasis.Extract(snapshot.Context.ViewMatrix, out Vector3 right, out Vector3 up, out float yScaleRatio);
        Matrix4x4 projection = snapshot.Context.ProjectionMatrix;
        var depthUv = new Vector4(1f / snapshot.Context.ViewportWidth, 1f / snapshot.Context.ViewportHeight, 0, 0);
        var cameraRight = new Vector4(right, 0);
        var cameraUp = new Vector4(up, yScaleRatio);
        var projectionPacked = new Vector4(projection.M33, projection.M34, projection.M43, projection.M44);
        float topDown2D = CameraBillboardBasis.IsStraightTopDown2D(right, up, yScaleRatio) ? 1f : 0f;
        float vanillaMoteY = Verse.Altitudes.AltitudeFor(Verse.AltitudeLayer.MoteOverhead);
        float depthLift = CameraBillboardBasis.NorthBehindLift(vanillaMoteY);
        float linearBlendFlag = snapshot.Context.ColorBlendSpace == LumenColorBlendSpace.Linear ? 1f : 0f;
        RetireStale(materials, frameIndex);
        RetireStale(trailMaterials, frameIndex);
        frameKeys.Clear();
        for (int i = 0; i < snapshot.Emitters.Length; i++)
        {
            ParticleEmitterFrame emitter = snapshot.Emitters[i];
            if (!emitter.IsVisibleOnMap(snapshot.Context.CurrentMapIndex)) continue;
            int instances = (int)emitter.Parameters.Slots.Y;
            if (instances <= 0) continue;
            Texture texture = textures[i];
            bool present = texturePresent[i];
            Material? material = MaterialFor(materials, shaders, "RimLumen.L2",
                emitter.Blend, present ? texture : Texture2D.whiteTexture, sceneDepth, frameIndex);
            if (material is null) continue;
            Span<DrawProperty> properties = arena.Allocate(8, out DrawPropertyRange range);
            PackCommonDrawProperties(properties, i, present, emitter.Blend, cameraRight, cameraUp, projectionPacked, depthUv,
                emitter.IsVertical3D, topDown2D, vanillaMoteY, depthLift);
            properties[6] = DrawProperty.Vector(TintPropertyId, emitter.MaterialTint);
            properties[7] = DrawProperty.Vector(ColorPropertyId,
                new Vector4(emitter.ColorSpace == LumenColorSpace.Linear ? 1f : 0f, textureSrgb[i] ? 1f : 0f, linearBlendFlag, 0f));
            InstancedDrawInfo instancing = resources.DrawInstances(i, instances);
            var command = new MeshDrawCommand(quad, material, range, Matrix4x4.Identity, 0,
                RimLumenRenderHost.LumenRenderPassId, instancing);
            FinalDrawItem? draw = maker.TryMake(in command, arena, UnityEngine.Shader.GlobalKeywordKey, queueOrder + i);
            if (draw is null) continue;
            draws.Add(draw);
        }

        if (snapshot.TrailEmitters.Length == 0 || resources.TrailEmitters is null) return;
        for (int t = 0; t < snapshot.TrailEmitters.Length; t++)
        {
            ParticleTrailEmitterFrame trailEmitter = snapshot.TrailEmitters[t];
            if (!trailEmitter.IsVisibleOnMap(snapshot.Context.CurrentMapIndex)) continue;
            int headerInstances = (int)trailEmitter.Parameters.Ranges.Y;
            if (headerInstances <= 0) continue;
            Texture texture = resources.TrailTextures[t];
            bool present = resources.TrailTexturePresent[t];
            Material? material = MaterialFor(trailMaterials, trailShaders, "RimLumen.L2Trail",
                trailEmitter.Blend, present ? texture : Texture2D.whiteTexture, sceneDepth, frameIndex);
            if (material is null) continue;
            Span<DrawProperty> properties = arena.Allocate(8, out DrawPropertyRange range);
            PackCommonDrawProperties(properties, t, present, trailEmitter.Blend, cameraRight, cameraUp, projectionPacked, depthUv,
                trailEmitter.IsVertical3D, topDown2D, vanillaMoteY, depthLift);
            properties[6] = DrawProperty.Vector(TintPropertyId, trailEmitter.MaterialTint);
            properties[7] = DrawProperty.Vector(ColorPropertyId,
                new Vector4(trailEmitter.ColorSpace == LumenColorSpace.Linear ? 1f : 0f, resources.TrailTextureSrgb[t] ? 1f : 0f, linearBlendFlag, 0f));
            InstancedDrawInfo instancing = resources.DrawTrailInstances(t, headerInstances);
            var command = new MeshDrawCommand(MeshForStrip(trailEmitter.MaxPoints), material, range, Matrix4x4.Identity, 0,
                RimLumenRenderHost.LumenRenderPassId, instancing);
            FinalDrawItem? draw = maker.TryMake(in command, arena, UnityEngine.Shader.GlobalKeywordKey,
                queueOrder + snapshot.Emitters.Length + t);
            if (draw is null) continue;
            draws.Add(draw);
        }
    }

    private static void PackCommonDrawProperties(Span<DrawProperty> properties, int row, bool texturePresent, LumenBlendMode blend,
        Vector4 cameraRight, Vector4 cameraUp, Vector4 projectionPacked, Vector4 depthUv, bool isVertical3D,
        float topDown2D, float vanillaMoteY, float depthLift)
    {
        properties[0] = DrawProperty.Vector(DrawPropertyId, new Vector4(row, texturePresent ? 1f : 0f, 1f,
            blend == LumenBlendMode.AlphaBlend ? 0f : 1f));
        properties[1] = DrawProperty.Vector(CameraRightPropertyId, cameraRight);
        properties[2] = DrawProperty.Vector(CameraUpPropertyId, cameraUp);
        properties[3] = DrawProperty.Vector(ProjectionPropertyId, projectionPacked);
        properties[4] = DrawProperty.Vector(DepthUvPropertyId, depthUv);
        properties[5] = DrawProperty.Vector(VerticalPropertyId,
            new Vector4(isVertical3D ? 1f : 0f, topDown2D, vanillaMoteY, depthLift));
    }

    internal Mesh MeshForStrip(int maxPoints) => trailMeshes[ParticleTrailMath.StripBucketIndex(maxPoints)];

    private static Mesh[] CreateAllStripMeshes()
    {
        Mesh[] meshes = new Mesh[ParticleTrailMath.StripBuckets.Length];
        for (int i = 0; i < meshes.Length; i++)
            meshes[i] = CreateStripMesh(ParticleTrailMath.StripBuckets[i]);
        return meshes;
    }

    internal static InstanceStorageBinding[] CreateStorageBindings(GpuComputeBuffer particles,
        GpuComputeBuffer emitters, GpuComputeBuffer curves) => new[]
    {
        new InstanceStorageBinding(ParticlesPropertyId, particles),
        new InstanceStorageBinding(EmittersPropertyId, emitters),
        new InstanceStorageBinding(CurvesPropertyId, curves)
    };

    internal static InstanceStorageBinding[] CreateTrailStorageBindings(GpuComputeBuffer trailHeaders,
        GpuComputeBuffer trailPoints, GpuComputeBuffer trailEmitters, GpuComputeBuffer curves) => new[]
    {
        new InstanceStorageBinding(TrailHeadersPropertyId, trailHeaders),
        new InstanceStorageBinding(TrailPointsPropertyId, trailPoints),
        new InstanceStorageBinding(TrailEmittersPropertyId, trailEmitters),
        new InstanceStorageBinding(CurvesPropertyId, curves)
    };

    // Static state only: emitter index, camera vectors and depth UVs are per-draw DrawProperties.
    private Material? MaterialFor(Dictionary<(LumenBlendMode Blend, Texture Texture), CachedMaterial> cache,
        ParticleMeshShaders shaderSet, string namePrefix, LumenBlendMode blend, Texture texture,
        Texture sceneDepth, ulong frameIndex)
    {
        var key = (blend, texture);
        if (cache.TryGetValue(key, out CachedMaterial cached))
        {
            cache[key] = cached with { LastUsedFrame = frameIndex };
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
            LumenBlendMode.Additive => shaderSet.Additive,
            LumenBlendMode.AlphaBlend => shaderSet.Alpha,
            LumenBlendMode.Premultiplied => shaderSet.Premultiplied,
            _ => throw new ArgumentOutOfRangeException(nameof(blend), blend, "Unknown L2 blend mode.")
        };
        var material = new Material(shader, $"{namePrefix}.{blend}.{texture.Name}");
        material.SetTexture(TexturePropertyId, texture);
        // The proxy mirrors the current submission slot's R32_SFLOAT snapshot; its backing image follows
        // target rebuilds, and the engine re-resolves the descriptor when the allocation changes.
        material.SetTexture(SceneDepthPropertyId, sceneDepth);
        cache[key] = new CachedMaterial(material, frameIndex);
        frameKeys.Add(key);
        return material;
    }

    private void RetireStale(Dictionary<(LumenBlendMode Blend, Texture Texture), CachedMaterial> cache, ulong frameIndex)
    {
        if (cache.Count == 0) return;
        retired.Clear();
        foreach (KeyValuePair<(LumenBlendMode Blend, Texture Texture), CachedMaterial> pair in cache)
            if (frameIndex >= pair.Value.LastUsedFrame && frameIndex - pair.Value.LastUsedFrame > (ulong)retirementAge)
                retired.Add(pair.Key);
        foreach ((LumenBlendMode Blend, Texture Texture) key in retired)
        {
            cache[key].Material.Dispose();
            cache.Remove(key);
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

    private static Mesh CreateStripMesh(int segments)
    {
        Mesh mesh = new() { DebugName = $"RimLumen.L2TrailStrip.{segments}" };
        MeshDataArray data = Mesh.AllocateWritableMeshData(1);
        bool applied = false;
        try
        {
            int centreCount = segments + 1;
            MeshData writable = data[0];
            writable.SetVertexBufferParams(2 * centreCount, new[]
            {
                new VertexAttribute(VertexAttributeSemantic.Position, VertexAttributeFormat.Float32, 3, 0, 0)
            });
            Span<Vector3> positions = writable.GetVertexData<Vector3>().AsSpan();
            for (int i = 0; i < centreCount; i++)
            {
                positions[2 * i] = new Vector3(i, -1f, 0f);
                positions[2 * i + 1] = new Vector3(i, 1f, 0f);
            }
            writable.SetIndexBufferParams(6 * segments, MeshIndexFormat.UInt32);
            Span<uint> indices = writable.GetIndexData<uint>().AsSpan();
            for (int i = 0; i < segments; i++)
            {
                uint minus = (uint)(2 * i), plus = minus + 1, nextMinus = minus + 2, nextPlus = minus + 3;
                int idx = i * 6;
                indices[idx] = minus;
                indices[idx + 1] = plus;
                indices[idx + 2] = nextPlus;
                indices[idx + 3] = minus;
                indices[idx + 4] = nextPlus;
                indices[idx + 5] = nextMinus;
            }
            writable.subMeshCount = 1;
            writable.SetSubMesh(0, new SubMesh(0, 6 * segments));
            Mesh.ApplyAndDisposeWritableMeshData(data, mesh);
            applied = true;
        }
        finally
        {
            if (!applied) data.Dispose();
        }
        return mesh;
    }

    // Drops cached Materials when idle-releasing GPU pools while retaining prebuilt quad and strip meshes.
    internal void ClearMaterials()
    {
        foreach (CachedMaterial cached in materials.Values) cached.Material.Dispose();
        materials.Clear();
        foreach (CachedMaterial cached in trailMaterials.Values) cached.Material.Dispose();
        trailMaterials.Clear();
        frameKeys.Clear();
        retired.Clear();
    }

    // Requires: main thread after the render thread went idle (LumenGpuParticleEngine.DisposeGpu).
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ClearMaterials();
        quad.Dispose();
        for (int i = 0; i < trailMeshes.Length; i++)
            trailMeshes[i].Dispose();
    }

    // Per-capture vector state is never stored here: see AppendDraws/DrawPropertyArena.
    private readonly record struct CachedMaterial(Material Material, ulong LastUsedFrame);
}

internal sealed class ParticleGpuFrame : IDisposable
{
    private readonly DeviceEx device;
    internal readonly VkDescriptorPool Pool;
    internal readonly VkDescriptorPool? TrailPool;
    internal readonly GpuComputeBuffer Emitters, Fields, Curves, MapCells, Events, SubLinks, ExplicitBursts;
    internal readonly GpuComputeBuffer? TrailEmitters, TrailHeaders, TrailPoints, ParticleToTrail;
    internal readonly int EmitterCapacity, FieldCapacity, CellCapacity, EventCapacity, LinkCapacity, BurstCapacity, TrailEmitterCapacity;
    internal readonly ParticleEmitterFrame[] SimulationEmitters;
    internal readonly Texture[] Textures;
    internal readonly bool[] TexturePresent;
    internal readonly bool[] TextureSrgb;
    internal readonly Texture[] TrailTextures;
    internal readonly bool[] TrailTexturePresent;
    internal readonly bool[] TrailTextureSrgb;
    internal readonly GpuComputeBuffer[] ComputeBuffers;
    internal readonly GpuComputeBuffer[]? TrailComputeBuffers;
    internal GpuComputeBuffer BurstStates => ComputeBuffers[6];
    private readonly GpuEmitterParams[] parameters;
    private readonly GpuTrailEmitterParams[] trailParameters;
    private RimLumen.Core.Scene.ParticleMapGrid[] uploadedMaps = Array.Empty<RimLumen.Core.Scene.ParticleMapGrid>();
    private readonly int[] curveInstanceIds;
    private readonly int[] trailCurveInstanceIds;
    private readonly InstanceStorageBinding[] storageBindings;
    private readonly InstanceStorageBinding[]? trailStorageBindings;
    private readonly InstancedDrawInfo?[] instancing;
    private readonly InstancedDrawInfo?[] trailInstancing;

    internal static long EmitterBufferBytes(int emitterCapacity) => (long)emitterCapacity * Marshal.SizeOf<GpuEmitterParams>();

    internal ParticleGpuFrame(EngineApplication app, ParticleFrame snapshot, int emitterCount,
        GpuComputeBuffer particles, GpuComputeBuffer deadBlocks, GpuComputeBuffer burstStates,
        GpuComputeBuffer? trailHeaders = null, GpuComputeBuffer? trailPoints = null, GpuComputeBuffer? particleToTrail = null)
    {
        device = app.Device;
        bool hasTrailCompute = trailHeaders is not null && trailPoints is not null && particleToTrail is not null;
        EmitterCapacity = Math.Max(1, emitterCount);
        FieldCapacity = Math.Max(1, snapshot.Fields.Length);
        CellCapacity = Math.Max(1, snapshot.CellCount);
        EventCapacity = Math.Max(1, snapshot.EventCapacity);
        LinkCapacity = Math.Max(1, snapshot.SubLinks.Length);
        BurstCapacity = Math.Max(1, snapshot.ExplicitBursts.Length);
        TrailEmitterCapacity = hasTrailCompute ? Math.Max(1, snapshot.TrailEmitters.Length) : 0;
        TrailHeaders = trailHeaders;
        TrailPoints = trailPoints;
        ParticleToTrail = particleToTrail;
        Pool = ParticleGpuDrawResources.CreateComputePool(device);
        Emitters = GpuComputeBuffer.Create(device, app.GraphicsDevice!.Allocator, "Lumen.Emitters", EmitterBufferBytes(EmitterCapacity));
        Fields = GpuComputeBuffer.Create(device, app.GraphicsDevice.Allocator, "Lumen.Fields", (long)FieldCapacity * 48);
        Curves = GpuComputeBuffer.Create(device, app.GraphicsDevice.Allocator, "Lumen.Curves",
            ((long)EmitterCapacity + 2L * TrailEmitterCapacity) * 64 * 32);
        MapCells = GpuComputeBuffer.Create(device, app.GraphicsDevice.Allocator, "Lumen.CollisionCells", (long)CellCapacity * 4);
        Events = GpuComputeBuffer.Create(device, app.GraphicsDevice.Allocator, "Lumen.SubEvents", (long)EventCapacity * Marshal.SizeOf<GpuSubEvent>(), deviceLocal: true);
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

        if (hasTrailCompute)
        {
            TrailPool = ParticleGpuDrawResources.CreateComputePool(device, 7);
            TrailEmitters = GpuComputeBuffer.Create(device, app.GraphicsDevice.Allocator, "Lumen.TrailEmitters",
                (long)TrailEmitterCapacity * Marshal.SizeOf<GpuTrailEmitterParams>());
            TrailTextures = new Texture[TrailEmitterCapacity];
            TrailTexturePresent = new bool[TrailEmitterCapacity];
            TrailTextureSrgb = new bool[TrailEmitterCapacity];
            trailParameters = new GpuTrailEmitterParams[TrailEmitterCapacity];
            trailCurveInstanceIds = new int[TrailEmitterCapacity];
            Array.Fill(trailCurveInstanceIds, -1);
            TrailComputeBuffers = new[]
            {
                particles, Emitters, Curves, trailHeaders!, trailPoints!, TrailEmitters, particleToTrail!
            };
            trailStorageBindings = ParticleGpuDrawResources.CreateTrailStorageBindings(trailHeaders!, trailPoints!, TrailEmitters, Curves);
            trailInstancing = new InstancedDrawInfo?[TrailEmitterCapacity];
        }
        else
        {
            TrailTextures = Array.Empty<Texture>();
            TrailTexturePresent = Array.Empty<bool>();
            TrailTextureSrgb = Array.Empty<bool>();
            trailParameters = Array.Empty<GpuTrailEmitterParams>();
            trailCurveInstanceIds = Array.Empty<int>();
            trailInstancing = Array.Empty<InstancedDrawInfo?>();
        }
    }

    internal InstancedDrawInfo DrawInstances(int row, int count)
    {
        InstancedDrawInfo? draw = instancing[row];
        if (draw is null) instancing[row] = draw = InstancedDrawInfo.Direct(count, storageBindings);
        else draw.ReconfigureDirect(count, storageBindings);
        return draw;
    }

    internal InstancedDrawInfo DrawTrailInstances(int row, int count)
    {
        InstancedDrawInfo? draw = trailInstancing[row];
        if (draw is null) trailInstancing[row] = draw = InstancedDrawInfo.Direct(count, trailStorageBindings!);
        else draw.ReconfigureDirect(count, trailStorageBindings!);
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
        if (TrailPool is { } tPool && TrailEmitters is not null)
        {
            device.ResetDescriptorPool(tPool, 0);
            for (int t = 0; t < frame.TrailEmitters.Length; t++)
            {
                ParticleTrailEmitterFrame trail = frame.TrailEmitters[t];
                int curveRow = EmitterCapacity + 2 * t;
                GpuTrailEmitterParams tp = trail.Parameters;
                tp.PointsAndCurve.Y = curveRow;
                trailParameters[t] = tp;
                if (trailCurveInstanceIds[t] != trail.InstanceId)
                {
                    Curves.Upload<GpuCurveSample>(trail.Curves, (long)curveRow * 64 * 32);
                    trailCurveInstanceIds[t] = trail.InstanceId;
                }
            }
            if (frame.TrailEmitters.Length > 0)
                TrailEmitters.Upload<GpuTrailEmitterParams>(trailParameters.AsSpan(0, frame.TrailEmitters.Length));
        }
    }

    public void Dispose()
    {
        device.DestroyDescriptorPool(Pool, device.VkAllocator);
        if (TrailPool is { } tPool) device.DestroyDescriptorPool(tPool, device.VkAllocator);
        Emitters.Dispose(); Fields.Dispose(); Curves.Dispose();
        MapCells.Dispose(); Events.Dispose(); SubLinks.Dispose(); ExplicitBursts.Dispose();
        TrailEmitters?.Dispose();
    }
}
