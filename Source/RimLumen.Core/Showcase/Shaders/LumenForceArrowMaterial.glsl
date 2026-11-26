#ifndef LUMEN_FORCE_ARROW_MATERIAL
#define LUMEN_FORCE_ARROW_MATERIAL
#include "LumenForceArrowTypes.glsl"

// std140, 48 bytes; every vector is a per-draw DrawProperty.
layout(std140, set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _LumenArrowDraw;   // x: first arrow of this emitter, y: Vertical3D, z: straight-down 2D camera, w: MoteOverhead altitude
    vec4 _LumenCameraRight; // as ParticleMaterial.glsl
    vec4 _LumenCameraUp;    // as ParticleMaterial.glsl; w is the Y scale ratio
} material;

// Waylith derives the set 2 layout from the fragment source only, so both stages include the storage buffer here.
layout(std430, set = 2, binding = 1) readonly buffer _LumenForceArrows { GpuForceArrow arrows[]; };

#endif
