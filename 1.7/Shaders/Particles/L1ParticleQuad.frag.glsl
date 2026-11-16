#version 450
#include "ParticleDisc.glsl"

// L1 CPU particles through MeshBatchRenderer: set 2 is the material set.
// binding 0: 48-byte std140 block; 1: particle texture (flipbook sheet); 2: R32_SFLOAT scene depth snapshot.
layout(std140, set = 2, binding = 0) uniform LumenL1Uniforms
{
    // x: sample the particle texture (1) or the procedural round falloff (0);
    // y: multiply rgb by alpha (Additive/Premultiplied; AlphaBlend's SrcAlpha factor already does it).
    vec4 _LumenDraw;
    // w: feather distance (xyz kept for legacy packing compatibility).
    vec4 _LumenDepth;
    // Actual capture projection coefficients (M33, M34, M43, M44).
    vec4 _LumenProjection;
} lumen;
layout(set = 2, binding = 1) uniform sampler2D _LumenParticleTexture; // @default = white
layout(set = 2, binding = 2) uniform sampler2D _LumenSceneDepth; // @default = white
layout(location = 0) in vec2 uv;
layout(location = 1) in vec4 color;
layout(location = 2) in vec2 radialUv;
layout(location = 0) out vec4 outColor;

float eyeDistance(float depth)
{
    vec4 p = lumen._LumenProjection;
    float denom = depth * p.z - p.x;
    return abs(denom) > 1e-6 ? abs((p.y - depth * p.w) / denom) : 0.0;
}

void main()
{
    vec4 texel;
    if (lumen._LumenDraw.x > 0.5)
    {
        texel = texture(_LumenParticleTexture, uv);
    }
    else
    {
        texel = vec4(1.0, 1.0, 1.0, lumenParticleDiscCoverage(radialUv));
    }
    vec4 c = texel * color;
    if (lumen._LumenDepth.w > 0.0)
    {
        float sceneRaw = texelFetch(_LumenSceneDepth, ivec2(gl_FragCoord.xy), 0).r;
        float separation = eyeDistance(sceneRaw) - eyeDistance(gl_FragCoord.z);
        c.a *= clamp(separation / lumen._LumenDepth.w, 0.0, 1.0);
    }
    if (c.a <= 0.0)
        discard;
    if (lumen._LumenDraw.y > 0.5)
        c.rgb *= c.a;
    outColor = c;
}
