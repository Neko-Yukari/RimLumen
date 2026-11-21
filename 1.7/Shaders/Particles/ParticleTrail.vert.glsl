#version 450
#include "ParticleTrailMaterial.glsl"
#include "ParticleProjection.glsl"

// Instanced particle trail strip: static bucket mesh (2*(S+1) vertices, 6*S indices) drawn once per header range.
// gl_InstanceIndex selects the trail header slot inside emitter; inPosition.x = centre index, inPosition.y = -1/+1 side.
layout(set = 0, binding = 0) uniform VertexUniforms { mat4 ViewProj; } camera;
layout(location = 0) in vec3 inPosition; // @vertex Position required

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec2 vRadialUv;
layout(location = 3) out vec2 vSoftAndGain;
layout(location = 4) out vec2 vDepthMask;

void readTrailSample(GpuTrailHeader header, GpuTrailEmitterParams trailEmitter, uint pointOffset, uint maxPoints,
    uint sampleIdx, uint visibleCount, out vec3 worldPos, out float cumDist)
{
    bool skipHead = (header.Flags & LUMEN_TRAIL_HDR_SKIP_HEAD) != 0u;
    if (sampleIdx == 0u && !skipHead)
    {
        worldPos = header.LiveHeadAndTotalLength.xyz;
        cumDist = header.HeadCumDistance;
    }
    else if (sampleIdx + 1u >= visibleCount)
    {
        worldPos = header.ClippedTailAndWidth.xyz;
        cumDist = header.ClippedTailCumDist;
    }
    else
    {
        uint fromNewest = skipHead ? sampleIdx : (sampleIdx - 1u);
        uint ringIdx = (header.RingHead + header.RingCount - 1u - fromNewest) % maxPoints;
        GpuTrailPoint pt = trailPoints[pointOffset + ringIdx];
        worldPos = pt.PositionAndRemaining.xyz;
        cumDist = pt.DistanceAndLifetime.x;
    }
    if ((header.Flags & LUMEN_TRAIL_HDR_WORLDSPACE) == 0u)
        worldPos = (trailEmitter.World * vec4(worldPos, 1.0)).xyz;
}

// Mirrors CameraBillboardBasis.ProjectToDisplay (see also RibbonStripGeometry.ProjectCentres).
vec3 projectTrailPoint(vec3 value, bool vertical3D, bool topDown2D, float yScale, vec3 groundUp,
    vec2 anchorXZ, float vanillaMoteY)
{
    if (!topDown2D)
    {
        if (vertical3D) value.y /= yScale;
        return value;
    }
    if (vertical3D)
    {
        vec3 offset = projectTopDownPitch(value - vec3(anchorXZ.x, LUMEN_GROUND_Y, anchorXZ.y), groundUp);
        return vec3(anchorXZ.x + offset.x, vanillaMoteY, anchorXZ.y + offset.z);
    }
    value.y = vanillaMoteY;
    return value;
}

// Mirrors RibbonStripGeometry.ProjectDirectionToFacingPlane.
vec2 projectDirectionToFacingPlane(vec3 direction, vec3 scaledX, vec3 scaledY, float yScale)
{
    vec3 scaled = vec3(direction.x, direction.y * yScale, direction.z);
    return vec2(dot(scaled, scaledX), dot(scaled, scaledY));
}

// Mirrors RibbonStripGeometry.ComputeMiterOffset (4x miter cap at max(0.25, abs(dot(miter, sideAfter)))).
vec2 computeMiterOffset(vec2 before, vec2 after, float halfWidth)
{
    vec2 sideBefore = dot(before, before) < 1e-10
        ? vec2(1.0, 0.0)
        : normalize(vec2(before.y, -before.x));
    vec2 sideAfter = dot(after, after) < 1e-10
        ? sideBefore
        : normalize(vec2(after.y, -after.x));
    vec2 miter = sideBefore + sideAfter;
    miter = dot(miter, miter) < 1e-10 ? sideAfter : normalize(miter);
    return miter * (halfWidth / max(0.25, abs(dot(miter, sideAfter))));
}

GpuCurveSample sampleTrailCurve(uint rowOffset, float t)
{
    float scaled = clamp(t, 0.0, 1.0) * 63.0;
    uint lower = uint(scaled);
    uint upper = min(lower + 1u, 63u);
    return lumenTrailInterpolateSample(curves[rowOffset + lower], curves[rowOffset + upper], scaled - float(lower));
}

void main()
{
    GpuTrailEmitterParams trailEmitter = trailEmitters[uint(material._LumenDraw.x)];
    uint hLocal = uint(gl_InstanceIndex);
    GpuTrailHeader header = trailHeaders[uint(trailEmitter.Ranges.x) + hLocal];
    vColor = vec4(0.0);
    vUv = vec2(0.0);
    vRadialUv = vec2(0.5, inPosition.y > 0.0 ? 1.0 : 0.0);
    vSoftAndGain = vec2(trailEmitter.Params.z, trailEmitter.Appearance.z * trailEmitter.Appearance.w);
    vDepthMask = vec2(0.0);
    if ((header.Flags & LUMEN_TRAIL_HDR_ACTIVE) == 0u || header.VisibleCount < 2u)
    {
        gl_Position = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    uint count = header.VisibleCount;
    // Clamp trailing bucket vertices to the clipped tail edge so unused segments collapse with zero area.
    uint idx = min(uint(inPosition.x), count - 1u);
    uint maxPoints = uint(trailEmitter.PointsAndCurve.x);
    uint pointOffset = uint(trailEmitter.Ranges.z) + hLocal * maxPoints;

    vec3 pCurr, pPrev, pNext;
    float currCumDist, unusedDist;
    readTrailSample(header, trailEmitter, pointOffset, maxPoints, idx, count, pCurr, currCumDist);
    uint prevIdx = idx > 0u ? (idx - 1u) : 0u;
    readTrailSample(header, trailEmitter, pointOffset, maxPoints, prevIdx, count, pPrev, unusedDist);
    // Step past an exact tail-clip coincidence at the last centre so the before tangent remains valid.
    if (idx > 1u && dot(pCurr - pPrev, pCurr - pPrev) <= 1e-10)
        readTrailSample(header, trailEmitter, pointOffset, maxPoints, idx - 2u, count, pPrev, unusedDist);
    uint nextIdx = idx + 1u < count ? (idx + 1u) : idx;
    readTrailSample(header, trailEmitter, pointOffset, maxPoints, nextIdx, count, pNext, unusedDist);

    bool vertical3D = material._LumenVertical.x > 0.5;
    bool topDown2D = material._LumenVertical.y > 0.5;
    float yScale = material._LumenCameraUp.w > 0.0 ? material._LumenCameraUp.w : 1.0;
    vec3 groundUp = groundUpOf(material._LumenCameraUp.xyz);
    vec2 anchorXZ = ((header.Flags & LUMEN_TRAIL_HDR_WORLDSPACE) != 0u)
        ? lumenUnpackAnchor(header.SpawnAnchorXZ)
        : lumenNodeGroundXZ(trailEmitter.World);

    vec3 dCurr = projectTrailPoint(pCurr, vertical3D, topDown2D, yScale, groundUp, anchorXZ, material._LumenVertical.z);
    vec3 dPrev = projectTrailPoint(pPrev, vertical3D, topDown2D, yScale, groundUp, anchorXZ, material._LumenVertical.z);
    vec3 dNext = projectTrailPoint(pNext, vertical3D, topDown2D, yScale, groundUp, anchorXZ, material._LumenVertical.z);

    bool ground = trailEmitter.Appearance.y > 0.5;
    vec3 axisX = ground ? vec3(1.0, 0.0, 0.0) : material._LumenCameraRight.xyz;
    vec3 axisY = ground ? vec3(0.0, 0.0, 1.0) : material._LumenCameraUp.xyz;
    float axisYScale = ground ? 1.0 : yScale;
    vec3 scaledX = vec3(axisX.x, axisX.y * axisYScale, axisX.z);
    vec3 scaledY = vec3(axisY.x, axisY.y * axisYScale, axisY.z);

    vec2 before = projectDirectionToFacingPlane(idx > 0u ? (dCurr - dPrev) : (dNext - dCurr), scaledX, scaledY, axisYScale);
    vec2 after = idx + 1u < count
        ? projectDirectionToFacingPlane(dNext - dCurr, scaledX, scaledY, axisYScale)
        : before;

    float totalLength = header.LiveHeadAndTotalLength.w;
    float distanceFromHead = max(0.0, header.HeadCumDistance - currCumDist);
    float t = totalLength > 0.0 ? clamp(distanceFromHead / totalLength, 0.0, 1.0) : 0.0;
    GpuCurveSample curveSample = sampleTrailCurve(uint(trailEmitter.PointsAndCurve.y) * 64u, t);
    float halfWidth = max(0.0, header.ClippedTailAndWidth.w * curveSample.Size.x) * 0.5;
    vec2 miter = computeMiterOffset(before, after, halfWidth);
    vec3 offset = axisX * miter.x + axisY * miter.y;

    vec3 worldPosition = dCurr + offset * inPosition.y;
    gl_Position = camera.ViewProj * vec4(worldPosition, 1.0);
    if (topDown2D && trailEmitter.DepthAndParticle.x > 0.5)
        vDepthMask = vec2(material._LumenVertical.w, worldPosition.z - trailEmitter.DepthAndParticle.y);

    float across = inPosition.y > 0.0 ? 1.0 : 0.0;
    float tileScale = (header.Flags & LUMEN_TRAIL_HDR_WORLDSPACE) == 0u ? trailEmitter.Params.w : 1.0;
    float u = trailEmitter.Appearance.x > 0.5 ? (distanceFromHead * tileScale) : t;
    vUv = vec2(u, across);
    vRadialUv = vec2(0.5, across);
    vColor = header.Color * curveSample.Color;
}
