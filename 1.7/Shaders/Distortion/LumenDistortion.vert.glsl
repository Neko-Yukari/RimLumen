#version 450

// Distortion quad: MeshPool.plane10 (X and Z in [-0.5, 0.5]) scaled to the node's 2R x 2R footprint by the model
// matrix. vLocal is the quad position in units of R, so |vLocal| = 1 on the affected area's outer radius.
layout(location = 0) in vec3 inPosition; // @vertex Position required

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

layout(location = 0) out vec2 vLocal;

void main()
{
    gl_Position = uVertex.ViewProj * (push.modelMatrix * vec4(inPosition, 1.0));
    vLocal = inPosition.xz * 2.0;
}
