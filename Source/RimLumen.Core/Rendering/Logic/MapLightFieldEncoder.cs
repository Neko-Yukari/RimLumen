using System;

namespace RimLumen.Core.Rendering.Logic;

public static class MapLightFieldEncoder
{
    public static byte EncodeRoof(bool roofed) => roofed ? (byte)255 : (byte)0;

    // Verse.GlowGrid.GroundGlowAt, Assembly-CSharp.decompiled.cs:69541-69542: artificial component only.
    public static byte EncodeArtificialGroundGlow(byte r, byte g, byte b)
    {
        int maxRgb = Math.Max(r, Math.Max(g, b));
        float normalized = MathF.Min(0.5f, (maxRgb / 255f) * 3.6f);
        return (byte)Math.Clamp((int)MathF.Round(normalized * 255f), 0, 255);
    }

    public static byte EncodeFog(bool fogged) => fogged ? (byte)255 : (byte)0;

    public static void EncodeCell(
        bool roofed,
        byte glowR,
        byte glowG,
        byte glowB,
        bool hasOutdoorTemperatureRoom,
        bool fogged,
        Span<byte> rgba)
    {
        if (rgba.Length < 4)
            throw new ArgumentException("Destination span must be at least 4 bytes.", nameof(rgba));
        rgba[0] = EncodeRoof(roofed);
        rgba[1] = EncodeArtificialGroundGlow(glowR, glowG, glowB);
        rgba[2] = hasOutdoorTemperatureRoom ? (byte)255 : (byte)0;
        rgba[3] = EncodeFog(fogged);
    }
}
