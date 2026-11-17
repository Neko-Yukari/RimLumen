#version 450
#include "ParticleMaterial.glsl"
#include "ParticleDisc.glsl"
#include "ParticleColor.glsl"

layout(set = 2, binding = 4) uniform sampler2D _LumenParticleTexture; // @default = white
layout(set = 2, binding = 5) uniform sampler2D _LumenSceneDepth; // @default = white
layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec2 vRadialUv;
layout(location = 3) in float vSoftDistance;
layout(location = 0) out vec4 outColor;

float eyeDistance(float depth)
{
    // z_ndc=(A*z_eye+B)/(C*z_eye+D), Vulkan depth in [0,1].
    // Orthographic, perspective and reverse-Z use the ACTUAL capture projection coefficients.
    vec4 p = material._LumenProjection;
    return abs((p.y - depth * p.w) / (depth * p.z - p.x));
}

void main()
{
    vec4 texel;
    if (material._LumenDraw.y > 0.5)
        texel = texture(_LumenParticleTexture, vUv);
    else
    {
        texel = vec4(1.0, 1.0, 1.0, lumenParticleDiscCoverage(vRadialUv));
    }
    vec4 color = lumenParticleStoredColor(vColor, material._LumenMaterialTint, texel,
        material._LumenColor.x > 0.5, material._LumenColor.y > 0.5);
    if (material._LumenDraw.z > 0.5)
    {
        vec2 depthUv = gl_FragCoord.xy * material._LumenDepthUv.xy + material._LumenDepthUv.zw;
        float sceneDepth = texture(_LumenSceneDepth, depthUv).r;
        float separation = eyeDistance(sceneDepth) - eyeDistance(gl_FragCoord.z);
        if (separation < 0.0)
            discard; // Occlusion test lives here: the material pipeline runs with fixed-function depth test disabled.
        if (vSoftDistance > 0.0)
            color.a *= clamp(separation / vSoftDistance, 0.0, 1.0);
    }
    if (color.a <= 0.0)
        discard;
    color = lumenColorForBlendSpace(color, material._LumenColor.z > 0.5);
    // Additive blends with One/One, so its rgb must be premultiplied (the alternative SrcAlpha factor is not used).
    if (material._LumenDraw.w > 0.5)
        color.rgb *= color.a;
    outColor = color;
}
