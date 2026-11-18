#version 450

// Light pool accumulation: one ground quad per light, spanning [-R, R] around the light, drawn with blend One, One
// into a cleared RGBA16F target, so overlapping pools add linearly. RimLumenLightPool.frag.glsl applies the sum.
// The vertex stage is Waylith's SceneDefault.vert.glsl (vUv at location 1, world position at 2; plane10 uv spans [0,1]).
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _LumenLight; // rgb: ColorRgb * effective intensity (>= 0, unclamped); a unused
    vec4 _LumenLightAt; // xy: light centre in world cells (X, Z); zw unused
} uMaterial;
// One texel per map cell (column x, row z): 1 where the cell is impassable (ParticleCollisionFields bit 0).
layout(set = 2, binding = 1) uniform sampler2D _LumenOccluders; // @default = black

// Widest distance (cells, across the segment, halfway along it) at which a wall corner still shades: it sets how
// wide the soft edge beside a wall corner is.
const float PenumbraReach = 0.25;

bool isWall(ivec2 cell, ivec2 mapSize)
{
    return all(greaterThanEqual(cell, ivec2(0))) && all(lessThan(cell, mapSize))
        && texelFetch(_LumenOccluders, cell, 0).r > 0.5;
}

// Mirrors LightOcclusion.Visibility (C#). Walls are infinitely high, so this is 2D line of sight on the cell grid:
// 0 when the segment target -> light passes through a wall cell other than the target's and the light's own cell;
// otherwise the corners of nearby wall cells darken it smoothly as they approach the segment (inner penumbra).
float lightVisibility(vec2 light, vec2 target)
{
    // The pixel under the light's centre has no segment to march.
    if (target == light) return 1.0;
    ivec2 lightCell = ivec2(floor(light));
    ivec2 targetCell = ivec2(floor(target));
    ivec2 mapSize = textureSize(_LumenOccluders, 0);
    // March column by column along the axis that moves further; swapped frames read the grid as (z, x).
    bool swapped = abs(light.y - target.y) > abs(light.x - target.x);
    vec2 a = swapped ? target.yx : target;
    vec2 b = swapped ? light.yx : light;
    ivec2 aCell = swapped ? targetCell.yx : targetCell;
    ivec2 bCell = swapped ? lightCell.yx : lightCell;
    vec2 d = b - a;
    float len = length(d);
    vec2 n = d / len;
    float slope = d.y / d.x;
    // Only cells within PenumbraReach of the segment can shade it: halo rows across it, columns past each end.
    float halo = PenumbraReach * sqrt(1.0 + slope * slope);
    float xMin = min(a.x, b.x);
    float xMax = max(a.x, b.x);
    float clearance = 1.0;
    for (int i = int(floor(xMin - PenumbraReach)); i <= int(floor(xMax + PenumbraReach)); i++)
    {
        float x0 = clamp(float(i), xMin, xMax);
        float x1 = clamp(float(i + 1), xMin, xMax);
        float z0 = a.y + (x0 - a.x) * slope;
        float z1 = a.y + (x1 - a.x) * slope;
        float zLow = min(z0, z1);
        float zHigh = max(z0, z1);
        for (int j = int(floor(zLow - halo)); j <= int(floor(zHigh + halo)); j++)
        {
            ivec2 cell = ivec2(i, j);
            if (cell == aCell || cell == bCell || !isWall(swapped ? cell.yx : cell, mapSize)) continue;
            if (x0 < x1 && zLow < float(j + 1) && zHigh > float(j)) return 0.0;
            for (int k = 0; k < 4; k++)
            {
                vec2 v = vec2(float(i + (k & 1)), float(j + (k >> 1))) - a;
                float s = dot(v, n) / len;
                if (s <= 0.0 || s >= 1.0) continue;
                // Distance from the segment over the reach at that depth, 0 touching and 1 clear. The reach closes at
                // both ends, so corners at or past the target or the light fade out instead of switching off.
                clearance = min(clearance, abs(v.x * n.y - v.y * n.x) / (4.0 * PenumbraReach * s * (1.0 - s)));
            }
        }
    }
    return smoothstep(0.0, 1.0, clearance);
}

void main()
{
    // d = offset / R, so dot(d, d) = (distance / R)^2 and the falloff (1 - (distance / R)^2)^2 is 0 at and beyond R.
    vec2 d = vUv * 2.0 - 1.0;
    float q = max(0.0, 1.0 - dot(d, d));
    float falloff = q * q;
    // Pixels outside the pool add nothing, so they skip the wall march.
    float visibility = falloff > 0.0 ? lightVisibility(uMaterial._LumenLightAt.xy, vWorldPos.xz) : 0.0;
    outColor = vec4(uMaterial._LumenLight.rgb * (falloff * visibility), 0.0);
}
