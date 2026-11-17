using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using RimLumen.Core.Contracts;
using RimUnity.Serialization;
using static RimLumen.Adapter.RimUnity.Tests.Export;

namespace RimLumen.Adapter.RimUnity.Tests;

internal static class FileTests
{
    private const string Hex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    internal static void Catalog()
    {
        using var temp = new TestDirectory();
        string owner = Path.Combine(temp.Root, "owner");
        Directory.CreateDirectory(Path.Combine(owner, "blobs"));
        string remote = Path.Combine(owner, "blobs", Hex + ".json");
        File.WriteAllText(remote, JsonSerializer.Serialize(Prefab(Particles()), Json));
        string local = Path.Combine(temp.Root, "blobs", Hex + ".json.gz");
        Directory.CreateDirectory(Path.Combine(temp.Root, "blobs"));
        File.WriteAllBytes(local, Gzip(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Prefab(Particles(64)), Json))));
        var manifest = new Manifest
        {
            formatVersion = 2,
            owner = "Test.Package",
            sharedFiles = new() { ["blobs/" + Hex + ".json"] = "Other.Package" },
            assets =
            [
                new AssetRecord { id = "shared", name = "Shared", path = "Effects/Shared", file = "blobs/" + Hex + ".json", type = "Prefab" },
                new AssetRecord { id = "localblob", name = "Local", path = "Effects/Local", file = "blobs/" + Hex + ".json.gz", type = "Prefab" },
                new AssetRecord { id = "plain", name = "Plain", path = "Effects/Plain", file = "prefab.json.gz", type = "Prefab" },
                new AssetRecord { id = "material", name = "Material", path = "Materials/Test", file = "material.json.gz", type = "Material" },
            ]
        };
        File.WriteAllText(Path.Combine(temp.Root, "catalog.json"), JsonSerializer.Serialize(manifest, Json));
        WriteGzipJson(Path.Combine(temp.Root, "prefab.json.gz"), Prefab(Particles()));
        WriteGzipJson(Path.Combine(temp.Root, "material.json.gz"), new ResourceData { type = "Material", name = "Gzip resource", colorSpace = "Linear" });

        var provider = new RimUnityAssetProvider();
        provider.RegisterPackageRoot("Test.Package", temp.Root);
        provider.RegisterPackageRoot("Other.Package", owner);
        Check.Equal(2, provider.PackageCount, "package count");
        Check.True(provider.TryResolveEffect("test.package:effects/shared", out var shared) && shared is not null, "shared owner resolution");
        Check.Equal("Test.Package:shared", shared!.EffectId, "canonical id");
        Check.True(provider.TryResolveEffect("EFFECTS/LOCAL", out var localAsset) && localAsset is not null, "case and local gzip blob");
        Check.Equal(64, localAsset!.Nodes[0].Emitter!.MaxParticles, "local blob content");
        Check.True(provider.TryResolveEffect("effects\\plain", out _), "backslash alias and gzip prefab");
        var catalog = new RimUnityPackageCatalog("Test.Package", temp.Root, _ => owner);
        ResourceData material = catalog.ReadResource("material");
        Check.Equal("Gzip resource", material.name, "gzip resource name");
        Check.Equal("Linear", material.colorSpace, "gzip resource fields");

        Check.Throws<InvalidDataException>(() => new RimUnityPackageCatalog("Test.Package", temp.Root), "owner is unavailable");
        Check.Throws<InvalidDataException>(() => new RimUnityPackageCatalog("Test.Package", temp.Root, _ => null), "owner is unavailable");

        string bad = Path.Combine(temp.Root, "bad");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "catalog.json"),
            "{\"formatVersion\":1,\"assets\":[{\"id\":\"x\",\"path\":\"Effects/Evil\",\"file\":\"../evil.json\",\"type\":\"Prefab\"}]}");
        Check.Throws<InvalidDataException>(() => new RimUnityPackageCatalog("Bad", bad), "越界");
        File.WriteAllText(Path.Combine(bad, "catalog.json"),
            "{\"formatVersion\":1,\"assets\":[],\"sharedFiles\":{\"blobs/XYZ.json\":\"Owner\"}}");
        Check.Throws<InvalidDataException>(() => new RimUnityPackageCatalog("Bad", bad), "SHA256");

        var version = new Manifest { formatVersion = 3, assets = [] };
        string v3 = Path.Combine(temp.Root, "v3");
        Directory.CreateDirectory(v3);
        File.WriteAllText(Path.Combine(v3, "catalog.json"), JsonSerializer.Serialize(version, Json));
        Check.Throws<InvalidDataException>(() => new RimUnityPackageCatalog("V3", v3), "Unsupported RimUnity manifest format");

        string gzipOnly = Path.Combine(temp.Root, "gzip-only");
        Directory.CreateDirectory(gzipOnly);
        WriteGzipJson(Path.Combine(gzipOnly, "catalog.json.gz"), new Manifest { assets = [] });
        Check.Throws<FileNotFoundException>(() => new RimUnityPackageCatalog("GzipOnly", gzipOnly), "catalog.json");
    }

    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    internal static void Reload()
    {
        using var temp = new TestDirectory();
        string prefabPath = Path.Combine(temp.Root, "prefab.json");
        var manifest = new Manifest
        {
            formatVersion = 2,
            assets = [new AssetRecord { id = "effect", name = "Effect", path = "Effects/Effect", file = "prefab.json", type = "Prefab" }],
        };
        File.WriteAllText(Path.Combine(temp.Root, "catalog.json"), JsonSerializer.Serialize(manifest, Json));
        File.WriteAllText(prefabPath, JsonSerializer.Serialize(Prefab(Particles(64)), Json));

        var provider = new RimUnityAssetProvider();
        provider.RegisterPackageRoot("Reload.Package", temp.Root);
        Check.True(provider.TryResolveEffect("Reload.Package:effect", out EffectAsset? first) && first is not null, "initial effect resolves");
        Check.Equal(64, first!.Nodes[0].Emitter!.MaxParticles, "initial prefab");

        File.WriteAllText(prefabPath, JsonSerializer.Serialize(Prefab(Particles(128)), Json));
        provider.RegisterPackageRoot("Reload.Package", temp.Root);
        Check.True(provider.TryResolveEffect("Reload.Package:effect", out EffectAsset? second) && second is not null,
            "effect resolves after same-root reload");
        Check.Equal(128, second!.Nodes[0].Emitter!.MaxParticles, "same-root reload re-reads the prefab");
        Check.True(!ReferenceEquals(first, second), "reload creates a new immutable instance");
        Check.Equal(64, first.Nodes[0].Emitter!.MaxParticles, "old instance keeps its content");

        manifest.assets.Add(new AssetRecord { id = "added", name = "Added", path = "Effects/Added", file = "added.json", type = "Prefab" });
        File.WriteAllText(Path.Combine(temp.Root, "catalog.json"), JsonSerializer.Serialize(manifest, Json));
        File.WriteAllText(Path.Combine(temp.Root, "added.json"), JsonSerializer.Serialize(Prefab(Particles()), Json));
        provider.RegisterPackageRoot("Reload.Package", temp.Root);
        Check.True(provider.TryResolveEffect("Reload.Package:added", out EffectAsset? added) && added is not null,
            "changed manifest adds a resolvable id");
    }

    private static void WriteGzipJson(string path, object value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, value.GetType(), Json));
        using FileStream file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        gzip.Write(bytes);
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }

    internal static void Textures()
    {
        using var temp = new TestDirectory();
        byte[] dxt1 = Rutx(10, 8, 8, [32, 8, 8], 11);
        string dxtPath = Path.Combine(temp.Root, "dxt1.rutex");
        File.WriteAllBytes(dxtPath, dxt1);
        var dxt = RimUnityTextureBridge.ReadPayload(dxtPath);
        Check.Equal(10, dxt.Format, "dxt format");
        Check.Equal(8, dxt.Width, "dxt width");
        Check.Equal(8, dxt.Height, "dxt height");
        Check.Equal(3, dxt.MipCount, "dxt mips");
        Check.Equal(32, dxt.Mips[0].Length, "dxt mip0");
        Check.Equal(8, dxt.Mips[2].Length, "dxt mip2");
        Check.True(dxt.Mips[0].All(b => b == 11), "dxt bytes intact");

        byte[] bc7 = Rutx(25, 16, 16, [256, 64], 22);
        var seven = RimUnityTextureBridge.ReadPayload(new MemoryStream(bc7));
        Check.Equal(25, seven.Format, "bc7 format");
        Check.Equal(2, seven.MipCount, "bc7 mips");
        Check.Equal(256, seven.Mips[0].Length, "bc7 mip0");
        Check.True(seven.Opaque, "bc7 opaque");

        string gzipPath = Path.Combine(temp.Root, "bc7.rutex.gz");
        File.WriteAllBytes(gzipPath, Gzip(bc7));
        var gzipped = RimUnityTextureBridge.ReadPayload(gzipPath);
        Check.Equal(2, gzipped.MipCount, "gzip mips");
        Check.Equal(256, gzipped.Mips[0].Length, "gzip bytes");

        Check.Throws<InvalidDataException>(() => RimUnityTextureBridge.ReadPayload(new MemoryStream(Mutate(dxt1, 0, 1))), "magic");
        Check.Throws<InvalidDataException>(() => RimUnityTextureBridge.ReadPayload(new MemoryStream(Mutate(dxt1, 4, 2))), "version");
        Check.Throws<InvalidDataException>(() => RimUnityTextureBridge.ReadPayload(new MemoryStream(dxt1[..^4])), "Truncated");
        Check.Throws<InvalidDataException>(() => RimUnityTextureBridge.ReadPayload(new MemoryStream(dxt1.Append((byte)0).ToArray())), "Trailing");
        Check.Throws<InvalidDataException>(() => RimUnityTextureBridge.ReadPayload(new MemoryStream(Mutate(dxt1, 8, 99))), "block format");
        Check.Throws<InvalidDataException>(() => RimUnityTextureBridge.ReadPayload(new MemoryStream(Mutate(dxt1, 12, 0))), "dimensions");
        Check.Throws<InvalidDataException>(() => RimUnityTextureBridge.ReadPayload(new MemoryStream(Mutate(dxt1, 20, 0))), "mip count");
        Check.Throws<InvalidDataException>(() => RimUnityTextureBridge.ReadPayload(new MemoryStream(Mutate(dxt1, 24, 2))), "opaque");
    }

    private static byte[] Mutate(byte[] source, int offset, byte value)
    {
        byte[] copy = (byte[])source.Clone();
        copy[offset] = value;
        return copy;
    }

    private static byte[] Rutx(int format, int width, int height, int[] mipSizes, byte fill)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(1481921874u);
        writer.Write(1);
        writer.Write(format);
        writer.Write(width);
        writer.Write(height);
        writer.Write(mipSizes.Length);
        writer.Write((byte)1);
        foreach (int size in mipSizes)
        {
            writer.Write(size);
            writer.Write(Enumerable.Repeat(fill, size).ToArray());
        }
        writer.Flush();
        return stream.ToArray();
    }
}
