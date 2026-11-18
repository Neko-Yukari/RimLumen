#version 450
#include "RimLumenOcclusion.glsl"

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec3 vWorldPos;
layout(location = 0) out vec4 outColor;
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;    // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _CastVect; // global, map sun light direction (SkyManager)
} u;

void main()
{
    vec3 ground = mix(u._Color.rgb, vec3(1.0), lumenSunVisibility(vWorldPos.xz, u._CastVect.xz));
    outColor = vec4(min(vec3(1.0), vColor.rgb / ground), vColor.a);
}
