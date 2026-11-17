using System;
using System.Collections.Concurrent;
using System.Linq;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Assets;

/// <summary>
/// One effect key registered by the mod scanner, with the source that owned it.
/// </summary>
internal readonly record struct LumenScannedAsset(string EffectId, EffectAsset Asset, string Source);

/// <summary>
/// Thread-safe native <see cref="EffectAsset"/> store used as the default
/// <see cref="ILumenAssetProvider"/>.
/// </summary>
public sealed class InMemoryAssetCatalog : ILumenAssetProvider
{
    private sealed class Entry
    {
        public Entry(EffectAsset asset, string? source)
        {
            Asset = asset;
            Source = source;
        }

        public EffectAsset Asset { get; }
        public string? Source { get; }
    }

    private readonly ConcurrentDictionary<string, Entry> _assets =
        new(StringComparer.Ordinal);

    // Ensures: number of currently registered effects.
    public int Count => _assets.Count;

    internal EffectAsset[] GetSnapshot() => _assets.Values.Select(entry => entry.Asset).ToArray();

    // Ensures: scanner-registered entries only, each with the source that registered it.
    internal LumenScannedAsset[] GetScannedSnapshot() => _assets
        .Where(pair => pair.Value.Source is not null)
        .Select(pair => new LumenScannedAsset(pair.Key, pair.Value.Asset, pair.Value.Source!))
        .ToArray();

    // Requires: asset is non-null; asset.EffectId is non-empty.
    // Ensures: throws ArgumentException when EffectId is null/empty, or when
    //          Nodes[i].ParentIndex < -1 or Nodes[i].ParentIndex >= i (self/forward reference,
    //          which is the only way a cycle can exist in an index-ordered node list).
    // Ensures: an existing entry with the same EffectId is replaced atomically. The new entry is
    //          code-owned: reload preserves it when absent from the fresh scan, otherwise replaces it.
    public void Register(EffectAsset asset) => Store(asset, source: null);

    // Ensures: registers a scanner-owned entry with the source that produced it.
    internal void RegisterScanned(EffectAsset asset, string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        Store(asset, source);
    }

    // Requires: effectId is non-null.
    // Ensures: removes the entry when present and returns true, otherwise returns false.
    public bool Unregister(string effectId) => _assets.TryRemove(effectId, out _);

    // Ensures: removes every entry; safe to call concurrently with readers.
    public void Clear() => _assets.Clear();

    // Requires: effectId is non-null.
    // Ensures: returns true with the registered asset, or false with asset == null.
    public bool TryResolveEffect(string effectId, out EffectAsset? asset)
    {
        bool found = _assets.TryGetValue(effectId, out Entry? entry);
        asset = found ? entry!.Asset : null;
        return found;
    }

    private void Store(EffectAsset asset, string? source)
    {
        if (string.IsNullOrEmpty(asset.EffectId))
        {
            throw new ArgumentException("EffectId must not be null or empty.", nameof(asset));
        }

        for (int i = 0; i < asset.Nodes.Length; i++)
        {
            int parent = asset.Nodes[i].ParentIndex;
            if (parent < -1 || parent >= i)
            {
                throw new ArgumentException($"Node {i} must refer to an earlier parent or -1.", nameof(asset));
            }
        }

        _assets[asset.EffectId] = new Entry(asset, source);
    }
}
