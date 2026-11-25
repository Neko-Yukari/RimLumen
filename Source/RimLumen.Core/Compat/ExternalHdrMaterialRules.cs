using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using RimLumen.Core.Contracts;
using RimLumen.Core.Rendering.Logic;
using Waylith.Rendering;

namespace RimLumen.Core.Compat;

public enum ExternalHdrBlendKind
{
    None = 0,
    Additive = 1,
    CoverageAlpha = 2,
}

// Eligibility, effective pipeline-state resolution, HDR-emissive detection, coverage-alpha normalization and
// color-space shader selection for third-party world draws routed into RimLumen's HDR layer.
public static class ExternalHdrMaterialRules
{
    public const int MinRenderQueueExclusive = 2500;
    public const int MaxRenderQueueExclusive = 3152;
    public const string LinearShaderKeySuffix = "_HdrOutput";
    public const float HdrValueThreshold = 1.001f;

    private static readonly int BlendSrcId = ShaderPropertyId.GetOrCreate("_BlendSrc");
    private static readonly int BlendDstId = ShaderPropertyId.GetOrCreate("_BlendDst");
    private static readonly int ZTestModeId = ShaderPropertyId.GetOrCreate("_ZTestMode");
    private static readonly int ZWriteModeId = ShaderPropertyId.GetOrCreate("_ZWriteMode");
    private static readonly int CullModeId = ShaderPropertyId.GetOrCreate("_CullMode");

    // Explicit allowlist of Unity / third-party VFX float uniforms that scale emitted RGB brightness above 1.0.
    // Avoids fuzzy substring matching so non-emissive scalars (_GlowMax, _MaxValue, _DistortionIntensity,
    // _TonemapStrength, _FresnelPower, _ScanIntensity, _Frequency, etc.) never trigger false HDR takeover.
    private static readonly HashSet<string> HdrBrightnessFloatProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "_HdrMultiply",
        "_HdrMultiplier",
        "_HdrIntensity",
        "_HdrBoost",
        "_HdrScale",
        "_HdrPower",
        "_Emission",
        "_EmissionIntensity",
        "_EmissionPower",
        "_EmissionStrength",
        "_EmissionScale",
        "_EmissionMultiplier",
        "_EmissionBoost",
        "_Emissive",
        "_EmissiveIntensity",
        "_EmissivePower",
        "_EmissiveStrength",
        "_EmissiveScale",
        "_EmissiveMultiplier",
        "_EmissiveBoost",
        "_Brightness",
        "_GlobalBrightness",
        "_GlowIntensity",
        "_GlowStrength",
        "_GlowEmission",
        "_GlowBoost",
        "_GlowPower",
        "_GlowMultiplier",
        "_BloomIntensity",
        "_BloomScale",
        "_BloomPower",
        "_BloomMultiplier",
        "_ColorIntensity",
        "_ColorBoost",
        "_ColorMultiplier",
    };

    private readonly record struct ColorSlotPlan(
        int PropertyId,
        int Offset,
        MaterialUniformType Type,
        bool IsInUniformBlock,
        bool HasAuthoredDefault,
        Vector4 DefaultValue,
        bool RequiresEmissionEnabled);

    private readonly record struct FloatSlotPlan(
        int PropertyId,
        int Offset,
        MaterialUniformType Type,
        bool IsInUniformBlock,
        bool HasAuthoredDefault,
        float DefaultValue);

    private sealed class HdrLayoutPlan(
        ColorSlotPlan[] colorSlots,
        FloatSlotPlan[] hdrFloatSlots,
        FloatSlotPlan? emissionEnabledSlot)
    {
        public static readonly HdrLayoutPlan Empty = new([], [], null);
        public readonly ColorSlotPlan[] ColorSlots = colorSlots;
        public readonly FloatSlotPlan[] HdrFloatSlots = hdrFloatSlots;
        public readonly FloatSlotPlan? EmissionEnabledSlot = emissionEnabledSlot;
    }

    private static readonly ConditionalWeakTable<MaterialLayout, HdrLayoutPlan> LayoutPlanCache = new();
    [ThreadStatic] private static DrawProperty[]? overrideScratch;

    public static bool IsHdrBrightnessMultiplierProperty(string? propertyName) =>
        !string.IsNullOrEmpty(propertyName) && HdrBrightnessFloatProperties.Contains(propertyName);

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
    // - Additive (SrcAlpha|One, One, Add): accepted unconditionally; fixes attachment alpha to (Zero, One, Add)
    //   so scene attenuation is preserved.
    // - CoverageAlpha (AlphaBlend SrcAlpha/OneMinusSrcAlpha or Premultiplied One/OneMinusSrcAlpha, Add):
    //   fixes attachment alpha to (One, OneMinusSrcAlpha, Add) for exact Porter-Duff compositing in RimLumenComposite.
    //   Callers MUST gate CoverageAlpha on IsHdrEmissive (so SDR smoke/dust/buildings stay before LightOverlay
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
        if (renderQueue <= MinRenderQueueExclusive || renderQueue >= GlowGroups.Range(GlowGroup.Early).MaxExclusive
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

        string expectedKey = sourceKey + LinearShaderKeySuffix;
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

    // Ensures: true iff the material (with optional per-draw property overrides) has an active declared @color uniform
    //          with RGB > HdrValueThreshold or a known declared HDR emission/brightness float uniform > HdrValueThreshold.
    //          Priority mirrors Waylith MaterialBindingResolver: overrides > material properties > authored default.
    public static bool IsHdrEmissiveMaterial(Material material, IDrawPropertyProvider? propertyOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(material);
        MaterialLayout layout = material.Shader.MaterialLayout;
        if (propertyOverrides is null || propertyOverrides.PropertyCount <= 0)
            return IsHdrEmissive(layout, material.Properties.Span, ReadOnlySpan<DrawProperty>.Empty);

        int count = propertyOverrides.PropertyCount;
        DrawProperty[] scratch = overrideScratch is { Length: >= 1 } existing && existing.Length >= count
            ? existing
            : (overrideScratch = new DrawProperty[System.Math.Max(count, 32)]);
        int copied = 0;
        try
        {
            copied = propertyOverrides.CopyTo(scratch.AsSpan(0, count));
            return IsHdrEmissive(layout, material.Properties.Span, scratch.AsSpan(0, copied));
        }
        finally
        {
            // Clear managed texture references immediately so scratch never pins released textures.
            Array.Clear(scratch, 0, copied > 0 ? copied : System.Math.Min(count, scratch.Length));
        }
    }

    internal static bool IsHdrEmissive(MaterialLayout layout, ReadOnlySpan<DrawProperty> materialProperties) =>
        IsHdrEmissive(layout, materialProperties, ReadOnlySpan<DrawProperty>.Empty);

    internal static bool IsHdrEmissive(
        MaterialLayout layout,
        ReadOnlySpan<DrawProperty> materialProperties,
        ReadOnlySpan<DrawProperty> overrideProperties)
    {
        ArgumentNullException.ThrowIfNull(layout);
        HdrLayoutPlan plan = LayoutPlanCache.GetValue(layout, static l => BuildHdrLayoutPlan(l));
        if (plan.ColorSlots.Length == 0 && plan.HdrFloatSlots.Length == 0)
            return false;

        bool? emissionEnabled = null;
        ColorSlotPlan[] colorSlots = plan.ColorSlots;
        for (int i = 0; i < colorSlots.Length; i++)
        {
            ColorSlotPlan slot = colorSlots[i];
            if (slot.RequiresEmissionEnabled && plan.EmissionEnabledSlot is { } toggle)
            {
                emissionEnabled ??= ResolveFloat(overrideProperties, materialProperties, in toggle, defaultWhenUnset: 0f) > 0.5f;
                if (!emissionEnabled.Value)
                    continue;
            }

            if (TryResolveColor(overrideProperties, materialProperties, in slot, out Vector4 color) && IsHdrRgb(in color))
                return true;
        }

        FloatSlotPlan[] floatSlots = plan.HdrFloatSlots;
        for (int i = 0; i < floatSlots.Length; i++)
        {
            if (TryResolveFloat(overrideProperties, materialProperties, in floatSlots[i], out float value)
                && float.IsFinite(value)
                && value > HdrValueThreshold)
            {
                return true;
            }
        }

        return false;
    }

    // Capture-time O(1) HDR emissivity check over Waylith's final resolved MaterialUniformBytes (which already
    // merged MaterialPropertyBlock overrides, material properties, GlobalShaderState and authored defaults).
    internal static bool IsHdrEmissive(MaterialLayout layout, in MaterialUniformBytes resolvedUniforms)
    {
        ArgumentNullException.ThrowIfNull(layout);
        HdrLayoutPlan plan = LayoutPlanCache.GetValue(layout, static l => BuildHdrLayoutPlan(l));
        if (plan.ColorSlots.Length == 0 && plan.HdrFloatSlots.Length == 0)
            return false;

        bool? emissionEnabled = null;
        ColorSlotPlan[] colorSlots = plan.ColorSlots;
        for (int i = 0; i < colorSlots.Length; i++)
        {
            ColorSlotPlan slot = colorSlots[i];
            if (slot.IsInUniformBlock)
                continue;

            if (slot.RequiresEmissionEnabled && plan.EmissionEnabledSlot is { IsInUniformBlock: false } toggle)
            {
                emissionEnabled ??= TryReadResolvedScalar(in resolvedUniforms, in toggle, out float toggleVal) && toggleVal > 0.5f;
                if (!emissionEnabled.Value)
                    continue;
            }

            if (TryReadResolvedColor(in resolvedUniforms, in slot, out Vector4 color) && IsHdrRgb(in color))
                return true;
        }

        FloatSlotPlan[] floatSlots = plan.HdrFloatSlots;
        for (int i = 0; i < floatSlots.Length; i++)
        {
            FloatSlotPlan slot = floatSlots[i];
            if (slot.IsInUniformBlock)
                continue;

            if (TryReadResolvedScalar(in resolvedUniforms, in slot, out float value)
                && float.IsFinite(value)
                && value > HdrValueThreshold)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsMaterialEligible(Material material, LumenColorBlendSpace blendSpace) =>
        IsMaterialEligible(material, propertyOverrides: null, blendSpace);

    public static bool IsMaterialEligible(Material material, IDrawPropertyProvider? propertyOverrides, LumenColorBlendSpace blendSpace)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (material.IsDeferredMaterial) return false;
        Shader shader = material.Shader;
        PipelineState effective = ResolveEffectivePipelineState(material);
        if (!TryAdaptPipelineState(in effective, material.RenderQueue, shader.SceneGrab != null, out _, out ExternalHdrBlendKind blendKind))
            return false;

        if (blendKind == ExternalHdrBlendKind.Additive)
            return TryResolveShaderForBlendSpace(shader, blendSpace, out _);

        if (!IsHdrEmissiveMaterial(material, propertyOverrides))
            return false;
        if (shader.PrecompiledStages is null
            && (material.KeywordKeyId != 0 || !string.IsNullOrEmpty(UnityEngine.Shader.GlobalKeywordKey)))
            return false;
        return ExternalHdrAlphaClampShaders.TryResolve(shader, blendSpace, out _);
    }

    private static HdrLayoutPlan BuildHdrLayoutPlan(MaterialLayout layout)
    {
        List<ColorSlotPlan>? colors = null;
        List<FloatSlotPlan>? floats = null;
        FloatSlotPlan? emissionEnabledSlot = null;

        CollectSlots(layout.UniformSlots, isInUniformBlock: false, ref colors, ref floats, ref emissionEnabledSlot);
        MaterialUniformBlockSlot[] blocks = layout.UniformBlocks;
        for (int b = 0; b < blocks.Length; b++)
            CollectSlots(blocks[b].Members, isInUniformBlock: true, ref colors, ref floats, ref emissionEnabledSlot);

        if (colors is null && floats is null)
            return HdrLayoutPlan.Empty;

        return new HdrLayoutPlan(
            colors is null ? [] : colors.ToArray(),
            floats is null ? [] : floats.ToArray(),
            emissionEnabledSlot);
    }

    private static void CollectSlots(MaterialUniformSlot[] slots, bool isInUniformBlock,
        ref List<ColorSlotPlan>? colors, ref List<FloatSlotPlan>? floats, ref FloatSlotPlan? emissionEnabledSlot)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            MaterialUniformSlot slot = slots[i];
            if (slot.IsColor && slot.Type is MaterialUniformType.Vector4 or MaterialUniformType.Vector3)
            {
                colors ??= new List<ColorSlotPlan>(2);
                bool requiresToggle = string.Equals(slot.PropertyName, "_EmissionColor", StringComparison.OrdinalIgnoreCase);
                colors.Add(new ColorSlotPlan(slot.PropertyId, slot.Offset, slot.Type, isInUniformBlock,
                    slot.HasAuthoredDefault, slot.DefaultValue.Vector4, requiresToggle));
            }
            else if (slot.Type == MaterialUniformType.Float && IsHdrBrightnessMultiplierProperty(slot.PropertyName))
            {
                floats ??= new List<FloatSlotPlan>(2);
                floats.Add(new FloatSlotPlan(slot.PropertyId, slot.Offset, slot.Type, isInUniformBlock,
                    slot.HasAuthoredDefault, slot.DefaultValue.Float));
            }
            else if (slot.Type is MaterialUniformType.Float or MaterialUniformType.Int
                     && string.Equals(slot.PropertyName, "_EmissionEnabled", StringComparison.OrdinalIgnoreCase))
            {
                float def = slot.Type == MaterialUniformType.Int ? slot.DefaultValue.Int : slot.DefaultValue.Float;
                emissionEnabledSlot = new FloatSlotPlan(slot.PropertyId, slot.Offset, slot.Type, isInUniformBlock,
                    slot.HasAuthoredDefault, def);
            }
        }
    }

    private static bool TryReadResolvedColor(in MaterialUniformBytes uniforms, in ColorSlotPlan slot, out Vector4 value)
    {
        if (slot.Type == MaterialUniformType.Vector4)
        {
            if (slot.Offset < 0 || slot.Offset + 16 > MaterialUniformBytes.MaxBytes)
            {
                value = default;
                return false;
            }
            value = uniforms.ReadVector4(slot.Offset);
            return true;
        }
        if (slot.Type == MaterialUniformType.Vector3)
        {
            if (slot.Offset < 0 || slot.Offset + 12 > MaterialUniformBytes.MaxBytes)
            {
                value = default;
                return false;
            }
            value = new Vector4(
                uniforms.ReadFloat(slot.Offset),
                uniforms.ReadFloat(slot.Offset + 4),
                uniforms.ReadFloat(slot.Offset + 8),
                1f);
            return true;
        }
        value = default;
        return false;
    }

    private static bool TryReadResolvedScalar(in MaterialUniformBytes uniforms, in FloatSlotPlan slot, out float value)
    {
        if (slot.Offset < 0 || slot.Offset + 4 > MaterialUniformBytes.MaxBytes)
        {
            value = 0f;
            return false;
        }
        if (slot.Type == MaterialUniformType.Float)
        {
            value = uniforms.ReadFloat(slot.Offset);
            return true;
        }
        if (slot.Type == MaterialUniformType.Int)
        {
            value = uniforms.ReadInt(slot.Offset);
            return true;
        }
        value = 0f;
        return false;
    }

    private static bool TryResolveColor(ReadOnlySpan<DrawProperty> overrides, ReadOnlySpan<DrawProperty> materialProperties,
        in ColorSlotPlan slot, out Vector4 value)
    {
        if (TryFindColor(overrides, slot.PropertyId, out value)
            || TryFindColor(materialProperties, slot.PropertyId, out value))
        {
            return true;
        }

        if (slot.HasAuthoredDefault)
        {
            value = slot.DefaultValue;
            return true;
        }

        if (GlobalShaderState.TryGetVector(slot.PropertyId, out value))
            return true;

        value = default;
        return false;
    }

    private static bool TryFindColor(ReadOnlySpan<DrawProperty> properties, int propertyId, out Vector4 value)
    {
        for (int i = properties.Length - 1; i >= 0; i--)
        {
            DrawProperty prop = properties[i];
            if (prop.PropertyId == propertyId && prop.Type is DrawPropertyType.Color or DrawPropertyType.Vector)
            {
                value = prop.VectorValue;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static bool TryResolveFloat(ReadOnlySpan<DrawProperty> overrides, ReadOnlySpan<DrawProperty> materialProperties,
        in FloatSlotPlan slot, out float value)
    {
        if (TryFindScalar(overrides, slot.PropertyId, slot.Type, out value)
            || TryFindScalar(materialProperties, slot.PropertyId, slot.Type, out value))
        {
            return true;
        }

        if (slot.HasAuthoredDefault)
        {
            value = slot.DefaultValue;
            return true;
        }

        if (slot.Type == MaterialUniformType.Float && GlobalShaderState.TryGetFloat(slot.PropertyId, out value))
            return true;

        value = 0f;
        return false;
    }

    private static bool TryFindScalar(ReadOnlySpan<DrawProperty> properties, int propertyId, MaterialUniformType slotType, out float value)
    {
        for (int i = properties.Length - 1; i >= 0; i--)
        {
            DrawProperty prop = properties[i];
            if (prop.PropertyId != propertyId)
                continue;
            if (slotType == MaterialUniformType.Float && prop.Type == DrawPropertyType.Float)
            {
                value = prop.FloatValue;
                return true;
            }
            if (slotType == MaterialUniformType.Int && prop.Type == DrawPropertyType.Int)
            {
                value = prop.IntValue;
                return true;
            }
        }
        value = 0f;
        return false;
    }

    private static float ResolveFloat(ReadOnlySpan<DrawProperty> overrides, ReadOnlySpan<DrawProperty> materialProperties,
        in FloatSlotPlan slot, float defaultWhenUnset) =>
        TryResolveFloat(overrides, materialProperties, in slot, out float value) ? value : defaultWhenUnset;

    private static bool IsHdrRgb(in Vector4 color) =>
        float.IsFinite(color.X)
        && float.IsFinite(color.Y)
        && float.IsFinite(color.Z)
        && (color.X > HdrValueThreshold || color.Y > HdrValueThreshold || color.Z > HdrValueThreshold);

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
