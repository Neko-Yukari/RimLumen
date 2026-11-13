#version 450
#include "ParticleMaterial.glsl"

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
        texel = texture(_LumenParticleTexture, vUv); // Texture binding must deliver linear RGB.
    else
    {
        vec2 radial = vRadialUv * 2.0 - 1.0;
        float coverage = max(0.0, 1.0 - dot(radial, radial));
        texel = vec4(1.0, 1.0, 1.0, coverage * coverage);
    }
    vec4 color = texel * vColor;
    if (material._LumenDraw.z > 0.5)
    {
        vec2 depthUv = gl_FragCoord.xy * material._LumenDepthUv.xy + material._LumenDepthUv.zw;
        float sceneDepth = texture(_LumenSceneDepth, depthUv).r;
        float separation = eyeDistance(sceneDepth) - eyeDistance(gl_FragCoord.z);
        if (separation < 0.0)
            discard; // The empty-vertex-layout graphics pipeline has no fixed-function depth test.
        if (vSoftDistance > 0.0)
            color.a *= clamp(separation / vSoftDistance, 0.0, 1.0);
    }
    if (color.a <= 0.0)
        discard;
    if (material._LumenDraw.w > 0.5)
        color.rgb *= color.a;
    outColor = color;
}
