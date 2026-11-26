#version 450
#include "../../../../1.7/Shaders/Particles/ParticleTypes.glsl"
#include "LumenForceArrowMaterial.glsl"
#include "../../../../1.7/Shaders/Particles/ParticleProjection.glsl"

// One instance per arrow. The mesh carries x: 0 tail, 1 head base, 2 tip; y: lateral side -1..1; z: 1 on the head.
layout(set = 0, binding = 0) uniform VertexUniforms { mat4 ViewProj; } camera;
layout(location = 0) in vec3 inPosition; // @vertex Position required

layout(location = 0) out vec4 vColor;

// The billboard display rule of ParticleBillboard.vert.glsl, applied to an arrow end point. It is affine in the
// point, so tail and tip map consistently.
vec3 displayPoint(vec3 point, vec2 anchor, bool vertical3D, bool topDown2D, float yScale, vec3 groundUp)
{
    if (topDown2D)
    {
        if (!vertical3D) return vec3(point.x, material._LumenArrowDraw.w, point.z);
        vec3 offset = projectTopDownPitch(point - vec3(anchor.x, LUMEN_GROUND_Y, anchor.y), groundUp);
        return vec3(anchor.x + offset.x, material._LumenArrowDraw.w, anchor.y + offset.z);
    }
    if (vertical3D) point.y /= yScale;
    return point;
}

void main()
{
    GpuForceArrow arrow = arrows[uint(material._LumenArrowDraw.x) + uint(gl_InstanceIndex)];
    vColor = vec4(0.0);
    gl_Position = vec4(0.0, 0.0, 0.0, 1.0);
    if (arrow.Start.w < 0.5) return;
    bool vertical3D = material._LumenArrowDraw.y > 0.5;
    bool topDown2D = material._LumenArrowDraw.z > 0.5;
    float yScale = material._LumenCameraUp.w > 0.0 ? material._LumenCameraUp.w : 1.0;
    vec3 groundUp = groundUpOf(material._LumenCameraUp.xyz);
    vec3 tail = displayPoint(arrow.Start.xyz, arrow.Anchor.xy, vertical3D, topDown2D, yScale, groundUp);
    vec3 tip = displayPoint(arrow.Start.xyz + arrow.Vector.xyz, arrow.Anchor.xy, vertical3D, topDown2D, yScale, groundUp);
    vec3 right = material._LumenCameraRight.xyz;
    vec3 up = material._LumenCameraUp.xyz;
    if (topDown2D)
    {
        right.y = 0.0;
        up.y = 0.0;
    }
    vec3 along = tip - tail;
    vec2 screen = vec2(dot(along, right), dot(along, up));
    float screenLength = length(screen);
    // Pointing straight at the camera: nothing readable to draw.
    if (screenLength < 1e-4) return;
    vec2 direction = screen / screenLength;
    vec3 side = right * -direction.y + up * direction.x;
    float headLength = min(0.35 * screenLength, 0.22);
    float t = inPosition.x < 0.5 ? 0.0 : inPosition.x < 1.5 ? 1.0 - headLength / screenLength : 1.0;
    float halfWidth = inPosition.z > 0.5 ? 0.11 : 0.045;
    vec3 worldPosition = tail + along * t + side * (inPosition.y * halfWidth);
    gl_Position = camera.ViewProj * vec4(worldPosition, 1.0);
    // Premultiplied over the HDR layer (blend One/OneMinusSrcAlpha).
    const float alpha = 1.0;
    vColor = vec4(LUMEN_ARROW_COLORS[uint(arrow.Start.w) - 1u] * alpha, alpha);
}
