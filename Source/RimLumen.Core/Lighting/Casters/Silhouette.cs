using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Lighting.Occlusion;

namespace RimLumen.Core.Lighting.Casters;

// A sprite's silhouette: the outline of its fine alpha >= 128 (the alpha a Cutout sprite keeps), as closed loops.
internal static class Silhouette
{
    // Douglas-Peucker tolerance in fine samples (1/32 cell at the host's 4 texels per drawn cell).
    private const float Tolerance = 1f;

    // Ensures: the loops in mask space, u east and v north over [0, 1]^2, each with the inside on its left (outer loops
    //          counter-clockwise) and at least 3 distinct vertices. Marching squares over the fine sample centres at
    //          iso 127.5, with zero alpha outside the mask, simplified by Douglas-Peucker.
    internal static Vector2[][] Of(AlphaMask mask)
    {
        int width = mask.Width * OcclusionLayout.MaskSamples, height = mask.Height * OcclusionLayout.MaskSamples;
        byte[] fine = mask.Fine;
        int Sample(int i, int j) => i < 0 || j < 0 || i >= width || j >= height ? 0 : fine[j * width + i];
        // Crossing keys: 2 * (row-major index of the edge's first sample over the grid grown by one sample) + vertical.
        int stride = width + 2;
        long Key(int i, int j, bool vertical) => 2L * ((j + 1) * stride + i + 1) + (vertical ? 1 : 0);
        var next = new Dictionary<long, long>();
        var at = new Dictionary<long, Vector2>();
        Span<int> value = stackalloc int[4];
        Span<long> edge = stackalloc long[4];
        Span<bool> inside = stackalloc bool[4];
        for (int j = -1; j < height; j++)
            for (int i = -1; i < width; i++)
            {
                // Corners counter-clockwise: (i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1); edge k joins corner k and k + 1.
                value[0] = Sample(i, j); value[1] = Sample(i + 1, j); value[2] = Sample(i + 1, j + 1); value[3] = Sample(i, j + 1);
                int count = 0;
                for (int k = 0; k < 4; k++) count += (inside[k] = value[k] >= 128) ? 1 : 0;
                if (count is 0 or 4) continue;
                edge[0] = Key(i, j, false); edge[1] = Key(i + 1, j, true); edge[2] = Key(i, j + 1, false); edge[3] = Key(i, j, true);
                // With the centre inside the outside runs are islands, else the inside runs are: either way each run
                // [k, m] of the island kind gives one segment with the inside on its left.
                bool island = !(count == 2 && inside[0] == inside[2] && value[0] + value[1] + value[2] + value[3] >= 4 * 128);
                for (int k = 0; k < 4; k++)
                {
                    if (inside[k] != island || inside[(k + 3) % 4] == island) continue;
                    int m = k;
                    while (inside[(m + 1) % 4] == island) m = (m + 1) % 4;
                    long from = island ? edge[m] : edge[(k + 3) % 4], to = island ? edge[(k + 3) % 4] : edge[m];
                    next.Add(from, to);
                }
                for (int k = 0; k < 4; k++)
                {
                    if (inside[k] == inside[(k + 1) % 4] || at.ContainsKey(edge[k])) continue;
                    (int ax, int az) = Corner(i, j, k);
                    (int bx, int bz) = Corner(i, j, (k + 1) % 4);
                    float f = (127.5f - value[k]) / (value[(k + 1) % 4] - value[k]);
                    at.Add(edge[k], new Vector2((ax + (bx - ax) * f + 0.5f) / width, (az + (bz - az) * f + 0.5f) / height));
                }
            }
        var loops = new List<Vector2[]>();
        var points = new List<Vector2>();
        while (next.Count > 0)
        {
            using Dictionary<long, long>.Enumerator first = next.GetEnumerator();
            first.MoveNext();
            long start = first.Current.Key, key = start;
            points.Clear();
            do
            {
                points.Add(at[key]);
                long following = next[key];
                next.Remove(key);
                key = following;
            } while (key != start);
            Vector2[] simple = Simplify(points, new Vector2(width, height));
            if (simple.Length >= 3) loops.Add(simple);
        }
        return loops.ToArray();
    }

    private static (int X, int Z) Corner(int i, int j, int k) => k switch { 0 => (i, j), 1 => (i + 1, j), 2 => (i + 1, j + 1), _ => (i, j + 1) };

    // Douglas-Peucker on a closed loop, measured in fine samples (scale): split at the vertex farthest from the first.
    private static Vector2[] Simplify(List<Vector2> loop, Vector2 scale)
    {
        int far = 0;
        for (int i = 1; i < loop.Count; i++)
            if (Vector2.DistanceSquared(loop[i] * scale, loop[0] * scale) > Vector2.DistanceSquared(loop[far] * scale, loop[0] * scale)) far = i;
        var keep = new bool[loop.Count + 1];
        keep[0] = keep[far] = keep[loop.Count] = true;
        Mark(loop, scale, 0, far, keep);
        Mark(loop, scale, far, loop.Count, keep);
        var result = new List<Vector2>();
        for (int i = 0; i < loop.Count; i++)
            if (keep[i]) result.Add(loop[i]);
        return result.ToArray();
    }

    // Keeps the vertex of (from, to) farthest from segment from-to while it lies beyond Tolerance; index loop.Count is
    // vertex 0 again.
    private static void Mark(List<Vector2> loop, Vector2 scale, int from, int to, bool[] keep)
    {
        if (to - from < 2) return;
        Vector2 a = loop[from] * scale, b = loop[to % loop.Count] * scale, ab = b - a;
        float length = ab.Length(), worst = 0f;
        int index = -1;
        for (int i = from + 1; i < to; i++)
        {
            Vector2 ap = loop[i] * scale - a;
            float d = length > 0f ? MathF.Abs(ab.X * ap.Y - ab.Y * ap.X) / length : ap.Length();
            if (d > worst) { worst = d; index = i; }
        }
        if (worst <= Tolerance) return;
        keep[index] = true;
        Mark(loop, scale, from, index, keep);
        Mark(loop, scale, index, to, keep);
    }

    // Ensures: unit loops stretched over the world rectangle at.
    internal static Vector2[][] Place(Vector2[][] unit, MaskPlacement at)
    {
        var placed = new Vector2[unit.Length][];
        var origin = new Vector2(at.CenterX - at.SizeX * 0.5f, at.CenterZ - at.SizeZ * 0.5f);
        var size = new Vector2(at.SizeX, at.SizeZ);
        for (int l = 0; l < unit.Length; l++)
        {
            placed[l] = new Vector2[unit[l].Length];
            for (int i = 0; i < unit[l].Length; i++) placed[l][i] = origin + unit[l][i] * size;
        }
        return placed;
    }
}
