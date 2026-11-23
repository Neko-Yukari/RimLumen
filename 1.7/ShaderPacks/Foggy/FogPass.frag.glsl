#version 450
#include "../../Shaders/PostFx/RimLumenPackUniforms.glslinc"

layout(set = 0, binding = 0) uniform sampler2D sceneImage;
layout(set = 0, binding = 1) uniform sampler2D bloomImage;
layout(set = 0, binding = 3) uniform sampler2D mapLightField;
layout(set = 0, binding = 4) uniform sampler2D lightSum;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

vec3 unproject(vec2 ndc, float z)
{
    vec4 p = ubo.InvViewProjection * vec4(ndc, z, 1.0);
    return p.xyz / p.w;
}

// A unit gradient per lattice point, its angle from the PCG2D integer hash (Jarzynski and Olano 2020). The drift grows
// with GameSeconds (TicksGame / 60) for the whole game; an integer hash keeps distinct values at any lattice point, where
// a float fract hash collapses once the drifted lattice passes ~1e4. Known limit: the float lattice coordinate itself
// steps by 1/32 cell past 2^18 cells of drift (about 6 game years at the top drift speed, 27 at the default).
vec2 lumenGradient(vec2 i)
{
    uvec2 v = uvec2(ivec2(i)) * 1664525u + 1013904223u;
    v.x += v.y * 1664525u;
    v.y += v.x * 1664525u;
    v ^= v >> 16u;
    v.x += v.y * 1664525u;
    v.y += v.x * 1664525u;
    v ^= v >> 16u;
    float a = float(v.x) * (6.2831853 / 4294967296.0);
    return vec2(cos(a), sin(a));
}

// Gradient noise: a unit gradient per lattice point, quintic fade. Value noise put one flat value
// per lattice square and read as soft axis-aligned rectangles several cells wide.
float gradientNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float a = dot(lumenGradient(i), f);
    float b = dot(lumenGradient(i + vec2(1.0, 0.0)), f - vec2(1.0, 0.0));
    float c = dot(lumenGradient(i + vec2(0.0, 1.0)), f - vec2(0.0, 1.0));
    float d = dot(lumenGradient(i + vec2(1.0, 1.0)), f - vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

// 36.87 degrees: no noise lattice lines up with the map axes.
const mat2 rotate = mat2(0.8, 0.6, -0.6, 0.8);

// Each octave is rotated, so no octave's lattice lines up with the map axes. 0.49 + v matches the old field's mean, so
// the wisp thresholds below keep their coverage.
float fbm(vec2 p)
{
    float v = 0.0;
    float amp = 0.55;
    for (int i = 0; i < 3; i++)
    {
        p = rotate * p;
        v += amp * gradientNoise(p);
        p = p * 2.03 + vec2(17.1, 31.7);
        amp *= 0.5;
    }
    return 0.49 + v;
}

void main()
{
    vec2 ndc = uv * 2.0 - 1.0;
    vec3 nearPoint = unproject(ndc, 0.0);
    vec3 farPoint = unproject(ndc, 1.0);
    float span = farPoint.y - nearPoint.y;
    float t = clamp(-nearPoint.y / (abs(span) < 1e-6 ? 1e-6 : span), 0.0, 1.0);
    vec3 world = mix(nearPoint, farPoint, t);

    vec2 fieldUv = world.xz / max(ubo.ScreenAndMapSize.zw, vec2(1.0));
    bool insideMap = fieldUv.x >= 0.0 && fieldUv.x <= 1.0 && fieldUv.y >= 0.0 && fieldUv.y <= 1.0;
    bool hasMapField = ubo.HasMapLightField > 0.5 && insideMap;

    float enclosure = 0.0;
    float artificialGroundGlow = 0.0;
    float fogOfWar = 0.0;
    if (hasMapField)
    {
        vec4 field = texture(mapLightField, fieldUv);
        artificialGroundGlow = field.g;
        // R is roof coverage; B only says an outdoor-temperature Room exists. A roof without that
        // fact (including room-less walls/rock) fully suppresses this pack's outdoor fog and darkening.
        enclosure = field.r * mix(1.0, 0.55, field.b);
        fogOfWar = field.a;
    }
    if (ubo.LayerFlags.y > 0.5)
    {
        // RimLumen's per-pixel light sum replaces the per-cell G: its luminance stands in for GroundGlowAt's
        // max channel / 255, through the same Min(0.5, 3.6 x) (MapLightFieldEncoder.EncodeArtificialGroundGlow).
        float lum = dot(texture(lightSum, uv).rgb, vec3(0.2126, 0.7152, 0.0722));
        artificialGroundGlow = clamp(lum * 3.6, 0.0, 0.5);
    }

    float outdoorFactor = 1.0 - smoothstep(0.25, 0.90, enclosure);
    float litProtect = smoothstep(0.04, 0.45, artificialGroundGlow);
    float skyGlow = clamp(ubo.SkyGlow, 0.0, 1.0);
    float nightFactor = smoothstep(0.88, 0.22, skyGlow);

    float darkness = nightFactor * outdoorFactor * (1.0 - litProtect) * (1.0 - fogOfWar) * max(0.0, ubo.PackParams[0][0]);

    // Wind: both layers drift along one heading (the old wind direction), drift speed x 0.5 cells per game second for
    // the fine layer, 0.7 of that for the coarse one, through one 50-cell domain warp carried at 0.4 of it.
    const vec2 heading = vec2(0.9255, 0.3786);
    float drift = ubo.GameSeconds * max(0.0, ubo.PackParams[0][2]) * 0.5;
    vec2 carried = rotate * ((world.xz - heading * drift * 0.4) * 0.02);
    vec2 ground = world.xz + 4.0 * vec2(gradientNoise(carried), gradientNoise(carried + vec2(5.2, 1.3)));
    float n1 = fbm((ground - heading * drift) * 0.085);
    float n2 = fbm((ground - heading * drift * 0.7) * 0.042 + vec2(4.2, 9.1));
    float wisp = smoothstep(0.28, 0.85, n1 * 0.65 + n2 * 0.35);

    float mist = wisp * outdoorFactor * (1.0 - fogOfWar) * (0.35 + 0.65 * nightFactor) * max(0.0, ubo.PackParams[0][1]);
    float litMist = clamp(artificialGroundGlow * 1.4 + dot(texture(bloomImage, uv).rgb, vec3(0.333)), 0.0, 1.0);

    outColor = vec4(mist, litMist, darkness, 1.0);
}
