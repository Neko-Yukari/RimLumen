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
    vec2 p = pc.rect.xy + corners[gl_VertexIndex] * pc.rect.zw;
    // The pipeline uses a positive-height Vulkan viewport: NDC -1 is the top edge.
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
