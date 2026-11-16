#version 450

// Coverage of vanilla FogOfWar.frag.glsl (Data/Core/Shaders/Map), paired with vanilla's own vertex stage
// (MapWorldUV.vert.glsl). Drawn with the overlay mask blend (Src = DstColor, Dst = OneMinusSrcAlpha), so a
// RimLumen target keeps exactly what vanilla fog leaves of the pixel underneath: dst * (1 - coverage).
// Vanilla blends the fog grey with SrcAlpha / OneMinusSrcAlpha; the grey adds nothing to a glow, so rgb is 0.

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv; // object-space XZ

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _DetailTex;

void main()
{
    vec2 uv = vUv / 64.0;
    uv.y = -uv.y;
    float coverage = texture(_MainTex, uv).a * texture(_DetailTex, uv * 8.0).a * vColor.a * uMaterial._Color.a;
    outColor = vec4(0.0, 0.0, 0.0, coverage);
}
