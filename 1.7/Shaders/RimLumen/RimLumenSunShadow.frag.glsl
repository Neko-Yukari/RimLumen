#version 450
#include "RimLumenLightPoolTerm.glsl"

// Vanilla SunShadow.frag.glsl (queue 3170, MultiplyDestination) with the light-pool lift: on frames with light pools
// LightPoolShadows rebinds this frame's vanilla solid-shadow draws to this fragment, keeping vanilla's vertex stage,
// pipeline state and per-draw uniforms. The uniform block must stay identical to vanilla's.
layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0) - sun shadow colour
} uMaterial;
layout(set = 2, binding = 1) uniform sampler2D _LumenLightSum; // @default = black

void main()
{
    outColor = lumenShadowOverPool(uMaterial._Color, lumenLightPoolUnderShadow(_LumenLightSum));
}
