#version 450
#include "ParticleTypes.glsl"
#include "ParticleMaterial.glsl"

// Triangle list, six generated vertices per instance. cmd.Draw(6, capacity, 0, 0).
// Empty vertex input layout; firstVertex and firstInstance must both be zero.
const vec2 quadUvs[6] = vec2[6](vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(1.0, 1.0),
    vec2(0.0, 0.0), vec2(1.0, 1.0), vec2(0.0, 1.0));
layout(set = 0, binding = 0) uniform VertexUniforms { mat4 ViewProj; } camera;
layout(std430, set = 2, binding = 1) readonly buffer _LumenParticles { GpuParticle particles[]; };
layout(std430, set = 2, binding = 2) readonly buffer _LumenEmitters { GpuEmitterParams emitters[]; };
layout(std430, set = 2, binding = 3) readonly buffer _LumenCurves { GpuCurveSample curves[]; };

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec2 vRadialUv;
layout(location = 3) out float vSoftDistance;

void main()
{
    vec2 inUv = quadUvs[gl_VertexIndex];
    vec2 inPosition = inUv - vec2(0.5);
    GpuEmitterParams emitter = emitters[uint(material._LumenDraw.x)];
    GpuParticle particle = particles[uint(emitter.Slots.x) + uint(gl_InstanceIndex)];
    vColor = vec4(0.0);
    vUv = inUv;
    vRadialUv = inUv;
    vSoftDistance = emitter.State.y;
    if (particle.SizeSeedEmitterFlags.w < 0.5 || particle.PositionAndAge.w >= particle.VelocityAndLifetime.w)
    {
        // All vertices of dead slots coincide, producing no fragments without a CPU count.
        gl_Position = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }
    float normalizedAge = clamp(particle.PositionAndAge.w / particle.VelocityAndLifetime.w, 0.0, 1.0);
    float samplePosition = normalizedAge * 63.0;
    uint lower = uint(samplePosition);
    uint row = uint(emitter.Tables.x) * 64u;
    GpuCurveSample a = curves[row + lower];
    GpuCurveSample b = curves[row + min(lower + 1u, 63u)];
    float blend = fract(samplePosition);
    float size = particle.SizeSeedEmitterFlags.x * mix(a.Size.x, b.Size.x, blend);
    vColor = particle.StartColor * mix(a.Color, b.Color, blend);
    vColor.rgb *= emitter.Appearance.z * emitter.State.w;
    // Appearance.w (BloomWeight) is reserved until the HDR pass exposes a separate bloom channel.

    vec3 center = particle.PositionAndAge.xyz;
    vec3 velocity = particle.VelocityAndLifetime.xyz;
    if (emitter.SizeCone.w < 0.5)
    {
        center = (emitter.World * vec4(center, 1.0)).xyz;
        velocity = (emitter.World * vec4(velocity, 0.0)).xyz;
    }
    vec3 right = material._LumenCameraRight.xyz;
    vec3 up = material._LumenCameraUp.xyz;
    float height = size;
    uint mode = uint(emitter.Flipbook.w);
    if (mode == 1u)
    {
        right = vec3(1.0, 0.0, 0.0);
        up = vec3(0.0, 0.0, 1.0);
    }
    else if (mode == 2u)
    {
        vec3 normal = lumenDirection(cross(right, up), vec3(0.0, 0.0, 1.0));
        vec3 projectedVelocity = velocity - normal * dot(velocity, normal);
        up = lumenDirection(projectedVelocity, up);
        right = lumenDirection(cross(up, normal), right);
        height = size * emitter.Appearance.y + length(velocity) * emitter.Appearance.x;
    }
    vec3 worldPosition = center + right * (inPosition.x * size) + up * (inPosition.y * height);
    gl_Position = camera.ViewProj * vec4(worldPosition, 1.0);

    uint columns = uint(emitter.Flipbook.x);
    uint rows = uint(emitter.Flipbook.y);
    uint frameCount = columns * rows;
    uint frame = min(uint(fract(normalizedAge * emitter.Flipbook.z) * float(frameCount)), frameCount - 1u);
    vUv = (inUv + vec2(float(frame % columns), float(frame / columns))) / vec2(float(columns), float(rows));
}
