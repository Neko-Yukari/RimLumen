#version 450

// Copies the raw scene depth (Waylith reverse-Z device depth) into an R32_SFLOAT color target so the
// particle materials can sample depth through the regular Material texture path.
// gl_FragCoord.xy is the pixel centre, so texelFetch reads the matching depth texel 1:1.
layout(set = 0, binding = 0) uniform sampler2D sceneDepth;
layout(location = 0) out float outDepth;

void main()
{
    outDepth = texelFetch(sceneDepth, ivec2(gl_FragCoord.xy), 0).r;
}
