using System;
using System.Numerics;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Scene;

internal sealed record ParticleMapGrid(int MapId, int Width, int Height, uint[] Cells);
internal readonly record struct ParticleMapHit(bool Hit, bool Roof, Vector3 Position, Vector3 Normal);

internal static class ParticleCollisionMath
{
    internal const float WallHeight = 2f;

    // Point particles, swept against unit XZ cells extruded from Y=0 to Y=2. No endpoint-only tunnelling.
    internal static ParticleMapHit Trace(ParticleMapGrid grid, Vector3 from, Vector3 to, CollisionSpec spec)
    {
        Vector3 delta = to - from;
        float begin = 0f, end = 1f;
        Vector3 unused = Vector3.Zero;
        if (!Slab(from.X, delta.X, 0, grid.Width, Vector3.UnitX, ref begin, ref end, ref unused) ||
            !Slab(from.Z, delta.Z, 0, grid.Height, Vector3.UnitZ, ref begin, ref end, ref unused)) return default;
        Vector3 start = from + delta * begin;
        int x = Math.Clamp((int)MathF.Floor(start.X), 0, grid.Width - 1);
        int z = Math.Clamp((int)MathF.Floor(start.Z), 0, grid.Height - 1);
        int sx = Math.Sign(delta.X), sz = Math.Sign(delta.Z);
        float tx = sx == 0 ? float.PositiveInfinity : ((sx > 0 ? x + 1 : x) - from.X) / delta.X;
        float tz = sz == 0 ? float.PositiveInfinity : ((sz > 0 ? z + 1 : z) - from.Z) / delta.Z;
        while (x >= 0 && z >= 0 && x < grid.Width && z < grid.Height && begin <= end)
        {
            uint cell = grid.Cells[z * grid.Width + x];
            if (spec.KillUnderRoof && (cell & 2u) != 0)
                return new(true, true, from + delta * begin, Vector3.Zero);
            if (spec.Mode != LumenCollisionMode.None && (cell & 1u) != 0)
            {
                float enter = 0f, exit = 1f;
                Vector3 normal = Vector3.Zero;
                if (Slab(from.X, delta.X, x, x + 1, Vector3.UnitX, ref enter, ref exit, ref normal) &&
                    Slab(from.Y, delta.Y, 0, WallHeight, Vector3.UnitY, ref enter, ref exit, ref normal) &&
                    Slab(from.Z, delta.Z, z, z + 1, Vector3.UnitZ, ref enter, ref exit, ref normal))
                {
                    Vector3 position = from + delta * enter;
                    if (normal == Vector3.Zero)
                    {
                        // Born inside a solid cell: project to its nearest face rather than bounce every frame inside it.
                        float nearest = float.PositiveInfinity;
                        Face(position.X - x, -Vector3.UnitX, ref nearest, ref normal);
                        Face(x + 1 - position.X, Vector3.UnitX, ref nearest, ref normal);
                        Face(position.Y, -Vector3.UnitY, ref nearest, ref normal);
                        Face(WallHeight - position.Y, Vector3.UnitY, ref nearest, ref normal);
                        Face(position.Z - z, -Vector3.UnitZ, ref nearest, ref normal);
                        Face(z + 1 - position.Z, Vector3.UnitZ, ref nearest, ref normal);
                        position += normal * nearest;
                    }
                    return new(true, false, position + normal * 0.0001f, normal);
                }
            }
            if (tx < tz) { begin = tx; tx += 1f / MathF.Abs(delta.X); x += sx; }
            else { begin = tz; tz += 1f / MathF.Abs(delta.Z); z += sz; }
        }
        return default;
    }

    private static void Face(float distance, Vector3 axis, ref float nearest, ref Vector3 normal)
    {
        if (distance < nearest) { nearest = distance; normal = axis; }
    }

    private static bool Slab(float p, float d, float low, float high, Vector3 axis,
        ref float enter, ref float exit, ref Vector3 normal)
    {
        if (d == 0f) return p >= low && p < high;
        float a = (low - p) / d, b = (high - p) / d;
        if (a > b) (a, b) = (b, a);
        if (a >= enter) { enter = a; normal = d > 0f ? -axis : axis; }
        exit = MathF.Min(exit, b);
        return enter <= exit;
    }

    internal static bool Respond(CollisionSpec spec, ParticleMapHit hit, ref Vector3 position, ref Vector3 velocity,
        ref float age, float lifetime)
    {
        if (!hit.Hit) return false;
        position = hit.Position;
        if (hit.Roof) { age = lifetime; return false; }
        if (spec.Mode == LumenCollisionMode.Kill) { age = lifetime; return true; }
        else
        {
            // RimUnity Source/RimUnity/Particles/ParticleSystem.Collision.cs:103-115,
            // commit 45ca90987f7d3721715b45c021cab986ee14c468. Static walls have surfaceVelocity = 0.
            float normalSpeed = Vector3.Dot(velocity, hit.Normal);
            if (normalSpeed < 0f)
            {
                velocity = (velocity - hit.Normal * ((1f + spec.Bounce) * normalSpeed)) * (1f - spec.Dampen);
                age += lifetime * spec.LifetimeLoss;
                return true;
            }
        }
        return false;
    }
}
