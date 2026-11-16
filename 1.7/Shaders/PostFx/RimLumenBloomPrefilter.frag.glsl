#version 450
layout(set = 0, binding = 0) uniform sampler2D sourceImage;
// Vanilla/third-party additive glow copies; the scene already shows their core, so they only feed bloom.
layout(set = 0, binding = 1) uniform sampler2D glowImage;
layout(push_constant) uniform Params { vec4 value; } pc;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;
// Half-float overflow and driver NaN handling differ across GPUs; zero NaN/Inf and cap the rest so one bad
// pixel cannot spread through the bloom chain.
vec3 sanitizeHdr(vec3 c)
{
    if (any(isnan(c)) || any(isinf(c))) return vec3(0.0);
    return clamp(c, vec3(0.0), vec3(1024.0));
}

void main()
{
    vec3 color = sanitizeHdr(texture(sourceImage, uv).rgb) + sanitizeHdr(texture(glowImage, uv).rgb);
    float brightness = max(color.r, max(color.g, color.b));
    float threshold = pc.value.z;
    float knee = pc.value.w;
    float excess = max(brightness - threshold, 0.0);
    // The soft shoulder starts above the threshold; knee=0 is exactly a hard threshold.
    float extracted = knee > 0.0 ? excess * smoothstep(0.0, knee, excess) : excess;
    float contribution = extracted / max(brightness, 0.00001);
    outColor = vec4(color * contribution, 0.0);
}
