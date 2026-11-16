#version 450
#include "Common.glslinc"

layout(set = 0, binding = 0) uniform sampler2D hdrImage;
layout(set = 0, binding = 1) uniform sampler2D prevAdapt;
layout(set = 0, binding = 2) uniform sampler2D glowImage;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

vec3 emission(vec2 at)
{
    return sanitizeHdr(texture(hdrImage, at).rgb) + sanitizeHdr(texture(glowImage, at).rgb);
}

void main()
{
    vec3 center = emission(vec2(0.5));
    vec3 sampleA = emission(vec2(0.35, 0.35));
    vec3 sampleB = emission(vec2(0.65, 0.65));
    float current = max(luminance(center), max(luminance(sampleA), luminance(sampleB)));
    // A history poisoned by an earlier bad frame restarts from the current value instead of staying broken.
    float previous = texture(prevAdapt, uv).r;
    if (isnan(previous) || isinf(previous)) previous = current;
    const float timeConstantSeconds = 0.2;
    float adapted = mix(previous, current, 1.0 - exp(-ubo.GameDeltaSeconds / timeConstantSeconds));
    outColor = vec4(clamp(adapted, 0.0, 1024.0), 0.0, 0.0, 1.0);
}
