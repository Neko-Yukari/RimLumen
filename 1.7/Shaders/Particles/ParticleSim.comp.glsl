#version 450
#include "ParticleTypes.glsl"

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;
layout(std430, set = 0, binding = 0) buffer ParticleStorage { GpuParticle particles[]; };
layout(std430, set = 0, binding = 1) readonly buffer EmitterStorage { GpuEmitterParams emitters[]; };
layout(std430, set = 0, binding = 2) readonly buffer FieldStorage { GpuForceField fields[]; };
layout(push_constant) uniform GpuSimPushConstants
{
    uint emitterIndex;
    uint frameSeed;
    uint pad0;
    uint pad1;
} sim;

shared uint deadPrefix[64];
shared uint remainingBudget;

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

vec3 sphereDirection(inout uint state)
{
    float y = 2.0 * randomUnit(state) - 1.0;
    float angle = 6.28318530718 * randomUnit(state);
    float radius = sqrt(max(0.0, 1.0 - y * y));
    return vec3(radius * cos(angle), y, radius * sin(angle));
}

GpuParticle spawnParticle(GpuEmitterParams emitter, uint slot)
{
    uint seed = uint(emitter.Tables.w) ^ sim.frameSeed ^ (slot * 0x27d4eb2du);
    vec3 position = vec3(0.0);
    vec3 direction = vec3(0.0, 1.0, 0.0);
    uint shape = uint(emitter.Shape.x);
    vec3 extents = emitter.Shape.yzw;
    if (shape == 1u)
    {
        direction = sphereDirection(seed);
        position = direction * pow(randomUnit(seed), 1.0 / 3.0) * extents;
    }
    else if (shape == 2u || shape == 3u)
    {
        float angle = 6.28318530718 * randomUnit(seed);
        float radius = sqrt(randomUnit(seed));
        vec3 radial = vec3(cos(angle), 0.0, sin(angle));
        position = radial * radius * extents;
        if (shape == 2u)
            direction = radial;
        else
        {
            float cosTheta = mix(1.0, cos(emitter.SizeCone.z), randomUnit(seed));
            direction = radial * sqrt(max(0.0, 1.0 - cosTheta * cosTheta));
            direction.y = cosTheta;
        }
    }
    else if (shape == 4u)
    {
        float x = randomUnit(seed);
        float y = randomUnit(seed);
        float z = randomUnit(seed);
        position = (vec3(x, y, z) * 2.0 - 1.0) * extents;
    }
    float speed = mix(emitter.Ranges.x, emitter.Ranges.y, randomUnit(seed));
    vec3 velocity = direction * speed;
    if (emitter.SizeCone.w > 0.5)
    {
        position = (emitter.World * vec4(position, 1.0)).xyz;
        velocity = (emitter.World * vec4(velocity, 0.0)).xyz;
    }
    GpuParticle particle;
    particle.PositionAndAge = vec4(position, 0.0);
    particle.VelocityAndLifetime = vec4(velocity, mix(emitter.Ranges.z, emitter.Ranges.w, randomUnit(seed)));
    particle.StartColor = emitter.StartColor;
    particle.SizeSeedEmitterFlags = vec4(mix(emitter.SizeCone.x, emitter.SizeCone.y, randomUnit(seed)),
        float(seed & 0x00ffffffu), float(sim.emitterIndex), 1.0);
    return particle;
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

vec3 acceleration(GpuEmitterParams emitter, vec3 position, out float drag)
{
    vec3 total = vec3(0.0);
    drag = 0.0;
    for (uint i = 0u; i < uint(emitter.Tables.z); ++i)
    {
        GpuForceField field = fields[uint(emitter.Tables.y) + i];
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
            // Center is the simulation origin. Parameters.x pulls inward (negative pushes), y lifts along axis.
            vec3 radial = position - dot(position, axis) * axis;
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
            vec3 delta = axisOrCenter - position;
            float distanceToCenter = length(delta);
            float weight = radius > 0.0 ? max(0.0, 1.0 - distanceToCenter / radius) : 1.0;
            total += lumenDirection(delta, vec3(0.0)) * (strength * weight);
        }
    }
    return total;
}

void main()
{
    // Exactly ONE workgroup per emitter. All lanes cross every barrier, including tail lanes.
    // Cursor-ordered full dead scan: fills min(budget, dead count), never drops budget merely
    // because a candidate is live. No readback, atomics, or cross-workgroup synchronization.
    GpuEmitterParams emitter = emitters[sim.emitterIndex];
    uint capacity = uint(emitter.Slots.y);
    uint lane = gl_LocalInvocationID.x;
    if (lane == 0u)
        remainingBudget = min(uint(emitter.Slots.z), capacity);
    barrier();
    for (uint chunk = 0u; chunk < capacity; chunk += 64u)
    {
        uint ordinal = chunk + lane;
        bool valid = ordinal < capacity;
        uint slot = uint(emitter.Slots.x) + ((uint(emitter.Slots.w) + ordinal) % capacity);
        GpuParticle particle = GpuParticle(vec4(0.0), vec4(0.0), vec4(0.0), vec4(0.0));
        bool dead = false;
        if (valid)
        {
            if (emitter.State.z <= 0.5)
                particle = particles[slot];
            if (particle.SizeSeedEmitterFlags.w > 0.5)
            {
                particle.PositionAndAge.w += emitter.State.x;
                if (particle.PositionAndAge.w >= particle.VelocityAndLifetime.w)
                    particle.SizeSeedEmitterFlags.w = 0.0;
                else
                {
                    float drag;
                    vec3 force = acceleration(emitter, particle.PositionAndAge.xyz, drag);
                    particle.VelocityAndLifetime.xyz = (particle.VelocityAndLifetime.xyz + force * emitter.State.x)
                        * exp(-drag * emitter.State.x);
                    particle.PositionAndAge.xyz += particle.VelocityAndLifetime.xyz * emitter.State.x;
                    if (sim.pad0 != 0u)
                    {
                        // KillBelowFloor: pad1 carries the floor height (float bits); expire descending particles below it.
                        float floorY = uintBitsToFloat(sim.pad1);
                        bool worldSpace = emitter.SizeCone.w > 0.5;
                        float worldY = worldSpace ? particle.PositionAndAge.y
                            : (emitter.World * vec4(particle.PositionAndAge.xyz, 1.0)).y;
                        float worldVelY = worldSpace ? particle.VelocityAndLifetime.y
                            : (emitter.World * vec4(particle.VelocityAndLifetime.xyz, 0.0)).y;
                        if (worldY < floorY && worldVelY < 0.0)
                            particle.SizeSeedEmitterFlags.w = 0.0;
                    }
                }
            }
            dead = particle.SizeSeedEmitterFlags.w < 0.5;
        }
        deadPrefix[lane] = dead ? 1u : 0u;
        barrier();
        for (uint offset = 1u; offset < 64u; offset <<= 1u)
        {
            uint preceding = lane >= offset ? deadPrefix[lane - offset] : 0u;
            barrier();
            deadPrefix[lane] += preceding;
            barrier();
        }
        if (valid)
        {
            if (dead && deadPrefix[lane] <= remainingBudget)
                particle = spawnParticle(emitter, slot);
            particles[slot] = particle;
        }
        barrier();
        if (lane == 0u)
            remainingBudget -= min(remainingBudget, deadPrefix[63]);
        barrier();
    }
}
