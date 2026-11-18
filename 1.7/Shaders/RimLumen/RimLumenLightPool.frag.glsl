#version 450
#include "RimLumenLightPoolTerm.glsl"

// Light pool composite: one full-screen pass at the queue-3101 split, right after vanilla LightOverlay (3100), with
// blend DstColor, One, so the scene becomes scene * (1 + compressHighlights(sum)). Fog of war, roof darkness and the
// other later map layers cover it exactly as they cover vanilla lighting.
layout(set = 0, binding = 0) uniform sampler2D lightSum; // linear sum of this frame's pools (Accumulate pass)
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

void main()
{
    // Alpha 0 with the alpha factors Zero, One leaves destination alpha unchanged.
    outColor = vec4(lumenLightPoolTerm(lightSum, ivec2(gl_FragCoord.xy)), 0.0);
}
