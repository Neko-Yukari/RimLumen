#version 450
#include "ParticleTrailTypes.glsl"

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

layout(std430, set = 0, binding = 0) readonly buffer Particles { GpuParticle particles[]; };
layout(std430, set = 0, binding = 1) readonly buffer Emitters { GpuEmitterParams emitters[]; };
layout(std430, set = 0, binding = 2) readonly buffer Curves { GpuCurveSample curves[]; };
layout(std430, set = 0, binding = 3) buffer TrailHeaders { GpuTrailHeader trailHeaders[]; };
layout(std430, set = 0, binding = 4) buffer TrailPoints { GpuTrailPoint trailPoints[]; };
layout(std430, set = 0, binding = 5) readonly buffer TrailEmitters { GpuTrailEmitterParams trailEmitters[]; };
layout(std430, set = 0, binding = 6) buffer ParticleToTrail { uint particleToTrail[]; };

layout(push_constant) uniform SimPush
{
    uint emitterIndex; // phase 0/1: trailEmitterIndex; phase 2/3: offset
    uint frameSeed;    // phase 2/3: clear count
    uint phase;        // 0: PostIntegrate, 1: PostSpawn, 2: ClearHeaders, 3: ClearMap
    uint pad;
} sim;

GpuCurveSample sampleLut(uint rowOffset, float t)
{
    float scaled = clamp(t, 0.0, 1.0) * 63.0;
    uint lower = uint(scaled);
    uint upper = min(lower + 1u, 63u);
    return lumenTrailInterpolateSample(curves[rowOffset + lower], curves[rowOffset + upper], scaled - float(lower));
}

uint ringIndexAt(GpuTrailHeader h, uint pointOffset, uint maxPoints, uint offset)
{
    return pointOffset + ((h.RingHead + offset) % maxPoints);
}

void ageAndEvictRing(inout GpuTrailHeader h, uint pointOffset, uint maxPoints, float dt)
{
    if (dt <= 0.0 || h.RingCount == 0u) return;
    for (uint i = 0u; i < h.RingCount; i++)
    {
        uint idx = ringIndexAt(h, pointOffset, maxPoints, i);
        trailPoints[idx].PositionAndRemaining.w -= dt;
    }
    while (h.RingCount > 1u && trailPoints[ringIndexAt(h, pointOffset, maxPoints, 1u)].PositionAndRemaining.w <= 0.0)
    {
        h.RingHead = (h.RingHead + 1u) % maxPoints;
        h.RingCount--;
    }
    if (h.RingCount == 1u && trailPoints[ringIndexAt(h, pointOffset, maxPoints, 0u)].PositionAndRemaining.w < 0.0)
        h.RingCount = 0u;
}

void pushRingPoint(inout GpuTrailHeader h, uint pointOffset, uint maxPoints, vec3 pos, float pointLifetime)
{
    float prevCum = h.RingCount > 0u
        ? trailPoints[ringIndexAt(h, pointOffset, maxPoints, h.RingCount - 1u)].DistanceAndLifetime.x
        : 0.0;
    float seg = h.RingCount > 0u
        ? distance(trailPoints[ringIndexAt(h, pointOffset, maxPoints, h.RingCount - 1u)].PositionAndRemaining.xyz, pos)
        : 0.0;
    if (h.RingCount == maxPoints)
    {
        h.RingHead = (h.RingHead + 1u) % maxPoints;
        h.RingCount--;
    }
    uint writeIdx = ringIndexAt(h, pointOffset, maxPoints, h.RingCount++);
    trailPoints[writeIdx] = GpuTrailPoint(vec4(pos, pointLifetime), vec4(prevCum + seg, pointLifetime, 0.0, 0.0));
}

void updateVisibleTailAndLength(inout GpuTrailHeader h, uint pointOffset, uint maxPoints)
{
    if (h.RingCount == 0u)
    {
        h.VisibleCount = 0u;
        h.LiveHeadAndTotalLength.w = 0.0;
        return;
    }
    GpuTrailPoint newest = trailPoints[ringIndexAt(h, pointOffset, maxPoints, h.RingCount - 1u)];
    vec3 liveToNewest = h.LiveHeadAndTotalLength.xyz - newest.PositionAndRemaining.xyz;
    float liveStep = length(liveToNewest);
    bool skipHead = (h.Flags & LUMEN_TRAIL_HDR_SKIP_HEAD) != 0u || liveStep <= 1e-6;
    if (skipHead)
    {
        h.Flags |= LUMEN_TRAIL_HDR_SKIP_HEAD;
        h.VisibleCount = h.RingCount;
        h.HeadCumDistance = newest.DistanceAndLifetime.x;
    }
    else
    {
        h.Flags &= ~LUMEN_TRAIL_HDR_SKIP_HEAD;
        h.VisibleCount = h.RingCount + 1u;
        h.HeadCumDistance = newest.DistanceAndLifetime.x + liveStep;
    }

    GpuTrailPoint oldest = trailPoints[ringIndexAt(h, pointOffset, maxPoints, 0u)];
    vec3 tailPos = oldest.PositionAndRemaining.xyz;
    float tailCum = oldest.DistanceAndLifetime.x;
    if (h.RingCount > 1u && oldest.PositionAndRemaining.w < 0.0)
    {
        GpuTrailPoint nextPt = trailPoints[ringIndexAt(h, pointOffset, maxPoints, 1u)];
        // Mirrors RibbonStripGeometry.ClipTailPoint.
        float denom = max(1e-6, nextPt.PositionAndRemaining.w - oldest.PositionAndRemaining.w);
        float fraction = clamp(-oldest.PositionAndRemaining.w / denom, 0.0, 1.0);
        tailPos = mix(tailPos, nextPt.PositionAndRemaining.xyz, fraction);
        tailCum = mix(tailCum, nextPt.DistanceAndLifetime.x, fraction);
    }
    h.ClippedTailAndWidth.xyz = tailPos;
    h.ClippedTailCumDist = tailCum;
    h.LiveHeadAndTotalLength.w = max(0.0, h.HeadCumDistance - tailCum);
}

void computeParticleTrailFrame(GpuTrailEmitterParams te, GpuEmitterParams emitter, GpuParticle p,
    out vec3 pos, out vec4 color, out float size, out float normAge, out bool trailWorld, out uint anchorXZ)
{
    bool emitterWorld = (te.Flags & LUMEN_TRAIL_FLAG_EMITTER_WORLDSPACE) != 0u;
    trailWorld = (te.Flags & LUMEN_TRAIL_FLAG_WORLDSPACE) != 0u;
    pos = (trailWorld && !emitterWorld) ? (te.World * vec4(p.PositionAndAge.xyz, 1.0)).xyz : p.PositionAndAge.xyz;
    normAge = clamp(p.PositionAndAge.w / p.VelocityAndLifetime.w, 0.0, 1.0);
    GpuCurveSample pSample = sampleLut(uint(emitter.Tables.x) * 64u, normAge);
    size = p.Size * pSample.Size.x * (emitterWorld ? 1.0 : te.Params.w);
    color = p.StartColor * pSample.Color;
    anchorXZ = emitterWorld ? p.SpawnAnchorXZ : lumenPackAnchor(lumenNodeGroundXZ(te.World));
}

void runPostIntegrate()
{
    GpuTrailEmitterParams te = trailEmitters[sim.emitterIndex];
    uint gId = gl_GlobalInvocationID.x;
    uint particleCapacity = uint(te.DepthAndParticle.z);
    // Clear rejected marks on slots that died in ParticleSim Phase 1 so Phase 2/3 spawns can re-roll ratio.
    if (gId < particleCapacity)
    {
        uint mapIdx = te.MapBase + gId;
        if (particleToTrail[mapIdx] == LUMEN_TRAIL_REJECTED &&
            particles[uint(te.Ranges.w) + gId].ActiveEmitter == 0u)
        {
            particleToTrail[mapIdx] = LUMEN_TRAIL_NONE;
        }
    }
    if (gId >= uint(te.Ranges.y)) return;

    uint hSlot = uint(te.Ranges.x) + gId;
    GpuTrailHeader h = trailHeaders[hSlot];
    if ((h.Flags & LUMEN_TRAIL_HDR_ACTIVE) == 0u) return;

    uint maxPoints = uint(te.PointsAndCurve.x);
    uint pointOffset = uint(te.Ranges.z) + gId * maxPoints;
    GpuEmitterParams emitter = emitters[uint(te.DepthAndParticle.w)];
    float dt = emitter.State.x;
    bool detached = (h.Flags & LUMEN_TRAIL_HDR_DETACHED) != 0u;

    if (!detached)
    {
        GpuParticle p = particles[h.ParticleSlot];
        if (p.ActiveEmitter == 0u)
        {
            uint pLocal = h.ParticleSlot - uint(te.Ranges.w);
            particleToTrail[te.MapBase + pLocal] = LUMEN_TRAIL_NONE;
            if ((te.Flags & LUMEN_TRAIL_FLAG_DIE_WITH_PARTICLES) != 0u)
            {
                h.Flags = 0u;
                h.VisibleCount = 0u;
                h.RingCount = 0u;
                trailHeaders[hSlot] = h;
                return;
            }
            ageAndEvictRing(h, pointOffset, maxPoints, dt);
            vec3 pos; vec4 pColor; float pSize; float normAge; bool trailWorld; uint anchorXZ;
            computeParticleTrailFrame(te, emitter, p, pos, pColor, pSize, normAge, trailWorld, anchorXZ);
            bool needFinalPoint = h.RingCount == 0u;
            if (!needFinalPoint)
            {
                vec3 newestPos = trailPoints[ringIndexAt(h, pointOffset, maxPoints, h.RingCount - 1u)].PositionAndRemaining.xyz;
                vec3 d = newestPos - pos;
                needFinalPoint = dot(d, d) > 1e-12;
            }
            if (needFinalPoint)
            {
                float pointLifetime = p.VelocityAndLifetime.w * te.PointsAndCurve.w *
                    ((te.Flags & LUMEN_TRAIL_FLAG_SIZE_AFFECTS_LIFETIME) != 0u ? pSize : 1.0);
                pushRingPoint(h, pointOffset, maxPoints, pos, pointLifetime);
            }
            vec4 lifeTint = sampleLut((uint(te.PointsAndCurve.y) + 1u) * 64u, normAge).Color;
            h.LiveHeadAndTotalLength.xyz = pos;
            h.ClippedTailAndWidth.w = te.Params.y * ((te.Flags & LUMEN_TRAIL_FLAG_SIZE_AFFECTS_WIDTH) != 0u ? pSize : 1.0);
            h.Color = (((te.Flags & LUMEN_TRAIL_FLAG_INHERIT_COLOR) != 0u) ? pColor : vec4(1.0)) * lifeTint;
            h.SpawnAnchorXZ = anchorXZ;
            h.Flags |= LUMEN_TRAIL_HDR_DETACHED | LUMEN_TRAIL_HDR_SKIP_HEAD;
            h.Flags = (h.Flags & ~LUMEN_TRAIL_HDR_WORLDSPACE) | (trailWorld ? LUMEN_TRAIL_HDR_WORLDSPACE : 0u);
        }
        else
        {
            ageAndEvictRing(h, pointOffset, maxPoints, dt);
            vec3 pos; vec4 pColor; float pSize; float normAge; bool trailWorld; uint anchorXZ;
            computeParticleTrailFrame(te, emitter, p, pos, pColor, pSize, normAge, trailWorld, anchorXZ);
            if (h.RingCount == 0u ||
                distance(trailPoints[ringIndexAt(h, pointOffset, maxPoints, h.RingCount - 1u)].PositionAndRemaining.xyz, pos) >= te.Params.x)
            {
                float pointLifetime = p.VelocityAndLifetime.w * te.PointsAndCurve.w *
                    ((te.Flags & LUMEN_TRAIL_FLAG_SIZE_AFFECTS_LIFETIME) != 0u ? pSize : 1.0);
                pushRingPoint(h, pointOffset, maxPoints, pos, pointLifetime);
            }
            vec4 lifeTint = sampleLut((uint(te.PointsAndCurve.y) + 1u) * 64u, normAge).Color;
            h.LiveHeadAndTotalLength.xyz = pos;
            h.ClippedTailAndWidth.w = te.Params.y * ((te.Flags & LUMEN_TRAIL_FLAG_SIZE_AFFECTS_WIDTH) != 0u ? pSize : 1.0);
            h.Color = (((te.Flags & LUMEN_TRAIL_FLAG_INHERIT_COLOR) != 0u) ? pColor : vec4(1.0)) * lifeTint;
            h.SpawnAnchorXZ = anchorXZ;
            h.Flags &= ~LUMEN_TRAIL_HDR_SKIP_HEAD;
            h.Flags = (h.Flags & ~LUMEN_TRAIL_HDR_WORLDSPACE) | (trailWorld ? LUMEN_TRAIL_HDR_WORLDSPACE : 0u);
        }
    }
    else
    {
        ageAndEvictRing(h, pointOffset, maxPoints, dt);
        h.Flags |= LUMEN_TRAIL_HDR_SKIP_HEAD;
    }

    updateVisibleTailAndLength(h, pointOffset, maxPoints);
    if ((h.Flags & LUMEN_TRAIL_HDR_DETACHED) != 0u && h.VisibleCount < 2u)
    {
        h.Flags = 0u;
        h.VisibleCount = 0u;
        h.RingCount = 0u;
    }
    trailHeaders[hSlot] = h;
}

// Mirrors ParticleTrailMath.ClaimTrailSlot (Bank 0 private + up to 64 overflow linear probes + deterministic Bank 0 recycle).
uint claimTrailSlot(GpuTrailEmitterParams te, uint pLocal, uint initialFlags)
{
    uint headerOffset = uint(te.Ranges.x);
    uint particleCapacity = uint(te.DepthAndParticle.z);
    if (atomicCompSwap(trailHeaders[headerOffset + pLocal].Flags, 0u, initialFlags) == 0u)
        return pLocal;
    for (uint b = 1u; b < te.BankCount; b++)
    {
        uint cand = b * particleCapacity + pLocal;
        if (atomicCompSwap(trailHeaders[headerOffset + cand].Flags, 0u, initialFlags) == 0u)
            return cand;
    }
    uint overflowCount = (te.BankCount > 1u ? te.BankCount - 1u : 0u) * particleCapacity;
    if (overflowCount > 0u)
    {
        uint probes = min(LUMEN_TRAIL_MAX_LINEAR_PROBE, overflowCount);
        for (uint k = 1u; k <= probes; k++)
        {
            uint cand = particleCapacity + ((pLocal + k) % overflowCount);
            if (atomicCompSwap(trailHeaders[headerOffset + cand].Flags, 0u, initialFlags) == 0u)
                return cand;
        }
    }
    return pLocal;
}

void runPostSpawn()
{
    GpuTrailEmitterParams te = trailEmitters[sim.emitterIndex];
    uint pLocal = gl_GlobalInvocationID.x;
    uint particleCapacity = uint(te.DepthAndParticle.z);
    if (pLocal >= particleCapacity) return;

    uint mapIdx = te.MapBase + pLocal;
    if (particleToTrail[mapIdx] != LUMEN_TRAIL_NONE) return;

    uint pSlot = uint(te.Ranges.w) + pLocal;
    GpuParticle p = particles[pSlot];
    if (p.ActiveEmitter == 0u) return;

    if (!lumenTrailRollRatio(te.RandomSeed, p.Seed, pLocal, te.PointsAndCurve.z))
    {
        particleToTrail[mapIdx] = LUMEN_TRAIL_REJECTED;
        return;
    }

    GpuEmitterParams emitter = emitters[uint(te.DepthAndParticle.w)];
    vec3 pos; vec4 pColor; float pSize; float normAge; bool trailWorld; uint anchorXZ;
    computeParticleTrailFrame(te, emitter, p, pos, pColor, pSize, normAge, trailWorld, anchorXZ);
    uint initialFlags = LUMEN_TRAIL_HDR_ACTIVE | LUMEN_TRAIL_HDR_SKIP_HEAD |
        (trailWorld ? LUMEN_TRAIL_HDR_WORLDSPACE : 0u) |
        ((te.Flags & LUMEN_TRAIL_FLAG_VERTICAL3D) != 0u ? LUMEN_TRAIL_HDR_VERTICAL3D : 0u);

    uint claimedLocal = claimTrailSlot(te, pLocal, initialFlags);
    uint hSlot = uint(te.Ranges.x) + claimedLocal;
    uint maxPoints = uint(te.PointsAndCurve.x);
    uint pointOffset = uint(te.Ranges.z) + claimedLocal * maxPoints;
    float pointLifetime = p.VelocityAndLifetime.w * te.PointsAndCurve.w *
        ((te.Flags & LUMEN_TRAIL_FLAG_SIZE_AFFECTS_LIFETIME) != 0u ? pSize : 1.0);
    float initWidth = te.Params.y * ((te.Flags & LUMEN_TRAIL_FLAG_SIZE_AFFECTS_WIDTH) != 0u ? pSize : 1.0);
    vec4 lifeTint = sampleLut((uint(te.PointsAndCurve.y) + 1u) * 64u, normAge).Color;
    vec4 initColor = (((te.Flags & LUMEN_TRAIL_FLAG_INHERIT_COLOR) != 0u) ? pColor : vec4(1.0)) * lifeTint;

    trailPoints[pointOffset] = GpuTrailPoint(vec4(pos, pointLifetime), vec4(0.0, pointLifetime, 0.0, 0.0));
    trailHeaders[hSlot] = GpuTrailHeader(
        vec4(pos, 0.0),
        vec4(pos, initWidth),
        initColor,
        0u,
        1u,
        1u,
        initialFlags,
        pSlot,
        anchorXZ,
        0.0,
        0.0);
    particleToTrail[mapIdx] = hSlot;
}

void main()
{
    uint gId = gl_GlobalInvocationID.x;
    if (sim.phase == 2u)
    {
        if (gId < sim.frameSeed)
        {
            uint idx = sim.emitterIndex + gId;
            trailHeaders[idx].Flags = 0u;
            trailHeaders[idx].VisibleCount = 0u;
            trailHeaders[idx].RingCount = 0u;
        }
        return;
    }
    if (sim.phase == 3u)
    {
        if (gId < sim.frameSeed)
            particleToTrail[sim.emitterIndex + gId] = LUMEN_TRAIL_NONE;
        return;
    }
    if (sim.phase == 0u) runPostIntegrate();
    else if (sim.phase == 1u) runPostSpawn();
}
