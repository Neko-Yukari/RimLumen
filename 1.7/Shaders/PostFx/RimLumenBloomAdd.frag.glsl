#version 450
layout(set = 0, binding = 0) uniform sampler2D bloomImage;
layout(push_constant) uniform Params { vec4 value; } pc;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;
void main()
{
    outColor = vec4(texture(bloomImage, uv).rgb * pc.value.x, 0.0);
}
