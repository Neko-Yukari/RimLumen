#version 450
#include "LumenForceArrowMaterial.glsl"

layout(location = 0) in vec4 vColor;
layout(location = 0) out vec4 outColor;

void main()
{
    outColor = vColor;
}
