#version 450
#include "RimLumenDepth.glslinc"

layout(std430, set = 2, binding = 1) readonly buffer RimLumenProbeData
{
    vec4 values[];
};
layout(location = 0) out vec4 outColor;

void main()
{
    vec4 parameters = values[0];
    float sceneDepth = RimLumenLinearDepth(values[1].x, parameters);
    float particleDepth = RimLumenLinearDepth(gl_FragCoord.z, parameters);
    float fade = RimLumenSoftParticleFade(sceneDepth, particleDepth, parameters.w);
    outColor = vec4(fade);
}
