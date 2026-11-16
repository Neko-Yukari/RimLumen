#ifndef RIMLUMEN_PARTICLE_DISC_GLSL
#define RIMLUMEN_PARTICLE_DISC_GLSL

float lumenParticleDiscCoverage(vec2 quadUv)
{
    vec2 radial = quadUv * 2.0 - 1.0;
    float coverage = max(0.0, 1.0 - dot(radial, radial));
    return coverage * coverage;
}

#endif
