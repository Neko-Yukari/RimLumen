#ifndef LUMEN_PARTICLE_COLOR
#define LUMEN_PARTICLE_COLOR

// RimUnity Source/Shared/ShaderColorSpace.cs:69-103,122-134 at
// 45ca90987f7d3721715b45c021cab986ee14c468. The native boundary excludes negative RGB.
// Requires: finite stored RGB >= 0. Ensures: extended sRGB decode without clamping.
vec3 lumenDecodeStored(vec3 storedRgb)
{
    return mix(pow((storedRgb + 0.055) / 1.055, vec3(2.4)), storedRgb / 12.92,
        lessThanEqual(storedRgb, vec3(0.04045)));
}

// Requires: finite linear RGB >= 0. Ensures: extended sRGB encode without clamping.
vec3 lumenEncodeStored(vec3 linearRgb)
{
    return mix(1.055 * pow(linearRgb, vec3(1.0 / 2.4)) - 0.055, linearRgb * 12.92,
        lessThanEqual(linearRgb, vec3(0.0031308)));
}

// Requires: nonnegative finite RGB, alpha in [0,1], and resolved texture metadata.
// Ensures: straight stored RGBA; vertex RGB is not decoded, alpha is never converted.
vec4 lumenParticleStoredColor(vec4 vertex, vec4 tint, vec4 texel, bool linearMaterial, bool textureSrgb)
{
    vec3 rgb = linearMaterial
        ? lumenEncodeStored(vertex.rgb * lumenDecodeStored(tint.rgb)
            * (textureSrgb ? lumenDecodeStored(texel.rgb) : texel.rgb))
        : vertex.rgb * tint.rgb * texel.rgb;
    return vec4(rgb, vertex.a * tint.a * texel.a);
}

#endif
