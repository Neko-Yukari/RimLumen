#version 450
#include "../RimLumen/RimLumenColorSpace.glsl"

layout(set = 0, binding = 0) uniform sampler2D hdrLinear;
layout(location = 0) out vec4 outColor;

void main()
{
    vec4 color = texelFetch(hdrLinear, ivec2(gl_FragCoord.xy), 0);
    outColor = vec4(lumenEncodeStored(color.rgb), color.a);
}
