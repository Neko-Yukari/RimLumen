#version 450
#include "ParticleTypes.glsl"

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;
layout(std430, set = 0, binding = 0) buffer ParticleStorage { GpuParticle particles[]; };
layout(std430, set = 0, binding = 1) readonly buffer EmitterStorage { GpuEmitterParams emitters[]; };
layout(std430, set = 0, binding = 2) readonly buffer FieldStorage { GpuForceField fields[]; };
layout(std430, set = 0, binding = 3) buffer DeadBlockStorage { uint deadBlocks[]; };
layout(std430, set = 0, binding = 4) readonly buffer MapStorage { uint mapCells[]; };
layout(std430, set = 0, binding = 5) buffer EventStorage { GpuSubEvent events[]; };
layout(std430, set = 0, binding = 6) buffer BurstStorage { GpuBurstState burstStates[]; };
layout(std430, set = 0, binding = 7) readonly buffer LinkStorage { GpuSubLink links[]; };
layout(std430, set = 0, binding = 8) readonly buffer CurveStorage { GpuCurveSample curves[]; };
layout(std430, set = 0, binding = 9) readonly buffer ExplicitStorage { GpuExplicitBurst explicitBursts[]; };
#include "ParticleCollision.glsl"
#include "ParticleBurstBudget.glsl"
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

vec3 rotateShape(vec4 quaternion, vec3 value)
{
    return value + 2.0 * cross(quaternion.xyz, cross(quaternion.xyz, value) + quaternion.w * value);
}

// RimUnity ParticleSystem.Shapes.cs, upstream 45ca90987f7d3721715b45c021cab986ee14c468; mapped from +Z to +Y.
float shapePhase(float spread, inout uint state)
{
    float phase = randomUnit(state);
    return spread > 0.0 ? floor(phase / spread) * spread : phase;
}

vec3 shapeNormalize(vec3 value)
{
    float len = length(value);
    return len > 1e-5 ? value / len : vec3(0.0);
}

float radialSample(float thickness, inout uint state)
{
    float inner = 1.0 - thickness;
    return sqrt(mix(inner * inner, 1.0, randomUnit(state)));
}

GpuParticle spawnParticle(GpuEmitterParams emitter, uint slot)
{
    uint seed = uint(emitter.Tables.w) ^ sim.frameSeed ^ (slot * 0x27d4eb2du);
    vec3 position = vec3(0.0);
    vec3 direction = vec3(0.0, 1.0, 0.0);
    uint shape = uint(emitter.Shape.x);
    vec3 extents = emitter.Shape.yzw;
    float angle = shapePhase(emitter.ShapeOptions.z, seed) * emitter.ShapeOptions.y;
    vec3 radial = vec3(cos(angle), 0.0, -sin(angle));
    if (shape == 1u || shape == 5u || shape == 6u || shape == 7u)
    {
        float y = 2.0 * randomUnit(seed) - 1.0;
        if (shape == 6u || shape == 7u) y = abs(y);
        direction = radial * sqrt(max(0.0, 1.0 - y * y)) + vec3(0.0, y, 0.0);
        float inner = 1.0 - emitter.ShapeOptions.x;
        float radius = shape == 5u || shape == 7u ? 1.0 : pow(mix(inner * inner * inner, 1.0, randomUnit(seed)), 1.0 / 3.0);
        position = direction * radius * extents;
    }
    else if (shape == 2u || shape == 8u)
    {
        float radius = shape == 8u ? 1.0 : radialSample(emitter.ShapeOptions.x, seed);
        position = radial * radius * extents;
        direction = radial;
    }
    else if (shape == 3u || shape == 9u || shape == 10u || shape == 11u)
    {
        float radius = shape == 9u || shape == 11u ? 1.0 : radialSample(emitter.ShapeOptions.x, seed);
        direction = shapeNormalize(radial * (sin(emitter.SizeCone.z) * radius) + vec3(0.0, cos(emitter.SizeCone.z), 0.0));
        position = radial * radius * extents;
        if (shape == 10u || shape == 11u)
            position += direction * (randomUnit(seed) * emitter.ShapeMix.w / max(0.001, direction.y));
    }
    else if (shape == 4u || shape == 13u || shape == 14u)
    {
        vec3 sample = vec3(randomUnit(seed), randomUnit(seed), randomUnit(seed)) * 2.0 - 1.0;
        if (shape != 4u)
        {
            vec3 extent = abs(extents * emitter.ShapeScale.xyz);
            vec3 weights = shape == 14u ? extent : vec3(extent.y * extent.z, extent.x * extent.z, extent.x * extent.y);
            float total = weights.x + weights.y + weights.z;
            float pick = randomUnit(seed) * total;
            int axis = total == 0.0 || pick < weights.x ? 0 : pick < weights.x + weights.y ? 1 : 2;
            for (int i = 0; i < 3; ++i)
            {
                if (shape == 14u ? i != axis : i == axis)
                {
                    float offset = 1.0 - randomUnit(seed) * emitter.BoxThicknessDonut[i];
                    sample[i] = randomUnit(seed) < 0.5 ? -offset : offset;
                }
            }
        }
        position = sample * extents;
    }
    else if (shape == 12u)
    {
        position.x = (shapePhase(emitter.ShapeOptions.w, seed) * 2.0 - 1.0) * extents.x;
        direction = vec3(0.0, 0.0, -1.0);
    }
    else if (shape == 15u)
    {
        float tubeAngle = randomUnit(seed) * 6.28318530718;
        float tubeRadius = emitter.BoxThicknessDonut.w * radialSample(emitter.ShapeOptions.x, seed);
        direction = radial * cos(tubeAngle) + vec3(0.0, sin(tubeAngle), 0.0);
        position = radial * extents + direction * tubeRadius;
    }
    else if (shape == 16u)
        position = vec3((randomUnit(seed) * 2.0 - 1.0) * extents.x, 0.0, (randomUnit(seed) * 2.0 - 1.0) * extents.z);

    position = rotateShape(emitter.ShapeRotation, position * emitter.ShapeScale.xyz) + emitter.ShapePosition.xyz;
    direction = rotateShape(emitter.ShapeRotation, direction);
    if (emitter.ShapeMix.x != 0.0)
        direction = shapeNormalize(mix(direction, sphereDirection(seed), emitter.ShapeMix.x));
    vec3 offset = position - emitter.ShapePosition.xyz;
    if (emitter.ShapeMix.y != 0.0 && dot(offset, offset) > 0.0)
        direction = shapeNormalize(mix(direction, shapeNormalize(offset), emitter.ShapeMix.y));
    if (emitter.ShapeMix.z != 0.0)
        position += sphereDirection(seed) * (pow(randomUnit(seed), 1.0 / 3.0) * emitter.ShapeMix.z);
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
    float sizeScale = emitter.SizeCone.w > 0.5 ? emitter.Appearance.w : 1.0;
    particle.Size = mix(emitter.SizeCone.x, emitter.SizeCone.y, randomUnit(seed)) * sizeScale;
    particle.Seed = seed;
    particle.ActiveEmitter = sim.emitterIndex + 1u;
    particle.SpawnAnchorXZ = lumenPackAnchor(lumenNodeGroundXZ(emitter.World));
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

vec3 ringWave(vec3 delta, float waveRadius, float halfThickness, float endRadius, float strength)
{
    if (waveRadius > endRadius) return vec3(0.0);
    float weight = max(0.0, 1.0 - abs(length(delta) - waveRadius) / halfThickness);
    return lumenDirection(delta, vec3(0.0)) * (strength * weight);
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
                delta.y = 0.0;
                float distance = length(delta);
                force = distance > 1e-6 ? delta / distance * (strength * max(0.0, 1.0 - distance / radius)) : vec3(0.0);
            }
            total += world ? force : (emitter.WorldInverse * vec4(force, 0.0)).xyz;
        }
    }
    return total;
}

void appendEvent(GpuEmitterParams emitter, GpuParticle particle, uint trigger)
{
    if (emitter.EmitEvents == 0u) return;
    float t = min(particle.PositionAndAge.w / particle.VelocityAndLifetime.w, 1.0) * 63.0;
    uint lower = uint(t), row = uint(emitter.Tables.x) * 64u;
    GpuCurveSample a = curves[row + lower], b = curves[row + min(lower + 1u, 63u)];
    bool world = emitter.SizeCone.w > 0.5;
    GpuSubEvent ev;
    ev.PositionAndTrigger = vec4(world ? particle.PositionAndAge.xyz : (emitter.World * vec4(particle.PositionAndAge.xyz, 1.0)).xyz, float(trigger));
    ev.Color = particle.StartColor * mix(a.Color, b.Color, fract(t));
    ev.Size = vec4(particle.Size * mix(a.Size.x, b.Size.x, fract(t)) * (world ? 1.0 : emitter.Appearance.w), 0, 0, 0);
    uint index = atomicAdd(burstStates[emitter.StateSlot].EventCount, 1u);
    events[uint(emitter.Events.x) + index] = ev;
}

GpuParticle integrateParticle(GpuEmitterParams emitter, uint slot)
{
    GpuParticle particle = GpuParticle(vec4(0.0), vec4(0.0), vec4(0.0), 0.0, 0u, 0u, 0u);
    if (emitter.State.z <= 0.5)
        particle = particles[slot];
    if (particle.ActiveEmitter != 0u)
    {
        particle.PositionAndAge.w += emitter.State.x;
        if (particle.PositionAndAge.w >= particle.VelocityAndLifetime.w)
            particle.ActiveEmitter = 0u;
        else
        {
            float drag;
            vec3 force = acceleration(emitter, particle.PositionAndAge.xyz, drag);
            particle.VelocityAndLifetime.xyz = (particle.VelocityAndLifetime.xyz + force * emitter.State.x)
                * exp(-drag * emitter.State.x);
            bool world = emitter.SizeCone.w > 0.5;
            vec3 from = world ? particle.PositionAndAge.xyz : (emitter.World * vec4(particle.PositionAndAge.xyz, 1.0)).xyz;
            particle.PositionAndAge.xyz += particle.VelocityAndLifetime.xyz * emitter.State.x;
            if (emitter.Map.y > 0.0)
            {
                vec3 to = world ? particle.PositionAndAge.xyz : (emitter.World * vec4(particle.PositionAndAge.xyz, 1.0)).xyz;
                vec3 position, normal;
                uint hit = traceMap(emitter, from, to, position, normal);
                if (hit != 0u)
                {
                    vec3 velocity = world ? particle.VelocityAndLifetime.xyz : (emitter.World * vec4(particle.VelocityAndLifetime.xyz, 0.0)).xyz;
                    bool collision = hit == 1u && emitter.Collision.x == 2.0;
                    if (hit == 2u || emitter.Collision.x == 2.0) particle.PositionAndAge.w = particle.VelocityAndLifetime.w;
                    else
                    {
                        // RimUnity ParticleSystem.Collision.cs:103-115 @45ca90987f7d3721715b45c021cab986ee14c468.
                        float approach = dot(velocity, normal);
                        if (approach < 0.0)
                        {
                            collision = true;
                            velocity = (velocity - normal * ((1.0 + emitter.Collision.y) * approach)) * (1.0 - emitter.Collision.z);
                            particle.PositionAndAge.w += particle.VelocityAndLifetime.w * emitter.Collision.w;
                        }
                    }
                    particle.PositionAndAge.xyz = world ? position : (emitter.WorldInverse * vec4(position, 1.0)).xyz;
                    particle.VelocityAndLifetime.xyz = world ? velocity : (emitter.WorldInverse * vec4(velocity, 0.0)).xyz;
                    if (collision) appendEvent(emitter, particle, 1u);
                    if (particle.PositionAndAge.w >= particle.VelocityAndLifetime.w) particle.ActiveEmitter = 0u;
                }
            }
            if ((sim.pad0 & 1u) != 0u)
            {
                float worldY = world ? particle.PositionAndAge.y
                    : (emitter.World * vec4(particle.PositionAndAge.xyz, 1.0)).y;
                float worldVelY = world ? particle.VelocityAndLifetime.y
                    : (emitter.World * vec4(particle.VelocityAndLifetime.xyz, 0.0)).y;
                if (worldY < LUMEN_GROUND_Y && worldVelY < 0.0)
                    particle.ActiveEmitter = 0u;
            }
        }
        if (particle.ActiveEmitter == 0u) appendEvent(emitter, particle, 0u);
    }
    return particle;
}

void consumeEvents(GpuEmitterParams emitter)
{
    uint debt = burstStates[emitter.StateSlot].Debt;
    uint cursor = 0u, capacity = uint(emitter.Slots.y), baseSlot = uint(emitter.Slots.x);
    uint random = uint(emitter.Tables.w) ^ sim.frameSeed ^ 0x68bc21ebu;
    for (uint i = 0u; i < emitter.SourceLinkCount; ++i)
    {
        GpuSubLink link = links[emitter.SourceLinkOffset + i];
        GpuEmitterParams source = emitters[link.Source];
        uint count = burstStates[source.StateSlot].EventCount;
        for (uint j = 0u; j < count; ++j)
        {
            GpuSubEvent ev = events[uint(source.Events.x) + j];
            if (uint(ev.PositionAndTrigger.w) != link.Trigger || randomUnit(random) >= link.Probability) continue;
            uint requested = acceptBurst(link.Count, emitter.DensityQ, debt);
            // Reuse capture's one world/local conversion decision; a dropped birth does not refund carry.
            if (emitter.WorldPhysics.y == 0.0) continue;
            for (uint k = 0u; k < requested && cursor < capacity; ++k)
            {
                while (cursor < capacity && particles[baseSlot + cursor].ActiveEmitter != 0u) cursor++;
                if (cursor == capacity) break;
                uint slot = baseSlot + cursor++;
                GpuParticle child = spawnParticle(emitter, slot);
                child.PositionAndAge.xyz = emitter.SizeCone.w > 0.5 ? ev.PositionAndTrigger.xyz
                    : (emitter.WorldInverse * vec4(ev.PositionAndTrigger.xyz, 1.0)).xyz;
                // A sub-emitter birth anchors at its event point (read only by world-space particles).
                child.SpawnAnchorXZ = lumenPackAnchor(ev.PositionAndTrigger.xz);
                // RimUnity ParticleSystem.SubEmissionEvents.cs:144-155 @45ca90987f7d3721715b45c021cab986ee14c468.
                if ((link.Inherit & 1u) != 0u) child.StartColor *= ev.Color;
                if ((link.Inherit & 2u) != 0u) child.Size *= ev.Size.x;
                particles[slot] = child;
            }
        }
    }
    burstStates[emitter.StateSlot].Debt = debt;
}

uint inclusivePrefix(uint value)
{
    uint lane = gl_LocalInvocationID.x;
    deadPrefix[lane] = value;
    barrier();
    for (uint offset = 1u; offset < 64u; offset <<= 1u)
    {
        uint preceding = lane >= offset ? deadPrefix[lane - offset] : 0u;
        barrier();
        deadPrefix[lane] += preceding;
        barrier();
    }
    return deadPrefix[lane];
}

void main()
{
    // Cursor-ordered dead ranks fill exactly min(budget, dead count), without atomics or readback.
    // Every workgroup crosses its local barriers uniformly, including lanes beyond the final slot.
    GpuEmitterParams emitter = emitters[sim.emitterIndex];
    uint capacity = uint(emitter.Slots.y);
    uint lane = gl_LocalInvocationID.x;
    uint phase = sim.pad0 >> 1u;
    if (phase == 4u)
    {
        if (lane != 0u) return;
        uint debt = emitter.State.z > 0.5 ? 0u : burstStates[emitter.StateSlot].Debt;
        uint version = emitter.State.z > 0.5 ? 0u : burstStates[emitter.StateSlot].ResetVersion;
        uint requested = 0u;
        for (uint i = 0u; i < emitter.ExplicitCount; ++i)
        {
            GpuExplicitBurst burst = explicitBursts[emitter.ExplicitOffset + i];
            if (version != burst.ResetVersion) { debt = 0u; version = burst.ResetVersion; }
            requested = min(capacity, requested + acceptBurst(burst.Count, burst.DensityQ, debt));
        }
        if (version != emitter.ResetVersion) debt = 0u;
        burstStates[emitter.StateSlot] = GpuBurstState(debt, emitter.ResetVersion, requested, 0u);
        return;
    }
    if (phase == 5u)
    {
        if (lane == 0u) consumeEvents(emitter);
        return;
    }
    uint budget = emitter.Events.z > 0.5 ? burstStates[emitter.StateSlot].Budget : min(uint(emitter.Slots.z), capacity);
    // An explicit request can spend its entire budget on density debt in phase 4.
    if (budget == 0u && (phase == 2u || phase == 3u)) return;
    if (phase == 2u)
    {
        uint blocks = (capacity + 63u) / 64u;
        if (lane == 0u) remainingBudget = 0u;
        barrier();
        for (uint chunk = 0u; chunk < blocks; chunk += 64u)
        {
            uint block = chunk + lane;
            uint count = block < blocks ? deadBlocks[block] : 0u;
            uint prefix = inclusivePrefix(count);
            if (block < blocks) deadBlocks[block] = remainingBudget + prefix - count;
            barrier();
            if (lane == 0u) remainingBudget += deadPrefix[63];
            barrier();
        }
        return;
    }
    if (phase == 1u || phase == 3u)
    {
        uint block = gl_WorkGroupID.x;
        uint ordinal = block * 64u + lane;
        bool valid = ordinal < capacity;
        uint slot = uint(emitter.Slots.x) + ((uint(emitter.Slots.w) + ordinal) % capacity);
        GpuParticle particle = GpuParticle(vec4(0.0), vec4(0.0), vec4(0.0), 0.0, 0u, 0u, 0u);
        bool dead = false;
        if (valid)
        {
            particle = phase == 1u ? integrateParticle(emitter, slot) : particles[slot];
            dead = particle.ActiveEmitter == 0u;
        }
        if (phase == 1u && budget == 0u)
        {
            if (valid) particles[slot] = particle;
            return;
        }
        uint rank = inclusivePrefix(dead ? 1u : 0u);
        if (phase == 1u)
        {
            if (valid) particles[slot] = particle;
            if (lane == 0u) deadBlocks[block] = deadPrefix[63];
        }
        else if (valid && dead && deadBlocks[block] + rank <= budget)
            particles[slot] = spawnParticle(emitter, slot);
        return;
    }
    // Small emitters keep one dispatch, using the same integration and block-local prefix.
    if (lane == 0u) remainingBudget = budget;
    barrier();
    for (uint chunk = 0u; chunk < capacity; chunk += 64u)
    {
        uint ordinal = chunk + lane;
        bool valid = ordinal < capacity;
        uint slot = uint(emitter.Slots.x) + ((uint(emitter.Slots.w) + ordinal) % capacity);
        GpuParticle particle = GpuParticle(vec4(0.0), vec4(0.0), vec4(0.0), 0.0, 0u, 0u, 0u);
        bool dead = false;
        if (valid)
        {
            particle = integrateParticle(emitter, slot);
            dead = particle.ActiveEmitter == 0u;
        }
        uint rank = inclusivePrefix(dead ? 1u : 0u);
        if (valid)
        {
            if (dead && rank <= remainingBudget) particle = spawnParticle(emitter, slot);
            particles[slot] = particle;
        }
        barrier();
        if (lane == 0u) remainingBudget -= min(remainingBudget, deadPrefix[63]);
        barrier();
    }
}
