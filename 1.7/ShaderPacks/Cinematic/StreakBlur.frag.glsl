#version 450
#include "Common.glslinc"

layout(set = 0, binding = 0) uniform sampler2D sourceImage;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

void main()
{
    float stepX = pc.value.x * max(1.0, pc.value.z);
    vec3 sum = texture(sourceImage, uv).rgb * 0.227027;
    sum += (texture(sourceImage, uv + vec2(stepX * 1.3846, 0.0)).rgb
          + texture(sourceImage, uv - vec2(stepX * 1.3846, 0.0)).rgb) * 0.316216;
    sum += (texture(sourceImage, uv + vec2(stepX * 3.2307, 0.0)).rgb
          + texture(sourceImage, uv - vec2(stepX * 3.2307, 0.0)).rgb) * 0.070270;
    outColor = vec4(sum, 0.0);
}
