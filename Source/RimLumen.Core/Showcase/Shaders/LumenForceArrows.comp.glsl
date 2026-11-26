#version 450
#include "../../../../1.7/Shaders/Particles/ParticleTypes.glsl"
#include "LumenForceArrowTypes.glsl"

// One invocation per sampled particle: the first live slot of its window, decomposed into velocity and one arrow per
// force channel through the simulation's own lumenAccumulateFieldForce. Reads the L2 buffers in place; no readback.
layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;
layout(std430, set = 0, binding = 0) readonly buffer ParticleStorage { GpuParticle particles[]; };
layout(std430, set = 0, binding = 1) readonly buffer EmitterStorage { GpuEmitterParams emitters[]; };
layout(std430, set = 0, binding = 2) readonly buffer FieldStorage { GpuForceField fields[]; };
layout(std430, set = 0, binding = 3) writeonly buffer ArrowStorage { GpuForceArrow arrows[]; };
layout(push_constant) uniform ForceArrowPush
{
    uint emitterCount;
    uint pad0;
    uint pad1;
    uint pad2;
} push;

#include "../../../../1.7/Shaders/Particles/ParticleForces.glsl"

void writeArrow(uint index, uint channel, vec3 start, vec3 vector, float scale, vec2 anchor)
{
    float magnitude = length(vector);
    if (magnitude < 1e-4)
    {
        arrows[index] = GpuForceArrow(vec4(0.0), vec4(0.0), vec4(0.0));
        return;
    }
    float drawn = min(magnitude * scale, LUMEN_ARROW_MAX_LENGTH);
    arrows[index] = GpuForceArrow(vec4(start, float(channel + 1u)), vec4(vector * (drawn / magnitude), drawn),
        vec4(anchor, 0.0, 0.0));
}

void main()
{
    uint id = gl_GlobalInvocationID.x;
    uint row = id / LUMEN_ARROW_SAMPLES;
    if (row >= push.emitterCount) return;
    uint sampleIndex = id % LUMEN_ARROW_SAMPLES;
    uint base = id * LUMEN_ARROW_CHANNELS;
    GpuEmitterParams emitter = emitters[row];
    uint offset = uint(emitter.Slots.x), capacity = uint(emitter.Slots.y);
    uint window = max(1u, capacity / LUMEN_ARROW_SAMPLES);
    uint first = sampleIndex * window, last = min(first + window, capacity);
    int found = -1;
    for (uint s = first; s < last; ++s)
    {
        GpuParticle candidate = particles[offset + s];
        if (candidate.ActiveEmitter != 0u && candidate.PositionAndAge.w < candidate.VelocityAndLifetime.w)
        {
            found = int(s);
            break;
        }
    }
    if (found < 0)
    {
        for (uint c = 0u; c < LUMEN_ARROW_CHANNELS; ++c)
            arrows[base + c] = GpuForceArrow(vec4(0.0), vec4(0.0), vec4(0.0));
        return;
    }
    GpuParticle particle = particles[offset + uint(found)];
    vec3 position = particle.PositionAndAge.xyz;
    vec3 velocity = particle.VelocityAndLifetime.xyz;
    vec3 terms[7] = vec3[7](vec3(0.0), vec3(0.0), vec3(0.0), vec3(0.0), vec3(0.0), vec3(0.0), vec3(0.0));
    float drag = 0.0;
    for (uint i = 0u; i < uint(emitter.Tables.z); ++i)
    {
        GpuForceField field = fields[uint(emitter.Tables.y) + i];
        vec3 term = vec3(0.0);
        lumenAccumulateFieldForce(emitter, field, position, term, drag);
        terms[lumenArrowChannelOfKind(uint(field.CenterOrAxisAndKind.w))] += term;
    }
    // The integration multiplies velocity by exp(-drag * dt): its instantaneous acceleration is -drag * velocity.
    terms[LUMEN_ARROW_DRAG] = -drag * velocity;
    terms[LUMEN_ARROW_VELOCITY] = velocity;
    bool world = emitter.SizeCone.w > 0.5;
    vec3 start = world ? position : (emitter.World * vec4(position, 1.0)).xyz;
    vec2 anchor = world ? lumenUnpackAnchor(particle.SpawnAnchorXZ) : lumenNodeGroundXZ(emitter.World);
    for (uint c = 0u; c < LUMEN_ARROW_CHANNELS; ++c)
    {
        vec3 vector = world ? terms[c] : (emitter.World * vec4(terms[c], 0.0)).xyz;
        writeArrow(base + c, c, start, vector,
            c == LUMEN_ARROW_VELOCITY ? LUMEN_ARROW_VELOCITY_SCALE : LUMEN_ARROW_FORCE_SCALE, anchor);
    }
}
