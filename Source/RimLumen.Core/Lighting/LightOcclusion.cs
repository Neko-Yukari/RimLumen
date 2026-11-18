using System;
using System.Numerics;
using RimLumen.Core.Scene;

namespace RimLumen.Core.Lighting;

// Reference of lightVisibility in RimLumenLightPoolAccumulate.frag.glsl, line for line. Walls (bit 0 of the particle
// collision grid) are infinitely high, so this is 2D line of sight on the cell grid. Roofs never block.
internal static class LightOcclusion
{
    // Widest distance (cells, across the segment, halfway along it) at which a wall corner still shades: it sets how
    // wide the soft edge beside a wall corner is.
    internal const float PenumbraReach = 0.25f;

    // Ensures: 0 when the segment target -> light passes through a wall cell other than the target's and the light's
    //          own cell, which therefore never block; otherwise smoothstep of the clearance of the corners of the other
    //          wall cells, 1 once every corner is at least the reach away, which is 4 * PenumbraReach * s * (1 - s) at
    //          fraction s of the way to the light. Cells outside the map are open. Continuous in target and light
    //          except across the edge of a wall cell the target enters, whose side facing the light is lit.
    internal static float Visibility(ParticleMapGrid walls, float lightX, float lightZ, float targetX, float targetZ)
    {
        var light = new Vector2(lightX, lightZ);
        var target = new Vector2(targetX, targetZ);
        if (target == light) return 1f;
        (int X, int Y) lightCell = Floor(light);
        (int X, int Y) targetCell = Floor(target);
        bool swapped = MathF.Abs(light.Y - target.Y) > MathF.Abs(light.X - target.X);
        Vector2 a = swapped ? Swap(target) : target;
        Vector2 b = swapped ? Swap(light) : light;
        (int X, int Y) aCell = swapped ? (targetCell.Y, targetCell.X) : targetCell;
        (int X, int Y) bCell = swapped ? (lightCell.Y, lightCell.X) : lightCell;
        Vector2 d = b - a;
        float len = d.Length();
        Vector2 n = d / len;
        float slope = d.Y / d.X;
        float halo = PenumbraReach * MathF.Sqrt(1f + slope * slope);
        float xMin = MathF.Min(a.X, b.X);
        float xMax = MathF.Max(a.X, b.X);
        float clearance = 1f;
        for (int i = (int)MathF.Floor(xMin - PenumbraReach); i <= (int)MathF.Floor(xMax + PenumbraReach); i++)
        {
            float x0 = Math.Clamp(i, xMin, xMax);
            float x1 = Math.Clamp(i + 1, xMin, xMax);
            float z0 = a.Y + (x0 - a.X) * slope;
            float z1 = a.Y + (x1 - a.X) * slope;
            float zLow = MathF.Min(z0, z1);
            float zHigh = MathF.Max(z0, z1);
            for (int j = (int)MathF.Floor(zLow - halo); j <= (int)MathF.Floor(zHigh + halo); j++)
            {
                (int X, int Y) cell = (i, j);
                if (cell == aCell || cell == bCell || !IsWall(walls, swapped ? (j, i) : cell)) continue;
                if (x0 < x1 && zLow < j + 1 && zHigh > j) return 0f;
                for (int k = 0; k < 4; k++)
                {
                    Vector2 v = new Vector2(i + (k & 1), j + (k >> 1)) - a;
                    float s = Vector2.Dot(v, n) / len;
                    if (s <= 0f || s >= 1f) continue;
                    clearance = MathF.Min(clearance, MathF.Abs(v.X * n.Y - v.Y * n.X) / (4f * PenumbraReach * s * (1f - s)));
                }
            }
        }
        return clearance * clearance * (3f - 2f * clearance);
    }

    private static bool IsWall(ParticleMapGrid walls, (int X, int Y) cell) =>
        cell.X >= 0 && cell.Y >= 0 && cell.X < walls.Width && cell.Y < walls.Height
        && (walls.Cells[cell.Y * walls.Width + cell.X] & 1u) != 0;

    private static (int X, int Y) Floor(Vector2 p) => ((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y));

    private static Vector2 Swap(Vector2 p) => new(p.Y, p.X);
}
