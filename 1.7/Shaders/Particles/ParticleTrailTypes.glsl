#ifndef LUMEN_PARTICLE_TRAIL_TYPES
#define LUMEN_PARTICLE_TRAIL_TYPES

#include "ParticleTypes.glsl"

const uint LUMEN_TRAIL_NONE = 0xFFFFFFFFu;
const uint LUMEN_TRAIL_REJECTED = 0xFFFFFFFEu;
// Mirrors ParticleTrailMath.MaxLinearProbe: fixed overflow probe bound to guarantee O(1) GPU thread work.
const uint LUMEN_TRAIL_MAX_LINEAR_PROBE = 64u;

const uint LUMEN_TRAIL_HDR_ACTIVE = 1u;
const uint LUMEN_TRAIL_HDR_DETACHED = 2u;
const uint LUMEN_TRAIL_HDR_WORLDSPACE = 4u;
const uint LUMEN_TRAIL_HDR_VERTICAL3D = 8u;
const uint LUMEN_TRAIL_HDR_SKIP_HEAD = 16u;

const uint LUMEN_TRAIL_FLAG_DIE_WITH_PARTICLES = 1u;
const uint LUMEN_TRAIL_FLAG_WORLDSPACE = 2u;
const uint LUMEN_TRAIL_FLAG_SIZE_AFFECTS_WIDTH = 4u;
const uint LUMEN_TRAIL_FLAG_SIZE_AFFECTS_LIFETIME = 8u;
const uint LUMEN_TRAIL_FLAG_INHERIT_COLOR = 16u;
const uint LUMEN_TRAIL_FLAG_VERTICAL3D = 32u;
const uint LUMEN_TRAIL_FLAG_EMITTER_WORLDSPACE = 64u;

// 80 bytes (5 * 16B), std430 matching C# GpuTrailHeader.
struct GpuTrailHeader
{
    vec4 LiveHeadAndTotalLength;
    vec4 ClippedTailAndWidth;
    vec4 Color;
    uint RingHead;
    uint RingCount;
    uint VisibleCount;
    uint Flags;
    uint ParticleSlot;
    uint SpawnAnchorXZ;
    float HeadCumDistance;
    float ClippedTailCumDist;
};

// 32 bytes (2 * 16B), std430 matching C# GpuTrailPoint.
struct GpuTrailPoint
{
    vec4 PositionAndRemaining;
    vec4 DistanceAndLifetime;
};

// 160 bytes (10 * 16B), std430 matching C# GpuTrailEmitterParams.
struct GpuTrailEmitterParams
{
    mat4 World;
    vec4 Ranges; // x: HeaderOffset, y: HeaderCapacity, z: PointOffset, w: ParticleSlotOffset
    vec4 PointsAndCurve; // x: MaxPoints, y: CurveRowBase, z: Ratio, w: LifetimeMultiplier
    vec4 Params; // x: MinVertexDistance, y: Width, z: SoftParticleFadeDistance, w: NodeSizeScale
    vec4 Appearance; // x: TextureMode, y: Alignment, z: HdrIntensity, w: IntensityMultiplier
    vec4 DepthAndParticle; // x: DepthMaskOn, y: DepthReferenceZ, z: ParticleCapacity, w: ParticleEmitterIndex
    uint MapBase;
    uint Flags;
    uint BankCount;
    uint RandomSeed;
};

// Mirrors ParticleTrailMath.Hash & RollRatio.
uint lumenTrailHash(uint value)
{
    value ^= value >> 16u;
    value *= 0x7FEB352Du;
    value ^= value >> 15u;
    value *= 0x846CA68Bu;
    return value ^ (value >> 16u);
}

bool lumenTrailRollRatio(uint emitterSeed, uint particleSeed, uint localSlot, float ratio)
{
    if (ratio >= 1.0) return true;
    if (ratio <= 0.0) return false;
    uint h = lumenTrailHash(emitterSeed ^ lumenTrailHash(particleSeed ^ (localSlot * 0x9E3779B9u)));
    return (float(h) * (1.0 / 4294967296.0)) < ratio;
}

GpuCurveSample lumenTrailInterpolateSample(GpuCurveSample a, GpuCurveSample b, float blend)
{
    return GpuCurveSample(mix(a.Color, b.Color, blend), mix(a.Size, b.Size, blend));
}

#endif
