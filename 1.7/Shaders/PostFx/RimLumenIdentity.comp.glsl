#version 450
layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;
layout(std430, set = 0, binding = 0) readonly buffer RimLumenInput
{
    uint inputValues[];
};
layout(std430, set = 0, binding = 1) writeonly buffer RimLumenOutput
{
    uint outputValues[];
};
void main()
{
    uint index = gl_GlobalInvocationID.x;
    outputValues[index] = inputValues[index];
}
