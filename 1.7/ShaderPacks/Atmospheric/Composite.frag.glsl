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
    vec4 PackParam0;
    vec4 PackParam1;
    vec4 PackParam2;
    vec4 PackParam3;
} ubo;

layout(set = 0, binding = 0) uniform sampler2D hdrImage;
layout(set = 0, binding = 1) uniform sampler2D bloomImage;
layout(set = 0, binding = 2) uniform sampler2D sceneImage;
layout(set = 0, binding = 3) uniform sampler2D fogBlurImage;
layout(set = 0, binding = 4) uniform sampler2D mapLightField;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

// Half-float overflow and driver NaN handling differ across GPUs; zero NaN/Inf and cap the rest.
vec3 sanitizeHdr(vec3 c)
{
    if (any(isnan(c)) || any(isinf(c))) return vec3(0.0);
    return clamp(c, vec3(0.0), vec3(1024.0));
}

void main()
{
    vec4 scene = texture(sceneImage, uv);
    vec4 hdr = texture(hdrImage, uv);
    hdr.rgb = sanitizeHdr(hdr.rgb);
    vec3 bloom = texture(bloomImage, uv).rgb;
    vec4 fogData = texture(fogBlurImage, uv);

    float mist = clamp(fogData.r, 0.0, 0.85);
    float litMist = clamp(fogData.g, 0.0, 1.0);
    float darkness = clamp(fogData.b, 0.0, 0.92);

    vec3 nightTint = mix(vec3(1.0), vec3(0.76, 0.86, 1.06), darkness);
    vec3 darkenedScene = scene.rgb * (1.0 - darkness * 0.68) * nightTint;

    vec3 coolMistColor = vec3(0.42, 0.50, 0.62) * mix(1.0, 0.45, darkness);
    vec3 warmMistColor = vec3(0.95, 0.78, 0.56);
    vec3 mistColor = mix(coolMistColor, warmMistColor, litMist * 0.65);

    vec3 baseWithFog = mix(darkenedScene, max(darkenedScene, mistColor), mist * 0.42);

    float scatterStrength = ubo.BloomParams.x + max(0.0, ubo.PackParam0.w) * (0.22 + mist * 0.75);
    vec3 hdrAccum = hdr.rgb + bloom * scatterStrength;
    float peak = max(hdrAccum.r, max(hdrAccum.g, hdrAccum.b));
    float shoulder = max(peak - 1.0, 0.0);
    float desat = shoulder / (shoulder + 4.5);
    vec3 hue = hdrAccum / max(peak, 0.00001);
    vec3 mappedHdr = mix(hue, vec3(1.0), desat) * (1.0 - exp(-peak));

    vec3 finalRgb = baseWithFog * (1.0 - hdr.a) + mappedHdr;
    outColor = vec4(finalRgb, 1.0);
}
