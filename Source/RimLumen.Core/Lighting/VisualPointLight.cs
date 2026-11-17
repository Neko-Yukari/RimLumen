using System;
using System.Numerics;

namespace RimLumen.Core.Lighting;

// One frame's light pool: centre (X, Z) is the node's world position projected to the ground, Radius is
// PointLightSpec.GlowRadius in cells (> 0), Light is ColorRgb * effective intensity (every component >= 0, max > 0).
internal readonly record struct VisualPointLight(float X, float Z, float Radius, Vector3 Light)
{
    // Radial falloff w = (1 - (d/R)^2)^2 matching RimLumenLightPool.frag.glsl: 1 at d = 0, strictly
    // decreasing on [0, R], and 0 for d >= R or R <= 0 without per-cell quantization.
    internal static float Falloff(float distance, float radius)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(distance);
        if (!(radius > 0f) || !(distance < radius)) return 0f;
        float ratio = distance / radius;
        float q = 1f - ratio * ratio;
        return q * q;
    }

    internal Vector3 EvaluateAt(float worldX, float worldZ)
    {
        float dx = worldX - X;
        float dz = worldZ - Z;
        return Light * Falloff(MathF.Sqrt(dx * dx + dz * dz), Radius);
    }
}
