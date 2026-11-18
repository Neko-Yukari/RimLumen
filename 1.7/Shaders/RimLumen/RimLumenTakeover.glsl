// Shared by the takeover LightOverlay (3100) and sun shade (3170); declares no bindings.

// Projected light-pool sum at this pixel: hue kept, max component <= 1.
vec3 lumenTakeoverGlow(sampler2D lightSum)
{
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    ivec2 size = textureSize(lightSum, 0);
    vec3 sum = pixel.x < size.x && pixel.y < size.y ? texelFetch(lightSum, pixel, 0).rgb : vec3(0.0);
    if (any(isnan(sum))) sum = vec3(0.0);
    sum = clamp(sum, vec3(0.0), vec3(65504.0)); // RGBA16F finite range before hue projection
    return sum / max(1.0, max(sum.r, max(sum.g, sum.b)));
}

// SunShadowComposition.Overlay: vanilla LightOverlay colour with the sky share scaled by s; clampBase from the unshadowed sky.
vec4 lumenTakeoverOverlay(vec4 sky, float roof, vec3 s, vec3 glow)
{
    vec4 skyContribution = sky * (1.0 - roof);
    vec4 color = vec4(skyContribution.rgb * s + glow, skyContribution.a + roof);
    float clampBase = 1.5 - 0.5 * min(skyContribution.r, skyContribution.b);
    if (color.r > clampBase) color *= clampBase / color.r;
    if (color.g > clampBase) color *= clampBase / color.g;
    if (color.b > clampBase) color *= clampBase / color.b;
    return color;
}
