// The light-pool term L, shared by the queue-3101 composite (RimLumenLightPool.frag.glsl) and the sun-shadow fragments
// (RimLumenSunShadow*.frag.glsl): both read the same screen texel through lumenLightPoolTerm, so the shadow undoes
// exactly the L the composite applied.

// Identity up to a peak of 1, so a lone pool keeps scene * (1 + L). Above 1 the peak holds at 1 and the excess
// whitens the colour along its own hue instead of clipping per channel: overlapping pools stop at 2x.
vec3 compressHighlights(vec3 color)
{
    float peak = max(color.r, max(color.g, color.b));
    if (peak <= 1.0) return color;
    float excess = peak - 1.0;
    return mix(color / peak, vec3(1.0), excess / (excess + 4.0));
}

vec3 lumenLightPoolTerm(sampler2D lightSum, ivec2 texel)
{
    vec3 sum = texelFetch(lightSum, texel, 0).rgb;
    // The RGBA16F sum can overflow to +Inf (Inf / Inf would be NaN in compressHighlights): zero NaN, saturate the rest.
    if (any(isnan(sum))) sum = vec3(0.0);
    sum = clamp(sum, vec3(0.0), vec3(1024.0));
    return compressHighlights(sum);
}

// Shadow side. Until the queue-3101 pass binds this submission's LightSum the material slot resolves to Waylith's black
// fallback texture; texels outside its extent read as no pool, so the placeholder is never stretched over the screen.
vec3 lumenLightPoolUnderShadow(sampler2D lightSum)
{
    ivec2 texel = ivec2(gl_FragCoord.xy);
    return all(lessThan(texel, textureSize(lightSum, 0))) ? lumenLightPoolTerm(lightSum, texel) : vec3(0.0);
}

// A sun shadow multiplies the scene by c, but the composite already made a pool pixel B * (1 + L); multiplying by
// (c + L) / (1 + L) instead leaves B * (c + L): the shadow removes sunlight, not the pool. Mirrors
// VisualPointLight.ShadowOverPool.
vec4 lumenShadowOverPool(vec4 shadow, vec3 light)
{
    if (light == vec3(0.0)) return shadow;
    return vec4((shadow.rgb + light) / (1.0 + light), shadow.a);
}
