#version 450
#include "RimLumenObjectShadow.glsl"

// One silhouette edge's shadow strength at a ground pixel, line for line ObjectShadow.PointEdge / SunEdge, written into
// the light's mask channel with blend Max: one light's overlapping shadows darken once.
layout(location = 0) in vec2 vGround;
layout(location = 1) in vec4 vEdge;
layout(location = 2) in vec2 vShape; // height, fade
layout(location = 0) out vec4 outColor;

float lumenPointEdge(vec4 light, vec2 a, vec2 b, float h, bool fade, vec2 p)
{
    if (any(greaterThan(abs(p - light.xy), vec2(light.w)))) return 0.0;
    vec2 d = p - light.xy, e = b - a, w = a - light.xy;
    float den = lumenCross(d, e);
    if (den == 0.0) return 0.0;
    float lambda = lumenCross(w, e) / den, s = lumenCross(w, d) / den;
    if (s < 0.0 || s > 1.0 || lambda <= 0.0 || lambda > 1.0) return 0.0;
    if (h >= light.z)
    {
        float rim = light.w / length(d);
        return rim <= 1.0 ? 0.0 : (rim - 1.0) / (rim - lambda);
    }
    float t = light.z * (1.0 - lambda);
    if (t > h) return 0.0;
    return fade ? 1.0 - t / h : 1.0;
}

float lumenSunEdge(vec2 cast, vec2 a, vec2 b, float h, bool fade, vec2 p)
{
    vec2 e = b - a, w = p - a;
    float den = lumenCross(cast, e);
    if (den == 0.0) return 0.0;
    float t = lumenCross(w, e) / den, s = -lumenCross(w, cast) / den;
    if (s < 0.0 || s > 1.0 || t < 0.0 || t > h) return 0.0;
    return fade ? 1.0 - t / h : 1.0;
}

void main()
{
    bool fade = vShape.y > 0.5;
    float strength = material._LumenCasterSun.z > 0.5
        ? lumenSunEdge(material._LumenCasterSun.xy, vEdge.xy, vEdge.zw, vShape.x, fade, vGround)
        : lumenPointEdge(material._LumenCasterLight, vEdge.xy, vEdge.zw, vShape.x, fade, vGround);
    if (strength <= 0.0) discard;
    outColor = material._LumenCasterChannel * strength;
}
