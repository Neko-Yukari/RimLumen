#version 450
layout(set = 0, binding = 0) uniform sampler2D hdrImage;
layout(set = 0, binding = 1) uniform sampler2D bloomImage;
layout(set = 0, binding = 2) uniform sampler2D sceneImage;
layout(push_constant) uniform Params { vec4 value; } pc;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;
// Half-float overflow and driver NaN handling differ across GPUs; zero NaN/Inf and cap the rest.
vec3 sanitizeHdr(vec3 c)
{
    if (any(isnan(c)) || any(isinf(c))) return vec3(0.0);
    return clamp(c, vec3(0.0), vec3(1024.0));
}

// Identity up to a peak of 1, so the layer adds exactly what was drawn. Above 1 the peak holds at 1 and the
// excess whitens the colour toward white along its own hue instead of clipping per channel.
vec3 compressHighlights(vec3 color)
{
    float peak = max(color.r, max(color.g, color.b));
    if (peak <= 1.0) return color;
    float excess = peak - 1.0;
    return mix(color / peak, vec3(1.0), excess / (excess + 4.0));
}

void main()
{
    vec4 hdr = texture(hdrImage, uv);
    hdr.rgb = sanitizeHdr(hdr.rgb);
    vec3 mapped = compressHighlights(hdr.rgb + texture(bloomImage, uv).rgb * pc.value.x);
    // Zero by default; later distortion contributors can supply an explicit screen-space offset.
    vec2 offset = hdr.rg * pc.value.y * pc.value.zw;
    vec3 refracted = texture(sceneImage, uv + offset).rgb - texture(sceneImage, uv).rgb;
    outColor = vec4(mapped + refracted * hdr.a, hdr.a);
}
