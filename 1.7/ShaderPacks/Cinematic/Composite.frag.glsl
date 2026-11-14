#version 450
#include "Common.glslinc"

layout(set = 0, binding = 0) uniform sampler2D hdrImage;
layout(set = 0, binding = 1) uniform sampler2D bloomImage;
layout(set = 0, binding = 2) uniform sampler2D sceneImage;
layout(set = 0, binding = 3) uniform sampler2D streakImage;
layout(set = 0, binding = 4) uniform sampler2D adaptImage;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

void main()
{
    vec4 hdr = texture(hdrImage, uv);
    vec3 bloom = texture(bloomImage, uv).rgb * ubo.BloomParams.x;
    vec3 streak = texture(streakImage, uv).rgb;

    float tintBias = clamp(ubo.PackParam0.z, -1.0, 1.0);
    vec3 coolTint = vec3(0.65, 0.85, 1.25);
    vec3 warmTint = vec3(1.20, 0.96, 0.68);
    vec3 streakTint = mix(coolTint, warmTint, tintBias * 0.5 + 0.5);

    float adapted = clamp(texture(adaptImage, vec2(0.5)).r, 0.0, 8.0);
    float exposureScale = 1.0 / (1.0 + adapted * 0.35 * max(0.0, ubo.PackParam0.y));

    vec3 color = (hdr.rgb + bloom + streak * streakTint * max(0.0, ubo.PackParam0.x)) * exposureScale;
    float peak = max(color.r, max(color.g, color.b));
    float shoulder = max(peak - 0.85, 0.0);
    float desat = shoulder / (shoulder + 3.2);
    vec3 hue = color / max(peak, 0.00001);
    vec3 mapped = mix(hue, vec3(1.0), desat) * (1.0 - exp(-peak * 1.05));
    outColor = vec4(mapped, hdr.a);
}
