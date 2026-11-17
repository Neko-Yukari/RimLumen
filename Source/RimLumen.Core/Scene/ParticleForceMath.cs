using System;
using System.Numerics;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Scene;

// CPU twin of ParticleSim.comp.glsl acceleration/potentialGradient.
internal static class ParticleForceMath
{
    internal static Vector3 Acceleration(EmitterSpec spec, in Matrix4x4 world, Vector3 position, out float drag, float nodeTime = 0f)
    {
        Vector3 total = Vector3.Zero;
        drag = 0f;
        foreach (ForceFieldSpec field in spec.ForceFields)
        {
            switch (field.Kind)
            {
                case ForceFieldKind.GravityDrag:
                    total += field.CenterOrAxis * field.Strength;
                    drag += MathF.Max(0f, field.Parameters.X);
                    break;
                case ForceFieldKind.Vortex:
                    Vector3 axis = Direction(field.CenterOrAxis, Vector3.UnitY);
                    Vector3 relative = position - (spec.WorldSpaceSimulation ? world.Translation : Vector3.Zero);
                    Vector3 radial = relative - Vector3.Dot(relative, axis) * axis;
                    float weight = field.Radius > 0f ? MathF.Max(0f, 1f - radial.Length() / field.Radius) : 1f;
                    Vector3 radialDirection = Direction(radial, Vector3.Zero);
                    total += (Vector3.Cross(axis, radialDirection) * field.Strength - radialDirection * field.Parameters.X
                        + axis * field.Parameters.Y) * weight;
                    break;
                case ForceFieldKind.PointAttractor:
                    Vector3 center = spec.WorldSpaceSimulation ? Vector3.Transform(field.CenterOrAxis, world) : field.CenterOrAxis;
                    Vector3 delta = center - position;
                    float attraction = field.Radius > 0f ? MathF.Max(0f, 1f - delta.Length() / field.Radius) : 1f;
                    total += Direction(delta, Vector3.Zero) * (field.Strength * attraction);
                    break;
                case ForceFieldKind.RingWave:
                    Vector3 waveCenter = spec.WorldSpaceSimulation ? Vector3.Transform(field.CenterOrAxis, world) : field.CenterOrAxis;
                    total += RingWave(position - waveCenter, field.Parameters.X * nodeTime, field.Parameters.Y, field.Radius, field.Strength);
                    break;
                case ForceFieldKind.CurlTurbulence:
                    Vector3 q = (position - field.Parameters) * field.Frequency;
                    Vector3 dx = PotentialGradient(q, 0x68bc21ebu);
                    Vector3 dy = PotentialGradient(q, 0x02e5be93u);
                    Vector3 dz = PotentialGradient(q, 0x967a889bu);
                    Vector3 curl = new(dz.Y - dy.Z, dx.Z - dz.X, dy.X - dx.Y);
                    total += curl * (field.Strength * field.Frequency);
                    break;
            }
        }
        return total;
    }

    internal static Vector3 RingWave(Vector3 delta, float waveRadius, float halfThickness, float endRadius, float strength)
    {
        if (waveRadius > endRadius) return Vector3.Zero;
        float weight = MathF.Max(0f, 1f - MathF.Abs(delta.Length() - waveRadius) / halfThickness);
        return Direction(delta, Vector3.Zero) * (strength * weight);
    }

    private static Vector3 Direction(Vector3 value, Vector3 fallback)
        => value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : fallback;

    private static Vector3 PotentialGradient(Vector3 point, uint channel)
    {
        Vector3 cell = new(MathF.Floor(point.X), MathF.Floor(point.Y), MathF.Floor(point.Z));
        Vector3 t = point - cell;
        Vector3 interpolation = t * t * t * (t * (t * 6f - new Vector3(15f)) + new Vector3(10f));
        Vector3 derivative = 30f * t * t * (t - Vector3.One) * (t - Vector3.One);
        Vector3 gradient = Vector3.Zero;
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 bit = new(corner & 1, (corner >> 1) & 1, (corner >> 2) & 1);
            Vector3 sign = bit * 2f - Vector3.One;
            Vector3 weight = (Vector3.One - interpolation) * (Vector3.One - bit) + interpolation * bit;
            Vector3 slope = sign * derivative;
            gradient += LatticeValue(cell + bit, channel)
                * new Vector3(slope.X * weight.Y * weight.Z, weight.X * slope.Y * weight.Z, weight.X * weight.Y * slope.Z);
        }
        return gradient;
    }

    private static float LatticeValue(Vector3 cell, uint channel)
    {
        uint state = unchecked((uint)(int)cell.X * 0x8da6b343u ^ (uint)(int)cell.Y * 0xd8163841u
            ^ (uint)(int)cell.Z * 0xcb1ab31fu ^ channel);
        state = unchecked(state + 0x9e3779b9u);
        state = unchecked((state ^ (state >> 16)) * 0x85ebca6bu);
        state = unchecked((state ^ (state >> 13)) * 0xc2b2ae35u);
        state ^= state >> 16;
        return 2f * ((state >> 8) * (1f / 16777216f)) - 1f;
    }
}
