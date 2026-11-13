#ifndef LUMEN_PARTICLE_MATERIAL
#define LUMEN_PARTICLE_MATERIAL

// std140, 80 bytes. Set all five vectors for each draw.
layout(std140, set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _LumenDraw;        // emitter index, texture present, depth present, premultiply output
    vec4 _LumenCameraRight; // normalized world camera right xyz
    vec4 _LumenCameraUp;    // normalized world camera up xyz
    vec4 _LumenProjection; // System.Numerics projection M33, M43, M34, M44 (A,B,C,D)
    vec4 _LumenDepthUv;    // gl_FragCoord.xy * xy + zw -> depth texture UV
} material;

#endif
