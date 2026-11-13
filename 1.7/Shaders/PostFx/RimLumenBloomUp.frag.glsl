#version 450
layout(set = 0, binding = 0) uniform sampler2D lowImage;
layout(set = 0, binding = 1) uniform sampler2D highImage;
layout(push_constant) uniform Params { vec4 value; } pc;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;
void main()
{
    vec3 sum = vec3(0.0);
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            float weight = float((x == 0 ? 2 : 1) * (y == 0 ? 2 : 1));
            sum += texture(lowImage, uv + vec2(x, y) * pc.value.xy).rgb * weight;
        }
    outColor = vec4(texture(highImage, uv).rgb + sum * (pc.value.z / 16.0), 0.0);
}
