#ifndef LUMEN_PARTICLE_TYPES
#define LUMEN_PARTICLE_TYPES

const float LUMEN_GROUND_Y = 0.0;

// std430 matches the sequential C# structs: 64 / 480 / 48 / 32 bytes.
struct GpuParticle
{
    vec4 PositionAndAge;
    vec4 VelocityAndLifetime;
    vec4 StartColor;
    float Size;
    uint Seed;
    uint ActiveEmitter; // 0 dead; emitter index + 1 while alive
    uint SpawnAnchorXZ; // world-space birth ground anchor (node, or sub-emitter event point), lumenPackAnchor
};

struct GpuEmitterParams
{
    // Slang emits RowMajor SSBO storage and matrix * column-vector operations.
    // The CPU transposes System.Numerics row-vector scene transforms once at upload.
    // Camera ViewProj already uses the host's column-vector convention; it is not transposed.
    mat4 World;
    vec4 Slots;
    vec4 Shape;
    vec4 Ranges;
    vec4 SizeCone;
    vec4 StartColor;
    vec4 Flipbook;
    vec4 Appearance;
    vec4 Tables;
    vec4 State;
    vec4 ShapePosition; // w: LumenDepthMask on (1) or off (0)
    vec4 ShapeRotation;
    vec4 ShapeScale; // w: LumenDepthMask reference Z
    vec4 ShapeOptions; // 256: radiusThickness, arc radians, arcSpread, radiusSpread
    vec4 ShapeMix; // 272: randomDirection, sphericalDirection, randomPosition, coneLength
    vec4 BoxThicknessDonut; // 288: boxThickness xyz, donutRadius
    vec4 WorldRotation; // 304: unit world quaternion xyzw
    mat4 WorldInverse;
    vec4 WorldPhysics;
    vec4 Collision;
    vec4 Map;
    vec4 Events;
    uint DensityQ;
    uint ResetVersion;
    uint ExplicitOffset;
    uint ExplicitCount;
    uint SourceLinkOffset;
    uint SourceLinkCount;
    uint StateSlot;
    uint EmitEvents;
};

struct GpuSubEvent
{
    vec4 PositionAndTrigger;
    vec4 Color;
    vec4 Size;
};

struct GpuSubLink
{
    uint Source;
    uint Trigger;
    uint Count;
    uint Inherit;
    float Probability;
    float Padding0;
    float Padding1;
    float Padding2;
};

struct GpuBurstState { uint Debt; uint ResetVersion; uint Budget; uint EventCount; };
struct GpuExplicitBurst { uint Count; uint DensityQ; uint ResetVersion; uint Padding; };

struct GpuForceField
{
    vec4 CenterOrAxisAndKind;
    vec4 ParametersAndRadius;
    vec4 StrengthAndFrequency;
};

struct GpuCurveSample
{
    vec4 Color;
    vec4 Size;
};

vec3 lumenDirection(vec3 value, vec3 fallback)
{
    float squaredLength = dot(value, value);
    return squaredLength > 1e-12 ? value * inversesqrt(squaredLength) : fallback;
}

// A node's ground anchor: the XZ of its world translation. Mirrors CameraBillboardBasis.NodeGroundXZ.
vec2 lumenNodeGroundXZ(mat4 world)
{
    return (world * vec4(0.0, 0.0, 0.0, 1.0)).xz;
}

// Ground anchor as two int16 in 1/32 cell units (range [-1024, 1024) cells), X low and Z high. Kept as uint so no
// bit pattern passes through a float register.
uint lumenPackAnchor(vec2 anchorXZ)
{
    ivec2 q = ivec2(clamp(round(anchorXZ * 32.0), vec2(-32768.0), vec2(32767.0)));
    return (uint(q.x) & 0xffffu) | (uint(q.y) << 16u);
}

vec2 lumenUnpackAnchor(uint packedXZ)
{
    return vec2(float(int(packedXZ << 16u) >> 16), float(int(packedXZ) >> 16)) * (1.0 / 32.0);
}

#endif
