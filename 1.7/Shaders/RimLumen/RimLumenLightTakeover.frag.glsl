#version 450
#include "RimLumenTakeover.glsl"

// Takeover LightOverlay (3100): vanilla's sky and the light-pool sum, unshadowed. The sun is applied at 3170 by
// RimLumenSunShadowTakeover.frag.glsl, behind the scene depth and vanilla's stencil like vanilla SunShadow.
layout(location = 0) in vec4 vColor;
layout(location = 0) out vec4 outColor;
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
} u;
layout(set = 2, binding = 1) uniform sampler2D _LumenLightSum; // @default = black

void main()
{
    outColor = lumenTakeoverOverlay(u._Color, vColor.a, vec3(1.0), lumenTakeoverGlow(_LumenLightSum)) * 0.5;
}
