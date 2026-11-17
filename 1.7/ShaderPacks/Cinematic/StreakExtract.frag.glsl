#version 450
#include "Common.glslinc"

layout(set = 0, binding = 0) uniform sampler2D hdrImage;
layout(set = 0, binding = 1) uniform sampler2D glowImage;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

void main()
{
    vec3 c = sanitizeHdr(texture(hdrImage, uv).rgb) + glowInBlendSpace(sanitizeHdr(texture(glowImage, uv).rgb));
    float peak = max(c.r, max(c.g, c.b));
    float threshold = max(0.35, ubo.BloomParams.y * 0.9);
    float knee = max(0.15, ubo.BloomParams.z);
    float soft = clamp(peak - threshold + knee, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee);
    float contrib = max(peak - threshold, soft) / max(peak, 0.00001);
    outColor = vec4(c * contrib, 0.0);
}
