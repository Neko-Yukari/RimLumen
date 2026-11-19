// Main texture rows follow world z; the four gathered components are (01, 11, 10, 00).
layout(set = 2, binding = 1) uniform sampler2D _LumenOcclusion; // @default = black
layout(set = 2, binding = 2) uniform sampler2D _LumenOcclusionCoarse; // @default = black
layout(set = 2, binding = 3) uniform sampler2D _LumenDoorOpen; // @default = black

bool lumenInside(ivec2 p, ivec2 size) { return all(greaterThanEqual(p, ivec2(0))) && all(lessThan(p, size)); }

float lumenDoorOpacityWithState(ivec2 p, float value, int flags, vec4 door)
{
    bool axisZ = (flags & 2) != 0;
    float local = (float((axisZ ? p.y : p.x) % 4) + 0.5) * 0.25;
    float index = round(door.g * 255.0);
    float width = round(door.b * 255.0);
    float u = (index + local) / width - 0.5;
    return abs(u) < door.r * 0.5 ? 0.0 : value;
}

float lumenDoorOpacity(ivec2 p, float value, int flags)
{
    if ((flags & 1) == 0) return value;
    return lumenDoorOpacityWithState(p, value, flags, texelFetch(_LumenDoorOpen, p / 4, 0));
}

// OcclusionLayout.DecodeDistance: B as the signed distance in texels, positive inside.
float lumenDecodeDistance(float b) { return (b * 255.0 - 128.0) / 32.0; }

// deep: a wall-core texel (d >= DeepCore 3.5, never a door).
bool lumenSolidTexel(ivec2 p, out bool deep)
{
    deep = false;
    if (!lumenInside(p, textureSize(_LumenOcclusion, 0))) return false;
    vec4 v = texelFetch(_LumenOcclusion, p, 0);
    int flags = int(round(v.a * 255.0));
    deep = (flags & 1) == 0 && lumenDecodeDistance(v.b) >= 3.5;
    return lumenDoorOpacity(p, v.r, flags) == 1.0;
}

bool lumenSolid(vec2 p, out bool deep) { return lumenSolidTexel(ivec2(floor(p * 4.0)), deep); }

bool lumenRunTexel(ivec2 p, bool height)
{
    bool deep;
    if (!height) return lumenSolidTexel(p, deep);
    if (!lumenInside(p, textureSize(_LumenOcclusion, 0))) return false;
    vec4 v = texelFetch(_LumenOcclusion, p, 0);
    return v.g != 0.0 && (int(round(v.a * 255.0)) & 4) != 0;
}

// LightTransport.RunLength: the contiguous run of solid (height: FlagPointHeight G > 0) texels from start along direction, at
// most limit. Solid starts in start's own texel; height starts in the first texel along direction.
float lumenRunLength(vec2 start, vec2 direction, float limit, bool height)
{
    bool deep;
    if (!height && !lumenSolid(start, deep)) return 0.0;
    float distance = 0.0;
    vec2 texel = start * 4.0;
    ivec2 cell = ivec2(floor(texel));
    if (direction.x < 0.0 && texel.x == float(cell.x)) cell.x--;
    if (direction.y < 0.0 && texel.y == float(cell.y)) cell.y--;
    while (distance < limit)
    {
        if (!lumenRunTexel(cell, height)) break;
        float tx = direction.x > 0.0 ? ((float(cell.x) + 1.0) * 0.25 - start.x) / direction.x : direction.x < 0.0 ? (float(cell.x) * 0.25 - start.x) / direction.x : 1e30;
        float tz = direction.y > 0.0 ? ((float(cell.y) + 1.0) * 0.25 - start.y) / direction.y : direction.y < 0.0 ? (float(cell.y) * 0.25 - start.y) / direction.y : 1e30;
        distance = min(limit, min(tx, tz));
        if (tx <= distance) cell.x += direction.x > 0.0 ? 1 : -1;
        if (tz <= distance) cell.y += direction.y > 0.0 ? 1 : -1;
    }
    return distance;
}

// TexelSplit.Of: x = edge, clamp(bilerp(d) / EdgeRamp 0.125, 0, 1) with d = -DistanceBand on texels inside another term's
// footprint (d > 0, not occupied); y = bilerp(occupied); z = bilerp(occupied ? value : 0); w = bilerp(occupied ? 0 :
// value). Texels in gather order 00, 10, 01, 11. Shared by the point-light and sun paths (design 1.8).
vec4 lumenTexelSplit(vec4 values, vec4 occupied, vec4 distances, vec2 f)
{
    vec4 inside = values * occupied;
    vec4 outside = values - inside;
    vec4 gated = mix(distances, vec4(-4.0), vec4(lessThan(occupied, vec4(0.5))) * vec4(greaterThan(distances, vec4(0.0))));
    return vec4(clamp(mix(mix(gated.x, gated.y, f.x), mix(gated.z, gated.w, f.x), f.y) / 0.125, 0.0, 1.0),
        mix(mix(occupied.x, occupied.y, f.x), mix(occupied.z, occupied.w, f.x), f.y),
        mix(mix(inside.x, inside.y, f.x), mix(inside.z, inside.w, f.x), f.y),
        mix(mix(outside.x, outside.y, f.x), mix(outside.z, outside.w, f.x), f.y));
}

// LightTransport.Distance of the four gathered texels: decoded B, -DistanceBand outside the map.
vec4 lumenGatherDistances(vec2 gatherUv, ivec2 points[4], ivec2 size)
{
    vec4 b = textureGather(_LumenOcclusion, gatherUv, 2).wzxy;
    vec4 d;
    for (int i = 0; i < 4; i++) d[i] = lumenInside(points[i], size) ? lumenDecodeDistance(b[i]) : -4.0;
    return d;
}

void lumenSample(vec2 p, bool doors, out float solid, out float cover)
{
    ivec2 size = textureSize(_LumenOcclusion, 0);
    vec2 uv = p * 4.0 - 0.5;
    ivec2 base = ivec2(floor(uv));
    vec2 f = fract(uv);
    vec2 gatherUv = p * 4.0 / vec2(size);
    vec4 values = textureGather(_LumenOcclusion, gatherUv, 0).wzxy;
    ivec2 points[4] = ivec2[4](base, base + ivec2(1, 0), base + ivec2(0, 1), base + ivec2(1, 1));
    vec4 flags = doors ? textureGather(_LumenOcclusion, gatherUv, 3).wzxy : vec4(0.0);
    vec4 opacities = vec4(0.0);
    ivec2 doorCells[4];
    vec4 doorValues[4];
    int doorCount = 0;
    for (int i = 0; i < 4; i++)
    {
        if (!lumenInside(points[i], size)) continue;
        float opacity = values[i];
        int flag = int(round(flags[i] * 255.0));
        if (doors && (flag & 1) != 0)
        {
            ivec2 cell = points[i] / 4;
            int found = -1;
            for (int j = 0; j < doorCount; j++) if (doorCells[j] == cell) { found = j; break; }
            if (found < 0)
            {
                found = doorCount++;
                doorCells[found] = cell;
                doorValues[found] = texelFetch(_LumenDoorOpen, cell, 0);
            }
            opacity = lumenDoorOpacityWithState(points[i], opacity, flag, doorValues[found]);
        }
        opacities[i] = opacity;
    }
    vec4 split = lumenTexelSplit(opacities, vec4(equal(opacities, vec4(1.0))), lumenGatherDistances(gatherUv, points, size), f);
    solid = split.x;
    cover = split.w;
}

// LightTransport.Visibility: returns the solid term, cover receives the cover term.
float lightVisibility(vec2 light, vec2 target, out float cover)
{
    cover = 1.0;
    vec2 delta = light - target;
    float len = length(delta);
    if (len == 0.0) return 1.0;
    cover = 0.0;
    vec2 direction = delta / len;
    float lightRun = lumenRunLength(light, -direction, len, false);
    bool deepTarget;
    bool skipTarget = lumenSolid(target, deepTarget);
    if (skipTarget && deepTarget) return 0.0;
    bool exitBand = false;
    float solidVisibility = 1.0, pendingSolid = 1.0, coverVisibility = 1.0, distance = 0.0;
    ivec2 mapSize = textureSize(_LumenOcclusionCoarse, 0);
    ivec2 cell = ivec2(floor(target));
    if (direction.x < 0.0 && target.x == float(cell.x)) cell.x--;
    if (direction.y < 0.0 && target.y == float(cell.y)) cell.y--;
    while (distance < len)
    {
        float nextX = direction.x > 0.0 ? (float(cell.x + 1) - target.x) / direction.x : direction.x < 0.0 ? (float(cell.x) - target.x) / direction.x : 1e30;
        float nextZ = direction.y > 0.0 ? (float(cell.y + 1) - target.y) / direction.y : direction.y < 0.0 ? (float(cell.y) - target.y) / direction.y : 1e30;
        float end = min(len, min(nextX, nextZ));
        bool inside = lumenInside(cell, mapSize);
        vec4 coarse = inside ? texelFetch(_LumenOcclusionCoarse, cell, 0) : vec4(0.0);
        if (!inside || coarse.r == 0.0)
        {
            skipTarget = false;
            exitBand = false;
            solidVisibility = min(solidVisibility, pendingSolid);
            pendingSolid = 1.0;
            distance = end;
            if (nextX <= end) cell.x += direction.x > 0.0 ? 1 : -1;
            if (nextZ <= end) cell.y += direction.y > 0.0 ? 1 : -1;
            continue;
        }
        bool doors = coarse.b != 0.0;
        int steps = int(ceil((end - distance) * 4.0));
        float ds = (end - distance) / float(steps);
        for (int step = 0; step < steps; step++)
        {
            float at = distance + (float(step) + 0.5) * ds;
            vec2 p = target + direction * at;
            bool deep = false;
            bool isSolid = (skipTarget || exitBand || lightRun > 0.0) && lumenSolid(p, deep);
            if (skipTarget)
            {
                if (isSolid)
                {
                    if (deep) return 0.0;
                    continue;
                }
                skipTarget = false;
                exitBand = true;
            }
            if (exitBand && isSolid) return 0.0;
            if (at >= len - lightRun)
            {
                pendingSolid = 1.0;
                continue;
            }
            float s, o;
            lumenSample(p, doors, s, o);
            if (exitBand)
            {
                if (s == 0.0) exitBand = false;
            }
            else if (lightRun > 0.0 && !isSolid && s > 0.0)
            {
                pendingSolid = min(pendingSolid, 1.0 - s);
            }
            else
            {
                solidVisibility = min(solidVisibility, min(pendingSolid, 1.0 - s));
                pendingSolid = 1.0;
            }
            coverVisibility *= pow(1.0 - o, ds);
            if (solidVisibility * coverVisibility < 1.0 / 255.0) return 0.0;
        }
        distance = end;
        if (nextX <= end) cell.x += direction.x > 0.0 ? 1 : -1;
        if (nextZ <= end) cell.y += direction.y > 0.0 ? 1 : -1;
    }
    cover = coverVisibility;
    return min(solidVisibility, lightRun > 0.0 ? 1.0 : pendingSolid);
}

// Sun path (SunTransport.Shadow): split G height at q against height t, the edge ramp inside the footprint, faded by
// 1 - t / h on FlagSunFade texels. point: texels without FlagPointHeight (4) read G = 0.
float lumenSunShadow(vec2 q, float t, bool point)
{
    ivec2 size = textureSize(_LumenOcclusion, 0);
    vec2 uv = q * 4.0 - 0.5;
    ivec2 base = ivec2(floor(uv));
    vec2 f = fract(uv);
    vec2 gatherUv = q * 4.0 / vec2(size);
    vec4 heights = textureGather(_LumenOcclusion, gatherUv, 1).wzxy;
    if (point)
    {
        vec4 flags = textureGather(_LumenOcclusion, gatherUv, 3).wzxy;
        for (int i = 0; i < 4; i++) if ((int(round(flags[i] * 255.0)) & 4) == 0) heights[i] = 0.0;
    }
    ivec2 points[4] = ivec2[4](base, base + ivec2(1, 0), base + ivec2(0, 1), base + ivec2(1, 1));
    for (int i = 0; i < 4; i++) if (!lumenInside(points[i], size)) heights[i] = 0.0;
    vec4 split = lumenTexelSplit(heights, vec4(greaterThan(heights, vec4(0.0))), lumenGatherDistances(gatherUv, points, size), f);
    float edge = split.x;
    if (edge <= 0.0) return 0.0;
    float h = split.z / split.y * 2.0;
    float s = clamp((h - t) / 0.125, 0.0, 1.0) * edge;
    ivec2 nearest = ivec2(floor(q * 4.0));
    if (s > 0.0 && lumenInside(nearest, size) && (int(round(texelFetch(_LumenOcclusion, nearest, 0).a * 255.0)) & 128) != 0)
        s *= 1.0 - t / h;
    return s;
}

// SunTransport.HeightVisibility: the one height march; the sun skips nothing and reads every G (coarse G), a point light
// skips both own footprints and reads G only on FlagPointHeight texels (coarse A).
float lumenHeightVisibility(vec2 p, vec2 direction, float rise, float reach, float skipBefore, float skipFrom, bool point)
{
    ivec2 mapSize = textureSize(_LumenOcclusionCoarse, 0);
    ivec2 cell = ivec2(floor(p));
    if (!lumenInside(cell, mapSize)) return 1.0;
    vec4 here = texelFetch(_LumenOcclusionCoarse, cell, 0);
    float shadow = skipBefore == 0.0 && (point ? here.a : here.g) != 0.0 ? lumenSunShadow(p, 0.0, point) : 0.0;
    float distance = 0.0;
    if (direction.x < 0.0 && p.x == float(cell.x)) cell.x--;
    if (direction.y < 0.0 && p.y == float(cell.y)) cell.y--;
    while (distance < reach && shadow < 1.0)
    {
        float nextX = direction.x > 0.0 ? (float(cell.x + 1) - p.x) / direction.x : direction.x < 0.0 ? (float(cell.x) - p.x) / direction.x : 1e30;
        float nextZ = direction.y > 0.0 ? (float(cell.y + 1) - p.y) / direction.y : direction.y < 0.0 ? (float(cell.y) - p.y) / direction.y : 1e30;
        float end = min(reach, min(nextX, nextZ));
        vec4 coarse = lumenInside(cell, mapSize) ? texelFetch(_LumenOcclusionCoarse, cell, 0) : vec4(0.0);
        if ((point ? coarse.a : coarse.g) != 0.0)
        {
            int steps = int(ceil((end - distance) * 4.0));
            float ds = (end - distance) / float(steps);
            for (int step = 0; step < steps; step++)
            {
                float at = distance + (float(step) + 0.5) * ds;
                if (at < skipBefore || at >= skipFrom) continue;
                shadow = max(shadow, lumenSunShadow(p + direction * at, at * rise, point));
            }
        }
        distance = end;
        if (nextX <= end) cell.x += direction.x > 0.0 ? 1 : -1;
        if (nextZ <= end) cell.y += direction.y > 0.0 ? 1 : -1;
    }
    return 1.0 - shadow;
}

// SunTransport.Visibility: castXZ is vanilla _CastVect.xz (|castXZ| > 0). 1 outside the map.
float lumenSunVisibility(vec2 p, vec2 castXZ)
{
    float len = length(castXZ);
    return lumenHeightVisibility(p, -castXZ / len, 1.0 / len, len * 2.0, 0.0, 1e30, false);
}

// SunTransport.PointVisibility: light at ground position light and height (>= 0) over ground point target.
float lumenPointVisibility(vec2 light, float height, vec2 target)
{
    vec2 delta = light - target;
    float len = length(delta);
    if (len == 0.0) return 1.0;
    vec2 direction = delta / len;
    float reach = height > 2.0 ? len * 2.0 / height : len;
    return lumenHeightVisibility(target, direction, height / len, reach,
        lumenRunLength(target, direction, len, true), len - lumenRunLength(light, -direction, len, true), true);
}
