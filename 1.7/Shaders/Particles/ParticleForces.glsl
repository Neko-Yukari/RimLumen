#ifndef LUMEN_PARTICLE_FORCES
#define LUMEN_PARTICLE_FORCES
#include "ParticleTypes.glsl"

// Per-field force terms of the L2 integration in ParticleSim.comp.glsl. CPU twin: Scene/ParticleForceMath.cs.

uint randomBits(inout uint state)
{
    state += 0x9e3779b9u;
    uint value = state;
    value = (value ^ (value >> 16u)) * 0x85ebca6bu;
    value = (value ^ (value >> 13u)) * 0xc2b2ae35u;
    return value ^ (value >> 16u);
}

float randomUnit(inout uint state)
{
    return float(randomBits(state) >> 8u) * (1.0 / 16777216.0);
}

float latticeValue(ivec3 cell, uint channel)
{
    uint state = uint(cell.x) * 0x8da6b343u ^ uint(cell.y) * 0xd8163841u
        ^ uint(cell.z) * 0xcb1ab31fu ^ channel;
    return 2.0 * randomUnit(state) - 1.0;
}

vec3 potentialGradient(vec3 point, uint channel)
{
    ivec3 cell = ivec3(floor(point));
    vec3 t = fract(point);
    // Quintic interpolation is C2 across cells; the analytic gradient is continuous.
    vec3 interpolation = t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
    vec3 derivative = 30.0 * t * t * (t - 1.0) * (t - 1.0);
    vec3 gradient = vec3(0.0);
    for (int corner = 0; corner < 8; ++corner)
    {
        ivec3 bit = ivec3(corner & 1, (corner >> 1) & 1, (corner >> 2) & 1);
        vec3 sign = vec3(bit) * 2.0 - 1.0;
        vec3 weight = mix(vec3(1.0) - interpolation, interpolation, vec3(bit));
        vec3 slope = sign * derivative;
        gradient += latticeValue(cell + bit, channel)
            * vec3(slope.x * weight.y * weight.z, weight.x * slope.y * weight.z, weight.x * weight.y * slope.z);
    }
    return gradient;
}

vec3 ringWave(vec3 delta, float waveRadius, float halfThickness, float endRadius, float strength)
{
    if (waveRadius > endRadius) return vec3(0.0);
    float weight = max(0.0, 1.0 - abs(length(delta) - waveRadius) / halfThickness);
    return lumenDirection(delta, vec3(0.0)) * (strength * weight);
}

// Adds one field's acceleration to `total` and its linear drag rate to `drag`, in the particle's simulation space.
void lumenAccumulateFieldForce(GpuEmitterParams emitter, GpuForceField field, vec3 position, inout vec3 total, inout float drag)
{
    uint kind = uint(field.CenterOrAxisAndKind.w);
    vec3 axisOrCenter = field.CenterOrAxisAndKind.xyz;
    vec3 parameters = field.ParametersAndRadius.xyz;
    float radius = field.ParametersAndRadius.w;
    float strength = field.StrengthAndFrequency.x;
    if (kind == 0u)
    {
        total += axisOrCenter * strength;
        drag += max(0.0, parameters.x);
    }
    else if (kind == 1u)
    {
        vec3 axis = lumenDirection(axisOrCenter, vec3(0.0, 1.0, 0.0));
        // Local origin follows the node even for world-space particles; the authored axis does not rotate.
        vec3 center = emitter.SizeCone.w > 0.5 ? (emitter.World * vec4(0.0, 0.0, 0.0, 1.0)).xyz : vec3(0.0);
        vec3 relative = position - center;
        vec3 radial = relative - dot(relative, axis) * axis;
        float distanceToAxis = length(radial);
        float weight = radius > 0.0 ? max(0.0, 1.0 - distanceToAxis / radius) : 1.0;
        vec3 radialDirection = lumenDirection(radial, vec3(0.0));
        total += (cross(axis, radialDirection) * strength - radialDirection * parameters.x
            + axis * parameters.y) * weight;
    }
    else if (kind == 2u)
    {
        // Independent smooth value-noise channels form a vector potential. Analytic curl,
        // including the frequency chain rule, is divergence-free (no spatial envelope).
        float frequency = field.StrengthAndFrequency.y;
        vec3 q = (position - parameters) * frequency;
        vec3 dx = potentialGradient(q, 0x68bc21ebu);
        vec3 dy = potentialGradient(q, 0x02e5be93u);
        vec3 dz = potentialGradient(q, 0x967a889bu);
        vec3 curl = vec3(dz.y - dy.z, dx.z - dz.x, dy.x - dx.y);
        total += curl * (strength * frequency);
    }
    else if (kind == 3u)
    {
        vec3 center = emitter.SizeCone.w > 0.5 ? (emitter.World * vec4(axisOrCenter, 1.0)).xyz : axisOrCenter;
        vec3 delta = center - position;
        float distanceToCenter = length(delta);
        float weight = radius > 0.0 ? max(0.0, 1.0 - distanceToCenter / radius) : 1.0;
        total += lumenDirection(delta, vec3(0.0)) * (strength * weight);
    }
    else if (kind == 4u)
    {
        vec3 center = emitter.SizeCone.w > 0.5 ? (emitter.World * vec4(axisOrCenter, 1.0)).xyz : axisOrCenter;
        total += ringWave(position - center, parameters.x * emitter.WorldPhysics.x, parameters.y, radius, strength);
    }
    else
    {
        bool world = emitter.SizeCone.w > 0.5;
        vec3 worldPosition = world ? position : (emitter.World * vec4(position, 1.0)).xyz;
        vec3 delta = worldPosition - axisOrCenter, force;
        if (kind == 5u) force = ringWave(delta, parameters.x, parameters.y, radius, strength);
        else
        {
            if (kind == 6u) delta.y = 0.0;
            float distance = length(delta);
            force = distance > 1e-6 ? delta / distance * (strength * max(0.0, 1.0 - distance / radius)) : vec3(0.0);
        }
        total += world ? force : (emitter.WorldInverse * vec4(force, 0.0)).xyz;
    }
}

#endif
