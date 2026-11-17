#version 450

// One distortion node into the RG16F `distortion` target (blend One, One, so overlapping nodes add). The output is the
// scene sample offset in framebuffer pixels; a positive amplitude shows the scene behind pushed outward, so the sample
// comes from nearer the centre. Lengths and amplitudes are in units of R (vLocal spans the unit disc). The local-to-pixel
// map is the inverse of the quad's screen derivatives, so the footprint follows any camera, Vertical3D pitch included.
layout(location = 0) in vec2 vLocal;

layout(location = 0) out vec4 outOffset;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _LumenRingRadii;       // up to four ring radii; amplitude 0 marks an absent ring
    vec4 _LumenRingAmplitudes;  // signed ring amplitudes
    vec4 _LumenDistortionShape; // x: ring half-width (0 = HeatHaze), y: haze amplitude, z: haze lattice steps per R,
                                // w: haze phase (lattice steps along time)
} uMaterial;

float hash13(vec3 p)
{
    p = fract(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return fract((p.x + p.y) * p.z);
}

// Smooth value noise in [-1, 1].
float valueNoise(vec3 p)
{
    vec3 i = floor(p);
    vec3 f = fract(p);
    vec3 u = f * f * (3.0 - 2.0 * f);
    float x00 = mix(hash13(i), hash13(i + vec3(1.0, 0.0, 0.0)), u.x);
    float x10 = mix(hash13(i + vec3(0.0, 1.0, 0.0)), hash13(i + vec3(1.0, 1.0, 0.0)), u.x);
    float x01 = mix(hash13(i + vec3(0.0, 0.0, 1.0)), hash13(i + vec3(1.0, 0.0, 1.0)), u.x);
    float x11 = mix(hash13(i + vec3(0.0, 1.0, 1.0)), hash13(i + vec3(1.0, 1.0, 1.0)), u.x);
    return mix(mix(x00, x10, u.y), mix(x01, x11, u.y), u.z) * 2.0 - 1.0;
}

void main()
{
    // Derivatives first: they must be taken in uniform control flow.
    mat2 localPerPixel = mat2(dFdx(vLocal), dFdy(vLocal));
    float r = length(vLocal);
    vec4 shape = uMaterial._LumenDistortionShape;
    vec2 localOffset;
    if (shape.x > 0.0)
    {
        // Each ring is a (1 - x^2)^2 bump over [radius - half-width, radius + half-width].
        vec4 x = (vec4(r) - uMaterial._LumenRingRadii) / shape.x;
        vec4 bump = max(vec4(0.0), 1.0 - x * x);
        float push = dot(uMaterial._LumenRingAmplitudes, bump * bump);
        localOffset = -push * vLocal / max(r, 1e-4);
    }
    else
    {
        vec3 p = vec3(vLocal * shape.z, shape.w);
        localOffset = shape.y * vec2(valueNoise(p), valueNoise(p + vec3(17.3, 41.9, 7.1)));
    }
    // Every kind fades to zero over the outer fifth of R and stays zero beyond it.
    localOffset *= clamp((1.0 - r) * 5.0, 0.0, 1.0);
    outOffset = vec4(inverse(localPerPixel) * localOffset, 0.0, 0.0);
}
