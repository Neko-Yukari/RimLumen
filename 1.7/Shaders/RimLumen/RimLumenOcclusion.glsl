// Main texture rows follow world z; the four gathered components are (01, 11, 10, 00).
layout(set = 2, binding = 1) uniform sampler2D _LumenOcclusion; // @default = black
layout(set = 2, binding = 2) uniform sampler2D _LumenOcclusionCoarse; // @default = black
layout(set = 2, binding = 3) uniform sampler2D _LumenDoorOpen; // @default = black

bool lumenInside(ivec2 p, ivec2 size) { return all(greaterThanEqual(p, ivec2(0))) && all(lessThan(p, size)); }

float lumenDoorOpacityWithState(ivec2 p, float value, int flags, vec4 door)
{
    bool axisZ = (flags & 2) != 0;
    float local = (float((axisZ ? p.y : p.x) & 3) + 0.5) * 0.25;
    float index = round(door.g * 255.0);
    float width = round(door.b * 255.0);
    float u = (index + local) / width - 0.5;
    return abs(u) < door.r * 0.5 ? 0.0 : value;
}

float lumenDoorOpacity(ivec2 p, float value, int flags)
{
    if ((flags & 1) == 0) return value;
    return lumenDoorOpacityWithState(p, value, flags, texelFetch(_LumenDoorOpen, p >> 2, 0));
}

// OcclusionLayout.DecodeDistance: B as the signed distance in texels, positive inside.
float lumenDecodeDistance(float b) { return (b * 255.0 - 128.0) / 32.0; }

bool lumenSolidTexel(ivec2 p)
{
    if (!lumenInside(p, textureSize(_LumenOcclusion, 0))) return false;
    vec4 v = texelFetch(_LumenOcclusion, p, 0);
    return lumenDoorOpacity(p, v.r, int(round(v.a * 255.0))) == 1.0;
}

bool lumenSolid(vec2 p) { return lumenSolidTexel(ivec2(floor(p * 4.0))); }

// GridWalk.Enter: the cell of spacing size holding start; a start on a cell edge belongs to the cell ahead.
ivec2 lumenGridEnter(vec2 start, vec2 direction, float size)
{
    vec2 g = start / size;
    ivec2 cell = ivec2(floor(g));
    if (direction.x < 0.0 && g.x == float(cell.x)) cell.x--;
    if (direction.y < 0.0 && g.y == float(cell.y)) cell.y--;
    return cell;
}

// GridWalk.Leave: the distance where the ray leaves cell, at most limit; cell steps into the next cell on every axis
// whose edge it reaches by then.
float lumenGridLeave(vec2 start, vec2 direction, float size, float limit, inout ivec2 cell)
{
    float tx = direction.x > 0.0 ? (float(cell.x + 1) * size - start.x) / direction.x : direction.x < 0.0 ? (float(cell.x) * size - start.x) / direction.x : 1e30;
    float tz = direction.y > 0.0 ? (float(cell.y + 1) * size - start.y) / direction.y : direction.y < 0.0 ? (float(cell.y) * size - start.y) / direction.y : 1e30;
    float end = min(limit, min(tx, tz));
    if (tx <= end) cell.x += direction.x > 0.0 ? 1 : -1;
    if (tz <= end) cell.y += direction.y > 0.0 ? 1 : -1;
    return end;
}

// LightTransport.RunLength: the contiguous run of solid texels from start along direction, at most limit; 0 when start's
// own texel is not solid.
float lumenRunLength(vec2 start, vec2 direction, float limit)
{
    if (!lumenSolid(start)) return 0.0;
    float distance = 0.0;
    ivec2 cell = lumenGridEnter(start, direction, 0.25);
    while (distance < limit && lumenSolidTexel(cell))
        distance = lumenGridLeave(start, direction, 0.25, limit, cell);
    return distance;
}

// TexelSplit.Of: x = distance, bilerp(d) with d = -DistanceBand on texels inside another term's footprint (d > 0, not
// occupied), and lumenEdge(split) = clamp(x / EdgeRamp 0.125, 0, 1); y = bilerp(occupied); z = bilerp(occupied ?
// value : 0); w = bilerp(occupied ? 0 : value). Texels in gather order 00, 10, 01, 11. Shared by the point-light and sun paths (design 1.8).
vec4 lumenTexelSplit(vec4 values, vec4 occupied, vec4 distances, vec2 f)
{
    vec4 inside = values * occupied;
    vec4 outside = values - inside;
    vec4 gated = mix(distances, vec4(-4.0), vec4(lessThan(occupied, vec4(0.5))) * vec4(greaterThan(distances, vec4(0.0))));
    return vec4(mix(mix(gated.x, gated.y, f.x), mix(gated.z, gated.w, f.x), f.y),
        mix(mix(occupied.x, occupied.y, f.x), mix(occupied.z, occupied.w, f.x), f.y),
        mix(mix(inside.x, inside.y, f.x), mix(inside.z, inside.w, f.x), f.y),
        mix(mix(outside.x, outside.y, f.x), mix(outside.z, outside.w, f.x), f.y));
}

// TexelSplit.Edge.
float lumenEdge(vec4 split) { return clamp(split.x / 0.125, 0.0, 1.0); }

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
            ivec2 cell = points[i] >> 2;
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
    solid = lumenEdge(split);
    cover = split.w;
}

// LightTransport.Transport: returns the solid term, cover receives the cover term. Requires: target not on a solid texel.
float lumenTransport(vec2 light, vec2 target, out float cover)
{
    cover = 1.0;
    vec2 delta = light - target;
    float len = length(delta);
    if (len == 0.0) return 1.0;
    cover = 0.0;
    vec2 direction = delta / len;
    float lightRun = lumenRunLength(light, -direction, len);
    float solidVisibility = 1.0, pendingSolid = 1.0, coverVisibility = 1.0, distance = 0.0;
    ivec2 mapSize = textureSize(_LumenOcclusionCoarse, 0);
    ivec2 next = lumenGridEnter(target, direction, 1.0);
    while (distance < len)
    {
        ivec2 cell = next;
        float end = lumenGridLeave(target, direction, 1.0, len, next);
        bool inside = lumenInside(cell, mapSize);
        vec4 coarse = inside ? texelFetch(_LumenOcclusionCoarse, cell, 0) : vec4(0.0);
        if (!inside || coarse.r == 0.0)
        {
            solidVisibility = min(solidVisibility, pendingSolid);
            pendingSolid = 1.0;
            distance = end;
            continue;
        }
        bool doors = coarse.b != 0.0;
        int steps = int(ceil((end - distance) * 4.0));
        float ds = (end - distance) / float(steps);
        for (int step = 0; step < steps; step++)
        {
            float at = distance + (float(step) + 0.5) * ds;
            vec2 p = target + direction * at;
            bool isSolid = lightRun > 0.0 && lumenSolid(p);
            if (at >= len - lightRun)
            {
                pendingSolid = 1.0;
                continue;
            }
            float s, o;
            lumenSample(p, doors, s, o);
            if (lightRun > 0.0 && !isSolid && s > 0.0)
            {
                pendingSolid = min(pendingSolid, 1.0 - s);
            }
            else
            {
                solidVisibility = min(solidVisibility, min(pendingSolid, 1.0 - s));
                pendingSolid = 1.0;
            }
            if (o > 0.0) coverVisibility *= pow(1.0 - o, ds);
            if (solidVisibility * coverVisibility < 1.0 / 255.0) return 0.0;
        }
        distance = end;
    }
    cover = coverVisibility;
    return min(solidVisibility, lightRun > 0.0 ? 1.0 : pendingSolid);
}

// LightTransport.Visibility: returns the solid term, cover receives the cover term. A target on a solid texel takes the
// better of the points half a texel outside its cell's light-facing free edges, level with it; when both edges face
// the light and neither is free, the point half a texel outside the corner between them (an inner corner); else 0.
float lightVisibility(vec2 light, vec2 target, out float cover)
{
    vec2 candidates[2];
    int count = 0;
    if (!lumenSolid(target))
    {
        candidates[count++] = target;
    }
    else
    {
        vec2 cell = floor(target);
        vec2 face = vec2(light.x > cell.x ? cell.x + 1.125 : cell.x - 0.125, light.y > cell.y ? cell.y + 1.125 : cell.y - 0.125);
        bool facesX = light.x > cell.x + 1.0 || light.x < cell.x, facesZ = light.y > cell.y + 1.0 || light.y < cell.y;
        vec2 cx = vec2(face.x, target.y), cz = vec2(target.x, face.y);
        if (facesX && !lumenSolid(cx)) candidates[count++] = cx;
        if (facesZ && !lumenSolid(cz)) candidates[count++] = cz;
        if (count == 0 && facesX && facesZ && !lumenSolid(face)) candidates[count++] = face;
    }
    float best = 0.0;
    cover = 0.0;
    for (int i = 0; i < count; i++)
    {
        float c;
        float s = lumenTransport(light, candidates[i], c);
        if (s * c > best * cover)
        {
            best = s;
            cover = c;
            if (best * cover >= 1.0) break;
        }
    }
    return best;
}

// Sun path (SunTransport.Shadow): split G height at q against height t, the edge ramp inside the footprint, faded by
// 1 - f * t / h with f the FlagSunFade (128) share of the occupied texels. distance: the split's distance (texels,
// positive inside).
float lumenSunShadow(vec2 q, float t, out float distance)
{
    ivec2 size = textureSize(_LumenOcclusion, 0);
    vec2 uv = q * 4.0 - 0.5;
    ivec2 base = ivec2(floor(uv));
    vec2 f = fract(uv);
    vec2 gatherUv = q * 4.0 / vec2(size);
    vec4 heights = textureGather(_LumenOcclusion, gatherUv, 1).wzxy;
    vec4 flags = textureGather(_LumenOcclusion, gatherUv, 3).wzxy;
    vec4 fades;
    for (int i = 0; i < 4; i++)
    {
        fades[i] = (int(round(flags[i] * 255.0)) & 128) != 0 ? 1.0 : 0.0;
    }
    ivec2 points[4] = ivec2[4](base, base + ivec2(1, 0), base + ivec2(0, 1), base + ivec2(1, 1));
    for (int i = 0; i < 4; i++) if (!lumenInside(points[i], size)) heights[i] = 0.0;
    vec4 occupied = vec4(greaterThan(heights, vec4(0.0)));
    vec4 split = lumenTexelSplit(heights, occupied, lumenGatherDistances(gatherUv, points, size), f);
    distance = split.x;
    float edge = lumenEdge(split);
    if (edge <= 0.0) return 0.0;
    float h = split.z / split.y * 2.0;
    fades *= occupied;
    float fade = mix(mix(fades.x, fades.y, f.x), mix(fades.z, fades.w, f.x), f.y) / split.y;
    return clamp((h - t) / 0.125, 0.0, 1.0) * edge * (1.0 - fade * t / h);
}

// SunTransport.Step: the shadow at a and, where the split distance crossed the ramp top since the step right before
// (last >= 0, the t = 0 step at p included), also where it reaches the ramp top, placed linearly between the two.
float lumenHeightStep(vec2 p, vec2 direction, float rise, float a, inout float last, inout float lastDistance)
{
    float d, unused;
    float s = lumenSunShadow(p + direction * a, a * rise, d);
    if (last >= 0.0 && lastDistance < 0.125 && d > 0.125)
    {
        float c = last + (a - last) * (0.125 - lastDistance) / (d - lastDistance);
        s = max(s, lumenSunShadow(p + direction * c, c * rise, unused));
    }
    last = a;
    lastDistance = d;
    return s;
}

// SunTransport.Visibility: the blockLight height march; castXZ is vanilla _CastVect.xz (|castXZ| > 0). 1 outside the map.
float lumenSunVisibility(vec2 p, vec2 castXZ)
{
    float len = length(castXZ);
    vec2 direction = -castXZ / len;
    float rise = 1.0 / len, reach = len * 2.0;
    ivec2 mapSize = textureSize(_LumenOcclusionCoarse, 0);
    ivec2 cell = ivec2(floor(p));
    if (!lumenInside(cell, mapSize)) return 1.0;
    float shadow = 0.0, distance = 0.0, last = -1.0, lastDistance = 0.0;
    if (texelFetch(_LumenOcclusionCoarse, cell, 0).g != 0.0)
    {
        shadow = lumenSunShadow(p, 0.0, lastDistance);
        last = 0.0;
    }
    ivec2 next = lumenGridEnter(p, direction, 1.0);
    while (distance < reach && shadow < 1.0)
    {
        cell = next;
        float end = lumenGridLeave(p, direction, 1.0, reach, next);
        vec4 coarse = lumenInside(cell, mapSize) ? texelFetch(_LumenOcclusionCoarse, cell, 0) : vec4(0.0);
        if (coarse.g != 0.0)
        {
            // One fine step per texel crossed, at the middle of the ray's run through it (plus lumenHeightStep's outline
            // entry), starting in this cell's texels.
            ivec2 texel = clamp(ivec2(floor((p + direction * distance) * 4.0)), cell * 4, cell * 4 + 3);
            for (float a = distance; a < end;)
            {
                float b = lumenGridLeave(p, direction, 0.25, end, texel);
                shadow = max(shadow, lumenHeightStep(p, direction, rise, (a + b) * 0.5, last, lastDistance));
                a = b;
            }
        }
        else last = -1.0;
        distance = end;
    }
    return 1.0 - shadow;
}

// ObjectShadow mask term at this screen texel: the mask of the light's round dotted with its ShadowSlot.Selector. A
// cleared mask slot resolves to Waylith's black fallback; texels outside its extent read no shadow.
float lumenObjectShadow(sampler2D mask, vec4 selector)
{
    ivec2 texel = ivec2(gl_FragCoord.xy);
    return all(lessThan(texel, textureSize(mask, 0))) ? dot(texelFetch(mask, texel, 0), selector) : 0.0;
}
