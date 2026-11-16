#version 450
layout(std140, set = 0, binding = 5) uniform ShaderPackUniforms
{
    mat4 InvViewProjection;
    vec4 ScreenAndMapSize;
    vec4 EnvParams;
    vec4 BloomParams;
    vec4 PackParam0;
    vec4 PackParam1;
    vec4 PackParam2;
    vec4 PackParam3;
} ubo;

layout(set = 0, binding = 0) uniform sampler2D hdrImage;
layout(set = 0, binding = 1) uniform sampler2D bloomImage;
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
    float crisp = clamp(ubo.PackParam0.x, 0.0, 1.0);
    vec3 color = hdr.rgb * (1.0 + crisp * 0.25) + bloom;

    float peak = max(color.r, max(color.g, color.b));
    float shoulder = max(peak - 1.0, 0.0);
    float desat = shoulder / (shoulder + 5.0);
    vec3 hue = color / max(peak, 0.00001);
    float mappedPeak = 1.0 - exp(-peak * (1.0 + crisp * 0.2));
    vec3 mapped = mix(hue, vec3(1.0), desat) * mappedPeak;
    outColor = vec4(mapped, hdr.a);
}
