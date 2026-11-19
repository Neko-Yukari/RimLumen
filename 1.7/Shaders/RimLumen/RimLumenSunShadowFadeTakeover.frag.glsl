#version 450
#include "RimLumenOcclusion.glsl"

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec3 vWorldPos;
layout(location = 0) out vec4 outColor;
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;    // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _CastVect; // global, map sun light direction (SkyManager)
    vec4 _LumenSunChannel; // ShadowSlot.Selector of the sun; 0 without object shadows
} u;
layout(set = 2, binding = 4) uniform sampler2D _LumenObjectShadow; // @default = black

void main()
{
    float visibility = min(lumenSunVisibility(vWorldPos.xz, u._CastVect.xz), 1.0 - lumenObjectShadow(_LumenObjectShadow, u._LumenSunChannel));
    vec3 ground = mix(u._Color.rgb, vec3(1.0), visibility);
    outColor = vec4(min(vec3(1.0), vColor.rgb / ground), vColor.a);
}
