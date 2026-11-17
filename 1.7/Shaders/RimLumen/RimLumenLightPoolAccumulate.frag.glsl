#version 450

// Light pool accumulation: one ground quad per light, spanning [-R, R] around the light, drawn with blend One, One
// into a cleared RGBA16F target, so overlapping pools add linearly. RimLumenLightPool.frag.glsl applies the sum.
// The vertex stage is Waylith's SceneDefault.vert.glsl (vUv at location 1; plane10 uv spans [0,1]).
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _LumenLight; // rgb: ColorRgb * effective intensity (>= 0, unclamped); a unused
} uMaterial;

void main()
{
    // d = offset / R, so dot(d, d) = (distance / R)^2 and the falloff (1 - (distance / R)^2)^2 is 0 at and beyond R.
    vec2 d = vUv * 2.0 - 1.0;
    float q = max(0.0, 1.0 - dot(d, d));
    outColor = vec4(uMaterial._LumenLight.rgb * (q * q), 0.0);
}
