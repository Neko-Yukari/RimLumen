#version 450
#include "RimLumenObjectShadow.glsl"

// One quad per silhouette edge (CasterMesh) at the Shadows altitude, so sprite pixels reject it by depth. Its far side
// covers where the edge's shadow can fall; RimLumenObjectShadow.frag.glsl evaluates the exact strength. Sun: the edge
// moved by _CastVect * h. Point light: the edge scaled about the light by H / (H - h), where a point of height h
// projects, when h < H; otherwise until its line lies 1.5 R from the light, past every corner of the pool box (a line
// within 1/64 cell of the light counts as 1/64 away).
layout(set = 0, binding = 0) uniform VertexUniforms { mat4 ViewProj; } camera;
layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 1) in vec4 inEdge;     // @vertex TexCoord0 required (a.xz, b.xz)
layout(location = 2) in vec4 inShape;    // @vertex TexCoord1 required (height, fade, end: 0 a / 1 b, far)
layout(location = 3) in vec4 inRect;     // @vertex TexCoord2 required (occupied cells [min, max + 1))
layout(location = 0) out vec2 vGround;
layout(location = 1) out vec4 vEdge;
layout(location = 2) out vec2 vShape;

void main()
{
    vec2 a = inEdge.xy, b = inEdge.zw;
    float h = inShape.x;
    vec2 ground = inShape.z > 0.5 ? b : a;
    if (material._LumenCasterSun.z > 0.5)
        ground += material._LumenCasterSun.xy * (h * inShape.w);
    else
    {
        vec4 light = material._LumenCasterLight;
        vec2 cell = floor(light.xy);
        // A caster whose occupied cells hold the light's cell casts nothing for it.
        if (all(greaterThanEqual(cell, inRect.xy)) && all(lessThan(cell, inRect.zw)))
        {
            gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
            return;
        }
        if (inShape.w > 0.5)
        {
            float line = abs(lumenCross(b - a, light.xy - a)) / length(b - a);
            float scale = h < light.z ? light.z / (light.z - h) : max(1.0, 1.5 * light.w / max(line, 1.0 / 64.0));
            ground = light.xy + (ground - light.xy) * scale;
        }
    }
    gl_Position = camera.ViewProj * vec4(ground.x, inPosition.y, ground.y, 1.0);
    vGround = ground;
    vEdge = inEdge;
    vShape = inShape.xy;
}
