#ifndef LUMEN_PARTICLE_TYPES
#define LUMEN_PARTICLE_TYPES

// std430 matches the sequential C# structs: 64 / 256 / 48 / 32 bytes.
struct GpuParticle
{
    vec4 PositionAndAge;
    vec4 VelocityAndLifetime;
    vec4 StartColor;
    vec4 SizeSeedEmitterFlags;
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
    vec4 ShapePosition;
    vec4 ShapeRotation;
    vec4 ShapeScale;
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

#endif
