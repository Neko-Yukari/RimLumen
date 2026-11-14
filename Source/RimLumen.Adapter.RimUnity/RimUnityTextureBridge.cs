using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using RimLumen.Core.Contracts;
using RimUnity.Assets;
using RimUnity.Serialization;
using Waylith;

namespace RimLumen.Adapter.RimUnity;

public sealed record RimUnityTexturePayload(int Format, int Width, int Height, bool Opaque, byte[][] Mips)
{
    public int MipCount => Mips.Length;
}

public sealed class RimUnityTextureBridge
{
    private readonly HashSet<string> resolvedKeys = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> ResolvedTextureKeys => resolvedKeys;

    // Metadata remains usable offline; the catalog owns textures, not this bridge.
    public string ResolveTextureKey(RimUnityPackageCatalog catalog, string textureKey)
    {
        AssetRecord record = catalog.Find(textureKey) ?? throw new InvalidDataException("Unknown texture: " + textureKey);
        if (record.type != "Texture2D") throw new InvalidDataException("Asset is not a Texture2D: " + textureKey);
        resolvedKeys.Add(textureKey);
        if (EngineApplication.Instance?.GraphicsDevice is not null)
        {
            var texture = AssetLoader.LoadDirectory<global::RimUnity.Texture2D>(catalog.DataDirectory, record.path);
            RimLumen.Core.Assets.LumenTextureRegistry.Register(textureKey, texture.CoreTexture);
        }
        return textureKey;
    }

    public static RimUnityTexturePayload ReadPayload(string path)
    {
        using Stream file = File.OpenRead(path);
        if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) return ReadPayload(file);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return ReadPayload(gzip);
    }

    // Leaves the caller's stream open. Sizes are checked before allocating mip storage.
    public static RimUnityTexturePayload ReadPayload(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            if (reader.ReadUInt32() != 1481921874u) throw new InvalidDataException("Invalid RUTX magic.");
            if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported RUTX version.");
            int format = reader.ReadInt32();
            int width = reader.ReadInt32(), height = reader.ReadInt32(), count = reader.ReadInt32();
            byte opaque = reader.ReadByte();
            if (opaque > 1) throw new InvalidDataException("Invalid RUTX opaque flag.");
            if (width <= 0 || height <= 0) throw new InvalidDataException("RUTX dimensions must be positive.");
            int maxMips = 1;
            for (int size = Math.Max(width, height); size > 1; size >>= 1) maxMips++;
            if (count < 1 || count > maxMips) throw new InvalidDataException("Invalid RUTX mip count.");
            int blockBytes = format switch
            {
                10 or 26 => 8,
                12 or 25 or 27 => 16,
                _ => throw new InvalidDataException("Unsupported RUTX block format: " + format),
            };
            var mips = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                long expected = ((Math.Max(1, width >> i) + 3L) / 4) * ((Math.Max(1, height >> i) + 3L) / 4) * blockBytes;
                int length = reader.ReadInt32();
                if (expected > int.MaxValue || length != expected)
                    throw new InvalidDataException($"Invalid RUTX mip {i} length: {length}; expected {expected}.");
                mips[i] = reader.ReadBytes(length);
                if (mips[i].Length != length) throw new InvalidDataException($"Truncated RUTX mip {i}.");
            }
            if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing data after RUTX mips.");
            return new RimUnityTexturePayload(format, width, height, opaque == 1, mips);
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException("Truncated RUTX header or mip length.", error);
        }
    }
}
