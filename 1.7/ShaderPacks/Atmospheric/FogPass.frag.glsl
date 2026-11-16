#version 450
layout(std140, set = 0, binding = 5) uniform ShaderPackUniforms
{
    mat4 InvViewProjection;
    vec4 ScreenAndMapSize;
    float GameSeconds;
    float RealSeconds;
    float SkyGlow;
    float ZoomRootSize;
    float HasHdrDraws;
    float HasSceneDepth;
    float HasMapLightField;
    float IsPaused;
    float DepthValid;
    float GameDeltaSeconds;
    float RealDeltaSeconds;
    float Padding;
    vec4 BloomParams;
    vec4 PackParams[4];
} ubo;

layout(set = 0, binding = 0) uniform sampler2D sceneImage;
layout(set = 0, binding = 1) uniform sampler2D bloomImage;
layout(set = 0, binding = 2) uniform sampler2D depthImage;
layout(set = 0, binding = 3) uniform sampler2D mapLightField;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

vec3 unproject(vec2 ndc, float z)
{
    vec4 p = ubo.InvViewProjection * vec4(ndc, z, 1.0);
    return p.xyz / p.w;
}

float hash21(vec2 p)
{
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float valueNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    float a = hash21(i);
    float b = hash21(i + vec2(1.0, 0.0));
    float c = hash21(i + vec2(0.0, 1.0));
    float d = hash21(i + vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

float fbm(vec2 p)
{
    float v = 0.0;
    float amp = 0.55;
    for (int i = 0; i < 3; i++)
    {
        v += amp * valueNoise(p);
        p = p * 2.03 + vec2(17.1, 31.7);
        amp *= 0.5;
    }
    return v;
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

    float outdoorFactor = 1.0 - smoothstep(0.25, 0.90, enclosure);
    float litProtect = smoothstep(0.04, 0.45, artificialGroundGlow);
    float skyGlow = clamp(ubo.SkyGlow, 0.0, 1.0);
    float nightFactor = smoothstep(0.88, 0.22, skyGlow);

    float darkness = nightFactor * outdoorFactor * (1.0 - litProtect) * (1.0 - fogOfWar) * max(0.0, ubo.PackParams[0][0]);

    float drift = ubo.GameSeconds * max(0.0, ubo.PackParams[0][2]);
    vec2 wind = vec2(drift * 0.22, drift * 0.09);
    float n1 = fbm(world.xz * 0.085 - wind);
    float n2 = fbm(world.xz * 0.042 + wind * 0.6 + vec2(4.2, 9.1));
    float wisp = smoothstep(0.28, 0.85, n1 * 0.65 + n2 * 0.35);

    // Subtle attenuation on 3D walls when scene depth is present (IsoRim).
    float depth = texture(depthImage, uv).r;
    float is3DGeom = ubo.DepthValid * step(0.0001, min(depth, 1.0 - depth));
    float mistHeightMask = mix(1.0, 0.55, is3DGeom);

    float mist = wisp * outdoorFactor * (1.0 - fogOfWar) * (0.35 + 0.65 * nightFactor) * mistHeightMask * max(0.0, ubo.PackParams[0][1]);
    float litMist = clamp(artificialGroundGlow * 1.4 + dot(texture(bloomImage, uv).rgb, vec3(0.333)), 0.0, 1.0);

    outColor = vec4(mist, litMist, darkness, 1.0);
}
