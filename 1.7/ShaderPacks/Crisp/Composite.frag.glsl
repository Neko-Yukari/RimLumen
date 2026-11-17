#version 450
#include "../../Shaders/RimLumen/RimLumenColorSpace.glsl"
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
    float ColorBlendSpace;
    vec4 BloomParams;
    vec4 PackParams[4];
} ubo;
#include "../../Shaders/PostFx/RimLumenDistortion.glslinc"

layout(set = 0, binding = 0) uniform sampler2D hdrImage;
layout(set = 0, binding = 1) uniform sampler2D bloomImage;
layout(set = 0, binding = 2) uniform sampler2D sceneImage;
layout(set = 0, binding = 6) uniform sampler2D distortionImage;
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
    vec4 hdr = texture(hdrImage, uv);
    hdr.rgb = sanitizeHdr(hdr.rgb);
    vec3 bloom = texture(bloomImage, uv).rgb * ubo.BloomParams.x;
    bool linearBlend = ubo.ColorBlendSpace > 0.5;
    vec2 sceneUv = lumenRefractedUv(distortionImage, uv);
    // Linear: empty, undistorted pixels leave the scene bit-exact.
    if (linearBlend && hdr == vec4(0.0) && bloom == vec3(0.0) && sceneUv == uv)
    {
        outColor = vec4(0.0);
        return;
    }
    float crisp = clamp(ubo.PackParams[0][0], 0.0, 1.0);
    vec3 color = hdr.rgb * (1.0 + crisp * 0.25) + bloom;

    float peak = max(color.r, max(color.g, color.b));
    float shoulder = max(peak - 1.0, 0.0);
    float desat = shoulder / (shoulder + 5.0);
    vec3 hue = color / max(peak, 0.00001);
    float mappedPeak = 1.0 - exp(-peak * (1.0 + crisp * 0.2));
    vec3 mapped = mix(hue, vec3(1.0), desat) * mappedPeak;
    if (!linearBlend)
    {
        // Refracted pixels are written opaquely as the PremultipliedAlpha blend over the refracted scene.
        outColor = sceneUv == uv ? vec4(mapped, hdr.a)
            : vec4(mapped + texture(sceneImage, sceneUv).rgb * (1.0 - clamp(hdr.a, 0.0, 1.0)), 1.0);
        return;
    }
    // Linear: the tone-mapped layer goes over the decoded scene in linear light and is written opaquely.
    // mapped -> 0 as hdr, bloom -> 0, so the result stays continuous with the untouched scene.
    vec3 scene = lumenDecodeStored(texture(sceneImage, sceneUv).rgb);
    float a = clamp(hdr.a, 0.0, 1.0);
    outColor = vec4(lumenEncodeStored(max(scene * (1.0 - a) + mapped, vec3(0.0))), 1.0);
}
