using System;
using System.Collections.Generic;
using System.IO;
using RimLumen.Core.Contracts;
using RimUnity.Serialization;

namespace RimLumen.Adapter.RimUnity;

public sealed class RimUnityAssetProvider : ILumenAssetProvider
{
    private readonly Dictionary<string, string> roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RimUnityPackageCatalog> catalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (EffectAsset Asset, RimUnityTranslationReport Report)> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly RimUnityPrefabTranslator translator = new();
    public RimUnityTextureBridge Textures { get; } = new();
    public int PackageCount => roots.Count;

    // Catalogs are opened lazily so all shared blob owners can be registered first. An explicit
    // registration, including the same root, invalidates lazy catalogs and translated instances.
    public void RegisterPackageRoot(string packageId, string rimUnityDataDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        roots[packageId] = Path.GetFullPath(rimUnityDataDir);
        catalogs.Clear();
        cache.Clear();
    }

    public bool TryResolveEffect(string effectId, out EffectAsset? asset)
    {
        if (!Find(effectId, out var catalog, out var record))
        {
            asset = null;
            return false;
        }
        string canonical = catalog!.PackageId + ":" + record!.id;
        if (!cache.TryGetValue(canonical, out var translated))
        {
            asset = translator.Translate(canonical, catalog, record, out var report);
            translated = (asset, report);
            cache.Add(canonical, translated);
        }
        asset = translated.Asset;
        foreach (var node in asset.Nodes)
        {
            if (node.Emitter?.TextureKey is { Length: > 0 } emitterKey) Textures.ResolveTextureKey(catalog, emitterKey);
            if (node.Ribbon?.TextureKey is { Length: > 0 } ribbonKey) Textures.ResolveTextureKey(catalog, ribbonKey);
        }
        return true;
    }

    public bool TryInspectTranslation(string effectId, out RimUnityTranslationReport? report)
    {
        if (!TryResolveEffect(effectId, out _) || !Find(effectId, out var catalog, out var record))
        {
            report = null;
            return false;
        }
        report = cache[catalog!.PackageId + ":" + record!.id].Report;
        return true;
    }

    private bool Find(string effectId, out RimUnityPackageCatalog? catalog, out AssetRecord? record)
    {
        catalog = null;
        record = null;
        if (string.IsNullOrWhiteSpace(effectId)) return false;
        int colon = effectId.IndexOf(':');
        string path = (colon >= 0 ? effectId[(colon + 1)..] : effectId).Replace('\\', '/');
        foreach (var package in roots)
        {
            if (colon >= 0 && !string.Equals(package.Key, effectId[..colon], StringComparison.OrdinalIgnoreCase)) continue;
            if (!catalogs.TryGetValue(package.Key, out var candidate))
            {
                candidate = new RimUnityPackageCatalog(package.Key, package.Value,
                    owner => roots.TryGetValue(owner, out string? root) ? root : null);
                catalogs.Add(package.Key, candidate);
            }
            foreach (var item in candidate.Manifest.assets)
            {
                if (item.type == "Prefab" && (string.Equals(item.id, path, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.path.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase)))
                {
                    catalog = candidate;
                    record = item;
                    return true;
                }
            }
        }
        return false;
    }
}
