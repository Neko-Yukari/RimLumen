#version 450
#include "../../Shaders/PostFx/RimLumenPackComposite.glslinc"

layout(set = 0, binding = 0) uniform sampler2D hdrImage;
layout(set = 0, binding = 1) uniform sampler2D bloomImage;
layout(set = 0, binding = 2) uniform sampler2D sceneImage;
layout(set = 0, binding = 6) uniform sampler2D distortionImage;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

void main()
{
    LumenLayerSample s = lumenSampleLayer(hdrImage, bloomImage, distortionImage, uv, ubo.BloomParams.x);
    float crisp = clamp(ubo.PackParams[0][0], 0.0, 1.0);
    vec3 color = s.hdr.rgb * (1.0 + crisp * 0.25) + s.bloom;

    float peak = max(color.r, max(color.g, color.b));
    float shoulder = max(peak - 1.0, 0.0);
    float desat = shoulder / (shoulder + 5.0);
    vec3 hue = color / max(peak, 0.00001);
    float mappedPeak = 1.0 - exp(-peak * (1.0 + crisp * 0.2));
    vec3 mapped = mix(hue, vec3(1.0), desat) * mappedPeak;
    // mapped -> 0 as hdr, bloom -> 0, so Linear stays continuous with the untouched scene.
    outColor = lumenCompositeOver(s, sceneImage, mapped);
}
