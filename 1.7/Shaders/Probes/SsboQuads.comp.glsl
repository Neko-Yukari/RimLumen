#version 450

// T1b probe: writes per-instance quad records. Indirect args await the Vma assembly reference.
// set 0 is the compute kernel layout built by ComputeKernelCache from the binding list in C#.

struct QuadInstance
{
    vec2 position;
    vec2 size;
    vec4 color;
};

layout(local_size_x = 32, local_size_y = 1, local_size_z = 1) in;

layout(set = 0, binding = 0) buffer QuadInstances
{
    QuadInstance instances[];
} quads;

layout(push_constant) uniform Push
{
    float framePhase;
    float quadSize;
    float aspect;
    float reserved;
} push;

const uint COUNT = 32u;

void main()
{
    uint i = gl_GlobalInvocationID.x;
    if (i >= COUNT)
    {
        return;
    }

    // The phase moves every instance, so data left over from an earlier frame is visibly wrong.
    float phase = push.framePhase + float(i) * 0.37;
    float col = float(i % 8u);
    float row = float(i / 8u);
    float x = (col + 0.5 + 0.35 * sin(phase)) / 8.0;
    float y = (row + 0.5 + 0.35 * cos(phase)) / 4.0;

    float flicker = 0.5 + 0.5 * sin(phase * 0.5);
    quads.instances[i].position = vec2(x, y);
    quads.instances[i].size = vec2(push.quadSize / max(push.aspect, 0.001), push.quadSize);
    quads.instances[i].color = vec4(1.0 + 2.5 * flicker, 0.4 + 1.6 * (1.0 - flicker), 0.2, 1.0);

}
