#version 450

layout(location = 0) in vec4 vColor;
layout(location = 0) out vec4 outColor;

void main()
{
    // The HDR pass convention is linear premultiplied alpha.
    outColor = vec4(vColor.rgb * vColor.a, vColor.a);
}
