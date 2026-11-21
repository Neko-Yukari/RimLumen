#version 450
#include "ParticleMaterial.glsl"
#include "ParticleProjection.glsl"

// Instanced billboards: one static quad mesh (4 vertices, 6 indices) drawn once per live slot.
// gl_InstanceIndex selects the particle; the vertex position carries the quad corner in xy.
layout(set = 0, binding = 0) uniform VertexUniforms { mat4 ViewProj; } camera;
layout(location = 0) in vec3 inPosition; // @vertex Position required

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec2 vRadialUv;
// x: soft-particle fade distance; y: scalar radiance gain (hdrIntensity * intensity), applied after colour conversion.
layout(location = 3) out vec2 vSoftAndGain;
// LumenDepthMask (mirrors CameraBillboardBasis.DepthMaskAttribute): x occlusion lift (0 = off), y world Z north of the reference.
layout(location = 4) out vec2 vDepthMask;

void main()
{
    vec2 quadUv = inPosition.xy;
    vec2 quadCorner = quadUv - vec2(0.5);
    GpuEmitterParams emitter = emitters[uint(material._LumenDraw.x)];
    GpuParticle particle = particles[uint(emitter.Slots.x) + uint(gl_InstanceIndex)];
    vColor = vec4(0.0);
    vUv = quadUv;
    vRadialUv = quadUv;
    vSoftAndGain = vec2(emitter.State.y, emitter.Appearance.z * emitter.State.w);
    vDepthMask = vec2(0.0);
    if (particle.ActiveEmitter == 0u || particle.PositionAndAge.w >= particle.VelocityAndLifetime.w)
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
    float size = particle.Size * mix(a.Size.x, b.Size.x, blend);
    vColor = particle.StartColor * mix(a.Color, b.Color, blend);
    // Appearance.w = cbrt(abs(det(World3x3))); world-space size is baked at birth, local-space at draw.
    if (emitter.SizeCone.w < 0.5) size *= emitter.Appearance.w;

    vec3 center = particle.PositionAndAge.xyz;
    vec3 velocity = particle.VelocityAndLifetime.xyz;
    if (emitter.SizeCone.w < 0.5)
    {
        center = (emitter.World * vec4(center, 1.0)).xyz;
        velocity = (emitter.World * vec4(velocity, 0.0)).xyz;
    }
    // Display rule (mirrors CameraBillboardBasis.ProjectToDisplay): the simulation is true 3D, only the drawn position
    // adapts to the camera. Straight-down 2D draws every plane on the mote layer (_LumenVertical.z).
    bool vertical3D = material._LumenVertical.x > 0.5;
    bool topDown2D = material._LumenVertical.y > 0.5;
    float yScale = material._LumenCameraUp.w > 0.0 ? material._LumenCameraUp.w : 1.0;
    vec3 groundUp = groundUpOf(material._LumenCameraUp.xyz);
    if (topDown2D)
    {
        if (vertical3D)
        {
            // The node's ground position; a world-space particle keeps its birth anchor when the node moves.
            vec2 anchor = emitter.SizeCone.w > 0.5 ? lumenUnpackAnchor(particle.SpawnAnchorXZ) : lumenNodeGroundXZ(emitter.World);
            vec3 offset = projectTopDownPitch(center - vec3(anchor.x, LUMEN_GROUND_Y, anchor.y), groundUp);
            center = vec3(anchor.x + offset.x, material._LumenVertical.z, anchor.y + offset.z);
            velocity = projectTopDownPitch(velocity, groundUp);
        }
        else
            center.y = material._LumenVertical.z;
    }
    else if (vertical3D)
        center.y /= yScale;
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
        vec3 vRot = vertical3D ? velocity : vec3(velocity.x, velocity.y * yScale, velocity.z);
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
    else if (mode == 4u && !vertical3D)
    {
        vec3 rawRight = vec3(right.x, right.y * yScale, right.z);
        vec3 rawUp = vec3(up.x, up.y * yScale, up.z);
        vec3 forward = cross(rawRight, rawUp);
        vec3 horizontal = cross(vec3(0.0, 1.0, 0.0), forward);
        float horizontalLength = length(horizontal);
        right = horizontalLength > 1e-6 ? horizontal / horizontalLength : normalize(rawRight);
        up = vec3(0.0, 1.0, 0.0);
    }
    else if (mode == 5u)
    {
        vec4 q = emitter.WorldRotation;
        vec3 x = vec3(1.0, 0.0, 0.0);
        vec3 y = vec3(0.0, 1.0, 0.0);
        right = x + 2.0 * cross(q.xyz, cross(q.xyz, x) + q.w * x);
        up = y + 2.0 * cross(q.xyz, cross(q.xyz, y) + q.w * y);
        if (vertical3D && topDown2D)
        {
            right = projectTopDownPitch(right, groundUp);
            up = projectTopDownPitch(up, groundUp);
        }
        else if (vertical3D)
        {
            right.y /= yScale;
            up.y /= yScale;
        }
    }
    if (topDown2D)
    {
        // Every corner shares the draw layer; the orthographic straight-down view keeps the XZ footprint.
        right.y = 0.0;
        up.y = 0.0;
    }
    vec3 worldPosition = center + right * (quadCorner.x * size) + up * (quadCorner.y * height);
    gl_Position = camera.ViewProj * vec4(worldPosition, 1.0);
    // ShapePosition.w flags the node's depth mask; ShapeScale.w is its reference Z.
    if (topDown2D && emitter.ShapePosition.w > 0.5)
        vDepthMask = vec2(material._LumenVertical.w, worldPosition.z - emitter.ShapeScale.w);

    uint columns = uint(emitter.Flipbook.x);
    uint rows = uint(emitter.Flipbook.y);
    uint frameCount = columns * rows;
    uint frame = min(uint(fract(normalizedAge * emitter.Flipbook.z) * float(frameCount)), frameCount - 1u);
    // Vanilla MeshMakerPlanes.NewPlaneMesh has uv (0,1) at +Z (v=1 is image top);
    // RimUnity ParticleSystemRenderer.cs TextureSheet uses 1f - (row+1)/numTilesY.
    vec2 tileOrigin = vec2(float(frame % columns) / float(columns), 1.0 - float(frame / columns + 1u) / float(rows));
    vUv = tileOrigin + quadUv / vec2(float(columns), float(rows));
}
