using System;
using System.Numerics;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Scene;

// RimUnity ParticleSystem.Shapes.cs, upstream 45ca90987f7d3721715b45c021cab986ee14c468; mapped from +Z to +Y.
internal static class ParticleShapeSampler
{
    // Requires: validated spec and nonzero particle-birth PRNG state, separate from scheduled-burst probability.
    // Ensures: node-local position/direction after shape TRS and ordered direction/position mixing; no node/world transform.
    internal static void Sample(EmitterSpec spec, ref uint randomState, out Vector3 position, out Vector3 direction)
    {
        position = Vector3.Zero;
        direction = Vector3.UnitY;
        float angle = Phase(spec.ArcSpread, ref randomState) * spec.ArcDegrees * (MathF.PI / 180f);
        Vector3 radial = new(MathF.Cos(angle), 0f, -MathF.Sin(angle));
        Vector3 extent = spec.ShapeExtents;
        switch (spec.Shape)
        {
            case LumenShapeKind.Sphere:
            case LumenShapeKind.SphereShell:
            case LumenShapeKind.Hemisphere:
            case LumenShapeKind.HemisphereShell:
            {
                float y = 2f * ParticleRandom.Next01(ref randomState) - 1f;
                if (spec.Shape is LumenShapeKind.Hemisphere or LumenShapeKind.HemisphereShell) y = MathF.Abs(y);
                direction = radial * MathF.Sqrt(MathF.Max(0f, 1f - y * y)) + Vector3.UnitY * y;
                float inner = 1f - spec.RadiusThickness;
                float radius = spec.Shape is LumenShapeKind.SphereShell or LumenShapeKind.HemisphereShell
                    ? 1f : MathF.Pow(float.Lerp(inner * inner * inner, 1f, ParticleRandom.Next01(ref randomState)), 1f / 3f);
                position = direction * radius * extent;
                break;
            }
            case LumenShapeKind.CircleXZ:
            case LumenShapeKind.CircleEdgeXZ:
            {
                float radius = spec.Shape == LumenShapeKind.CircleEdgeXZ ? 1f : Radial(spec.RadiusThickness, ref randomState);
                position = radial * radius * extent;
                direction = radial;
                break;
            }
            case LumenShapeKind.Cone:
            case LumenShapeKind.ConeShell:
            case LumenShapeKind.ConeVolume:
            case LumenShapeKind.ConeVolumeShell:
            {
                float radius = spec.Shape is LumenShapeKind.ConeShell or LumenShapeKind.ConeVolumeShell ? 1f : Radial(spec.RadiusThickness, ref randomState);
                float coneAngle = spec.ConeAngleDegrees * (MathF.PI / 180f);
                direction = Normalize(radial * (MathF.Sin(coneAngle) * radius) + Vector3.UnitY * MathF.Cos(coneAngle));
                position = radial * radius * extent;
                if (spec.Shape is LumenShapeKind.ConeVolume or LumenShapeKind.ConeVolumeShell)
                    position += direction * (ParticleRandom.Next01(ref randomState) * spec.ConeLength / MathF.Max(0.001f, direction.Y));
                break;
            }
            case LumenShapeKind.Box:
            case LumenShapeKind.BoxShell:
            case LumenShapeKind.BoxEdge:
            {
                Vector3 sample = new(2f * ParticleRandom.Next01(ref randomState) - 1f,
                    2f * ParticleRandom.Next01(ref randomState) - 1f, 2f * ParticleRandom.Next01(ref randomState) - 1f);
                if (spec.Shape != LumenShapeKind.Box)
                {
                    Vector3 size = Vector3.Abs(extent * spec.ShapeScale);
                    Vector3 weights = spec.Shape == LumenShapeKind.BoxEdge ? size : new Vector3(size.Y * size.Z, size.X * size.Z, size.X * size.Y);
                    float total = weights.X + weights.Y + weights.Z;
                    float pick = ParticleRandom.Next01(ref randomState) * total;
                    int axis = total == 0f || pick < weights.X ? 0 : pick < weights.X + weights.Y ? 1 : 2;
                    for (int i = 0; i < 3; i++)
                    {
                        if (spec.Shape == LumenShapeKind.BoxEdge ? i != axis : i == axis)
                        {
                            float offset = 1f - ParticleRandom.Next01(ref randomState) * spec.BoxThickness[i];
                            sample[i] = ParticleRandom.Next01(ref randomState) < 0.5f ? -offset : offset;
                        }
                    }
                }
                position = sample * extent;
                break;
            }
            case LumenShapeKind.SingleSidedEdge:
                position.X = (Phase(spec.RadiusSpread, ref randomState) * 2f - 1f) * extent.X;
                direction = -Vector3.UnitZ;
                break;
            case LumenShapeKind.Donut:
            {
                float tubeAngle = ParticleRandom.Next01(ref randomState) * (2f * MathF.PI);
                float tubeRadius = spec.DonutRadius * Radial(spec.RadiusThickness, ref randomState);
                direction = radial * MathF.Cos(tubeAngle) + Vector3.UnitY * MathF.Sin(tubeAngle);
                position = radial * extent + direction * tubeRadius;
                break;
            }
            case LumenShapeKind.Rectangle:
                position = new Vector3((ParticleRandom.Next01(ref randomState) * 2f - 1f) * extent.X,
                    0f, (ParticleRandom.Next01(ref randomState) * 2f - 1f) * extent.Z);
                break;
        }

        position = Vector3.Transform(position * spec.ShapeScale, spec.ShapeRotation) + spec.ShapePosition;
        direction = Vector3.Transform(direction, spec.ShapeRotation);
        if (spec.RandomDirectionAmount != 0f)
            direction = Normalize(Vector3.Lerp(direction, Sphere(ref randomState), spec.RandomDirectionAmount));
        Vector3 offsetFromCenter = position - spec.ShapePosition;
        if (spec.SphericalDirectionAmount != 0f && offsetFromCenter.LengthSquared() > 0f)
            direction = Normalize(Vector3.Lerp(direction, Normalize(offsetFromCenter), spec.SphericalDirectionAmount));
        if (spec.RandomPositionAmount != 0f)
            position += Sphere(ref randomState) * (MathF.Pow(ParticleRandom.Next01(ref randomState), 1f / 3f) * spec.RandomPositionAmount);
    }

    private static float Phase(float spread, ref uint randomState)
    {
        float phase = ParticleRandom.Next01(ref randomState);
        return spread > 0f ? MathF.Floor(phase / spread) * spread : phase;
    }

    private static float Radial(float thickness, ref uint randomState)
    {
        float inner = 1f - thickness;
        return MathF.Sqrt(float.Lerp(inner * inner, 1f, ParticleRandom.Next01(ref randomState)));
    }

    private static Vector3 Sphere(ref uint randomState)
    {
        float y = 2f * ParticleRandom.Next01(ref randomState) - 1f;
        float angle = 2f * MathF.PI * ParticleRandom.Next01(ref randomState);
        float radius = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
        return new Vector3(radius * MathF.Cos(angle), y, radius * MathF.Sin(angle));
    }

    private static Vector3 Normalize(Vector3 value)
    {
        float length = value.Length();
        return length > 1e-5f ? value / length : Vector3.Zero;
    }
}

internal static class ParticleRandom
{
    // Requires: nonzero xorshift32 state owned by one emitter stream.
    // Ensures: advances that state once and returns its high 24 bits as a uniform value in [0,1).
    internal static float Next01(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state >> 8) * (1f / 16777216f);
    }
}
