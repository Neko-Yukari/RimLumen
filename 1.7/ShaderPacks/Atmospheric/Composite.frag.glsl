#version 450
#include "../../Shaders/RimLumen/RimLumenColorSpace.glsl"
#include "../../Shaders/PostFx/RimLumenPackUniforms.glslinc"
#include "../../Shaders/PostFx/RimLumenDistortion.glslinc"

layout(set = 0, binding = 0) uniform sampler2D hdrImage;
layout(set = 0, binding = 1) uniform sampler2D bloomImage;
layout(set = 0, binding = 2) uniform sampler2D sceneImage;
layout(set = 0, binding = 3) uniform sampler2D fogBlurImage;
layout(set = 0, binding = 4) uniform sampler2D mapLightField;
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
    // Atmospheric always writes the graded scene opaquely; the refracted scene is what gets graded, under the layer
    // and bloom at the layer UV. The fog field is a low-frequency grade and stays at uv.
    vec2 sceneUv = lumenRefractedUv(distortionImage, uv);
    vec2 layerUv = lumenLayerUv(uv, sceneUv);
    vec4 scene = texture(sceneImage, sceneUv);
    vec4 hdr = texture(hdrImage, layerUv);
    hdr.rgb = sanitizeHdr(hdr.rgb);
    vec3 bloom = texture(bloomImage, layerUv).rgb;
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

    float scatterStrength = ubo.BloomParams.x + max(0.0, ubo.PackParams[0][3]) * (0.22 + mist * 0.75);
    vec3 hdrAccum = hdr.rgb + bloom * scatterStrength;
    float peak = max(hdrAccum.r, max(hdrAccum.g, hdrAccum.b));
    float shoulder = max(peak - 1.0, 0.0);
    float desat = shoulder / (shoulder + 4.5);
    vec3 hue = hdrAccum / max(peak, 0.00001);
    vec3 mappedHdr = mix(hue, vec3(1.0), desat) * (1.0 - exp(-peak));

    // Legacy, or a Linear pixel with no effect: the graded scene exactly as Legacy writes it.
    if (ubo.ColorBlendSpace < 0.5 || (hdr == vec4(0.0) && bloom == vec3(0.0)))
    {
        outColor = vec4(baseWithFog * (1.0 - hdr.a) + mappedHdr, 1.0);
        return;
    }
    // Linear: the scene grade stays in stored units (unchanged without effects); the linear-radiance layer goes over
    // it in linear light, so coverage weights radiance instead of its sRGB encoding.
    float a = clamp(hdr.a, 0.0, 1.0);
    outColor = vec4(lumenEncodeStored(max(lumenDecodeStored(baseWithFog) * (1.0 - a) + mappedHdr, vec3(0.0))), 1.0);
}
