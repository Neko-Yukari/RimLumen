using System;
using System.Numerics;

namespace RimLumen.Core.Lighting.Occlusion;

// Texel layout of the occlusion map (design .scratch/design/lighting-takeover.md 1.1). Shared by the C# reference
// (LightTransport) and RimLumenOcclusion.glsl; change both together.
internal static class OcclusionLayout
{
    internal const int TexelsPerCell = 4;
    internal const int Channels = 4;

    // Main texture R: point-light opacity. Solid = infinitely high blocker; cover opacity is capped below it.
    internal const byte Solid = 255;
    internal const byte MaxCover = 254;

    // Main texture B (design 1.8): signed distance d in texels from the texel centre to the nearest footprint edge,
    // positive inside, stored round(DistanceZero + DistanceScale * d); 0 (unstamped) reads d = -DistanceBand. Solid
    // non-door texels hold the distance to the edge of the solid region they belong to, saturated at MaxDistance.
    internal const int DistanceBand = 4;
    internal const float DistanceZero = 128f, DistanceScale = 32f;
    internal const float MaxDistance = (255f - DistanceZero) / DistanceScale;
    // Every 2 x 2 gather's edge rises from 0 to 1 over this many texels inside the footprint.
    internal const float EdgeRamp = 0.125f;
    // A solid non-door texel at least this deep is wall core: a target inside it stays dark (design 1.7).
    internal const float DeepCore = 3.5f;
    // Fine alpha samples per texel along each axis (AlphaMask.Distance).
    internal const int MaskSamples = 8;

    // Main texture A flags.
    internal const byte FlagDoor = 1;
    internal const byte FlagDoorAxisZ = 2; // set: the door's leaves slide along world z
    // G was written by a non-blockLight height occluder. Point lights read G only here: blockLight texels are governed
    // by the solid and door-gap term alone. The sun reads G everywhere.
    internal const byte FlagPointHeight = 4;
    internal const byte FlagSunFade = 128; // shadowData box: sun shadow fades as 1 - t/h

    // Coarse texture B flags.
    internal const byte CoarseDoor = 1;

    // Main texture G encodes sun height h in [0, MaxSunHeight] as round(h / MaxSunHeight * 255).
    internal const float MaxSunHeight = 2f;

    internal static byte EncodeDistance(float d) => (byte)Math.Clamp((int)MathF.Round(DistanceZero + DistanceScale * d), 0, byte.MaxValue);
    internal static float DecodeDistance(byte b) => (b - DistanceZero) / DistanceScale;
}

// Design 1.8 split of one 2 x 2 gather, shared by the point-light and sun paths (lumenTexelSplit in
// RimLumenOcclusion.glsl). Texels in order 00, 10, 01, 11; occupied is 1 or 0 per texel; distances the decoded B;
// (fx, fz) the bilinear fraction. One edge rule for every texel: a texel inside another term's footprint (d > 0, not
// occupied) reads d = -DistanceBand, then Distance = bilerp(d) and Edge = clamp(Distance / EdgeRamp, 0, 1), so the edge
// starts on the footprint boundary. Coverage = bilerp(occupied), Inside = bilerp(occupied ? value : 0), Outside =
// bilerp(occupied ? 0 : value).
internal readonly record struct TexelSplit(float Distance, float Coverage, float Inside, float Outside)
{
    internal float Edge => Math.Clamp(Distance / OcclusionLayout.EdgeRamp, 0f, 1f);

    internal static TexelSplit Of(Vector4 values, Vector4 occupied, Vector4 distances, float fx, float fz)
    {
        Vector4 inside = values * occupied;
        var gated = new Vector4(Gate(distances.X, occupied.X), Gate(distances.Y, occupied.Y), Gate(distances.Z, occupied.Z), Gate(distances.W, occupied.W));
        return new(Bilerp(gated, fx, fz), Bilerp(occupied, fx, fz),
            Bilerp(inside, fx, fz), Bilerp(values - inside, fx, fz));
    }

    private static float Gate(float d, float occupied) => occupied == 0f && d > 0f ? -OcclusionLayout.DistanceBand : d;

    internal static float Bilerp(Vector4 v, float fx, float fz) => (v.X * (1 - fx) + v.Y * fx) * (1 - fz) + (v.Z * (1 - fx) + v.W * fx) * fz;
}

// Inclusive cell rectangle; the offline tests cannot load Verse.CellRect.
internal readonly record struct CellBox(int MinX, int MinZ, int MaxX, int MaxZ)
{
    // Ensures: the box grown by margin on every side, clipped to [0, width) x [0, height).
    internal CellBox Expanded(int margin, int width, int height) =>
        new(Math.Max(0, MinX - margin), Math.Max(0, MinZ - margin),
            Math.Min(width - 1, MaxX + margin), Math.Min(height - 1, MaxZ + margin));
}

// World-space rectangle a mask is stretched over: centre and size in cells, already rotated by the host.
internal readonly record struct MaskPlacement(float CenterX, float CenterZ, float SizeX, float SizeZ);

// A sprite's alpha (design 1.3), built once per cached sprite from its fine alpha: MaskSamples x MaskSamples box-filtered
// samples per texel of the drawn size, column 0 the west edge, row 0 the south edge, Fine[row * Width * MaskSamples + col].
// Alpha: Width x Height texels (4 per drawn cell), each the mean of its fine samples; cover reads it.
// Distance: the signed distance, in mask texels, from each fine sample centre to the edge of fine alpha >= 128 (positive
//           inside), over the mask grown by DistanceBand texels on every side, clamped to +-DistanceBand; B reads it.
internal sealed class AlphaMask
{
    internal readonly int Width, Height;
    internal readonly byte[] Fine, Alpha;
    internal readonly float[] Distance;
    internal int DistanceWidth => (Width + 2 * OcclusionLayout.DistanceBand) * OcclusionLayout.MaskSamples;
    internal int DistanceHeight => (Height + 2 * OcclusionLayout.DistanceBand) * OcclusionLayout.MaskSamples;

    // Requires: width, height > 0; fine.Length == width * height * MaskSamples^2.
    internal AlphaMask(int width, int height, byte[] fine)
    {
        const int s = OcclusionLayout.MaskSamples;
        Width = width; Height = height; Fine = fine;
        Alpha = new byte[width * height];
        for (int z = 0; z < height; z++)
            for (int x = 0; x < width; x++)
            {
                int sum = 0;
                for (int fz = 0; fz < s; fz++)
                    for (int fx = 0; fx < s; fx++) sum += fine[(z * s + fz) * width * s + x * s + fx];
                Alpha[z * width + x] = (byte)MathF.Round(sum / (float)(s * s));
            }
        int pad = OcclusionLayout.DistanceBand * s, w = DistanceWidth, h = DistanceHeight;
        var inside = new bool[w * h];
        for (int z = 0; z < height * s; z++)
            for (int x = 0; x < width * s; x++) inside[(z + pad) * w + x + pad] = fine[z * width * s + x] >= 128;
        float[] toInside = SquaredDistance(inside, w, h, true), toOutside = SquaredDistance(inside, w, h, false);
        Distance = new float[w * h];
        for (int i = 0; i < Distance.Length; i++)
        {
            float d = inside[i] ? MathF.Sqrt(toOutside[i]) - 0.5f : 0.5f - MathF.Sqrt(toInside[i]);
            Distance[i] = Math.Clamp(d / s, -OcclusionLayout.DistanceBand, OcclusionLayout.DistanceBand);
        }
    }

    // Ensures: a mask opaque over width x height texels.
    internal static AlphaMask Opaque(int width, int height)
    {
        var fine = new byte[width * height * OcclusionLayout.MaskSamples * OcclusionLayout.MaskSamples];
        Array.Fill(fine, byte.MaxValue);
        return new AlphaMask(width, height, fine);
    }

    // Exact squared Euclidean distance from every sample centre to the nearest sample whose inside flag equals target
    // (Felzenszwalb and Huttenlocher, columns then rows); 1e20 when there is none.
    private static float[] SquaredDistance(bool[] inside, int w, int h, bool target)
    {
        const float far = 1e20f;
        var grid = new float[w * h];
        for (int i = 0; i < grid.Length; i++) grid[i] = inside[i] == target ? 0f : far;
        int n = Math.Max(w, h);
        var f = new float[n];
        var d = new float[n];
        var v = new int[n];
        var b = new float[n + 1];
        for (int x = 0; x < w; x++)
        {
            for (int z = 0; z < h; z++) f[z] = grid[z * w + x];
            Transform(f, d, v, b, h);
            for (int z = 0; z < h; z++) grid[z * w + x] = d[z];
        }
        for (int z = 0; z < h; z++)
        {
            Array.Copy(grid, z * w, f, 0, w);
            Transform(f, d, v, b, w);
            Array.Copy(d, 0, grid, z * w, w);
        }
        return grid;
    }

    private static void Transform(float[] f, float[] d, int[] v, float[] b, int n)
    {
        int k = 0;
        v[0] = 0; b[0] = float.NegativeInfinity; b[1] = float.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            float s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2f * q - 2f * v[k]);
            while (s <= b[k])
            {
                k--;
                s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2f * q - 2f * v[k]);
            }
            k++;
            v[k] = q; b[k] = s; b[k + 1] = float.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (b[k + 1] < q) k++;
            d[q] = (q - v[k]) * (q - v[k]) + f[v[k]];
        }
    }
}

// One drawn mesh of a thing: most things draw one, a plant one per printed mesh (Plant.Print).
internal readonly record struct SpritePiece(AlphaMask Mask, MaskPlacement At);

// What a thing stamps (design 1.2), resolved once by the host from its def. Blocker: def.blockLight; Door: the leaf axis
// of a door; Height: sun height (staticSunShadowHeight, or the shadowData height), 0 = none; Fade: the height is a
// shadowData height (FlagSunFade); Cover: fillPercent of a non-blocker with Fillage != None, else 0.
internal readonly record struct Occluder(bool Blocker, DoorAxis? Door, float Height, bool Fade, float Cover);

// Immutable snapshot published by OcclusionBuilder.Publish; an in-flight GPU frame never observes a later edit.
// Texels: RGBA8, (Width * 4) x (Height * 4), row-major from world (0, 0); texel (tx, tz) covers world
//         [tx / 4, (tx + 1) / 4) x [tz / 4, (tz + 1) / 4). Coarse: RGBA8, Width x Height, R and G = max over the cell's
//         3 x 3 neighbourhood of the main R and G, B = CoarseDoor when any cell of that 3 x 3 neighbourhood holds door
//         texels (so a fine step whose 2 x 2 gather can reach a door texel knows to read A and DoorStates), A = max over
//         the same neighbourhood of G on FlagPointHeight texels (the point-light coarse height).
internal sealed record OcclusionMap(int Width, int Height, byte[] Texels, byte[] Coarse)
{
    internal int TexelWidth => Width * OcclusionLayout.TexelsPerCell;
    internal int TexelHeight => Height * OcclusionLayout.TexelsPerCell;
}

// Per-cell door state, RGBA8 Width x Height (design Q1 approach A): R = the door's open half-gap g in door widths,
// encoded round(g * 2 * 255) (g in [0, 0.5]; 0.5 = no leaves at all), G = this cell's index along the door axis
// (0 = the door's min-x / min-z cell), B = door width in cells along that axis, A = 0. The host derives g from the
// door's own leaf geometry (design 1.5): Building_Door 0.45 * OpenPct, Building_MultiTileDoor 0.35 * OpenPct, with
// OpenPct quantised to 1/16 first; doors whose leaves are not drawn (stuck open) get 0.5. Non-door cells are all zero.
// Republished only when a door's encoded state changes.
internal sealed record DoorStates(int Width, int Height, byte[] Cells);

// What a light-pool frame reads: one map snapshot and the door state captured on the same main-thread capture.
internal readonly record struct OcclusionFrame(OcclusionMap Map, DoorStates Doors);
