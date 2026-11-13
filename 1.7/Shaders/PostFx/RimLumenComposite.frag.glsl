#version 450
layout(set = 0, binding = 0) uniform sampler2D hdrImage;
layout(set = 0, binding = 1) uniform sampler2D bloomImage;
layout(set = 0, binding = 2) uniform sampler2D sceneImage;
layout(push_constant) uniform Params { vec4 value; } pc;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;
void main()
{
    vec4 hdr = texture(hdrImage, uv);
    vec3 color = hdr.rgb + texture(bloomImage, uv).rgb * pc.value.x;
    float peak = max(color.r, max(color.g, color.b));
    float shoulder = max(peak - 1.0, 0.0);
    float desaturation = shoulder / (shoulder + 4.0);
    vec3 hue = color / max(peak, 0.00001);
    vec3 mapped = mix(hue, vec3(1.0), desaturation) * (1.0 - exp(-peak));
    // Zero by default; later distortion contributors can supply an explicit screen-space offset.
    vec2 offset = hdr.rg * pc.value.y * pc.value.zw;
    vec3 refracted = texture(sceneImage, uv + offset).rgb - texture(sceneImage, uv).rgb;
    outColor = vec4(mapped + refracted * hdr.a, hdr.a);
}
