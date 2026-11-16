#version 450

// L1 CPU particles: world-space quads built by CpuParticleSimulator, drawn through MeshBatchRenderer.
// The engine frame UBO supplies the camera matrix; no push constants exist for material shaders.
layout(set = 0, binding = 0) uniform VertexUniforms { mat4 ViewProj; } camera;
layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 1) in vec2 inUv;       // @vertex TexCoord0 required
layout(location = 2) in vec4 inColor;    // @vertex Color required
layout(location = 3) in vec2 inRadialUv; // @vertex TexCoord1 required
layout(location = 0) out vec2 uv;
layout(location = 1) out vec4 color;
layout(location = 2) out vec2 radialUv;

void main()
{
    gl_Position = camera.ViewProj * vec4(inPosition, 1.0);
    uv = inUv;
    radialUv = inRadialUv;
    color = inColor;
}
