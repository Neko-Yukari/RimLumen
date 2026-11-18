#version 450
#include "RimLumenOcclusion.glsl"

layout(location = 0) in vec4 vColor;
layout(location = 2) in vec3 vWorldPos;
layout(location = 0) out vec4 outColor;
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _LumenCastVect;
    vec4 _LumenSunShadowColor;
} u;
layout(set = 2, binding = 4) uniform sampler2D _LumenLightSum; // @default = black

void main()
{
    float vis = lumenSunVisibility(vWorldPos.xz, u._LumenCastVect.xz);
    vec3 ground = mix(u._LumenSunShadowColor.rgb, vec3(1.0), vis);
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    vec3 sum = lumenInside(pixel, textureSize(_LumenLightSum, 0)) ? texelFetch(_LumenLightSum, pixel, 0).rgb : vec3(0.0);
    if (any(isnan(sum))) sum = vec3(0.0);
    sum = clamp(sum, vec3(0.0), vec3(65504.0)); // RGBA16F finite range before hue projection
    vec3 glow = sum / max(1.0, max(sum.r, max(sum.g, sum.b)));
    vec4 sky = u._Color * (1.0 - vColor.a);
    vec4 color = vec4(sky.rgb * ground + glow, sky.a + vColor.a);
    float clampBase = 1.5 - 0.5 * min(sky.r, sky.b);
    if (color.r > clampBase) color *= clampBase / color.r;
    if (color.g > clampBase) color *= clampBase / color.g;
    if (color.b > clampBase) color *= clampBase / color.b;
    outColor = color * 0.5;
}
