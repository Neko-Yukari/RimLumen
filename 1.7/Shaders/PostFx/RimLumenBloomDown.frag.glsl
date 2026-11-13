#version 450
layout(set = 0, binding = 0) uniform sampler2D sourceImage;
layout(push_constant) uniform Params { vec4 value; } pc;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;
void main()
{
    vec2 stepSize = pc.value.xy;
    vec3 sum = texture(sourceImage, uv + stepSize * vec2(-0.5, -0.5)).rgb;
    sum += texture(sourceImage, uv + stepSize * vec2(0.5, -0.5)).rgb;
    sum += texture(sourceImage, uv + stepSize * vec2(-0.5, 0.5)).rgb;
    sum += texture(sourceImage, uv + stepSize * vec2(0.5, 0.5)).rgb;
    outColor = vec4(sum * 0.25, 0.0);
}
