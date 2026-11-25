using System;
using System.Collections.Generic;
using System.Numerics;
using System.IO;
using RimLumen.Core.Contracts;
using Waylith.Rendering;

namespace RimLumen.Adapter.RimUnity.Tests;

public static class ExternalHdrRulesSuite
{
    public static void AdditiveBlendsNormalizeAttachmentAlphaToPreserveSceneAttenuation()
    {
        // Additive variants must keep destination coverage alpha untouched (Zero, One, Add) so composite does not darken the scene.
        BlendState[] additiveInputs =
        [
            new(true, BlendFactor.SrcAlpha, BlendFactor.One, BlendOp.Add, BlendFactor.One, BlendFactor.One, BlendOp.Add),
            new(true, BlendFactor.SrcAlpha, BlendFactor.One, BlendOp.Add, BlendFactor.SrcAlpha, BlendFactor.One, BlendOp.Add),
            new(true, BlendFactor.SrcAlpha, BlendFactor.One, BlendOp.Add, BlendFactor.Zero, BlendFactor.One, BlendOp.Add),
            new(true, BlendFactor.One, BlendFactor.One, BlendOp.Add, BlendFactor.One, BlendFactor.One, BlendOp.Add),
            new(true, BlendFactor.One, BlendFactor.One, BlendOp.Add, BlendFactor.Zero, BlendFactor.One, BlendOp.Max),
        ];
        foreach (BlendState input in additiveInputs)
        {
            Require(ExternalHdrMaterialRules.TryNormalizeBlendForHdrLayer(in input, out BlendState normalized, out ExternalHdrBlendKind kind),
                $"Expected additive blend {input} to normalize.");
            Equal(ExternalHdrBlendKind.Additive, kind);
            Equal(new BlendState(true, input.SrcColor, BlendFactor.One, BlendOp.Add, BlendFactor.Zero, BlendFactor.One, BlendOp.Add),
                normalized);
        }
    }

    public static void CoverageAlphaBlendsNormalizeAttachmentAlphaForExactPorterDuffComposite()
    {
        // AlphaBlend (SrcAlpha, OneMinusSrcAlpha) and Premultiplied (One, OneMinusSrcAlpha) normalize attachment alpha
        // to (One, OneMinusSrcAlpha, Add) so RimLumenComposite computes exact hdr.rgb + bloom + (1 - hdr.a) * scene.
        BlendState[] coverageInputs =
        [
            BlendState.Alpha,
            new(true, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, BlendOp.Add, BlendFactor.One, BlendFactor.One, BlendOp.Add),
            new(true, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, BlendOp.Add, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, BlendOp.Add),
            new(true, BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add, BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add),
            new(true, BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add, BlendFactor.Zero, BlendFactor.One, BlendOp.Max),
        ];
        foreach (BlendState input in coverageInputs)
        {
            Require(ExternalHdrMaterialRules.TryNormalizeBlendForHdrLayer(in input, out BlendState normalized, out ExternalHdrBlendKind kind),
                $"Expected coverage-alpha blend {input} to normalize.");
            Equal(ExternalHdrBlendKind.CoverageAlpha, kind);
            Equal(new BlendState(true, input.SrcColor, BlendFactor.OneMinusSrcAlpha, BlendOp.Add,
                    BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add),
                normalized);
        }
    }

    public static void NonComposableBlendsAreRejected()
    {
        BlendState[] rejected =
        [
            BlendState.Opaque,
            BlendState.MultiplyDestination,
            new(true, BlendFactor.DstColor, BlendFactor.Zero, BlendOp.Add, BlendFactor.One, BlendFactor.Zero, BlendOp.Add),
            new(true, BlendFactor.OneMinusDstColor, BlendFactor.One, BlendOp.Add, BlendFactor.Zero, BlendFactor.One, BlendOp.Add),
            new(true, BlendFactor.One, BlendFactor.Zero, BlendOp.Add, BlendFactor.One, BlendFactor.Zero, BlendOp.Add),
            new(true, BlendFactor.SrcColor, BlendFactor.OneMinusSrcAlpha, BlendOp.Add, BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add),
            new(true, BlendFactor.SrcAlpha, BlendFactor.One, BlendOp.ReverseSubtract, BlendFactor.Zero, BlendFactor.One, BlendOp.Add),
            new(true, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, BlendOp.ReverseSubtract, BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add),
            new(true, BlendFactor.SrcAlpha, BlendFactor.One, BlendOp.Subtract, BlendFactor.Zero, BlendFactor.One, BlendOp.Add),
            new(true, BlendFactor.SrcAlpha, BlendFactor.One, BlendOp.Min, BlendFactor.Zero, BlendFactor.One, BlendOp.Add),
            new(true, BlendFactor.SrcAlpha, BlendFactor.One, BlendOp.Max, BlendFactor.Zero, BlendFactor.One, BlendOp.Add),
        ];
        foreach (BlendState input in rejected)
        {
            Require(!ExternalHdrMaterialRules.TryNormalizeBlendForHdrLayer(in input, out _, out ExternalHdrBlendKind kind),
                $"Expected blend {input} to be rejected.");
            Equal(ExternalHdrBlendKind.None, kind);
        }
    }

    public static void PipelineEligibilityGuardsDepthWriteStencilColorWriteSceneGrabAndQueueWindow()
    {
        var depthBias = new DepthBiasState(-1f, -2f);
        var validAdditive = new PipelineState(
            new BlendState(true, BlendFactor.SrcAlpha, BlendFactor.One, BlendOp.Add, BlendFactor.One, BlendFactor.One, BlendOp.Add),
            new DepthState(Test: true, Write: false),
            FaceCull.Back,
            default(StencilState),
            ColorWrite: true,
            depthBias);

        Require(ExternalHdrMaterialRules.TryAdaptPipelineState(in validAdditive, 2900, hasSceneGrab: false,
                out PipelineState adapted, out ExternalHdrBlendKind kind),
            "Expected queue 2900 transparent non-depth-writing additive draw to be eligible.");
        Equal(ExternalHdrBlendKind.Additive, kind);
        Equal(new BlendState(true, BlendFactor.SrcAlpha, BlendFactor.One, BlendOp.Add, BlendFactor.Zero, BlendFactor.One, BlendOp.Add), adapted.Blend);
        Equal(new DepthState(Test: true, Write: false), adapted.Depth);
        Equal(FaceCull.Back, adapted.Cull);
        Equal(depthBias, adapted.DepthBias);

        var validAlpha = validAdditive with { Blend = BlendState.Alpha };
        Require(ExternalHdrMaterialRules.TryAdaptPipelineState(in validAlpha, 3000, hasSceneGrab: false,
                out PipelineState adaptedAlpha, out ExternalHdrBlendKind alphaKind),
            "Expected queue 3000 transparent non-depth-writing alpha-blend draw to adapt.");
        Equal(ExternalHdrBlendKind.CoverageAlpha, alphaKind);
        Equal(new BlendState(true, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, BlendOp.Add,
            BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add), adaptedAlpha.Blend);

        // Queue window boundaries: strictly inside (2500, 3152).
        Require(ExternalHdrMaterialRules.TryAdaptPipelineState(in validAdditive, 2501, false, out _), "Queue 2501 should be accepted.");
        Require(ExternalHdrMaterialRules.TryAdaptPipelineState(in validAdditive, 3151, false, out _), "Queue 3151 should be accepted.");
        Require(!ExternalHdrMaterialRules.TryAdaptPipelineState(in validAdditive, 2500, false, out _), "Queue 2500 must be rejected.");
        Require(!ExternalHdrMaterialRules.TryAdaptPipelineState(in validAdditive, 2000, false, out _), "Queue 2000 must be rejected.");
        Require(!ExternalHdrMaterialRules.TryAdaptPipelineState(in validAdditive, 3152, false, out _), "Queue 3152 must be rejected.");
        Require(!ExternalHdrMaterialRules.TryAdaptPipelineState(in validAdditive, 3170, false, out _), "Queue 3170 must be rejected.");

        // Read-only depth & no stencil / grab / colorWrite off (checked on both Additive and CoverageAlpha).
        foreach (PipelineState candidate in new[] { validAdditive, validAlpha })
        {
            var depthWrite = candidate with { Depth = new DepthState(Test: true, Write: true) };
            Require(!ExternalHdrMaterialRules.TryAdaptPipelineState(in depthWrite, 3000, false, out _), "Depth write must be rejected.");
            var stencilOn = candidate with { Stencil = new StencilState(Enabled: true, StencilCompare.Always, StencilOp.Replace, StencilOp.Keep, StencilOp.Keep, 1, 0xFF, 0xFF) };
            Require(!ExternalHdrMaterialRules.TryAdaptPipelineState(in stencilOn, 3000, false, out _), "Stencil must be rejected.");
            var noColorWrite = candidate with { ColorWrite = false };
            Require(!ExternalHdrMaterialRules.TryAdaptPipelineState(in noColorWrite, 3000, false, out _), "ColorWrite=false must be rejected.");
            Require(!ExternalHdrMaterialRules.TryAdaptPipelineState(in candidate, 3000, hasSceneGrab: true, out _), "SceneGrab must be rejected.");
        }
    }

    public static void MaterialFloatOverridesMirrorWaylithPipelineStateResolution()
    {
        var baseState = new PipelineState(BlendState.Opaque, new DepthState(Test: true, Write: true), FaceCull.Back);

        // _BlendSrc=5 (SrcAlpha) + _BlendDst=1 (One) + _ZWriteMode=0 makes an opaque shader additive and HDR-eligible.
        PipelineState additive = ExternalHdrMaterialRules.ResolveEffectivePipelineState(
            in baseState, blendSrc: 5f, blendDst: 1f, zTestMode: 8f, zWriteMode: 0f, cullMode: 0f);
        Equal(new BlendState(true, BlendFactor.SrcAlpha, BlendFactor.One, BlendOp.Add, BlendFactor.SrcAlpha, BlendFactor.One, BlendOp.Add), additive.Blend);
        Equal(new DepthState(Test: false, Write: false), additive.Depth);
        Equal(FaceCull.None, additive.Cull);
        Require(ExternalHdrMaterialRules.TryAdaptPipelineState(in additive, 3000, false, out PipelineState adapted, out ExternalHdrBlendKind addKind),
            "Overridden additive non-depth-writing material must adapt cleanly.");
        Equal(ExternalHdrBlendKind.Additive, addKind);
        Equal(BlendFactor.Zero, adapted.Blend.SrcAlpha);
        Equal(BlendFactor.One, adapted.Blend.DstAlpha);

        // Partial override: _BlendDst=1 only defaults _BlendSrc to 5 (SrcAlpha), matching MaterialBindingCache -> Additive.
        PipelineState partialDst = ExternalHdrMaterialRules.ResolveEffectivePipelineState(
            in baseState, blendSrc: null, blendDst: 1f, zTestMode: null, zWriteMode: 0f, cullMode: 1f);
        Equal(BlendFactor.SrcAlpha, partialDst.Blend.SrcColor);
        Equal(BlendFactor.One, partialDst.Blend.DstColor);
        Equal(FaceCull.Front, partialDst.Cull);
        Require(ExternalHdrMaterialRules.TryAdaptPipelineState(in partialDst, 3000, false, out _, out ExternalHdrBlendKind partialDstKind),
            "Additive partial _BlendDst=1 override should be accepted.");
        Equal(ExternalHdrBlendKind.Additive, partialDstKind);

        // Partial override: _BlendSrc=1 only defaults _BlendDst to 10 (OneMinusSrcAlpha) -> Premultiplied CoverageAlpha.
        PipelineState partialSrc = ExternalHdrMaterialRules.ResolveEffectivePipelineState(
            in baseState, blendSrc: 1f, blendDst: null, zTestMode: null, zWriteMode: 0f, cullMode: 1f);
        Equal(BlendFactor.One, partialSrc.Blend.SrcColor);
        Equal(BlendFactor.OneMinusSrcAlpha, partialSrc.Blend.DstColor);
        Require(ExternalHdrMaterialRules.TryAdaptPipelineState(in partialSrc, 3000, false, out PipelineState premulAdapted, out ExternalHdrBlendKind premulKind),
            "Premultiplied partial _BlendSrc=1 override should adapt as CoverageAlpha.");
        Equal(ExternalHdrBlendKind.CoverageAlpha, premulKind);
        Equal(BlendFactor.One, premulAdapted.Blend.SrcAlpha);
        Equal(BlendFactor.OneMinusSrcAlpha, premulAdapted.Blend.DstAlpha);

        // Explicit AlphaBlend override (_BlendSrc=5, _BlendDst=10) -> CoverageAlpha.
        PipelineState alphaOverride = ExternalHdrMaterialRules.ResolveEffectivePipelineState(
            in baseState, blendSrc: 5f, blendDst: 10f, zTestMode: null, zWriteMode: 0f, cullMode: null);
        Require(ExternalHdrMaterialRules.TryAdaptPipelineState(in alphaOverride, 3000, false, out PipelineState alphaAdapted, out ExternalHdrBlendKind alphaKind),
            "AlphaBlend float override should adapt as CoverageAlpha.");
        Equal(ExternalHdrBlendKind.CoverageAlpha, alphaKind);
        Equal(BlendFactor.One, alphaAdapted.Blend.SrcAlpha);
        Equal(BlendFactor.OneMinusSrcAlpha, alphaAdapted.Blend.DstAlpha);

        // Non-composable override (_BlendSrc=2 DstColor, _BlendDst=0 Zero) must be rejected.
        PipelineState multiply = ExternalHdrMaterialRules.ResolveEffectivePipelineState(
            in baseState, blendSrc: 2f, blendDst: 0f, zTestMode: null, zWriteMode: 0f, cullMode: null);
        Require(!ExternalHdrMaterialRules.TryAdaptPipelineState(in multiply, 3000, false, out _),
            "Multiply blend float override must be rejected.");

        // _ZWriteMode=1 on an additive shader turns on Depth.Write and must be rejected.
        var additiveBase = new PipelineState(BlendState.Additive, new DepthState(Test: true, Write: false), FaceCull.None);
        PipelineState depthWriteOverride = ExternalHdrMaterialRules.ResolveEffectivePipelineState(
            in additiveBase, blendSrc: null, blendDst: null, zTestMode: 4f, zWriteMode: 1f, cullMode: null);
        Require(depthWriteOverride.Depth.Test && depthWriteOverride.Depth.Write, "Expected ZTest=4 -> Test=true and ZWrite=1 -> Write=true.");
        Require(!ExternalHdrMaterialRules.TryAdaptPipelineState(in depthWriteOverride, 3000, false, out _),
            "_ZWriteMode=1 override must reject the material.");
    }

    public static void ColorSpaceShaderResolutionRequiresExactHdrOutputKeyInLinearMode()
    {
        var layoutA = new object();
        var layoutB = new object();
        var source = new FakeShader("Mod.Pkg.ParticleAdd", layoutA, HasErrors: false, HasSceneGrab: false);
        var exactLinear = new FakeShader("Mod.Pkg.ParticleAdd_HdrOutput", layoutA, HasErrors: false, HasSceneGrab: false);
        var truncatedAlias = new FakeShader("Mod.Pkg", layoutA, HasErrors: false, HasSceneGrab: false);
        var wrongLayout = new FakeShader("Mod.Pkg.ParticleAdd_HdrOutput", layoutB, HasErrors: false, HasSceneGrab: false);
        var brokenLinear = new FakeShader("Mod.Pkg.ParticleAdd_HdrOutput", layoutA, HasErrors: true, HasSceneGrab: false);
        var grabLinear = new FakeShader("Mod.Pkg.ParticleAdd_HdrOutput", layoutA, HasErrors: false, HasSceneGrab: true);

        static bool Compatible(FakeShader s, FakeShader c) => !c.HasErrors && !c.HasSceneGrab && ReferenceEquals(s.Layout, c.Layout);
        static FakeShader? ResolveNone(string _) => null;

        // Legacy mode keeps the encoded source shader directly without looking up _HdrOutput.
        Require(ExternalHdrMaterialRules.TryResolveShaderForBlendSpace<FakeShader>(
            source, source.Key, source.HasErrors, source.HasSceneGrab, LumenColorBlendSpace.Legacy,
            _ => throw new InvalidOperationException("Legacy mode must not query registry."),
            static s => s.Key, Compatible, out FakeShader legacyTarget), "Legacy mode should keep source shader.");
        Require(ReferenceEquals(source, legacyTarget), "Legacy target must be source shader.");

        // Linear mode accepts exact key match with compatible layout.
        var registry = new Dictionary<string, FakeShader>(StringComparer.Ordinal) { [exactLinear.Key] = exactLinear };
        Require(ExternalHdrMaterialRules.TryResolveShaderForBlendSpace<FakeShader>(
            source, source.Key, source.HasErrors, source.HasSceneGrab, LumenColorBlendSpace.Linear,
            k => registry.TryGetValue(k, out FakeShader? v) ? v : null,
            static s => s.Key, Compatible, out FakeShader linearTarget), "Linear mode should resolve exact _HdrOutput variant.");
        Require(ReferenceEquals(exactLinear, linearTarget), "Linear target must be the _HdrOutput shader.");

        // Linear mode rejects missing _HdrOutput (e.g. Gamma shader) so it is never double-gamma-encoded.
        Require(!ExternalHdrMaterialRules.TryResolveShaderForBlendSpace<FakeShader>(
            source, source.Key, source.HasErrors, source.HasSceneGrab, LumenColorBlendSpace.Linear,
            ResolveNone, static s => s.Key, Compatible, out _), "Missing _HdrOutput must be rejected in Linear mode.");

        // Linear mode rejects dotted NormalizePath fallback alias whose Key != expectedKey.
        Require(!ExternalHdrMaterialRules.TryResolveShaderForBlendSpace<FakeShader>(
            source, source.Key, source.HasErrors, source.HasSceneGrab, LumenColorBlendSpace.Linear,
            _ => truncatedAlias, static s => s.Key, Compatible, out _), "Path-truncated alias must be rejected.");

        // Linear mode rejects layout mismatch, errored shader, or sceneGrab variant.
        foreach (FakeShader bad in new[] { wrongLayout, brokenLinear, grabLinear })
            Require(!ExternalHdrMaterialRules.TryResolveShaderForBlendSpace<FakeShader>(
                source, source.Key, source.HasErrors, source.HasSceneGrab, LumenColorBlendSpace.Linear,
                _ => bad, static s => s.Key, Compatible, out _), $"Incompatible variant {bad} must be rejected.");

        // Source errors or sceneGrab fail in both Legacy and Linear.
        var erroredSource = new FakeShader("Mod.Broken", layoutA, HasErrors: true, HasSceneGrab: false);
        var grabSource = new FakeShader("Mod.Grab", layoutA, HasErrors: false, HasSceneGrab: true);
        Require(!ExternalHdrMaterialRules.TryResolveShaderForBlendSpace<FakeShader>(
            erroredSource, erroredSource.Key, erroredSource.HasErrors, erroredSource.HasSceneGrab, LumenColorBlendSpace.Legacy,
            ResolveNone, static s => s.Key, Compatible, out _), "Errored source must be rejected.");
        Require(!ExternalHdrMaterialRules.TryResolveShaderForBlendSpace<FakeShader>(
            grabSource, grabSource.Key, grabSource.HasErrors, grabSource.HasSceneGrab, LumenColorBlendSpace.Legacy,
            ResolveNone, static s => s.Key, Compatible, out _), "SceneGrab source must be rejected.");
    }

    public static void ShaderNamesDetermineEmissionAcrossMaterialValuesAndOverrides()
    {
        Material cfxrProcGlow = NamedMaterial("Cartoon FX/Remaster/Particle Procedural Glow",
            FloatSlot(0, "_GlowMax", 1f, hasDefault: true),
            FloatSlot(4, "_GlowMin", 0f, hasDefault: true),
            FloatSlot(8, "_HdrMultiply", 2f, hasDefault: true),
            FloatSlot(12, "_MaxValue", 10f, hasDefault: true));

        Equal(true, Submitted(cfxrProcGlow, [
                PropFloat("_GlowMax", 1.5f),
                PropFloat("_MaxValue", 2.0f),
                PropFloat("_HdrMultiply", 0.0f),
            ]));

        Equal(true, Submitted(cfxrProcGlow, [
                PropFloat("_GlowMax", 0.58f),
                PropFloat("_MaxValue", 2.0f),
                PropFloat("_HdrMultiply", 6.0f),
            ]));

        Equal(true, Submitted(cfxrProcGlow, ReadOnlySpan<DrawProperty>.Empty));

        Material fireSphere = NamedMaterial("EGA/Particles/FireSphere",
            ColorSlot(0, "_Color", Vector4.One, hasDefault: true),
            FloatSlot(16, "_Emission", 2f, hasDefault: true),
            FloatSlot(20, "_Frequency", 10f, hasDefault: true),
            FloatSlot(24, "_StartFrequency", 4f, hasDefault: true));

        Equal(true, Submitted(fireSphere, [PropVector("_Color", Vector4.One), PropFloat("_Emission", 12f), PropFloat("_Frequency", 10f)]));
        Equal(true, Submitted(fireSphere, [PropVector("_Color", Vector4.One), PropFloat("_Emission", 1f), PropFloat("_Frequency", 10f), PropFloat("_StartFrequency", 2f)]));

        Material vefectsTrail = NamedMaterial("Vefects/SH_Vefects_VFX_BIRP_Trail_New",
            ColorSlot(0, "_Color01", new Vector4(1, 1, 1, 0), hasDefault: true),
            VectorSlot(16, "_ColorPanSpeed", Vector4.Zero, hasDefault: true),
            VectorSlot(32, "_DissolveTex_ST", new Vector4(0, 0, 1, 1), hasDefault: true),
            FloatSlot(48, "_EmissiveIntensity", 1f, hasDefault: true),
            FloatSlot(52, "_DistortionIntensity", 1f, hasDefault: true),
            FloatSlot(56, "_MaskDistortionIntensity", 1f, hasDefault: true));

        Equal(true, Submitted(vefectsTrail, [PropFloat("_EmissiveIntensity", 3f)]));
        Equal(true, Submitted(vefectsTrail, [
                PropVector("_ColorPanSpeed", new Vector4(5f, 5f, 0f, 0f)),
                PropVector("_DissolveTex_ST", new Vector4(2f, 0.2f, 0f, 0f)),
                PropFloat("_EmissiveIntensity", 1f),
                PropFloat("_DistortionIntensity", 3f),
                PropFloat("_MaskDistortionIntensity", 4f),
            ]));

        Material tintLayout = NamedMaterial("Custom/StarPortal",
            ColorSlot(0, "_Color", Vector4.One, hasDefault: true),
            FloatSlot(16, "_CenterFresnelPower", 2f, hasDefault: true),
            FloatSlot(20, "_TonemapStrength", 7f, hasDefault: true),
            FloatSlot(24, "_Brightness", 1f, hasDefault: true));

        Equal(true, Submitted(tintLayout, [PropColor("_Color", new Vector4(2.5f, 0.4f, 0.1f, 1f))]));
        Equal(true, Submitted(tintLayout, [PropVector("_Color", new Vector4(0.1f, 3.0f, 0.2f, 1f))]));
        Equal(true, Submitted(tintLayout, [PropVector("_Color", new Vector4(1f, 1f, 1f, 2f))]));
        Equal(true, Submitted(tintLayout, [PropFloat("_CenterFresnelPower", 3.5f), PropFloat("_TonemapStrength", 16f), PropFloat("_Brightness", 0.6f)]));
        Equal(true, Submitted(tintLayout, [PropFloat("_Brightness", 3f)]));
        Equal(true, Submitted(tintLayout, [PropVector("_Color", new Vector4(4f, 4f, 4f, 1f)), PropVector("_Color", Vector4.One)]));
        Equal(true, Submitted(tintLayout, [PropColor("_Color", new Vector4(float.PositiveInfinity, 0f, 0f, 1f)), PropFloat("_Brightness", float.NaN)]));
        Equal(true, Submitted(tintLayout, [PropFloat("_HdrMultiply", 8f), PropColor("_UndeclaredHdrColor", new Vector4(5f, 5f, 5f, 1f))]));

        Material stdParticle = NamedMaterial("RimUnity_StandardParticle2",
            ColorSlot(0, "_Color", Vector4.One, hasDefault: true),
            ColorSlot(16, "_EmissionColor", new Vector4(0, 0, 0, 1), hasDefault: true),
            FloatSlot(32, "_EmissionEnabled", 0f, hasDefault: true));

        Equal(false, Submitted(stdParticle, [PropColor("_EmissionColor", new Vector4(4f, 2f, 0f, 1f)), PropFloat("_EmissionEnabled", 0f)]));
        Equal(false, Submitted(stdParticle, [PropColor("_EmissionColor", new Vector4(4f, 2f, 0f, 1f)), PropFloat("_EmissionEnabled", 1f)]));

        Equal(true, Submitted(cfxrProcGlow, [PropFloat("_HdrMultiply", 6.0f)],
            [PropFloat("_HdrMultiply", 0.0f)]));
        Equal(true, Submitted(cfxrProcGlow, [PropFloat("_HdrMultiply", 0.0f)],
            [PropFloat("_HdrMultiply", 6.0f)]));
        Equal(true, Submitted(tintLayout, [PropColor("_Color", new Vector4(4f, 2f, 1f, 1f))],
            [PropColor("_Color", new Vector4(0.5f, 0.5f, 0.5f, 1f))]));
        Equal(true, Submitted(tintLayout, [PropColor("_Color", Vector4.One)],
            [PropColor("_Color", new Vector4(3f, 0.5f, 0.2f, 1f))]));

        var resolvedSdr = default(MaterialUniformBytes);
        resolvedSdr.Write(0, MaterialUniformValue.FromVector(MaterialUniformType.Vector4, Vector4.One));
        resolvedSdr.Write(16, MaterialUniformValue.FromVector(MaterialUniformType.Vector4, new Vector4(4f, 2f, 0f, 1f)));
        resolvedSdr.Write(32, MaterialUniformValue.FromFloat(0f));
        Equal(false, Captured(stdParticle, in resolvedSdr));

        var resolvedHdr = resolvedSdr;
        resolvedHdr.Write(32, MaterialUniformValue.FromFloat(1f));
        Equal(false, Captured(stdParticle, in resolvedHdr));

        var resolvedMultOff = default(MaterialUniformBytes);
        resolvedMultOff.Write(8, MaterialUniformValue.FromFloat(0f));
        Equal(true, Captured(cfxrProcGlow, in resolvedMultOff));
        resolvedMultOff.Write(8, MaterialUniformValue.FromFloat(6f));
        Equal(true, Captured(cfxrProcGlow, in resolvedMultOff));
    }

    public static void AlphaClampRewriterHandlesLinearProgramsAndPlainShadersWhileRejectingUnsafeShaders()
    {
        const string rimUnityLinearFrag = """
            #version 450
            vec3 RU_Encode(vec3 c) { return c; }
            layout(location = 0)
            out vec4 entryPointParam_fragment_program_0;
            void RU_SourceMain() {
                entryPointParam_fragment_program_0 = vec4(4.0, 2.0, 0.5, 2.0);
            }
            void main() { RU_SourceMain(); entryPointParam_fragment_program_0.rgb = RU_Encode(entryPointParam_fragment_program_0.rgb); }
            // @rimunity-linear-program 1
            """;

        // Linear mode: strips RU_Encode and clamps alpha to [0, 1] with NaN guard.
        Require(ExternalHdrAlphaClampShaders.TryRewriteFragmentForAlphaClamp(
            rimUnityLinearFrag, LumenColorBlendSpace.Linear, allowPlainDiskRewrite: false, out string linearRewritten),
            "RimUnity linear program must rewrite cleanly in Linear mode.");
        Require(!linearRewritten.Contains("RU_Encode(entryPointParam_fragment_program_0.rgb)", StringComparison.Ordinal),
            "Linear rewrite must strip RU_Encode.");
        Require(linearRewritten.Contains("clamp(_lumenAlpha, 0.0, 1.0)", StringComparison.Ordinal)
                && linearRewritten.Contains("isnan(_lumenAlpha)", StringComparison.Ordinal)
                && linearRewritten.Contains("entryPointParam_fragment_program_0.a =", StringComparison.Ordinal),
            "Linear rewrite must clamp output alpha with NaN guard.");

        // Legacy mode: keeps RU_Encode AND clamps alpha to [0, 1].
        Require(ExternalHdrAlphaClampShaders.TryRewriteFragmentForAlphaClamp(
            rimUnityLinearFrag, LumenColorBlendSpace.Legacy, allowPlainDiskRewrite: false, out string legacyRewritten),
            "RimUnity linear program must rewrite cleanly in Legacy mode.");
        Require(legacyRewritten.Contains("RU_Encode(entryPointParam_fragment_program_0.rgb)", StringComparison.Ordinal),
            "Legacy rewrite must preserve RU_Encode.");
        Require(legacyRewritten.Contains("clamp(_lumenAlpha, 0.0, 1.0)", StringComparison.Ordinal),
            "Legacy rewrite must clamp output alpha.");

        // Plain disk GLSL shader (including CustomShaderLoader disk-backed Gamma shaders) rewrites safely in Legacy.
        const string plainFrag = """
            #version 450
            // layout(location = 1) out vec4 commentedOut;
            /* void main() { fake(); } */
            layout(location = 0) out vec4 outColor;
            void main() {
                outColor = vec4(3.0, 1.0, 0.2, 2.0);
            }
            """;
        Require(ExternalHdrAlphaClampShaders.TryRewriteFragmentForAlphaClamp(
            plainFrag, LumenColorBlendSpace.Legacy, allowPlainDiskRewrite: true, out string plainRewritten),
            "Disk-backed Gamma/plain GLSL shader must rewrite cleanly in Legacy mode.");
        Require(plainRewritten.Contains("void _lumenSourceMain()", StringComparison.Ordinal)
                && plainRewritten.Contains("void main() { _lumenSourceMain(); float _lumenAlpha = outColor.a; outColor.a = isnan(_lumenAlpha) ? 0.0 : clamp(_lumenAlpha, 0.0, 1.0); }", StringComparison.Ordinal),
            "Plain rewrite must rename original main() and append clamped wrapper.");

        // Plain disk shader without LinearProgramMarker must be rejected in Linear mode (never mix Gamma into Linear).
        Require(!ExternalHdrAlphaClampShaders.TryRewriteFragmentForAlphaClamp(
            plainFrag, LumenColorBlendSpace.Linear, allowPlainDiskRewrite: true, out _),
            "Gamma disk shader without @rimunity-linear-program marker must be rejected in Linear mode.");

        // In-memory transformed shader (allowPlainDiskRewrite: false) without marker must be rejected in both spaces.
        Require(!ExternalHdrAlphaClampShaders.TryRewriteFragmentForAlphaClamp(
            plainFrag, LumenColorBlendSpace.Linear, allowPlainDiskRewrite: false, out _),
            "In-memory transformed shader lacking marker must be rejected in Linear mode.");
        Require(!ExternalHdrAlphaClampShaders.TryRewriteFragmentForAlphaClamp(
            plainFrag, LumenColorBlendSpace.Legacy, allowPlainDiskRewrite: false, out _),
            "In-memory transformed shader lacking marker must be rejected in Legacy mode.");

        // Key classifier distinguishes in-memory MappedColorSpaceShader / SkinnedShaderVariants from custom disk shaders.
        Require(ExternalHdrAlphaClampShaders.IsInMemoryTransformedShaderKey("RimUnity_ParticleAlpha_Linear"), "_Linear must be flagged.");
        Require(ExternalHdrAlphaClampShaders.IsInMemoryTransformedShaderKey("RimUnity_ParticleAlpha_Linear_HdrOutput"), "_Linear_HdrOutput must be flagged.");
        Require(ExternalHdrAlphaClampShaders.IsInMemoryTransformedShaderKey("Mod.Fire_GpuSkin"), "_GpuSkin must be flagged.");
        Require(ExternalHdrAlphaClampShaders.IsInMemoryTransformedShaderKey("Mod.Fire_GpuSkin_HdrOutput"), "_GpuSkin_HdrOutput must be flagged.");
        Require(!ExternalHdrAlphaClampShaders.IsInMemoryTransformedShaderKey("keeptpa.NivarianRace:FireSphere"), "Normal disk custom shader key must not be flagged.");

        // Unsafe / unparseable shaders (including macro-obfuscated extra outputs) must all be rejected.
        string[] unsafePlainShaders =
        [
            // Multiple color outputs (MRT)
            "#version 450\nlayout(location = 0) out vec4 a;\nlayout(location = 1) out vec4 b;\nvoid main() { a = vec4(1); b = vec4(1); }",
            // Macro-obfuscated second output (#define OUT out)
            "#version 450\n#define OUT out\nlayout(location = 0) out vec4 c;\nlayout(location = 1) OUT vec4 aux;\nvoid main() { c = vec4(1); aux = vec4(1); }",
            // Backslash-continued macro definition
            "#version 450\n#define MY_OUT \\\n    out\nlayout(location = 0) out vec4 c;\nlayout(location = 1) MY_OUT vec4 aux;\nvoid main() { c = vec4(1); }",
            // Dual-source blend index
            "#version 450\nlayout(location = 0, index = 1) out vec4 a;\nvoid main() { a = vec4(1); }",
            // Output array
            "#version 450\nlayout(location = 0) out vec4 a[2];\nvoid main() { a[0] = vec4(1); }",
            // Non-vec4 output
            "#version 450\nlayout(location = 0) out vec3 a;\nvoid main() { a = vec3(1); }",
            // Location != 0
            "#version 450\nlayout(location = 1) out vec4 a;\nvoid main() { a = vec4(1); }",
            // Output interface block
            "#version 450\nlayout(location = 0) out FragOut { vec4 c; } o;\nvoid main() { o.c = vec4(1); }",
            // Preprocessor conditional or #include in plain shader
            "#version 450\nlayout(location = 0) out vec4 outColor;\n#ifdef FOO\nvoid main() { outColor = vec4(1); }\n#endif",
            "#version 450\n#include \"extra.glsl\"\nlayout(location = 0) out vec4 outColor;\nvoid main() { outColor = vec4(1); }",
            // Missing main()
            "#version 450\nlayout(location = 0) out vec4 outColor;",
            // Symbol collision
            "#version 450\nlayout(location = 0) out vec4 outColor;\nvoid _lumenSourceMain() {}\nvoid main() { outColor = vec4(1); }",
        ];
        foreach (string bad in unsafePlainShaders)
        {
            Require(!ExternalHdrAlphaClampShaders.TryRewriteFragmentForAlphaClamp(
                bad, LumenColorBlendSpace.Legacy, allowPlainDiskRewrite: true, out _),
                $"Expected unsafe shader to be rejected: {bad}");
        }

        // RimUnity linear marker with mismatched output variable in RU_Encode must be rejected.
        const string mismatchedWrapper = """
            #version 450
            layout(location = 0) out vec4 outColor;
            void RU_SourceMain() { outColor = vec4(1.0); }
            void main() { RU_SourceMain(); otherVar.rgb = RU_Encode(otherVar.rgb); }
            // @rimunity-linear-program 1
            """;
        Require(!ExternalHdrAlphaClampShaders.TryRewriteFragmentForAlphaClamp(
            mismatchedWrapper, LumenColorBlendSpace.Linear, allowPlainDiskRewrite: false, out _),
            "Mismatched wrapper output variable must be rejected.");

        // Layout parity check: tolerates TexelSize TextureResourceSlot binding difference, rejects extra storage buffers (e.g. GPU skinning).
        var uniform = ColorSlot(0, "_Color", Vector4.One, hasDefault: true);
        var diskLayout = new MaterialLayout([uniform], [], 16);
        var boundTexelLayout = new MaterialLayout([uniform with { TextureResourceSlot = 0 }], [], 16);
        var skinnedLayout = new MaterialLayout([uniform], [], 16,
            [new MaterialStorageBufferSlot(ShaderPropertyId.GetOrCreate("_Bones"), "_Bones", 1)]);
        Require(ExternalHdrAlphaClampShaders.LayoutMatchesSource(diskLayout, boundTexelLayout),
            "Matching uniform/texture/storage slots must pass layout parity.");
        Require(!ExternalHdrAlphaClampShaders.LayoutMatchesSource(diskLayout, skinnedLayout),
            "In-memory storage buffer additions (e.g. GPU-skinned shaders) must fail layout parity.");

        // Compiled SPIR-V fragment output interface verification (post-preprocessor truth, Khronos Location=30 Component=31 Index=32).
        Require(ExternalHdrAlphaClampShaders.SpirvHasUniqueLocation0Vec4Output(
            BuildSpirv(includeSecondOutput: false, includeDualSourceIndex: false, includeComponentZero: true, vecComponents: 4, location: 0, includeBuiltInOutput: true)),
            "Valid single location=0 component=0 vec4 color output plus BuiltIn output must pass SPIR-V check.");
        Require(!ExternalHdrAlphaClampShaders.SpirvHasUniqueLocation0Vec4Output(
            BuildSpirv(includeSecondOutput: true, includeDualSourceIndex: false, includeComponentZero: false, vecComponents: 4, location: 0, includeBuiltInOutput: false)),
            "MRT (two color outputs) must fail SPIR-V check.");
        Require(!ExternalHdrAlphaClampShaders.SpirvHasUniqueLocation0Vec4Output(
            BuildSpirv(includeSecondOutput: false, includeDualSourceIndex: true, includeComponentZero: false, vecComponents: 4, location: 0, includeBuiltInOutput: false)),
            "Dual-source Index (decoration 32) must fail SPIR-V check.");
        Require(!ExternalHdrAlphaClampShaders.SpirvHasUniqueLocation0Vec4Output(
            BuildSpirv(includeSecondOutput: false, includeDualSourceIndex: false, includeComponentZero: false, vecComponents: 3, location: 0, includeBuiltInOutput: false)),
            "vec3 output must fail SPIR-V check.");
        Require(!ExternalHdrAlphaClampShaders.SpirvHasUniqueLocation0Vec4Output(
            BuildSpirv(includeSecondOutput: false, includeDualSourceIndex: false, includeComponentZero: false, vecComponents: 4, location: 1, includeBuiltInOutput: false)),
            "Location 1 output must fail SPIR-V check.");
    }

    private static byte[] BuildSpirv(bool includeSecondOutput, bool includeDualSourceIndex, bool includeComponentZero, uint vecComponents, uint location, bool includeBuiltInOutput)
    {
        var words = new List<uint> { 0x07230203u, 0x00010000u, 0u, 20u, 0u };
        static uint Inst(ushort op, int count) => ((uint)count << 16) | op;

        // Decorations (Khronos SPIR-V spec): BuiltIn=11, Location=30, Component=31, Index=32.
        words.AddRange([Inst(71, 4), 5u, 30u, location]); // OpDecorate %5 Location <location>
        if (includeComponentZero)
            words.AddRange([Inst(71, 4), 5u, 31u, 0u]); // OpDecorate %5 Component 0
        if (includeDualSourceIndex)
            words.AddRange([Inst(71, 4), 5u, 32u, 1u]); // OpDecorate %5 Index 1
        if (includeSecondOutput)
            words.AddRange([Inst(71, 4), 6u, 30u, 1u]); // OpDecorate %6 Location 1
        if (includeBuiltInOutput)
            words.AddRange([Inst(71, 4), 7u, 11u, 22u]); // OpDecorate %7 BuiltIn FragDepth

        // Types: %1 = float32, %2 = vecN, %3 = ptr Output vecN, %4 = ptr Output float32
        words.AddRange([Inst(22, 3), 1u, 32u]); // OpTypeFloat %1 32
        words.AddRange([Inst(23, 4), 2u, 1u, vecComponents]); // OpTypeVector %2 %1 N
        words.AddRange([Inst(32, 4), 3u, 3u, 2u]); // OpTypePointer %3 Output %2
        words.AddRange([Inst(32, 4), 4u, 3u, 1u]); // OpTypePointer %4 Output %1

        // Variables
        words.AddRange([Inst(59, 4), 3u, 5u, 3u]); // OpVariable %3 %5 Output
        if (includeSecondOutput)
            words.AddRange([Inst(59, 4), 3u, 6u, 3u]);
        if (includeBuiltInOutput)
            words.AddRange([Inst(59, 4), 4u, 7u, 3u]);

        byte[] bytes = new byte[words.Count * 4];
        for (int i = 0; i < words.Count; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), words[i]);
        return bytes;
    }

    private sealed record FakeShader(string Key, object Layout, bool HasErrors, bool HasSceneGrab);

    private static Material NamedMaterial(string name, params MaterialUniformSlot[] slots)
    {
        string game = Environment.GetEnvironmentVariable("RIMWORLD_DIR")!;
        ShaderSourceLoader.RegisterShaderSourceDirectory(Path.Combine(game, "Rendering/Shaders"));
        var program = new ShaderProgramDefinition { vertexShaderFileName = "SceneDefault.vert.glsl",
            fragmentShaderFileName = "SceneDefault.frag.glsl", transparent = true, depthWrite = false };
        var state = new PipelineState(new BlendState(true, BlendFactor.One, BlendFactor.One, BlendOp.Add,
            BlendFactor.One, BlendFactor.One, BlendOp.Add), new DepthState(true, false), FaceCull.None, default, true);
        var shader = new Shader("TestExport." + name, name, program, materialLayout: new MaterialLayout(slots, [], slots.Length * 16),
            pipelineState: state);
        global::RimUnity.Shader.FromEngineShader(shader.Key, shader).name = name;
        return new Material(shader) { RenderQueue = 3000 };
    }

    private static bool Submitted(Material template, ReadOnlySpan<DrawProperty> values, ReadOnlySpan<DrawProperty> overrides = default)
    {
        var instance = new Material(template);
        instance.SetProperties(values);
        foreach (var property in overrides) instance.SetProperty(in property);
        return ExternalHdrMaterialRules.IsMaterialEligible(instance, LumenColorBlendSpace.Legacy);
    }

    private static bool Captured(Material material, in MaterialUniformBytes uniforms)
    {
        var shader = material.Shader;
        var variant = new ShaderVariantKey(shader, 0, string.Empty, shader.UseTexture, false, false);
        var binding = new MaterialBinding(1, 1, 1, shader.Key, shader, shader.MaterialLayout, uniforms, default,
            material.RenderQueue, variant, shader.PipelineState, null, false);
        var geometry = new MeshDrawGeometry(0, 0, [], "empty", Waylith.Graphics.Meshes.VertexLayout.Empty, null, default, 0, 0, 0, default, default);
        var draw = new FinalDrawItem(Matrix4x4.Identity, geometry, binding);
        return new ExternalHdrDrawAdapter(() => 0).AdaptBinding(draw, LumenColorBlendSpace.Legacy) != null;
    }

    private static MaterialUniformSlot ColorSlot(int offset, string name, Vector4 defaultVal, bool hasDefault) =>
        new(offset, MaterialUniformType.Vector4, name, ShaderPropertyId.GetOrCreate(name),
            MaterialUniformValue.FromVector(MaterialUniformType.Vector4, defaultVal), -1, IsColor: true, HasAuthoredDefault: hasDefault);

    private static MaterialUniformSlot VectorSlot(int offset, string name, Vector4 defaultVal, bool hasDefault) =>
        new(offset, MaterialUniformType.Vector4, name, ShaderPropertyId.GetOrCreate(name),
            MaterialUniformValue.FromVector(MaterialUniformType.Vector4, defaultVal), -1, IsColor: false, HasAuthoredDefault: hasDefault);

    private static MaterialUniformSlot FloatSlot(int offset, string name, float defaultVal, bool hasDefault) =>
        new(offset, MaterialUniformType.Float, name, ShaderPropertyId.GetOrCreate(name),
            MaterialUniformValue.FromFloat(defaultVal), -1, IsColor: false, HasAuthoredDefault: hasDefault);

    private static DrawProperty PropFloat(string name, float value) =>
        DrawProperty.Float(ShaderPropertyId.GetOrCreate(name), value);

    private static DrawProperty PropVector(string name, Vector4 value) =>
        DrawProperty.Vector(ShaderPropertyId.GetOrCreate(name), value);

    private static DrawProperty PropColor(string name, Vector4 value) =>
        DrawProperty.Color(ShaderPropertyId.GetOrCreate(name), value);

    private static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; actual {actual}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
