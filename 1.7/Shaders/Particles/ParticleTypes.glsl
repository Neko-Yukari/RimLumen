#ifndef LUMEN_PARTICLE_TYPES
#define LUMEN_PARTICLE_TYPES

// std430 matches the sequential C# structs: 64 / 320 / 48 / 32 bytes.
struct GpuParticle
{
    vec4 PositionAndAge;
    vec4 VelocityAndLifetime;
    vec4 StartColor;
    float Size;
    uint Seed;
    uint ActiveEmitter; // 0 dead; emitter index + 1 while alive
    uint SpawnAnchorXZ; // world-space birth ground anchor, lumenPackAnchor
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
    vec4 ShapePosition; // w: play-origin X (Vertical3D ground anchor)
    vec4 ShapeRotation;
    vec4 ShapeScale; // w: play-origin Z (Vertical3D ground anchor)
    vec4 ShapeOptions; // 256: radiusThickness, arc radians, arcSpread, radiusSpread
    vec4 ShapeMix; // 272: randomDirection, sphericalDirection, randomPosition, coneLength
    vec4 BoxThicknessDonut; // 288: boxThickness xyz, donutRadius
    vec4 WorldRotation; // 304: unit world quaternion xyzw
};

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
