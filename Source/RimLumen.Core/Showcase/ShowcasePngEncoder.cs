using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace RimLumen.Core.Showcase;

public static class ShowcasePngEncoder
{
    private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly uint[] CrcTable = BuildCrcTable();

    // Requires: rgbaTopDown.Length == width * height * 4, width > 0, height > 0.
    // Ensures: encodes a standard 8-bit RGBA PNG (IHDR + zlib IDAT + IEND) with valid CRC32 chunks.
    public static byte[] EncodeRgba(ReadOnlySpan<byte> rgbaTopDown, int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        int stride = checked(width * 4);
        int expectedLength = checked(stride * height);
        if (rgbaTopDown.Length != expectedLength)
        {
            throw new ArgumentException($"Expected {expectedLength} RGBA bytes for {width}x{height}, got {rgbaTopDown.Length}.", nameof(rgbaTopDown));
        }

        using var output = new MemoryStream();
        output.Write(PngSignature, 0, PngSignature.Length);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[0..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        ihdr[10] = 0; // deflate
        ihdr[11] = 0; // adaptive filter
        ihdr[12] = 0; // no interlace
        WriteChunk(output, "IHDR", ihdr);

        byte[] idatPayload;
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                byte[] scanline = new byte[stride + 1];
                scanline[0] = 0; // filter None
                for (int y = 0; y < height; y++)
                {
                    rgbaTopDown.Slice(y * stride, stride).CopyTo(scanline.AsSpan(1));
                    zlib.Write(scanline, 0, scanline.Length);
                }
            }
            idatPayload = compressed.ToArray();
        }

        WriteChunk(output, "IDAT", idatPayload);
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    public static void WriteRgba(string path, ReadOnlySpan<byte> rgbaTopDown, int width, int height)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllBytes(path, EncodeRgba(rgbaTopDown, width, height));
    }

    internal static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }

    private static void WriteChunk(Stream output, string chunkType, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        output.Write(header);

        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(chunkType, typeBytes);
        output.Write(typeBytes);
        output.Write(payload);

        uint crc = 0xFFFFFFFFu;
        foreach (byte b in typeBytes)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        foreach (byte b in payload)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        crc ^= 0xFFFFFFFFu;

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[i] = c;
        }
        return table;
    }
}
