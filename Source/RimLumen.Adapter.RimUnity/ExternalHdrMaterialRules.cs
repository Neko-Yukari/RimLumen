using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using RimLumen.Core.Contracts;
using Waylith.Rendering;

namespace RimLumen.Adapter.RimUnity;

internal enum ExternalHdrBlendKind
{
    None = 0,
    Additive = 1,
    CoverageAlpha = 2,
}

// Eligibility, effective pipeline-state resolution, HDR-emissive detection, coverage-alpha normalization and
// color-space shader selection for third-party world draws routed into RimLumen's HDR layer.
internal static class ExternalHdrMaterialRules
{
    public const int MinRenderQueueExclusive = 2500;
    public const int MaxRenderQueueExclusive = 3152;
    public const string LinearShaderKeySuffix = "_HdrOutput";

    private static readonly int BlendSrcId = ShaderPropertyId.GetOrCreate("_BlendSrc");
    private static readonly int BlendDstId = ShaderPropertyId.GetOrCreate("_BlendDst");
    private static readonly int ZTestModeId = ShaderPropertyId.GetOrCreate("_ZTestMode");
    private static readonly int ZWriteModeId = ShaderPropertyId.GetOrCreate("_ZWriteMode");
    private static readonly int CullModeId = ShaderPropertyId.GetOrCreate("_CullMode");

    // Mirrors Waylith MaterialBindingCache.ResolvePipelineState so submission-time eligibility matches capture-time state.
    public static PipelineState ResolveEffectivePipelineState(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);
        float? blendSrc = material.TryGetFloat(BlendSrcId, out float src) ? src : null;
        float? blendDst = material.TryGetFloat(BlendDstId, out float dst) ? dst : null;
        float? zTestMode = material.TryGetFloat(ZTestModeId, out float zTest) ? zTest : null;
        float? zWriteMode = material.TryGetFloat(ZWriteModeId, out float zWrite) ? zWrite : null;
        float? cullMode = material.TryGetFloat(CullModeId, out float cull) ? cull : null;
        return ResolveEffectivePipelineState(material.Shader.PipelineState, blendSrc, blendDst, zTestMode, zWriteMode, cullMode);
    }

    public static PipelineState ResolveEffectivePipelineState(in PipelineState baseState,
        float? blendSrc, float? blendDst, float? zTestMode, float? zWriteMode, float? cullMode)
    {
        BlendState blend = baseState.Blend;
        DepthState depth = baseState.Depth;
        FaceCull cull = baseState.Cull;
        bool changed = false;

        if (blendSrc.HasValue || blendDst.HasValue)
        {
            BlendFactor src = ToBlendFactor(blendSrc.HasValue ? (int)blendSrc.Value : 5);
            BlendFactor dst = ToBlendFactor(blendDst.HasValue ? (int)blendDst.Value : 10);
            blend = new BlendState(Enabled: true, src, dst, blend.ColorOp, src, dst, blend.AlphaOp);
            changed = true;
        }
        if (zTestMode.HasValue)
        {
            int mode = (int)zTestMode.Value;
            depth = depth with { Test = mode != 0 && mode != 8 };
            changed = true;
        }
        if (zWriteMode.HasValue)
        {
            depth = depth with { Write = (int)zWriteMode.Value != 0 };
            changed = true;
        }
        if (cullMode.HasValue)
        {
            cull = ToFaceCull((int)cullMode.Value);
            changed = true;
        }

        return changed ? baseState with { Blend = blend, Depth = depth, Cull = cull } : baseState;
    }

    // Composable blends for the queue-3152 RGBA16F HDR layer:
    // - Additive (SrcAlpha|One, One, Add): composable; fixes attachment alpha to (Zero, One, Add)
    //   so scene attenuation is preserved.
    // - CoverageAlpha (AlphaBlend SrcAlpha/OneMinusSrcAlpha or Premultiplied One/OneMinusSrcAlpha, Add):
    //   fixes attachment alpha to (One, OneMinusSrcAlpha, Add) for exact Porter-Duff compositing in RimLumenComposite.
    //   Callers gate both kinds by shader identity (so SDR smoke/dust/buildings stay before LightOverlay
    //   at queue 3100) AND clamp fragment output alpha to [0,1] via ExternalHdrAlphaClampShaders.
    public static bool TryNormalizeBlendForHdrLayer(in BlendState blend, out BlendState normalized) =>
        TryNormalizeBlendForHdrLayer(in blend, out normalized, out _);

    public static bool TryNormalizeBlendForHdrLayer(in BlendState blend, out BlendState normalized, out ExternalHdrBlendKind kind)
    {
        if (!blend.Enabled || blend.ColorOp != BlendOp.Add)
        {
            normalized = default;
            kind = ExternalHdrBlendKind.None;
            return false;
        }

        if (blend.DstColor == BlendFactor.One
            && (blend.SrcColor == BlendFactor.SrcAlpha || blend.SrcColor == BlendFactor.One))
        {
            normalized = blend with { SrcAlpha = BlendFactor.Zero, DstAlpha = BlendFactor.One, AlphaOp = BlendOp.Add };
            kind = ExternalHdrBlendKind.Additive;
            return true;
        }

        if (blend.DstColor == BlendFactor.OneMinusSrcAlpha
            && (blend.SrcColor == BlendFactor.SrcAlpha || blend.SrcColor == BlendFactor.One))
        {
            normalized = blend with { SrcAlpha = BlendFactor.One, DstAlpha = BlendFactor.OneMinusSrcAlpha, AlphaOp = BlendOp.Add };
            kind = ExternalHdrBlendKind.CoverageAlpha;
            return true;
        }

        normalized = default;
        kind = ExternalHdrBlendKind.None;
        return false;
    }

    // Ensures: true iff the draw fits the queue-3152 read-only-depth HDR pass without stencil/grab/non-composable blend.
    public static bool TryAdaptPipelineState(in PipelineState state, int renderQueue, bool hasSceneGrab, out PipelineState adapted) =>
        TryAdaptPipelineState(in state, renderQueue, hasSceneGrab, out adapted, out _);

    public static bool TryAdaptPipelineState(in PipelineState state, int renderQueue, bool hasSceneGrab,
        out PipelineState adapted, out ExternalHdrBlendKind blendKind)
    {
        if (renderQueue <= MinRenderQueueExclusive || renderQueue >= MaxRenderQueueExclusive
            || hasSceneGrab || state.Depth.Write || state.Stencil.Enabled || !state.ColorWrite
            || !TryNormalizeBlendForHdrLayer(state.Blend, out BlendState normalizedBlend, out blendKind))
        {
            adapted = default;
            blendKind = ExternalHdrBlendKind.None;
            return false;
        }

        adapted = state with { Blend = normalizedBlend };
        return true;
    }

    public static bool TryResolveShaderForBlendSpace(Shader sourceShader, LumenColorBlendSpace blendSpace, out Shader targetShader)
    {
        ArgumentNullException.ThrowIfNull(sourceShader);
        return TryResolveShaderForBlendSpace(
            sourceShader,
            sourceShader.Key,
            sourceShader.HasErrors,
            sourceShader.SceneGrab != null,
            blendSpace,
            static key => ShaderRegistry.TryResolve(key, out Shader? candidate) ? candidate : null,
            static candidate => candidate.Key,
            (source, candidate) => !candidate.HasErrors
                && candidate.SceneGrab == null
                && ReferenceEquals(candidate.MaterialLayout, source.MaterialLayout)
                && ReferenceEquals(candidate.VertexInputLayout, source.VertexInputLayout),
            out targetShader);
    }

    // Pure generic form for offline contract tests without invoking Waylith GPU/shader loaders.
    internal static bool TryResolveShaderForBlendSpace<TShader>(
        TShader sourceShader,
        string? sourceKey,
        bool sourceHasErrors,
        bool sourceHasSceneGrab,
        LumenColorBlendSpace blendSpace,
        Func<string, TShader?> resolveByKey,
        Func<TShader, string?> keyOf,
        Func<TShader, TShader, bool> isCompatible,
        out TShader targetShader) where TShader : class
    {
        if (sourceHasErrors || sourceHasSceneGrab)
        {
            targetShader = null!;
            return false;
        }

        if (blendSpace == LumenColorBlendSpace.Legacy)
        {
            targetShader = sourceShader;
            return true;
        }

        if (blendSpace != LumenColorBlendSpace.Linear || string.IsNullOrEmpty(sourceKey))
        {
            targetShader = null!;
            return false;
        }

        string expectedKey = GetKeyInfo(sourceKey).LinearTargetKey;
        TShader? candidate = resolveByKey(expectedKey);
        // ShaderRegistry.TryResolve falls back to NormalizePath (which truncates at '.'); require exact key match.
        if (candidate is null
            || !string.Equals(keyOf(candidate), expectedKey, StringComparison.Ordinal)
            || !isCompatible(sourceShader, candidate))
        {
            targetShader = null!;
            return false;
        }

        targetShader = candidate;
        return true;
    }

    // RimUnity v0.1.25 (2e29802), 1.7/Defs/RimUnityShaders.xml and RimUnityStandardParticles.xml.
    // Nivarian 1.7/Data/RimUnity/catalog.json names each original shader and its exported variants.
    // CustomShaderLoader.Wrap publishes sourceName on the wrapper; the engine key identifies compiled variants.
    private static readonly HashSet<string> EmissiveShaderNames = new(StringComparer.Ordinal)
    {
        "RimUnity_ParticleAdditive", "RimUnity_ParticleAdditiveBack", "RimUnity_WorldParticleAdditive",
        "RimUnity_StandardParticle4", "RimUnity_StandardParticle4Back",
        "Cartoon FX/Remaster/Particle Procedural Glow", "Nivarian/Particles/Masked HDR",
        "Nivarian/ContinuumBeacon/Erosion", "Nivarian/OrbitalBomb/AdditiveGlow", "Nivarian/TrailBloom",
        "Nivarian/TrailBloomWithNoise", "Nivarian/AdvancedBeam", "Nivarian/PolyVic Seam Rays",
        "Nivarian/PolyVic GPU Rays", "Nivarian/ZeroReturnField", "Nivarian/Hologram", "Nivarian/DigitalRain_BIRP",
        "AntManShader/Particles/DisturbanceAddMask", "Custom/LaserBeam_Holographic", "CooGee/Shield/Particle",
        "EGA/Particles/FireSphere", "Custom/StarPortal", "Vefects/SH_Vefects_VFX_BIRP_Trail_New",
        // Mixed shader families: only catalog variants authored with additive RGB and read-only depth.
        // Cartoon FX/Remaster/Particle Ubershader
        "RimUnity_Custom_3a4e7e8ad68c797f974dc994161541649646acb42b26bcaf01538121d6c230ff_ce08563f59025aee57bb1f9ca3285508bdeb3717494121515fa478699e967c68",
        "RimUnity_Custom_4f51ee17dcbdf407da9bfa1151a458c8913653240b647241f2881f5a3ca07779_a660e66f3ef06d4e79b17682a752424faee0f0bcb43c18c8924f64f75772601a",
        "RimUnity_Custom_5f881e7142a86499a77fd71a354fa9fee108600b9bfd0fa42fe0dc73be4ff716_66e62ba839f4fdefec34cbeb302f12c7bcaf3487e73e98d364ac4702c9fa452b",
        "RimUnity_Custom_7c9d782f27e059cd787ceb94199a316d8b1df0c0eb07d7237bab3883084c09e2_23b5808479cfe8033fb312b6692f1d32cfd63fa06aa98f027e4d3eae8dece070",
        "RimUnity_Custom_84b0e855d1681aa2b23c56442ab04074f87bc50fa7cf3b17f588907873e516e5_d30b0ee8cf6159848c54c525b3a6d2eadb93f3e73af86534c10cac4a6f818478",
        "RimUnity_Custom_a4270b82ff95e5aab1c5d3dfcf696b53391d546a31296e525b05e2f1374be9f7_5c7a607706317e2d13661eb328705a2d1959dd954d5a055fc350884b76969811",
        "RimUnity_Custom_ac87119be90714f7951e50863b2d4e9266d69927268b6718b9c00e9d0b504d3d_5fc8725ad9cb4d113a102b17383cadb036978a499dbb321215e25e66329c9042",
        "RimUnity_Custom_d89e502e8c67497a250b0d9e5483d0cf39f8ea44874ba35b87b09e7a8cb4c89e_c4699641801c2d82c0d13d3730f541053102abdbde41ab6ffc783a2dc89c9dfd",
        "RimUnity_Custom_db8d82a0a7010764886fa924a062e1260673afd8edca833f04b7f8e02bd3b41d_beb85d5dee1f84f83b62c49c3a0d0867f270cd45f839130b564cd905bf2eee18",
        "RimUnity_Custom_eb291a4a07efd219de02874b97d99d197204bb56282280af2274e080438dd055_92377325575feb80c259d5a96cc8431976710ed67f5d6a1af150a3f4ace648d3",
        "RimUnity_Custom_eb291a4a07efd219de02874b97d99d197204bb56282280af2274e080438dd055_c4e9b698418df2dbd00a26bb8fcda5f9f31c4bf8bc9d1f81a91e3860e2259878",
        "RimUnity_Custom_f180d0c5cfac954586dedef5ec743a4ec527afc4879e748c525748e19ad9480e_fa23455040908a8a17d79e7f4333b049bebd0d9fe187e7ada43d37615643e352",
        // Nivarian/3D Print Advanced
        "RimUnity_Custom_77263f4d1492c2578f2c8425be7e0b864cba926b37337c7dc66c29d9c8618e22_7dcd9227c06ffe1031307d7e412a5a3904a12fe9cc8ba9f09d90f22d1e361b3b",
        // Nivarian/Effects/ProceduralTechRingAdvanced
        "RimUnity_Custom_4a9538a51376368d7aefbcf07e0f773b843d010d4c1320119fba7b8eac9d0f91_62ce6ceea0d3243a2dd3aec7975393ded0084d496726d555095f10c55125a8f3",
    };

    internal sealed record ShaderKeyInfo(string BaseKey, string LinearTargetKey, bool IsStaticEmissive, bool HasSuffix);

    private const int MaxCachedKeys = 4096;
    private static readonly ConcurrentDictionary<string, ShaderKeyInfo> KeyCache = new(StringComparer.Ordinal);

    // Pure immutable key metadata: normalized base key, exact _HdrOutput key, and static whitelist hit.
    // Never caches mutable Shader objects, layouts, error state or bindings (Shader.ReloadLayouts / ShaderRegistry.Register).
    internal static ShaderKeyInfo GetKeyInfo(string key)
    {
        if (KeyCache.TryGetValue(key, out ShaderKeyInfo? cached)) return cached;
        ShaderKeyInfo computed = ComputeKeyInfo(key);
        if (KeyCache.Count < MaxCachedKeys) KeyCache.TryAdd(key, computed);
        return computed;
    }

    private static ShaderKeyInfo ComputeKeyInfo(string key)
    {
        string baseKey = key;
        if (baseKey.EndsWith(LinearShaderKeySuffix, StringComparison.Ordinal)) baseKey = baseKey[..^LinearShaderKeySuffix.Length];
        if (baseKey.EndsWith("_GpuSkin", StringComparison.Ordinal)) baseKey = baseKey[..^"_GpuSkin".Length];
        if (baseKey.EndsWith("_Linear", StringComparison.Ordinal)) baseKey = baseKey[..^"_Linear".Length];
        return new ShaderKeyInfo(
            baseKey,
            key + LinearShaderKeySuffix,
            EmissiveShaderNames.Contains(baseKey),
            !ReferenceEquals(baseKey, key));
    }

    internal static bool IsEmissiveShader(Shader shader)
    {
        ShaderKeyInfo info = GetKeyInfo(shader.Key);
        if (info.IsStaticEmissive) return true;
        if (info.HasSuffix)
        {
            if (!ShaderRegistry.TryResolve(info.BaseKey, out Shader? original) || original.Key != info.BaseKey) return false;
            shader = original;
        }
        return EmissiveShaderNames.Contains(global::RimUnity.Shader.FromEngineShader(info.BaseKey, shader).name);
    }

    internal static bool IsMaterialEligible(Material material, LumenColorBlendSpace blendSpace)
    {
        if (material.IsDeferredMaterial || !IsEmissiveShader(material.Shader)) return false;
        PipelineState state = ResolveEffectivePipelineState(material);
        return TryAdaptPipelineState(in state, material.RenderQueue, material.Shader.SceneGrab != null, out _, out var blendKind)
            && TrySelectShader(material.Shader, blendSpace, blendKind,
                material.KeywordKeyId != 0 || !string.IsNullOrEmpty(UnityEngine.Shader.GlobalKeywordKey), out _);
    }

    internal static bool TrySelectShader(Shader shader, LumenColorBlendSpace blendSpace, ExternalHdrBlendKind kind,
        bool usesVariant, out Shader target)
    {
        if (kind == ExternalHdrBlendKind.Additive)
        {
            if (TryResolveShaderForBlendSpace(shader, blendSpace, out target)) return true;
            ExternalHdrAlphaClampShaders.LogFailureOnce(shader.Key, blendSpace, "Compatible output shader is unavailable.");
            return false;
        }
        if (usesVariant && shader.PrecompiledStages is null)
        {
            ExternalHdrAlphaClampShaders.LogFailureOnce(shader.Key, blendSpace, "Dynamic keyword, instancing or MRT variant has no fixed shader stages.");
            target = null!;
            return false;
        }
        return ExternalHdrAlphaClampShaders.TryResolve(shader, blendSpace, out target);
    }

    private static FaceCull ToFaceCull(int unityCullMode) => unityCullMode switch
    {
        1 => FaceCull.Front,
        2 => FaceCull.Back,
        _ => FaceCull.None,
    };

    private static BlendFactor ToBlendFactor(int unityBlendMode) => unityBlendMode switch
    {
        0 => BlendFactor.Zero,
        1 => BlendFactor.One,
        2 => BlendFactor.DstColor,
        3 => BlendFactor.SrcColor,
        4 => BlendFactor.OneMinusDstColor,
        5 => BlendFactor.SrcAlpha,
        6 => BlendFactor.OneMinusSrcColor,
        10 => BlendFactor.OneMinusSrcAlpha,
        _ => BlendFactor.One,
    };
}
