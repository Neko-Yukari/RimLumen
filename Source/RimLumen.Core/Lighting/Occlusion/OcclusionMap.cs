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

    // Main texture B: wall-core depth in texels. A target inside solid with a run deeper than this stays dark.
    internal const int WallCoreDepth = 3;

    // Main texture A flags.
    internal const byte FlagDoor = 1;
    internal const byte FlagDoorAxisZ = 2; // set: the door's leaves slide along world z
    internal const byte FlagSunFade = 128; // shadowData box: sun shadow fades as 1 - t/h

    // Coarse texture B flags.
    internal const byte CoarseDoor = 1;

    // Main texture G encodes sun height h in [0, MaxSunHeight] as round(h / MaxSunHeight * 255).
    internal const float MaxSunHeight = 2f;
}

// Design 1.8 split of one 2 x 2 gather, shared by the point-light and sun paths (lumenTexelSplit in
// RimLumenOcclusion.glsl). Texels in order 00, 10, 01, 11; occupied is 1 or 0 per texel; (fx, fz) the bilinear fraction.
// Coverage = bilerp(occupied), Inside = bilerp(occupied ? value : 0), Outside = bilerp(occupied ? 0 : value).
internal readonly record struct TexelSplit(float Coverage, float Inside, float Outside)
{
    internal static TexelSplit Of(Vector4 values, Vector4 occupied, float fx, float fz)
    {
        Vector4 inside = values * occupied;
        return new(Bilerp(occupied, fx, fz), Bilerp(inside, fx, fz), Bilerp(values - inside, fx, fz));
    }

    private static float Bilerp(Vector4 v, float fx, float fz) => (v.X * (1 - fx) + v.Y * fx) * (1 - fz) + (v.Z * (1 - fx) + v.W * fx) * fz;
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

// A sprite's alpha, resampled by the host: column 0 is the west edge, row 0 the south edge, Alpha[row * Width + col].
internal sealed record AlphaMask(int Width, int Height, byte[] Alpha);

// Immutable snapshot published by OcclusionBuilder.Publish; an in-flight GPU frame never observes a later edit.
// Texels: RGBA8, (Width * 4) x (Height * 4), row-major from world (0, 0); texel (tx, tz) covers world
//         [tx / 4, (tx + 1) / 4) x [tz / 4, (tz + 1) / 4). Coarse: RGBA8, Width x Height, R and G = max over the cell's
//         3 x 3 neighbourhood of the main R and G, B = CoarseDoor when any cell of that 3 x 3 neighbourhood holds door
//         texels (so a fine step whose 2 x 2 gather can reach a door texel knows to read A and DoorStates), A unused (0).
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
