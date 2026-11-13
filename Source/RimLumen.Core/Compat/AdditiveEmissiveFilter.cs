using System;
using Waylith.Rendering;

namespace RimLumen.Core.Compat;

// Selects vanilla MapMain draws that are additive emissives (glows, fire, motes) for the optional HDR redraw.
public static class AdditiveEmissiveFilter
{
    // Built-in ambient/fullscreen additive shaders that must never be redrawn into the HDR target.
    public static readonly string[] ExcludedShaderKeys =
        { "GlowOverlay", "OxygenOverlayIndoors", "GravshipLensFlare", "UnlitAdditive", "MoteWater" };

    // Ensures: true iff Blend.Enabled && DstColor == One && ColorOp == Add && SrcColor in {SrcAlpha, One}
    //   && !Depth.Write && shaderKey is not in ExcludedShaderKeys (exact ordinal match; Shader.Key is the
    //   bare ShaderDef defName, Assembly-CSharp.decompiled.cs:43579; null/empty key => false).
    public static bool IsAdditiveEmissive(in PipelineState state, string? shaderKey)
    {
        if (!state.Blend.Enabled || state.Blend.DstColor != BlendFactor.One || state.Blend.ColorOp != BlendOp.Add
            || (state.Blend.SrcColor != BlendFactor.SrcAlpha && state.Blend.SrcColor != BlendFactor.One)
            || state.Depth.Write || string.IsNullOrEmpty(shaderKey))
        {
            return false;
        }
        foreach (string excludedKey in ExcludedShaderKeys)
        {
            if (string.Equals(shaderKey, excludedKey, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}
