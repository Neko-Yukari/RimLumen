#ifndef LUMEN_PARTICLE_TYPES
#define LUMEN_PARTICLE_TYPES

// std430 matches the sequential C# structs: 64 / 208 / 48 / 32 bytes.
struct GpuParticle
{
    vec4 PositionAndAge;
    vec4 VelocityAndLifetime;
    vec4 StartColor;
    vec4 SizeSeedEmitterFlags;
};

struct GpuEmitterParams
{
    // Upload System.Numerics.Matrix4x4 verbatim. Its row-major row-vector
    // bytes become GLSL columns: World * p already performs the CPU transform.
    // Do NOT transpose on either side a second time.
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
