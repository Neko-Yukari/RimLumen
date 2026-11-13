using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RimUnity.Serialization;

namespace RimLumen.Adapter.RimUnity;

/// <summary>Reads export metadata only; never instantiates a RimUnity object or a GPU resource.</summary>
public sealed class RimUnityPackageCatalog
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    private readonly Dictionary<string, AssetRecord> byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssetRecord> byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string?>? ownerRoot;

    public string PackageId { get; }
    public string DataDirectory { get; }
    public Manifest Manifest { get; }

    public RimUnityPackageCatalog(string packageId, string dataDirectory, Func<string, string?>? ownerRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        PackageId = packageId;
        DataDirectory = Path.GetFullPath(dataDirectory);
        this.ownerRoot = ownerRoot;
        string catalog = ContentFiles.Inside(DataDirectory, "catalog.json");
        Manifest = ReadJson<Manifest>(catalog);
        if (Manifest.formatVersion is not (1 or 2) || Manifest.assets is null)
            throw new InvalidDataException("Unsupported RimUnity manifest format: " + Manifest.formatVersion);
        if (Manifest.sharedFiles is not null)
            foreach (string relative in Manifest.sharedFiles.Keys) RequireBlob(relative);
        foreach (AssetRecord asset in Manifest.assets)
        {
            if (string.IsNullOrWhiteSpace(asset.id) || !byId.TryAdd(asset.id, asset))
                throw new InvalidDataException("Missing or duplicate asset id: " + asset.id);
            if (string.IsNullOrWhiteSpace(asset.path) || !byPath.TryAdd(asset.path, asset))
                throw new InvalidDataException("Missing or duplicate logical path: " + asset.path);
            Resolve(asset.file);
        }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (AssetRecord asset in Manifest.assets) ValidateDependencies(asset, visited, visiting);
    }

    public AssetRecord? Find(string idOrPath) =>
        byId.TryGetValue(idOrPath, out AssetRecord? record) || byPath.TryGetValue(idOrPath, out record) ? record : null;

    public string Resolve(string relative)
    {
        if (Manifest.sharedFiles is not null && Manifest.sharedFiles.TryGetValue(relative, out string? owner))
        {
            RequireBlob(relative);
            string? root = string.Equals(owner, PackageId, StringComparison.OrdinalIgnoreCase)
                ? DataDirectory : ownerRoot?.Invoke(owner);
            if (root is null) throw new InvalidDataException("Shared blob owner is unavailable: " + owner);
            string path = ContentFiles.Inside(root, relative);
            if (!File.Exists(path)) throw new FileNotFoundException("Missing shared blob from " + owner, path);
            return path;
        }
        if (relative.StartsWith("blobs/", StringComparison.Ordinal)) RequireBlob(relative);
        return ContentFiles.Inside(DataDirectory, relative);
    }

    public PrefabData ReadPrefab(AssetRecord record)
    {
        if (record.type != "Prefab") throw new InvalidDataException("Asset is not a Prefab: " + record.id);
        PrefabData data = ReadJson<PrefabData>(Resolve(record.file));
        if (data.formatVersion != 1) throw new InvalidDataException("Unsupported Prefab format: " + data.formatVersion);
        return data;
    }

    public ResourceData ReadResource(string assetId)
    {
        AssetRecord record = RequireAsset(assetId);
        ResourceData data = ReadJson<ResourceData>(Resolve(record.file));
        if (data.formatVersion != 1 || data.type != record.type)
            throw new InvalidDataException("Resource format/type mismatch: " + assetId);
        return data;
    }

    public ShaderData ReadShader(string assetId)
    {
        AssetRecord record = RequireAsset(assetId);
        if (record.type != "Shader") throw new InvalidDataException("Asset is not a Shader: " + assetId);
        ShaderData data = ReadJson<ShaderData>(Resolve(record.file));
        if (data.formatVersion != 1 || data.key != record.id || data.profile is not ("world" or "ui"))
            throw new InvalidDataException("Invalid Shader metadata: " + assetId);
        return data;
    }

    private AssetRecord RequireAsset(string id) => Find(id) ?? throw new InvalidDataException("Unknown asset: " + id);

    private static T ReadJson<T>(string path)
    {
        using Stream stream = ContentFiles.OpenJson(path);
        return JsonSerializer.Deserialize<T>(stream, Json) ?? throw new InvalidDataException("Empty JSON payload: " + path);
    }

    private static void RequireBlob(string relative)
    {
        if (relative.Contains('\\') || relative.Contains(':'))
            throw new InvalidDataException("Shared blob must use a flat SHA256 path: " + relative);
        ContentFiles.RequireBlob(relative);
    }

    private void ValidateDependencies(AssetRecord asset, HashSet<string> visited, HashSet<string> visiting)
    {
        if (visited.Contains(asset.id)) return;
        if (!visiting.Add(asset.id)) throw new InvalidDataException("Cyclic asset dependency: " + asset.id);
        if (asset.dependencies is not null)
            foreach (string id in asset.dependencies)
            {
                if (!byId.TryGetValue(id, out AssetRecord? dependency))
                    throw new InvalidDataException("Missing dependency " + id + " of " + asset.id);
                ValidateDependencies(dependency, visited, visiting);
            }
        visiting.Remove(asset.id);
        visited.Add(asset.id);
    }
}
