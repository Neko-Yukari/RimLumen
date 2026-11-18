#version 450

// The map's combined screen-dim coverage into HDR alpha only (blend colour Zero/One, alpha One/OneMinusSrcAlpha).
layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _LumenScreenDim; // x: coverage in (0, 1]
} uMaterial;

void main()
{
    outColor = vec4(0.0, 0.0, 0.0, uMaterial._LumenScreenDim.x);
}
