#ifndef RIMLUMEN_COLOR_SPACE
#define RIMLUMEN_COLOR_SPACE

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

// Requires: straight stored RGB >= 0 and this draw's captured frame-space flag.
// Ensures: RGB in the native HDR layer's blend space; alpha unchanged, no premultiplication.
vec4 lumenColorForBlendSpace(vec4 storedColor, bool linearBlendSpace)
{
    return vec4(linearBlendSpace ? lumenDecodeStored(storedColor.rgb) : storedColor.rgb, storedColor.a);
}

#endif
