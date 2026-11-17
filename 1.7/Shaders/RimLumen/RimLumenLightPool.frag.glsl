#version 450

// Light pool composite: one full-screen pass at the queue-3101 split, right after vanilla LightOverlay (3100), with
// blend DstColor, One, so the scene becomes scene * (1 + compressHighlights(sum)). Fog of war, roof darkness and the
// other later map layers cover it exactly as they cover vanilla lighting.
layout(set = 0, binding = 0) uniform sampler2D lightSum; // linear sum of this frame's pools (Accumulate pass)
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

// Identity up to a peak of 1, so a lone pool keeps scene * (1 + L). Above 1 the peak holds at 1 and the excess
// whitens the colour along its own hue instead of clipping per channel: overlapping pools stop at 2x.
vec3 compressHighlights(vec3 color)
{
    float peak = max(color.r, max(color.g, color.b));
    if (peak <= 1.0) return color;
    float excess = peak - 1.0;
    return mix(color / peak, vec3(1.0), excess / (excess + 4.0));
}

void main()
{
    vec3 sum = texture(lightSum, uv).rgb;
    // The RGBA16F sum can overflow to +Inf (Inf / Inf would be NaN in compressHighlights): zero NaN, saturate the rest.
    if (any(isnan(sum))) sum = vec3(0.0);
    sum = clamp(sum, vec3(0.0), vec3(1024.0));
    // Alpha 0 with the alpha factors Zero, One leaves destination alpha unchanged.
    outColor = vec4(compressHighlights(sum), 0.0);
}
