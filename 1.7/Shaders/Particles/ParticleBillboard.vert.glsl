#version 450
#include "ParticleMaterial.glsl"

// Instanced billboards: one static quad mesh (4 vertices, 6 indices) drawn once per live slot.
// gl_InstanceIndex selects the particle; the vertex position carries the quad corner in xy.
layout(set = 0, binding = 0) uniform VertexUniforms { mat4 ViewProj; } camera;
layout(location = 0) in vec3 inPosition; // @vertex Position required

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec2 vRadialUv;
layout(location = 3) out float vSoftDistance;

void main()
{
    vec2 quadUv = inPosition.xy;
    vec2 quadCorner = quadUv - vec2(0.5);
    GpuEmitterParams emitter = emitters[uint(material._LumenDraw.x)];
    GpuParticle particle = particles[uint(emitter.Slots.x) + uint(gl_InstanceIndex)];
    vColor = vec4(0.0);
    vUv = quadUv;
    vRadialUv = quadUv;
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
    // Vertical3D display (mirrors CameraBillboardBasis.ProjectVertical3D): the simulation is true 3D, only the
    // drawn position adapts to the camera.
    float verticalMode = material._LumenVertical.x;
    float yScale = material._LumenCameraUp.w > 0.0 ? material._LumenCameraUp.w : 1.0;
    if (verticalMode > 0.5)
    {
        float baseY = material._LumenVertical.y;
        float dy = center.y - baseY;
        if (verticalMode > 1.5)
        {
            center = vec3(center.x, max(baseY, material._LumenVertical.z), center.z + dy);
            velocity = vec3(velocity.x, 0.0, velocity.z + velocity.y);
        }
        else
            center.y = baseY + dy / yScale;
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
        vec3 rUnscaled = vec3(right.x, right.y * yScale, right.z);
        vec3 uUnscaled = vec3(up.x, up.y * yScale, up.z);
        // Vertical3D velocity is already a true 3D direction; the legacy path pre-squashes world Y.
        vec3 vRot = verticalMode > 0.5 ? velocity : vec3(velocity.x, velocity.y * yScale, velocity.z);
        vec2 vCam = vec2(dot(vRot, rUnscaled), dot(vRot, uUnscaled));
        float vCamLenSq = dot(vCam, vCam);
        if (vCamLenSq > 1e-10)
        {
            vec2 dir = vCam * inversesqrt(vCamLenSq);
            vec3 baseRight = right;
            vec3 baseUp = up;
            up = baseRight * dir.x + baseUp * dir.y;
            right = baseRight * dir.y - baseUp * dir.x;
        }
        height = size * emitter.Appearance.y + length(velocity) * emitter.Appearance.x;
    }
    vec3 worldPosition = center + right * (quadCorner.x * size) + up * (quadCorner.y * height);
    gl_Position = camera.ViewProj * vec4(worldPosition, 1.0);

    uint columns = uint(emitter.Flipbook.x);
    uint rows = uint(emitter.Flipbook.y);
    uint frameCount = columns * rows;
    uint frame = min(uint(fract(normalizedAge * emitter.Flipbook.z) * float(frameCount)), frameCount - 1u);
    vUv = (quadUv + vec2(float(frame % columns), float(frame / columns))) / vec2(float(columns), float(rows));
}
