#version 450

layout(push_constant) uniform Push
{
    vec4 rect;
    vec4 color;
    vec4 depth;
} pc;

layout(location = 0) out vec4 outColor;

void main()
{
    outColor = vec4(pc.color.rgb * pc.color.a, pc.color.a);
}
