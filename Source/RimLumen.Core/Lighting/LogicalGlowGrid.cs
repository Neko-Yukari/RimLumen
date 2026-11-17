using System;
using System.Collections.Generic;
using UnityEngine;

namespace RimLumen.Core.Lighting;

// One lit logical light: a full-strength white ground light at map cell (X, Z).
internal readonly record struct LogicalLight
{
    // Requires: glowRadius is finite and in (0, GlowGrid.MaxLightRadius]; anything else throws.
    internal LogicalLight(int x, int z, float glowRadius)
    {
        if (!IsValidRadius(glowRadius))
            throw new ArgumentOutOfRangeException(nameof(glowRadius), glowRadius, "Logical light glowRadius must be finite and in (0, 40].");
        X = x;
        Z = z;
        GlowRadius = glowRadius;
    }

    public int X { get; }
    public int Z { get; }
    public float GlowRadius { get; }

    // Vanilla GlowGrid.MaxLightRadius; CompProperties_Glower.ConfigErrors rejects larger radii.
    internal const float MaxRadius = 40f;

    internal static bool IsValidRadius(float glowRadius) => glowRadius > 0f && glowRadius <= MaxRadius;
}

// Host-free ground-glow field of one map's logical lights, in GlowGrid.GroundGlowAt units ([0, 0.5]).
// Cell index is z * width + x (CellIndices.CellToIndex). Main thread only: Glow is replaced, never
// mutated after publication, so a reader that took the reference keeps a consistent field.
internal sealed class LogicalGlowGrid
{
    private static readonly (int Dx, int Dz)[] Directions =
    {
        (0, -1), (1, 0), (0, 1), (-1, 0),
        (1, -1), (1, 1), (-1, 1), (-1, -1),
    };

    private readonly int width;
    private readonly int height;
    private readonly bool[] blockers;
    private readonly Dictionary<int, LogicalLight> lights = new();
    private bool dirty;

    internal LogicalGlowGrid(int width, int height)
    {
        this.width = width;
        this.height = height;
        blockers = new bool[width * height];
    }

    // Null while no logical light is lit on the map.
    internal float[]? Glow { get; private set; }

    internal void Set(int id, LogicalLight light)
    {
        if (lights.TryGetValue(id, out LogicalLight current) && current == light) return;
        lights[id] = light;
        dirty = true;
    }

    internal void Remove(int id)
    {
        if (lights.Remove(id)) dirty = true;
    }

    // Mirrors GlowGrid.LightBlockerAdded/Removed; only a blocker inside a lit light's reach dirties the field.
    internal void SetBlocker(int index, bool blocked)
    {
        if (blockers[index] == blocked) return;
        blockers[index] = blocked;
        if (dirty) return;
        int x = index % width;
        int z = index / width;
        foreach (LogicalLight light in lights.Values)
        {
            int reach = Mathf.CeilToInt(light.GlowRadius);
            if (Math.Abs(x - light.X) <= reach && Math.Abs(z - light.Z) <= reach)
            {
                dirty = true;
                return;
            }
        }
    }

    // Ensures: when dirty, publishes a freshly flooded field (null with no lights) and appends to changed
    //          every cell index whose value differs from the previous field; otherwise does nothing.
    internal void Rebuild(List<int> changed)
    {
        if (!dirty) return;
        dirty = false;
        float[]? previous = Glow;
        float[]? next = null;
        if (lights.Count > 0)
        {
            next = new float[blockers.Length];
            foreach (LogicalLight light in lights.Values) Flood(light, next);
        }

        Glow = next;
        for (int i = 0; i < blockers.Length; i++)
        {
            if ((previous?[i] ?? 0f) != (next?[i] ?? 0f)) changed.Add(i);
        }
    }

    // Vanilla GlowGrid.ComputeGlowGridsJob.Flood + SetGlowFromDist for a 255-white glower without overlight,
    // projected through GroundGlowAt and combined by Max across logical lights. Dijkstra over 8 neighbours in
    // 1/100 cells: the origin starts at 100, cardinal +100, diagonal +141, offers above RoundToInt(r * 100)
    // are dropped, blockers are never entered, and a diagonal is closed when both adjacent cardinals block.
    // Vanilla keeps cardinal blocker bits from an earlier pop when it skips a finalized neighbour; that
    // neighbour already offered the diagonal cell a shorter cardinal path, so evaluating the bits fresh here
    // yields the same distances.
    private void Flood(LogicalLight light, float[] glow)
    {
        int reach = Mathf.CeilToInt(light.GlowRadius);
        int side = reach * 2 + 1;
        int maxDist = Mathf.RoundToInt(light.GlowRadius * 100f);
        var dist = new int[side * side];
        var finalized = new bool[side * side];
        Array.Fill(dist, int.MaxValue);
        int origin = reach * side + reach;
        dist[origin] = 100;
        var queue = new PriorityQueue<int, int>();
        queue.Enqueue(origin, 100);
        Span<bool> cardinalBlocked = stackalloc bool[4];
        while (queue.TryDequeue(out int local, out _))
        {
            if (finalized[local]) continue;
            finalized[local] = true;
            int dx = local % side - reach;
            int dz = local / side - reach;
            int x = light.X + dx;
            int z = light.Z + dz;
            int cell = z * width + x;
            float glowAt = GroundGlowFromDist(dist[local] / 100f, light.GlowRadius);
            if (glowAt > glow[cell]) glow[cell] = glowAt;

            for (int i = 0; i < Directions.Length; i++)
            {
                int nx = x + Directions[i].Dx;
                int nz = z + Directions[i].Dz;
                bool inMap = nx >= 0 && nz >= 0 && nx < width && nz < height;
                bool blocked = inMap && blockers[nz * width + nx];
                if (i < 4) cardinalBlocked[i] = blocked;
                if (!inMap || blocked) continue;

                int next = (nz - light.Z + reach) * side + (nx - light.X + reach);
                if (finalized[next]) continue;
                int offer = dist[local] + (i < 4 ? 100 : 141);
                if (offer > maxDist) continue;
                // Diagonal 4..7 sits between cardinals (0,1), (1,2), (2,3), (3,0).
                if (i >= 4 && cardinalBlocked[i - 4] && cardinalBlocked[(i - 3) & 3]) continue;
                if (offer >= dist[next]) continue;
                dist[next] = offer;
                queue.Enqueue(next, offer);
            }
        }
    }

    // Requires: dist >= 1 (the origin cell sits at 1).
    // Ensures: GroundGlowAt of a lone 255-white glower: atten = Lerp(1 - d/r, 1/d^2, 0.4) truncated to a
    //          channel byte, then Min(0.5, byte / 255 * 3.6); 0 beyond the radius.
    internal static float GroundGlowFromDist(float dist, float glowRadius)
    {
        if (dist > glowRadius) return 0f;
        float atten = Mathf.Lerp(1f + -1f / glowRadius * dist, 1f / (dist * dist), 0.4f);
        int channel = (int)(255f * atten);
        return channel > 0 ? Mathf.Min(0.5f, channel / 255f * 3.6f) : 0f;
    }
}
