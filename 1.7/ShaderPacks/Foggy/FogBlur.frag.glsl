#version 450
layout(push_constant) uniform Params { vec4 value; } pc;
layout(set = 0, binding = 0) uniform sampler2D fogField;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

void main()
{
    vec2 d = pc.value.xy * 1.5;
    vec4 s = texture(fogField, uv) * 0.25;
    s += texture(fogField, uv + vec2(d.x, 0.0)) * 0.125;
    s += texture(fogField, uv - vec2(d.x, 0.0)) * 0.125;
    s += texture(fogField, uv + vec2(0.0, d.y)) * 0.125;
    s += texture(fogField, uv - vec2(0.0, d.y)) * 0.125;
    s += texture(fogField, uv + vec2(d.x, d.y)) * 0.0625;
    s += texture(fogField, uv + vec2(-d.x, d.y)) * 0.0625;
    s += texture(fogField, uv + vec2(d.x, -d.y)) * 0.0625;
    s += texture(fogField, uv + vec2(-d.x, -d.y)) * 0.0625;
    outColor = s;
}
