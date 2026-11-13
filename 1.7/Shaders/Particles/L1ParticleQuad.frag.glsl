#version 450
#include "../PostFx/RimLumenDepth.glslinc"
#define L1_SCENE_DEPTH 1

#if L1_SCENE_DEPTH
layout(set = 0, binding = 0) uniform sampler2D sceneDepth;
#endif
layout(location = 0) in vec2 uv;
layout(location = 1) in vec4 color;
layout(location = 0) out vec4 outColor;
layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 depthParams;
} pc;

void main()
{
    vec2 radial = uv * 2.0 - 1.0;
    float roundFalloff = 1.0 - smoothstep(0.0, 1.0, dot(radial, radial));
    float fade = 1.0;
#if L1_SCENE_DEPTH
    float sceneRaw = texelFetch(sceneDepth, ivec2(gl_FragCoord.xy), 0).r;
    fade = RimLumenSoftParticleFade(RimLumenLinearDepth(sceneRaw, pc.depthParams),
        RimLumenLinearDepth(gl_FragCoord.z, pc.depthParams), pc.depthParams.w);
#endif
    float alpha = color.a * roundFalloff * fade;
    outColor = vec4(color.rgb * alpha, alpha);
}
