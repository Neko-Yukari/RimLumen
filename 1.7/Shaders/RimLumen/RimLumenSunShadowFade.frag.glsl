#version 450
#include "RimLumenLightPoolTerm.glsl"

// Vanilla SunShadowFade.frag.glsl (queue 3175, MultiplyDestination) with the light-pool lift; see
// RimLumenSunShadow.frag.glsl. vColor is vanilla's interpolated mix(_Color, 1, fade). The uniform block must stay
// identical to vanilla's: the vertex stage reads _CastVect from it.
layout(location = 0) in vec4 vColor;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;    // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _CastVect; // global, map sun light direction (SkyManager)
} u;
layout(set = 2, binding = 1) uniform sampler2D _LumenLightSum; // @default = black

void main()
{
    outColor = lumenShadowOverPool(vColor, lumenLightPoolUnderShadow(_LumenLightSum));
}
