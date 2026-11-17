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

    // The scene multiplier term at one ground point: overlapping pools add linearly (RimLumenLightPoolAccumulate),
    // then RimLumenLightPool.frag.glsl compresses the sum once, so the scene becomes scene * (1 + result).
    internal static Vector3 AccumulateAndCompress(ReadOnlySpan<VisualPointLight> lights, float worldX, float worldZ)
    {
        Vector3 sum = Vector3.Zero;
        foreach (VisualPointLight light in lights) sum += light.EvaluateAt(worldX, worldZ);
        return CompressHighlights(sum);
    }

    // compressHighlights in RimLumenLightPool.frag.glsl: identity up to a peak of 1; above it the peak holds at 1 and
    // the excess whitens along the colour's own hue instead of clipping each channel.
    internal static Vector3 CompressHighlights(Vector3 sum)
    {
        float peak = MathF.Max(sum.X, MathF.Max(sum.Y, sum.Z));
        if (peak <= 1f) return sum;
        float excess = peak - 1f;
        return Vector3.Lerp(sum / peak, Vector3.One, excess / (excess + 4f));
    }
}
