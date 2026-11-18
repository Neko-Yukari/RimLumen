using System;
using System.Numerics;

namespace RimLumen.Core.Lighting;

// Takeover shading references (design 3.3 and 3.9), mirrored line for line by RimLumenLightTakeover.frag.glsl and
// RimLumenSunShadowFadeTakeover.frag.glsl.
internal static class SunShadowComposition
{
    // Ground sun factor S = mix(c, 1, vis). Both this and DynamicFactor apply per RGB channel in C# and GLSL.
    // Requires: c, vis in [0, 1].
    internal static float Ground(float c, float vis) => c + (1f - c) * vis;

    // Dynamic SunShadowFade output under takeover. s: ground factor S at the pixel (> 0), sd: vanilla dynamic factor
    // mix(_Color, 1, fade). Ensures: min(1, sd / s), so the pixel ends at s * result = min(s, sd): shaded once, never s*sd.
    internal static float DynamicFactor(float s, float sd) => MathF.Min(1f, sd / s);

    // Replaced LightOverlay colour before the 0.5 blend scale (vanilla LightOverlay.frag.glsl with the sun applied).
    // sky: _Color, roof: vertex alpha a, s: ground factor, glow: projected light-pool sum (max component <= 1).
    // Ensures: rgb = clampBase rule of vanilla applied to sky.rgb * (1 - roof) * s + glow, where clampBase is computed
    //          from the unshadowed sky contribution sky * (1 - roof) exactly as vanilla; a = vanilla's alpha
    //          (sky.a * (1 - roof) + roof) scaled by the same clamp factors. With s = 1 and glow = vColor.rgb this is vanilla's colour.
    internal static Vector4 Overlay(Vector4 sky, float roof, Vector3 s, Vector3 glow)
    {
        Vector4 skyContribution = sky * (1f - roof);
        var color = new Vector4(skyContribution.X * s.X + glow.X, skyContribution.Y * s.Y + glow.Y, skyContribution.Z * s.Z + glow.Z,
            skyContribution.W + roof);
        float clampBase = 1.5f - 0.5f * MathF.Min(skyContribution.X, skyContribution.Z);
        if (color.X > clampBase) color *= clampBase / color.X;
        if (color.Y > clampBase) color *= clampBase / color.Y;
        if (color.Z > clampBase) color *= clampBase / color.Z;
        return color;
    }
}
