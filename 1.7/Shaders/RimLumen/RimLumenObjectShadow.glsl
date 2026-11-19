// Object-shadow caster material (LightPoolRenderer), shared by RimLumenObjectShadow.{vert,frag}.glsl. Waylith derives
// the set-2 layout from the fragment source; both stages read the block.
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _LumenCasterLight;   // xy: light ground position (X, Z); z: light height H (>= 0); w: pool radius R
    vec4 _LumenCasterSun;     // xy: vanilla _CastVect.xz; z: 1 for the takeover sun, 0 for a point light
    vec4 _LumenCasterChannel; // ShadowSlot.Selector of the light's mask channel
} material;

float lumenCross(vec2 a, vec2 b) { return a.x * b.y - a.y * b.x; }
