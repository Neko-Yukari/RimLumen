using System;
using System.Collections.Concurrent;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Assets;

/// <summary>
/// Thread-safe native <see cref="EffectAsset"/> store used as the default
/// <see cref="ILumenAssetProvider"/>.
/// </summary>
public sealed class InMemoryAssetCatalog : ILumenAssetProvider
{
    private readonly ConcurrentDictionary<string, EffectAsset> _assets =
        new(StringComparer.Ordinal);

    // Ensures: number of currently registered effects.
    public int Count => _assets.Count;

    // Requires: asset is non-null; asset.EffectId is non-empty.
    // Ensures: throws ArgumentException when EffectId is null/empty, or when
    //          Nodes[i].ParentIndex < -1 or Nodes[i].ParentIndex >= i (self/forward reference,
    //          which is the only way a cycle can exist in an index-ordered node list).
    // Ensures: an existing entry with the same EffectId is replaced atomically.
    public void Register(EffectAsset asset)
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

        _assets[asset.EffectId] = asset;
    }

    // Requires: effectId is non-null.
    // Ensures: removes the entry when present and returns true, otherwise returns false.
    public bool Unregister(string effectId) => _assets.TryRemove(effectId, out _);

    // Ensures: removes every entry; safe to call concurrently with readers.
    public void Clear() => _assets.Clear();

    // Requires: effectId is non-null.
    // Ensures: returns true with the registered asset, or false with asset == null.
    public bool TryResolveEffect(string effectId, out EffectAsset? asset) => _assets.TryGetValue(effectId, out asset);
}
