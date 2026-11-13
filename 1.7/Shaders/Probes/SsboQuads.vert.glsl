#version 450

// T1b probe: no vertex buffer. Instance records come from the SSBO the compute pass wrote this frame.

struct QuadInstance
{
    vec2 position;
    vec2 size;
    vec4 color;
};

layout(set = 0, binding = 0) readonly buffer QuadInstances
{
    QuadInstance instances[];
} quads;

layout(location = 0) out vec4 vColor;

const vec2 corners[6] = vec2[6](
    vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(0.0, 1.0),
    vec2(1.0, 0.0), vec2(1.0, 1.0), vec2(0.0, 1.0));

void main()
{
    QuadInstance q = quads.instances[gl_InstanceIndex];
    vec2 p = q.position + (corners[gl_VertexIndex] - 0.5) * q.size;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
    vColor = q.color;
}
