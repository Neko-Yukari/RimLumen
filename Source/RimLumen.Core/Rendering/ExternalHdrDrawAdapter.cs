using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimLumen.Core.Compat;
using RimLumen.Core.Contracts;
using RimLumen.Core.Lighting;
using Waylith.Rendering;

namespace RimLumen.Core.Rendering;

// Adapts third-party world draws tagged with RimLumenRenderHost.ExternalHdrPassId to RimLumen's HDR layer:
// re-verifies final capture-time pipeline state and resolved GPU uniforms (including MaterialPropertyBlock overrides),
// selects the Linear _HdrOutput shader variant when in Linear blend space, clamps fragment alpha for CoverageAlpha
// draws so RGBA16F never sees unclamped a < 0 or a > 1, and normalizes attachment alpha to exact coverage.
// Never mutates a published MaterialBinding so in-flight render-thread frames keep immutable state.
internal static class ExternalHdrDrawAdapter
{
    // Negative id outside positive MaterialBindingCache ids and LightPoolShadows/LightingTakeover (-1..-6).
    // CopyWithBinding sets hasOverride=true, so MeshBatchRenderer uses per-frame material sets rather than id caching.
    private const int AdaptedBindingId = -7;

    private sealed class VariantSlots
    {
        public CachedEntry? Legacy;
        public CachedEntry? Linear;
    }

    private sealed record CachedEntry(
        Shader TargetShader,
        MaterialLayout MaterialLayout,
        PipelineState AdaptedState,
        ShaderVariantKey SourceVariant,
        int RenderQueue,
        TextureWrapMode? WrapModeOverride,
        MaterialStorageArray[] StorageArrays,
        string DebugName,
        MaterialBinding AdaptedBinding);

    private static readonly ConditionalWeakTable<MaterialBinding, VariantSlots> Cache = new();
    private static readonly HashSet<string> LoggedSkips = new(StringComparer.Ordinal);

    // Requires: main thread during RimLumenPipeline.CaptureFrame, for a draw tagged with ExternalHdrPassId.
    // Ensures: returns a newly rented copy targeting LumenRenderPassId backed by an immutable MaterialBinding and
    //          hasOverride=true (snapshotting Uniforms/Resources so later main-thread edits never race with an
    //          in-flight render-thread frame), or null if final captured state / per-draw overrides are not eligible
    //          (caller then restores the command's PassId back to MapMain so no draw is ever dropped).
    internal static FinalDrawItem? TryAdapt(FinalDrawItem draw, LumenColorBlendSpace colorBlendSpace)
    {
        MaterialBinding source = draw.Binding;
        Shader sourceShader = source.Shader;
        PipelineState sourceState = source.PipelineState;
        if (!ExternalHdrMaterialRules.TryAdaptPipelineState(in sourceState, draw.RenderQueue,
                sourceShader.SceneGrab != null, out PipelineState adaptedState, out ExternalHdrBlendKind blendKind))
        {
            LogSkipOnce(sourceShader.Key, draw.RenderQueue, colorBlendSpace);
            return null;
        }

        Shader targetShader;
        if (blendKind == ExternalHdrBlendKind.CoverageAlpha)
        {
            // Re-check final resolved GPU uniforms (which include MaterialPropertyBlock overrides and late same-frame
            // material updates) so an instance overriding HDR down to SDR stays on MapMain without glowing at night.
            if (!ExternalHdrMaterialRules.IsHdrEmissive(source.MaterialLayout, in draw.Uniforms))
                return null;

            if (sourceShader.PrecompiledStages is null
                && (source.ShaderVariant.DynamicKeywordKeyId != 0 || source.ShaderVariant.Instanced || source.ShaderVariant.OutlineMrt))
            {
                LogSkipOnce(sourceShader.Key, draw.RenderQueue, colorBlendSpace);
                return null;
            }
            if (!ExternalHdrAlphaClampShaders.TryResolve(sourceShader, colorBlendSpace, out targetShader))
            {
                LogSkipOnce(sourceShader.Key, draw.RenderQueue, colorBlendSpace);
                return null;
            }
        }
        else if (!ExternalHdrMaterialRules.TryResolveShaderForBlendSpace(sourceShader, colorBlendSpace, out targetShader))
        {
            LogSkipOnce(sourceShader.Key, draw.RenderQueue, colorBlendSpace);
            return null;
        }

        MaterialBinding adaptedBinding = GetOrCreateBinding(source, targetShader, in adaptedState, colorBlendSpace);
        return LightPoolShadows.CopyWithBinding(draw, adaptedBinding, RimLumenRenderHost.LumenRenderPassId);
    }

    private static MaterialBinding GetOrCreateBinding(MaterialBinding source, Shader targetShader,
        in PipelineState adaptedState, LumenColorBlendSpace colorBlendSpace)
    {
        VariantSlots slots = Cache.GetValue(source, static _ => new VariantSlots());
        CachedEntry? cached = colorBlendSpace == LumenColorBlendSpace.Linear ? slots.Linear : slots.Legacy;
        MaterialLayout layout = targetShader.MaterialLayout;
        if (cached is not null
            && ReferenceEquals(cached.TargetShader, targetShader)
            && ReferenceEquals(cached.MaterialLayout, layout)
            && cached.AdaptedState == adaptedState
            && cached.SourceVariant.Equals(source.ShaderVariant)
            && cached.RenderQueue == source.RenderQueue
            && cached.WrapModeOverride == source.WrapModeOverride
            && ReferenceEquals(cached.StorageArrays, source.StorageArrays)
            && string.Equals(cached.DebugName, source.DebugName, StringComparison.Ordinal))
        {
            return cached.AdaptedBinding;
        }

        ShaderVariantKey sourceVariant = source.ShaderVariant;
        var adaptedVariant = new ShaderVariantKey(targetShader, sourceVariant.DynamicKeywordKeyId,
            sourceVariant.DynamicKeywordKey, targetShader.UseTexture, sourceVariant.OutlineMrt, sourceVariant.Instanced);
        // Uniforms and Resources travel per-draw via CopyWithBinding (hasOverride: true), so keep them empty here
        // instead of pinning stale Texture references in the cache across material texture swaps.
        var binding = new MaterialBinding(AdaptedBindingId, source.MaterialId, source.MaterialVersion, source.DebugName,
            targetShader, layout, default, default, source.RenderQueue,
            adaptedVariant, adaptedState, source.WrapModeOverride, source.UsesGlobals);
        binding.UpdateStorageArrays(source.StorageArrays);

        var next = new CachedEntry(targetShader, layout, adaptedState, sourceVariant, source.RenderQueue,
            source.WrapModeOverride, source.StorageArrays, source.DebugName, binding);
        if (colorBlendSpace == LumenColorBlendSpace.Linear) slots.Linear = next;
        else slots.Legacy = next;
        return binding;
    }

    // Computes far-to-near transparent depth ranks for external HDR commands matching RimWorldWaylith TransparentDepthOrder,
    // so overlapping CoverageAlpha draws inside the HDR pass preserve the host's exact back-to-front draw order.
    internal sealed class DepthRanker
    {
        private ushort[] ranks = Array.Empty<ushort>();
        private float[] sortKeys = Array.Empty<float>();
        private int[] sortIndices = Array.Empty<int>();

        public ushort this[int index] => ranks[index];

        public bool ComputeForPass(MeshDrawCommand[] commands, int count, byte targetPassId, in System.Numerics.Matrix4x4 view)
        {
            int matched = 0;
            for (int i = 0; i < count; i++)
                if (commands[i].PassId == targetPassId) matched++;
            if (matched <= 1) return false;

            if (ranks.Length < count)
            {
                int size = Math.Max(count, Math.Max(64, ranks.Length * 2));
                ranks = new ushort[size];
                sortKeys = new float[size];
                sortIndices = new int[size];
            }

            int sortedCount = 0;
            for (int i = 0; i < count; i++)
            {
                ranks[i] = 0;
                ref MeshDrawCommand command = ref commands[i];
                if (command.PassId != targetPassId) continue;

                System.Numerics.Vector3 worldPos;
                if (command.Instancing?.SortCentre is { } centre)
                {
                    worldPos = centre;
                }
                else
                {
                    Waylith.Graphics.Meshes.MeshBounds bounds = SortBounds(in command, out bool sectionSubmesh);
                    float y = sectionSubmesh ? bounds.MinY : (bounds.MinY + bounds.MaxY) * 0.5f;
                    worldPos = TransformPoint(command.Matrix,
                        new System.Numerics.Vector3((bounds.MinX + bounds.MaxX) * 0.5f, y, (bounds.MinZ + bounds.MaxZ) * 0.5f));
                }

                float depth = MathF.Abs(TransformPoint(in view, worldPos).Z);
                if (float.IsFinite(depth))
                {
                    depth = MathF.Round(depth * 4096f) / 4096f;
                    sortKeys[sortedCount] = -depth;
                    sortIndices[sortedCount] = i;
                    sortedCount++;
                }
            }

            if (sortedCount <= 1) return false;
            Array.Sort(sortKeys, sortIndices, 0, sortedCount);
            int distinct = 1;
            for (int j = 1; j < sortedCount; j++)
                if (sortKeys[j] != sortKeys[j - 1]) distinct++;
            float scale = distinct > 65535 ? 65535f / (distinct - 1) : 1f;
            int rank = 0;
            for (int k = 0; k < sortedCount; k++)
            {
                if (k > 0 && sortKeys[k] != sortKeys[k - 1]) rank++;
                ranks[sortIndices[k]] = (ushort)(rank * scale);
            }
            return true;
        }

        private static Waylith.Graphics.Meshes.MeshBounds SortBounds(in MeshDrawCommand command, out bool sectionSubmesh)
        {
            sectionSubmesh = false;
            Waylith.Graphics.Meshes.SubMesh[] subMeshes = command.Mesh.CpuStorage.SubMeshes;
            if ((uint)command.SubmeshIndex < (uint)subMeshes.Length)
            {
                Waylith.Graphics.Meshes.MeshBounds sub = subMeshes[command.SubmeshIndex].Bounds;
                if (!sub.IsEmpty && (sub.MinX != 0f || sub.MinY != 0f || sub.MinZ != 0f || sub.MaxX != 0f || sub.MaxY != 0f || sub.MaxZ != 0f))
                {
                    Waylith.Graphics.Meshes.MeshBounds meshBounds = command.Mesh.Bounds;
                    sectionSubmesh = meshBounds.MinY != sub.MinY || meshBounds.MaxY != sub.MaxY;
                    return sub;
                }
            }
            return command.Mesh.Bounds;
        }

        private static System.Numerics.Vector3 TransformPoint(in System.Numerics.Matrix4x4 m, System.Numerics.Vector3 p) =>
            new(m.M11 * p.X + m.M12 * p.Y + m.M13 * p.Z + m.M14,
                m.M21 * p.X + m.M22 * p.Y + m.M23 * p.Z + m.M24,
                m.M31 * p.X + m.M32 * p.Y + m.M33 * p.Z + m.M34);
    }

    private static void LogSkipOnce(string shaderKey, int renderQueue, LumenColorBlendSpace colorBlendSpace)
    {
        string key = $"{shaderKey}|{renderQueue}|{colorBlendSpace}";
        if (LoggedSkips.Add(key))
            RimLumenRenderHost.LogWarning($"[RimLumen] Skipped external HDR draw for shader '{shaderKey}' at queue {renderQueue} ({colorBlendSpace}); restored draw to MapMain.");
    }
}
