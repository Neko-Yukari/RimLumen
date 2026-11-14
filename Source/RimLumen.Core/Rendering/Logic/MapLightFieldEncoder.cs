using System;

namespace RimLumen.Core.Rendering.Logic;

public static class MapLightFieldEncoder
{
    public static byte EncodeRoof(bool roofed) => roofed ? (byte)255 : (byte)0;

    public static byte EncodeGroundGlow(byte r, byte g, byte b, byte a)
    {
        if (a == 1) return 255;
        int maxRgb = Math.Max(r, Math.Max(g, b));
        float normalized = MathF.Min(0.5f, (maxRgb / 255f) * 3.6f);
        return (byte)Math.Clamp((int)MathF.Round(normalized * 255f), 0, 255);
    }

    public static byte EncodeEnclosure(bool roofed, bool usesOutdoorTemp)
    {
        if (!roofed) return 0;
        return usesOutdoorTemp ? (byte)128 : (byte)255;
    }

    public static byte EncodeFog(bool fogged) => fogged ? (byte)255 : (byte)0;

    public static void EncodeCell(
        bool roofed,
        byte glowR,
        byte glowG,
        byte glowB,
        byte glowA,
        bool usesOutdoorTemp,
        bool fogged,
        Span<byte> rgba)
    {
        if (rgba.Length < 4)
            throw new ArgumentException("Destination span must be at least 4 bytes.", nameof(rgba));
        rgba[0] = EncodeRoof(roofed);
        rgba[1] = EncodeGroundGlow(glowR, glowG, glowB, glowA);
        rgba[2] = EncodeEnclosure(roofed, usesOutdoorTemp);
        rgba[3] = EncodeFog(fogged);
    }
}
