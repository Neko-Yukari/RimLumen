#ifndef LUMEN_PARTICLE_TRAIL_MATERIAL
#define LUMEN_PARTICLE_TRAIL_MATERIAL

#include "ParticleTrailTypes.glsl"

// Shares the exact 128-byte MaterialUniforms block layout of ParticleMaterial.glsl for ParticleMeshShaders.
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    // x: trail emitter index; y: texture present; z: scene depth bound; w: premultiply additive rgb by alpha.
    vec4 _LumenDraw;
    vec4 _LumenCameraRight;
    vec4 _LumenCameraUp;
    vec4 _LumenProjection;
    vec4 _LumenDepthUv;
    vec4 _LumenVertical;
    vec4 _LumenMaterialTint;
    vec4 _LumenColor;
} material;

// Exact 4 Set-2 storage buffers (Vulkan minimum maxPerStageDescriptorStorageBuffers = 4).
layout(std430, set = 2, binding = 1) readonly buffer _LumenTrailHeaders { GpuTrailHeader trailHeaders[]; };
layout(std430, set = 2, binding = 2) readonly buffer _LumenTrailPoints { GpuTrailPoint trailPoints[]; };
layout(std430, set = 2, binding = 3) readonly buffer _LumenTrailEmitters { GpuTrailEmitterParams trailEmitters[]; };
layout(std430, set = 2, binding = 4) readonly buffer _LumenCurves { GpuCurveSample curves[]; };

#endif
