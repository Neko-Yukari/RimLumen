using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;
using Verse;
using Waylith.Graphics.Meshes;
using Waylith.Rendering;
using Shader = Waylith.Rendering.Shader;

namespace RimLumen.Core.Rendering;

// L1 CPU particles are one dynamic Mesh with one SubMesh per (texture, blend) sub-batch. The pipeline
// stages the simulator batch on the main thread right after capture, the engine drains the mesh upload
// when it captures the frame's draw snapshot, and Capture() appends one MeshDrawCommand per SubMesh so
// MeshBatchRenderer records them inside the RimLumenHdr pass.
// Threading: the simulator's vertex/index arrays are rewritten (and resized) by the next main-thread
// capture, so Stage copies them. Texture keys are resolved on the main thread
// in Stage; ContentFinder is never touched on the render thread.
internal sealed class L1ParticleBatchRenderer : IDisposable
{
    // Distinct particle textures per capture (one cached Material each). Materials outlive the frame they
    // were created for, but a capture that would need more than this many distinct materials drops its
    // remaining sub-batches with one Log.Error, matching the 128-descriptor budget this renderer used to own.
    private const int MaxActiveTextureSlots = 128;
    private static readonly VertexAttribute[] VertexLayout =
    {
        new(VertexAttributeSemantic.Position, VertexAttributeFormat.Float32, 3, 0, 0),
        new(VertexAttributeSemantic.TexCoord0, VertexAttributeFormat.Float32, 2, 0, 12),
        new(VertexAttributeSemantic.Color, VertexAttributeFormat.Float32, 4, 0, 20)
    };
    private static readonly int DrawPropertyId = Shader.PropertyToID("_LumenDraw");
    private static readonly int DepthPropertyId = Shader.PropertyToID("_LumenDepth");
    private static readonly int ProjectionPropertyId = Shader.PropertyToID("_LumenProjection");
    private static readonly int TexturePropertyId = Shader.PropertyToID("_LumenParticleTexture");
    private static readonly int SceneDepthPropertyId = Shader.PropertyToID("_LumenSceneDepth");

    private readonly BorrowedRenderTexture sceneDepth;
    private readonly int retirementAge;
    private readonly object gate = new();
    private readonly Mesh mesh = new() { DebugName = "RimLumen.L1Particles" };
    // Cached materials keep only static state (shader + textures). Everything per-frame travels through the
    // capture's DrawPropertyArena, so no cached Material is mutated while a submitted frame can still read it.
    private readonly Dictionary<(LumenBlendMode Blend, Texture Texture), CachedMaterial> materials = new();
    private readonly List<(LumenBlendMode Blend, Texture Texture)> frameKeys = new();
    private readonly List<(LumenBlendMode Blend, Texture Texture)> retired = new();
    private readonly List<StagedBatch> staged = new();
    private ParticleMeshShaders? shaders;
    private Vector4 depthParams = new(0.3f, 1000f, 1f, 1f);
    private Vector4 projectionPacked = new(1f, 0f, 0f, 1f);
    private ulong stagedFrameIndex = ulong.MaxValue;
    private bool overflowLogged;
    private bool disposed;

    // Requires: main thread; sceneDepth (the depth proxy) is bound per frame by the pipeline before the
    //           HDR pass records. framesInFlight is the engine's swapchain depth.
    // Ensures: allocates the dynamic mesh only; nothing is uploaded until the first Stage. Cached materials
    //          are retired once the capture index is more than 2*framesInFlight+2 ahead of their last use.
    internal L1ParticleBatchRenderer(BorrowedRenderTexture sceneDepth, int framesInFlight)
    {
        this.sceneDepth = sceneDepth;
        retirementAge = 2 * Math.Max(1, framesInFlight) + 2;
        mesh.MarkDynamic();
    }

    // Requires: main thread, after the simulator's CaptureMainThread for frameIndex (= (ulong)timing.FrameNumber).
    // Ensures: replaces the dynamic mesh with this frame's quads (vertices.Length == 4n, indices.Length == 6n,
    //   one SubMesh per sub-batch); n == 0 stages nothing. Each sub-batch's TextureKey is resolved here via
    //   LumenTextureRegistry; a null/empty sub-batch list synthesizes one (null, Additive) range.
    // Throws: ArgumentException when a sub-batch range lies outside indices (nothing is staged).
    internal void Stage(ulong frameIndex, ReadOnlySpan<ParticleVertex> vertices, ReadOnlySpan<uint> indices,
        IReadOnlyList<L1SubBatch>? subBatches, Vector4 depth, Vector4 projection)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            depthParams = depth;
            projectionPacked = projection;
            staged.Clear();
            stagedFrameIndex = frameIndex;
            if (vertices.IsEmpty || indices.IsEmpty) return;

            if (subBatches is null || subBatches.Count == 0)
            {
                staged.Add(Resolve(null, LumenBlendMode.Additive, 0, indices.Length));
            }
            else
            {
                foreach (L1SubBatch sub in subBatches)
                {
                    if (sub.IndexStart < 0 || sub.IndexCount < 0 || (long)sub.IndexStart + sub.IndexCount > indices.Length)
                        throw new ArgumentException($"L1 sub-batch [{sub.IndexStart}, +{sub.IndexCount}) lies outside {indices.Length} indices.", nameof(subBatches));
                    if (sub.IndexCount > 0) staged.Add(Resolve(sub.TextureKey, sub.BlendMode, sub.IndexStart, sub.IndexCount, sub.SoftParticleFadeDistance));
                }
            }
            if (staged.Count == 0) return;

            MeshDataArray data = Mesh.AllocateWritableMeshData(1);
            bool applied = false;
            try
            {
                MeshData writable = data[0];
                writable.SetVertexBufferParams(vertices.Length, VertexLayout);
                writable.SetIndexBufferParams(indices.Length, MeshIndexFormat.UInt32);
                vertices.CopyTo(writable.GetVertexData<ParticleVertex>().AsSpan());
                indices.CopyTo(writable.GetIndexData<uint>().AsSpan());
                writable.subMeshCount = staged.Count;
                for (int i = 0; i < staged.Count; i++)
                    writable.SetSubMesh(i, new SubMesh(staged[i].IndexStart, staged[i].IndexCount));
                // Copies into CPU storage and requests the engine upload; MeshDrawSubmission drains it on
                // the main thread right before this frame's draw snapshot is captured.
                Mesh.ApplyAndDisposeWritableMeshData(data, mesh);
                applied = true;
            }
            finally
            {
                if (!applied) data.Dispose();
            }
        }
    }

    private static StagedBatch Resolve(string? textureKey, LumenBlendMode blend, int indexStart, int indexCount, float softFade = 0.5f)
    {
        Texture texture = LumenTextureRegistry.ResolveForSamplingOnMainThread(textureKey, out bool hasTexture);
        return new StagedBatch(texture, hasTexture, blend, indexStart, indexCount, softFade);
    }

    // Requires: main thread, in RimLumenPipeline.Capture after MeshDrawSubmission.CaptureFlushedSnapshot.
    // Ensures: appends one draw per staged SubMesh to the pass-0 list, carrying this frame's _LumenDraw and
    //   _LumenDepth through the arena (never by mutating the cached Material); drops the remaining sub-batches
    //   after one Log.Error when the capture needs more than MaxActiveTextureSlots distinct materials.
    internal void AppendDraws(List<FinalDrawItem> draws, DrawItemFactory maker, DrawPropertyArena arena, int queueOrder,
        ulong frameIndex)
    {
        lock (gate)
        {
            if (disposed || staged.Count == 0) return;
            if (stagedFrameIndex != frameIndex)
            {
                staged.Clear();
                return;
            }
            shaders ??= ParticleMeshShaders.Create("RimLumen.L1", "Particles/L1ParticleQuad.vert.glsl",
                "Particles/L1ParticleQuad.frag.glsl", depthTest: true);
            RetireStale(frameIndex);
            frameKeys.Clear();
            for (int i = 0; i < staged.Count; i++)
            {
                StagedBatch batch = staged[i];
                Material? material = MaterialFor(batch, frameIndex);
                if (material is null) continue;
                Span<DrawProperty> properties = arena.Allocate(3, out DrawPropertyRange range);
                properties[0] = DrawProperty.Vector(DrawPropertyId,
                    new Vector4(batch.HasTexture ? 1f : 0f, batch.BlendMode == LumenBlendMode.AlphaBlend ? 0f : 1f, 0f, 0f));
                properties[1] = DrawProperty.Vector(DepthPropertyId,
                    new Vector4(depthParams.X, depthParams.Y, depthParams.Z, batch.SoftParticleFadeDistance));
                properties[2] = DrawProperty.Vector(ProjectionPropertyId, projectionPacked);
                var command = new MeshDrawCommand(mesh, material, range, Matrix4x4.Identity, i,
                    RimLumenRenderHost.LumenRenderPassId);
                FinalDrawItem? draw = maker.TryMake(in command, arena, UnityEngine.Shader.GlobalKeywordKey, queueOrder + i);
                if (draw is null) continue;
                draws.Add(draw);
            }
        }
    }

    // Returns the material for this key, creating it on first use. A capture may activate at most
    // MaxActiveTextureSlots distinct materials; the cap counts only this capture, so a session that touches
    // many textures over time is not permanently limited.
    private Material? MaterialFor(in StagedBatch batch, ulong frameIndex)
    {
        var key = (batch.BlendMode, batch.Texture);
        if (materials.TryGetValue(key, out CachedMaterial cached))
        {
            materials[key] = cached with { LastUsedFrame = frameIndex };
            if (!frameKeys.Contains(key)) frameKeys.Add(key);
            return cached.Material;
        }
        if (frameKeys.Count >= MaxActiveTextureSlots)
        {
            if (!overflowLogged)
            {
                overflowLogged = true;
                Log.Error($"[RimLumen] L1 particles need more than {MaxActiveTextureSlots} distinct materials in one frame; the remaining sub-batches are not drawn.");
            }
            return null;
        }
        Shader shader = (batch.BlendMode switch
        {
            LumenBlendMode.Additive => shaders!.Additive,
            LumenBlendMode.AlphaBlend => shaders!.Alpha,
            LumenBlendMode.Premultiplied => shaders!.Premultiplied,
            _ => throw new ArgumentOutOfRangeException(nameof(batch), batch.BlendMode, "Unknown L1 blend mode.")
        });
        var material = new Material(shader, $"RimLumen.L1.{batch.BlendMode}.{batch.Texture.Name}");
        // Static bindings only: the white fallback keeps the procedural falloff path, and the proxy mirrors
        // the current submission slot's R32_SFLOAT snapshot (its backing image follows the target rebuild).
        material.SetTexture(TexturePropertyId, batch.Texture);
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

    // Requires: render thread idle and GPU idle (called from RimLumenPipeline.Dispose after Quiesce).
    // Ensures: releases the dynamic mesh and cached materials; idempotent.
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            staged.Clear();
            foreach (CachedMaterial cached in materials.Values) cached.Material.Dispose();
            materials.Clear();
            frameKeys.Clear();
            retired.Clear();
            mesh.Dispose();
        }
    }

    // Texture is resolved on the main thread in Stage; HasTexture is false when Texture is the white fallback.
    private readonly record struct StagedBatch(Texture Texture, bool HasTexture, LumenBlendMode BlendMode, int IndexStart, int IndexCount,
        float SoftParticleFadeDistance);

    // Per-capture vector state is never stored here: see AppendDraws/DrawPropertyArena.
    private readonly record struct CachedMaterial(Material Material, ulong LastUsedFrame);
}
