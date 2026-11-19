#version 450

// Light pool accumulation: one ground quad per light, spanning [-R, R] around the light, drawn with blend One, One
// into a cleared RGBA16F target, so overlapping pools add linearly. RimLumenLightPool.frag.glsl applies the sum.
// The vertex stage is Waylith's SceneDefault.vert.glsl (vUv at location 1, world position at 2; plane10 uv spans [0,1]).
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _LumenLight; // rgb: ColorRgb * effective intensity (>= 0, unclamped); a: 0
    vec4 _LumenLightAt; // xy: light centre in world cells (X, Z); z: Kind (0 effect, 1 vanilla); w: radius
    vec4 _LumenShadowChannel; // ShadowSlot.Selector of the light's object-shadow channel; 0 without object shadows
} uMaterial;

#include "RimLumenOcclusion.glsl"
// The object-shadow mask of this draw's round (LightPoolRenderer).
layout(set = 2, binding = 4) uniform sampler2D _LumenObjectShadow; // @default = black

void main()
{
    // Kind selects only the falloff (VisualPointLight.EvaluateAt); both kinds share one visibility query:
    // cover * min(solid, 1 - object shadow).
    float falloff;
    vec3 rgb;
    if (uMaterial._LumenLightAt.z == 1.0)
    {
        float radius = uMaterial._LumenLightAt.w;
        float dPrime = 1.0 + distance(vWorldPos.xz, uMaterial._LumenLightAt.xy);
        falloff = dPrime <= radius ? mix(1.0 - dPrime / radius, 1.0 / (dPrime * dPrime), 0.4) : 0.0;
        rgb = uMaterial._LumenLight.rgb * falloff;
        rgb /= max(1.0, max(rgb.r, max(rgb.g, rgb.b)));
    }
    else
    {
        // d = offset / R, so dot(d, d) = (distance / R)^2 and the falloff (1 - (distance / R)^2)^2 is 0 at and beyond R.
        vec2 d = vUv * 2.0 - 1.0;
        float q = max(0.0, 1.0 - dot(d, d));
        falloff = q * q;
        rgb = uMaterial._LumenLight.rgb * falloff;
    }
    float visibility = 0.0;
    if (falloff > 0.0)
    {
        float cover;
        float solid = lightVisibility(uMaterial._LumenLightAt.xy, vWorldPos.xz, cover);
        visibility = min(solid, 1.0 - lumenObjectShadow(_LumenObjectShadow, uMaterial._LumenShadowChannel)) * cover;
    }
    outColor = vec4(rgb * visibility, 0.0);
}
