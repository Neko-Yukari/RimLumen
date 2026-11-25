using System;
using System.Runtime.CompilerServices;
using RimLumen.Core.Contracts;
using Waylith.Rendering;

namespace RimLumen.Adapter.RimUnity;

internal sealed class ExternalHdrDrawAdapter(Func<int> readBloomMask) : ILumenDrawAdapter
{
    public int ExcludedGlowLayerMask => readBloomMask();

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

    public MaterialBinding? AdaptBinding(FinalDrawItem draw, LumenColorBlendSpace colorBlendSpace)
    {
        MaterialBinding source = draw.Binding;
        Shader sourceShader = source.Shader;
        PipelineState sourceState = source.PipelineState;
        if (!ExternalHdrMaterialRules.IsEmissiveShader(sourceShader)
            || !ExternalHdrMaterialRules.TryAdaptPipelineState(in sourceState, draw.RenderQueue,
                sourceShader.SceneGrab != null, out PipelineState adaptedState, out ExternalHdrBlendKind blendKind)
            || !ExternalHdrMaterialRules.TrySelectShader(sourceShader, colorBlendSpace, blendKind,
                source.ShaderVariant.DynamicKeywordKeyId != 0 || source.ShaderVariant.Instanced || source.ShaderVariant.OutlineMrt,
                out Shader targetShader))
            return null;
        return GetOrCreateBinding(source, targetShader, in adaptedState, colorBlendSpace);
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

}
