#version 450

layout(set = 0, binding = 0) uniform sampler2D sceneDepth;
layout(push_constant) uniform Push
{
    vec4 rect;
    vec4 color;
    // zNear, zFar, isOrthographic, featherDistance
    vec4 depth;
} pc;

layout(location = 0) out vec4 outColor;

float linearDepth(float raw)
{
    float nearPlane = pc.depth.x;
    float farPlane = pc.depth.y;
    // Waylith is reverse-Z: 0 is far, 1 is near.
    if (pc.depth.z > 0.5)
        return farPlane - raw * (farPlane - nearPlane);
    return nearPlane * farPlane / (nearPlane + raw * (farPlane - nearPlane));
}

void main()
{
    float sceneRaw = texelFetch(sceneDepth, ivec2(gl_FragCoord.xy), 0).r;
    float feather = clamp((linearDepth(sceneRaw) - linearDepth(gl_FragCoord.z)) / pc.depth.w, 0.0, 1.0);
    float alpha = pc.color.a * feather;
    outColor = vec4(pc.color.rgb * alpha, alpha);
}
