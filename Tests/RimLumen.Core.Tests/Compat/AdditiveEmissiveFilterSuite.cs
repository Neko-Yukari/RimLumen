using System;
using RimLumen.Core.Compat;
using Waylith.Rendering;

namespace RimLumen.Core.Tests.Compat;

[LumenTestSuite]
public static class AdditiveEmissiveFilterSuite
{
    private static readonly PipelineState Additive = new(BlendState.Additive, new DepthState(true, false), FaceCull.None);

    [LumenFact]
    public static void AcceptsSrcAlphaAndOneForNonExcludedShader()
    {
        Expect(true, Additive, "MoteGlow");
        Expect(true, Additive with { Blend = Additive.Blend with { SrcColor = BlendFactor.One } }, "MoteGlow");
        Expect(true, Additive with { Depth = new DepthState(false, false) }, "MoteGlow");
    }

    [LumenFact]
    public static void RejectsDisabledBlending()
    {
        Expect(false, Additive with { Blend = Additive.Blend with { Enabled = false } }, "MoteGlow");
    }

    [LumenFact]
    public static void RejectsEveryDestinationFactorOtherThanOne()
    {
        BlendFactor[] rejected = [BlendFactor.Zero, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha,
            BlendFactor.SrcColor, BlendFactor.DstColor, BlendFactor.OneMinusSrcColor, BlendFactor.OneMinusDstColor];
        foreach (BlendFactor factor in rejected)
        {
            Expect(false, Additive with { Blend = Additive.Blend with { DstColor = factor } }, "MoteGlow");
        }
    }

    [LumenFact]
    public static void RejectsEveryColorOperationOtherThanAdd()
    {
        BlendOp[] rejected = [BlendOp.Subtract, BlendOp.ReverseSubtract, BlendOp.Min, BlendOp.Max];
        foreach (BlendOp operation in rejected)
        {
            Expect(false, Additive with { Blend = Additive.Blend with { ColorOp = operation } }, "MoteGlow");
        }
    }

    [LumenFact]
    public static void RejectsEverySourceFactorOtherThanSrcAlphaOrOne()
    {
        BlendFactor[] rejected = [BlendFactor.Zero, BlendFactor.OneMinusSrcAlpha, BlendFactor.SrcColor,
            BlendFactor.DstColor, BlendFactor.OneMinusSrcColor, BlendFactor.OneMinusDstColor];
        foreach (BlendFactor factor in rejected)
        {
            Expect(false, Additive with { Blend = Additive.Blend with { SrcColor = factor } }, "MoteGlow");
        }
    }

    [LumenFact]
    public static void RejectsDepthWritingDraws()
    {
        Expect(false, Additive with { Depth = new DepthState(true, true) }, "MoteGlow");
        Expect(false, Additive with { Depth = new DepthState(false, true) }, "MoteGlow");
    }

    [LumenFact]
    public static void RejectsGlowOverlay()
    {
        Expect(false, Additive, "GlowOverlay");
    }

    [LumenFact]
    public static void RejectsOxygenOverlayIndoors()
    {
        Expect(false, Additive, "OxygenOverlayIndoors");
    }

    [LumenFact]
    public static void RejectsGravshipLensFlare()
    {
        Expect(false, Additive, "GravshipLensFlare");
    }

    [LumenFact]
    public static void RejectsUnlitAdditive()
    {
        Expect(false, Additive, "UnlitAdditive");
    }

    [LumenFact]
    public static void RejectsMoteWater()
    {
        Expect(false, Additive, "MoteWater");
    }

    [LumenFact]
    public static void RejectsNullAndEmptyShaderKeys()
    {
        Expect(false, Additive, null);
        Expect(false, Additive, string.Empty);
    }

    [LumenFact]
    public static void MatchesExcludedKeysExactlyAndOrdinally()
    {
        Expect(true, Additive, "Foo/MoteWater");
        Expect(true, Additive, "Foo\\MoteWater");
        Expect(true, Additive, "motewater");
        Expect(true, Additive, "MoteWaterVariant");
    }

    private static void Expect(bool expected, PipelineState state, string? shaderKey)
    {
        bool actual = AdditiveEmissiveFilter.IsAdditiveEmissive(in state, shaderKey);
        if (actual != expected)
        {
            throw new InvalidOperationException($"Expected {expected}, actual {actual}; shader={shaderKey ?? "<null>"}, state={state}.");
        }
    }
}
