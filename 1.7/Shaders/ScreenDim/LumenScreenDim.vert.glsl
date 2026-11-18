#version 450

// Full-screen dim quad: MeshPool.plane10 (X and Z in [-0.5, 0.5]) mapped straight to clip space, ignoring camera and
// model matrices.
layout(location = 0) in vec3 inPosition; // @vertex Position required

void main()
{
    gl_Position = vec4(inPosition.xz * 2.0, 0.0, 1.0);
}
