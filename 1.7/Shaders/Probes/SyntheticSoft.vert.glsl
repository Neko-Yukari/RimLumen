#version 450

layout(push_constant) uniform Push
{
    vec4 rect;
    vec4 color;
    vec4 depth;
} pc;

const vec2 corners[6] = vec2[6](
    vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(0.0, 1.0),
    vec2(1.0, 0.0), vec2(1.0, 1.0), vec2(0.0, 1.0));

void main()
{
    vec2 corner = corners[gl_VertexIndex];
    vec2 p = pc.rect.xy + corner * pc.rect.zw;
    gl_Position = vec4(p * 2.0 - 1.0, corner.x, 1.0);
}
