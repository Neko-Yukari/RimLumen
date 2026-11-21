#ifndef LUMEN_PARTICLE_PROJECTION
#define LUMEN_PARTICLE_PROJECTION

// Mirrors CameraBillboardBasis.GroundUp / ProjectTopDownPitch: Vertical3D under the straight-down 2D camera is a
// true orthographic view pitched 60 degrees (sin 0.8660254, cos 0.5) about the ground-plane screen-up.
vec3 groundUpOf(vec3 up)
{
    float lengthSq = up.x * up.x + up.z * up.z;
    return lengthSq < 1e-12 ? vec3(0.0, 0.0, 1.0) : vec3(up.x, 0.0, up.z) * inversesqrt(lengthSq);
}

vec3 projectTopDownPitch(vec3 value, vec3 groundUp)
{
    float along = value.x * groundUp.x + value.z * groundUp.z;
    return vec3(value.x, 0.0, value.z) + (along * (0.8660254 - 1.0) + value.y * 0.5) * groundUp;
}

#endif
