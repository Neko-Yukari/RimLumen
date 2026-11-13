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
    //   && !Depth.Write && shaderKey is not in ExcludedShaderKeys (ordinal match against the key's last
    //   path segment, i.e. text after the final '/' or '\\'; null/empty key => false).
    public static bool IsAdditiveEmissive(in PipelineState state, string? shaderKey)
    {
        throw new NotImplementedException();
    }
}
