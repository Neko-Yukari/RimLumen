#ifndef LUMEN_PARTICLE_COLOR
#define LUMEN_PARTICLE_COLOR

#include "../RimLumen/RimLumenColorSpace.glsl"

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
