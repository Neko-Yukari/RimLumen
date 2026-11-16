#version 450
layout(set = 0, binding = 0) uniform sampler2D sourceImage;
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
    vec3 color = sanitizeHdr(texture(sourceImage, uv).rgb);
    float brightness = max(color.r, max(color.g, color.b));
    float threshold = pc.value.z;
    float knee = pc.value.w;
    float soft = clamp(brightness - threshold + knee, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee);
    float contribution = max(brightness - threshold, soft) / max(brightness, 0.00001);
    outColor = vec4(color * contribution, 0.0);
}
