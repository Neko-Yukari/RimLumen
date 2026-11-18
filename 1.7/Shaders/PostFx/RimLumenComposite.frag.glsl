#version 450
#include "../RimLumen/RimLumenColorSpace.glsl"
#include "RimLumenPackUniforms.glslinc"
#include "RimLumenDistortion.glslinc"
layout(set = 0, binding = 0) uniform sampler2D hdrImage;
layout(set = 0, binding = 1) uniform sampler2D bloomImage;
layout(set = 0, binding = 2) uniform sampler2D sceneImage;
layout(set = 0, binding = 6) uniform sampler2D distortionImage;
layout(push_constant) uniform Params { vec4 value; } pc;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;
// Half-float overflow and driver NaN handling differ across GPUs; zero NaN/Inf and cap the rest.
vec3 sanitizeHdr(vec3 c)
{
    if (any(isnan(c)) || any(isinf(c))) return vec3(0.0);
    return clamp(c, vec3(0.0), vec3(1024.0));
}

// Identity up to a peak of 1, so the layer adds exactly what was drawn. Above 1 the peak holds at 1 and the
// excess whitens the colour toward white along its own hue instead of clipping per channel.
vec3 compressHighlights(vec3 color)
{
    float peak = max(color.r, max(color.g, color.b));
    if (peak <= 1.0) return color;
    float excess = peak - 1.0;
    return mix(color / peak, vec3(1.0), excess / (excess + 4.0));
}

void main()
{
    // The scene behind the layer, refracted, and the layer with its bloom refracted with it unless the player turned
    // that off; both equal uv wherever nothing distorts.
    vec2 sceneUv = lumenRefractedUv(distortionImage, uv);
    vec2 layerUv = lumenLayerUv(uv, sceneUv);
    vec4 hdr = texture(hdrImage, layerUv);
    hdr.rgb = sanitizeHdr(hdr.rgb);
    vec3 bloom = texture(bloomImage, layerUv).rgb * pc.value.x;
    if (ubo.ColorBlendSpace < 0.5)
    {
        vec3 layer = compressHighlights(hdr.rgb + bloom);
        if (sceneUv == uv)
        {
            outColor = vec4(layer, hdr.a);
            return;
        }
        // Refracted: write opaquely what the PremultipliedAlpha blend would give over the refracted scene.
        float a = clamp(hdr.a, 0.0, 1.0);
        outColor = vec4(layer + texture(sceneImage, sceneUv).rgb * (1.0 - a), 1.0);
        return;
    }
    // Linear: hdr is premultiplied linear radiance. Empty, undistorted pixels leave the scene bit-exact.
    if (hdr == vec4(0.0) && bloom == vec3(0.0) && sceneUv == uv)
    {
        outColor = vec4(0.0);
        return;
    }
    // Composite over the decoded scene in linear light and write the encoded result opaquely, so coverage
    // weights radiance instead of its sRGB encoding. compressHighlights is identity up to 1: hdr, bloom -> 0
    // reproduces the scene exactly.
    // RGBA16F coverage is unclamped; Legacy's UNORM OneMinusSrcAlpha blend sees it clamped to [0, 1].
    float a = clamp(hdr.a, 0.0, 1.0);
    vec3 scene = lumenDecodeStored(texture(sceneImage, sceneUv).rgb);
    vec3 linear = max(scene * (1.0 - a) + hdr.rgb + bloom, vec3(0.0));
    outColor = vec4(lumenEncodeStored(compressHighlights(linear)), 1.0);
}
