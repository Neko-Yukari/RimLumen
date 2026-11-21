#version 450
#include "ParticleMaterial.glsl"
#include "ParticleDisc.glsl"
#include "ParticleColor.glsl"

layout(set = 2, binding = 4) uniform sampler2D _LumenParticleTexture; // @default = white
layout(set = 2, binding = 5) uniform sampler2D _LumenSceneDepth; // @default = white
#include "ParticleFragmentBody.glsl"

void main()
{
    vec4 texel = material._LumenDraw.y > 0.5
        ? texture(_LumenParticleTexture, vUv)
        : vec4(1.0, 1.0, 1.0, lumenParticleDiscCoverage(vRadialUv));
    lumenWriteParticleFragment(texel);
}
