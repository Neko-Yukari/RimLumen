#version 450
#include "Common.glslinc"

layout(set = 0, binding = 0) uniform sampler2D hdrImage;
layout(set = 0, binding = 1) uniform sampler2D prevAdapt;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

void main()
{
    vec3 center = sanitizeHdr(texture(hdrImage, vec2(0.5)).rgb);
    vec3 sampleA = sanitizeHdr(texture(hdrImage, vec2(0.35, 0.35)).rgb);
    vec3 sampleB = sanitizeHdr(texture(hdrImage, vec2(0.65, 0.65)).rgb);
    float current = max(luminance(center), max(luminance(sampleA), luminance(sampleB)));
    // A history poisoned by an earlier bad frame restarts from the current value instead of staying broken.
    float previous = texture(prevAdapt, uv).r;
    if (isnan(previous) || isinf(previous)) previous = current;
    float adapted = mix(previous, current, 0.08);
    outColor = vec4(clamp(adapted, 0.0, 1024.0), 0.0, 0.0, 1.0);
}
