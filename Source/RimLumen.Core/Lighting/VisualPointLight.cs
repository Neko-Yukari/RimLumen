using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Lighting.Casters;
using RimLumen.Core.Lighting.Occlusion;

namespace RimLumen.Core.Lighting;

// Effect pools come from RimLumen point lights; Vanilla pools are GlowGrid.lights read under takeover (design 3.5).
internal enum VisualPointLightKind : byte
{
    Effect,
    Vanilla,
}

// One frame's light pool: (X, Y, Z) is the node's world position, Y its height above the ground in cells (>= 0); Radius
// is PointLightSpec.GlowRadius in cells (> 0), Light is ColorRgb * effective intensity (every component >= 0, max > 0).
// Vanilla pools: centre is the glower's cell centre at VanillaHeight, Radius its glowRadius, Light glowColor.rgb / 255
// (design 3.5).
internal readonly record struct VisualPointLight(float X, float Y, float Z, float Radius, Vector3 Light, VisualPointLightKind Kind)
{
    // CompGlower has no height; one cell, the top of a vanilla wall (Wall staticSunShadowHeight 1.0).
    internal const float VanillaHeight = 1f;

    // Vanilla glow attenuation (design 2; Verse.Glow.ComputeGlowGridsJob.cs:171-189) on straight-line distance:
    // d' = 1 + distance; lerp(1 - d'/R, 1/d'^2, 0.4) while d' <= R, else 0. Requires: distance >= 0, radius > 0.
    internal static float VanillaFalloff(float distance, float radius)
    {
        float d = 1f + distance;
        if (d > radius) return 0f;
        return 1f - d / radius + (1f / (d * d) - (1f - d / radius)) * 0.4f;
    }

    // Per-light hue-preserving projection of a vanilla pool (ProjectToColor32Fast): rgb / max(1, max component).
    internal static Vector3 ProjectVanilla(Vector3 light) => light / MathF.Max(1f, MathF.Max(light.X, MathF.Max(light.Y, light.Z)));

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

    // Ensures: Effect: Light * Falloff * visibility. Vanilla: ProjectVanilla(Light * VanillaFalloff) * visibility.
    //          visibility = cover * min(solid, 1 - object shadow m): solid and the casters' mask are opaque blockers and
    //          combine by the darker term, cover alone multiplies. Kind selects only the falloff.
    internal Vector3 EvaluateAt(float worldX, float worldZ, OcclusionFrame walls, IReadOnlyList<Caster> casters)
    {
        float dx = worldX - X;
        float dz = worldZ - Z;
        float distance = MathF.Sqrt(dx * dx + dz * dz);
        Vector3 light = Kind == VisualPointLightKind.Effect ? Light * Falloff(distance, Radius) : ProjectVanilla(Light * VanillaFalloff(distance, Radius));
        var at = new Vector2(X, Z);
        var target = new Vector2(worldX, worldZ);
        float solid = LightTransport.Visibility(walls, at, target, out float cover);
        return light * (MathF.Min(solid, 1f - ObjectShadow.PointMask(new Vector4(at, Y, Radius), casters, target)) * cover);
    }

    // The scene multiplier term at one ground point: overlapping pools add linearly (RimLumenLightPoolAccumulate),
    // then RimLumenLightPool.frag.glsl compresses the sum once, so the scene becomes scene * (1 + result).
    internal static Vector3 AccumulateAndCompress(ReadOnlySpan<VisualPointLight> lights, float worldX, float worldZ,
        OcclusionFrame walls, IReadOnlyList<Caster> casters)
    {
        Vector3 sum = Vector3.Zero;
        foreach (VisualPointLight light in lights) sum += light.EvaluateAt(worldX, worldZ, walls, casters);
        return CompressHighlights(sum);
    }

    // lumenShadowOverPool in RimLumenLightPoolTerm.glsl: a sun shadow multiplies the scene by c, but the composite
    // already made a pool pixel B * (1 + L), so it multiplies by (c + L) / (1 + L) instead and the pixel becomes
    // B * (c + L): the shadow removes sunlight, not the pool. Alpha is unchanged; L = 0 returns the shadow as is.
    internal static Vector4 ShadowOverPool(Vector4 shadow, Vector3 pool)
    {
        if (pool == Vector3.Zero) return shadow;
        Vector3 c = new(shadow.X, shadow.Y, shadow.Z);
        return new Vector4((c + pool) / (Vector3.One + pool), shadow.W);
    }

    // compressHighlights in RimLumenLightPoolTerm.glsl: identity up to a peak of 1; above it the peak holds at 1 and
    // the excess whitens along the colour's own hue instead of clipping each channel.
    internal static Vector3 CompressHighlights(Vector3 sum)
    {
        float peak = MathF.Max(sum.X, MathF.Max(sum.Y, sum.Z));
        if (peak <= 1f) return sum;
        float excess = peak - 1f;
        return Vector3.Lerp(sum / peak, Vector3.One, excess / (excess + 4f));
    }
}
