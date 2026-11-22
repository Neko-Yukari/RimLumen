// Ported verbatim from RimLumen native shaders (WebGL2 / GLSL ES 3.00 for Three.js RawShaderMaterial).
// Provenance:
//   - 1.7/Shaders/Particles/L1ParticleQuad.vert.glsl:6-25
//   - 1.7/Shaders/RimLumen/RimLumenColorSpace.glsl:7-25
//   - 1.7/Shaders/Particles/ParticleDisc.glsl:4-9
//   - 1.7/Shaders/Particles/ParticleColor.glsl:8-15
//   - 1.7/Shaders/Particles/L1ParticleQuad.frag.glsl:35-65
//   - Source/RimLumen.Core/Lighting/VisualPointLight.cs:37-46 (point light radial falloff)
// Note: Browser preview renders without a RimWorld scene depth buffer, so soft-particle depth feathering
// and LumenDepthMask projectile occlusion are omitted (preview displays unclipped particle geometry).

export const L1_PARTICLE_VERTEX_SHADER = /* glsl */ `
precision highp float;

uniform mat4 uViewProj;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inUv;
layout(location = 2) in vec4 inColor;
layout(location = 3) in vec3 inRadialUvGain;
layout(location = 4) in vec2 inDepthMask;

out vec2 vUv;
out vec4 vColor;
out vec2 vRadialUv;
out float vHdrGain;

void main() {
    gl_Position = uViewProj * vec4(inPosition, 1.0);
    vUv = inUv;
    vColor = inColor;
    vRadialUv = inRadialUvGain.xy;
    vHdrGain = inRadialUvGain.z;
}
`;

export const L1_PARTICLE_FRAGMENT_SHADER = /* glsl */ `
precision highp float;

// x: sample texture (1) or procedural radial disc (0)
// y: premultiply RGB by alpha (1 for Additive / Premultiplied, 0 for AlphaBlend)
uniform vec4 uLumenDraw;
uniform vec4 uLumenMaterialTint;
// x: Linear material calculation (1 vs Stored 0), y: resolved texture is sRGB (1 vs 0)
uniform vec4 uLumenColor;
uniform sampler2D uParticleTexture;

in vec2 vUv;
in vec4 vColor;
in vec2 vRadialUv;
in float vHdrGain;

out vec4 outColor;

// 1.7/Shaders/RimLumen/RimLumenColorSpace.glsl:7-18
vec3 lumenDecodeStored(vec3 storedRgb) {
    return mix(
        pow(max(storedRgb + 0.055, vec3(0.0)) / 1.055, vec3(2.4)),
        storedRgb / 12.92,
        lessThanEqual(storedRgb, vec3(0.04045))
    );
}

vec3 lumenEncodeStored(vec3 linearRgb) {
    vec3 clamped = max(linearRgb, vec3(0.0));
    return mix(
        1.055 * pow(clamped, vec3(1.0 / 2.4)) - 0.055,
        clamped * 12.92,
        lessThanEqual(clamped, vec3(0.0031308))
    );
}

// 1.7/Shaders/Particles/ParticleDisc.glsl:4-9
float lumenParticleDiscCoverage(vec2 quadUv) {
    vec2 radial = quadUv * 2.0 - 1.0;
    float coverage = max(0.0, 1.0 - dot(radial, radial));
    return coverage * coverage;
}

// 1.7/Shaders/Particles/ParticleColor.glsl:8-15
vec4 lumenParticleStoredColor(vec4 vertex, vec4 tint, vec4 texel, bool linearMaterial, bool textureSrgb) {
    vec3 rgb = linearMaterial
        ? lumenEncodeStored(vertex.rgb * lumenDecodeStored(tint.rgb)
            * (textureSrgb ? lumenDecodeStored(texel.rgb) : texel.rgb))
        : vertex.rgb * tint.rgb * texel.rgb;
    return vec4(rgb, vertex.a * tint.a * texel.a);
}

// 1.7/Shaders/Particles/L1ParticleQuad.frag.glsl:35-65 (outputs linear HDR into Three.js HalfFloat target)
void main() {
    vec4 texel = uLumenDraw.x > 0.5
        ? texture(uParticleTexture, vUv)
        : vec4(1.0, 1.0, 1.0, lumenParticleDiscCoverage(vRadialUv));

    vec4 c = lumenParticleStoredColor(
        vColor,
        uLumenMaterialTint,
        texel,
        uLumenColor.x > 0.5,
        uLumenColor.y > 0.5
    );
    if (c.a <= 0.0) {
        discard;
    }

    // 1.7/Shaders/RimLumen/RimLumenColorSpace.glsl:22-25: decode stored RGB to linear HDR before additive blend;
    // Three.js HalfFloat target + OutputPass built-in tone mapping handle highlight compression & sRGB encode.
    c.rgb = lumenDecodeStored(c.rgb) * vHdrGain;
    if (uLumenDraw.y > 0.5) {
        c.rgb *= c.a;
    }
    outColor = c;
}
`;

// Ported from Source/RimLumen.Core/Lighting/VisualPointLight.cs:37-46: w = (1 - (d/R)^2)^2
export const POINT_LIGHT_VERTEX_SHADER = /* glsl */ `
precision highp float;

uniform mat4 uViewProj;
uniform mat4 uModel;

layout(location = 0) in vec3 position;
layout(location = 1) in vec2 uv;

out vec2 vLocalUv;

void main() {
    vLocalUv = uv * 2.0 - 1.0;
    gl_Position = uViewProj * (uModel * vec4(position, 1.0));
}
`;

export const POINT_LIGHT_FRAGMENT_SHADER = /* glsl */ `
precision highp float;

uniform vec3 uLightColor;

in vec2 vLocalUv;
out vec4 outColor;

vec3 lumenDecodeStored(vec3 storedRgb) {
    return mix(
        pow(max(storedRgb + 0.055, vec3(0.0)) / 1.055, vec3(2.4)),
        storedRgb / 12.92,
        lessThanEqual(storedRgb, vec3(0.04045))
    );
}

void main() {
    float rSq = dot(vLocalUv, vLocalUv);
    if (rSq >= 1.0) {
        discard;
    }
    float q = 1.0 - rSq;
    float w = q * q;
    float peak = max(uLightColor.r, max(uLightColor.g, uLightColor.b));
    float scale = max(peak, 1.0);
    vec3 linLight = peak > 1e-5 ? lumenDecodeStored(uLightColor / scale) * scale : vec3(0.0);
    outColor = vec4(linLight * (w * 0.45), 1.0);
}
`;
