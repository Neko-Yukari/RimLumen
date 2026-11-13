#version 450

layout(push_constant) uniform Push
{
    vec4 rect;
    vec4 color;
    vec4 depth;
} pc;

layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

void main()
{
    float d = max(abs(2.0 * uv.x - 1.0), abs(2.0 * uv.y - 1.0));
    float intensity = mix(pc.color.r, 1.0, smoothstep(0.0, 1.0, d));
    outColor = vec4(intensity * vec3(1.0, 0.55, 0.15), 1.0);
}
