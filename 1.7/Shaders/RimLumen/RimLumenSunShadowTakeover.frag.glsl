#version 450
#include "RimLumenOcclusion.glsl"
#include "RimLumenTakeover.glsl"

// Takeover sun shade (3170): a LightOverlay mesh lowered to the Shadows altitude with vanilla SunShadow's pipeline
// state, so the depth test keeps it off sprites and stencil bit 4 off sun-masked cells. Multiplies the scene, already
// at Overlay(1), by SunShadowComposition.ShadeFactor. Sun visibility: the darker of the blockLight march and the things'
// silhouette shadows (round 0's object-shadow mask, _LumenSunChannel).
layout(location = 0) in vec4 vColor;
layout(location = 2) in vec3 vWorldPos;
layout(location = 0) out vec4 outColor;
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _LumenCastVect;
    vec4 _LumenSunShadowColor;
    vec4 _LumenSunChannel; // ShadowSlot.Selector of the sun; 0 without object shadows
} u;
layout(set = 2, binding = 4) uniform sampler2D _LumenLightSum; // @default = black
layout(set = 2, binding = 5) uniform sampler2D _LumenObjectShadow; // @default = black

void main()
{
    float visibility = min(lumenSunVisibility(vWorldPos.xz, u._LumenCastVect.xz), 1.0 - lumenObjectShadow(_LumenObjectShadow, u._LumenSunChannel));
    vec3 s = mix(u._LumenSunShadowColor.rgb, vec3(1.0), visibility);
    vec3 glow = lumenTakeoverGlow(_LumenLightSum);
    vec3 lit = lumenTakeoverOverlay(u._Color, vColor.a, vec3(1.0), glow).rgb;
    vec3 shaded = lumenTakeoverOverlay(u._Color, vColor.a, s, glow).rgb;
    outColor = vec4(lit.r > 0.0 ? shaded.r / lit.r : 1.0, lit.g > 0.0 ? shaded.g / lit.g : 1.0,
        lit.b > 0.0 ? shaded.b / lit.b : 1.0, 1.0);
}
