#ifndef LUMEN_PARTICLE_MATERIAL
#define LUMEN_PARTICLE_MATERIAL
#include "ParticleTypes.glsl"

// std140, 128 bytes. Set all eight vectors for each draw.
layout(std140, set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _LumenDraw;        // emitter index, texture present, depth present, premultiply output
    vec4 _LumenCameraRight; // display camera right xyz, inverse-Y compensated
    vec4 _LumenCameraUp;    // display camera up xyz, inverse-Y compensated; w is Y scale ratio
    vec4 _LumenProjection; // System.Numerics projection M33, M43, M34, M44 (A,B,C,D)
    vec4 _LumenDepthUv;    // gl_FragCoord.xy * xy + zw -> depth texture UV
    vec4 _LumenVertical;   // Vertical3D display: x mode (0 legacy, 1 un-squash for 3D/IsoRim, 2 oblique for straight-down 2D), y baseY, z vanilla MoteOverhead altitude, w 0
    vec4 _LumenMaterialTint;
    vec4 _LumenColor;       // x: Linear material, y: resolved sRGB texture, z: Linear blending
} material;

// Waylith derives the material (set 2) descriptor layout from the FRAGMENT source only, so the
// instance buffers are declared in this header: both stages include it and the layout owns 1..3.
layout(std430, set = 2, binding = 1) readonly buffer _LumenParticles { GpuParticle particles[]; };
layout(std430, set = 2, binding = 2) readonly buffer _LumenEmitters { GpuEmitterParams emitters[]; };
layout(std430, set = 2, binding = 3) readonly buffer _LumenCurves { GpuCurveSample curves[]; };

#endif
