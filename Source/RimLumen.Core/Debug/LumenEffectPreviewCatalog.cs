using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Debug;

internal enum LumenEffectPreviewSourceKind : byte
{
    Json,
    XmlDef,
    Code,
    ExternalProvider,
}

internal sealed record LumenEffectPreviewEntry(
    string EffectId,
    EffectAsset Asset,
    LumenEffectPreviewSourceKind SourceKind,
    string PackageId,
    string SourceDetail)
{
    public bool HasStates => Asset.States.Length > 0;

    public bool IsLooping => Asset.States.Length > 0
        ? Asset.States.Any(state => state.Loop)
        : Asset.Nodes.Any(node => node.Emitter is { Looping: true });

    public bool RequiresTargetWorld => Asset.Nodes.Any(node => node.Ribbon is { EndAtTarget: true });

    public bool RequiresDepthReference => Asset.Nodes.Any(node => node.DepthMask != LumenDepthMask.None);
}

internal static class LumenEffectPreviewCatalog
{
    internal const float DefaultBeamTargetOffsetCells = 6f;

    // Ensures: builds a sorted, deduplicated list of registered native catalog assets plus any resolved
    //          external-provider assets without overwriting catalog entries.
    internal static IReadOnlyList<LumenEffectPreviewEntry> BuildEntries(
        InMemoryAssetCatalog catalog,
        IReadOnlyCollection<EffectAsset>? externalAssets = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        LumenScannedAsset[] scanned = catalog.GetScannedSnapshot();
        var scannedById = new Dictionary<string, LumenScannedAsset>(scanned.Length, StringComparer.Ordinal);
        foreach (LumenScannedAsset item in scanned)
        {
            scannedById[item.EffectId] = item;
        }

        EffectAsset[] snapshot = catalog.GetSnapshot();
        var entries = new Dictionary<string, LumenEffectPreviewEntry>(
            snapshot.Length + (externalAssets?.Count ?? 0),
            StringComparer.Ordinal);

        foreach (EffectAsset asset in snapshot)
        {
            if (scannedById.TryGetValue(asset.EffectId, out LumenScannedAsset scannedItem))
            {
                (string packageId, string detail) = SplitSource(scannedItem.Source);
                LumenEffectPreviewSourceKind kind = detail.StartsWith("LumenEffectDef/", StringComparison.Ordinal)
                    ? LumenEffectPreviewSourceKind.XmlDef
                    : LumenEffectPreviewSourceKind.Json;
                entries[asset.EffectId] = new LumenEffectPreviewEntry(asset.EffectId, asset, kind, packageId, detail);
            }
            else
            {
                entries[asset.EffectId] = new LumenEffectPreviewEntry(
                    asset.EffectId,
                    asset,
                    LumenEffectPreviewSourceKind.Code,
                    string.Empty,
                    string.Empty);
            }
        }

        if (externalAssets is not null)
        {
            foreach (EffectAsset external in externalAssets)
            {
                if (external is null || string.IsNullOrEmpty(external.EffectId) || entries.ContainsKey(external.EffectId))
                {
                    continue;
                }

                int colon = external.EffectId.IndexOf(':');
                string packageId = colon > 0 ? external.EffectId[..colon] : string.Empty;
                entries[external.EffectId] = new LumenEffectPreviewEntry(
                    external.EffectId,
                    external,
                    LumenEffectPreviewSourceKind.ExternalProvider,
                    packageId,
                    external.EffectId);
            }
        }

        return entries.Values
            .OrderBy(entry => entry.EffectId, StringComparer.Ordinal)
            .ToArray();
    }

    // Ensures: splits searchText on whitespace; every token must match EffectId, PackageId, SourceDetail,
    //          source-kind keywords, or extraSearchText (case-insensitive) while preserving entry order.
    internal static IReadOnlyList<LumenEffectPreviewEntry> FilterEntries(
        IReadOnlyList<LumenEffectPreviewEntry> entries,
        string? searchText,
        Func<LumenEffectPreviewEntry, string>? extraSearchText = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return entries;
        }

        string[] tokens = searchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return entries;
        }

        var filtered = new List<LumenEffectPreviewEntry>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            LumenEffectPreviewEntry entry = entries[i];
            string kindKeywords = SourceKindKeywords(entry.SourceKind);
            string? extra = extraSearchText?.Invoke(entry);
            bool matchesAll = true;
            for (int t = 0; t < tokens.Length; t++)
            {
                string token = tokens[t];
                bool matchesToken =
                    entry.EffectId.Contains(token, StringComparison.OrdinalIgnoreCase)
                    || entry.PackageId.Contains(token, StringComparison.OrdinalIgnoreCase)
                    || entry.SourceDetail.Contains(token, StringComparison.OrdinalIgnoreCase)
                    || kindKeywords.Contains(token, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(extra) && extra.Contains(token, StringComparison.OrdinalIgnoreCase));

                if (!matchesToken)
                {
                    matchesAll = false;
                    break;
                }
            }

            if (matchesAll)
            {
                filtered.Add(entry);
            }
        }

        return filtered;
    }

    // Ensures: preserves desiredEffectId when present (or returns null if missing so a broken asset is not
    //          silently swapped for an unrelated item); falls back to entries[0] only when desiredEffectId is empty.
    internal static LumenEffectPreviewEntry? SelectEntryAfterRefresh(
        IReadOnlyList<LumenEffectPreviewEntry> entries,
        string? desiredEffectId,
        bool selectFirstIfEmpty)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (!string.IsNullOrEmpty(desiredEffectId))
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].EffectId, desiredEffectId, StringComparison.Ordinal))
                {
                    return entries[i];
                }
            }

            return null;
        }

        return selectFirstIfEmpty && entries.Count > 0 ? entries[0] : null;
    }

    internal static string GetShortSourceDetail(LumenEffectPreviewEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.SourceKind == LumenEffectPreviewSourceKind.Json && !string.IsNullOrEmpty(entry.SourceDetail))
        {
            string fileName = Path.GetFileName(entry.SourceDetail);
            return string.IsNullOrEmpty(fileName) ? entry.SourceDetail : fileName;
        }

        return entry.SourceDetail;
    }

    internal static string ResolveEffectiveInitialState(EffectAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return asset.States.Length == 0 ? string.Empty : (asset.InitialState ?? asset.States[0].Name);
    }

    // Ensures: constructs a LumenPlayRequest anchored on the Y=0 ground plane and populates DepthReferenceZ /
    //          TargetWorld when the asset declares DepthMask or Ribbon.EndAtTarget so preview playback succeeds
    //          without requiring external host callers to provide those fields manually.
    internal static LumenPlayRequest BuildPlayRequest(
        EffectAsset asset,
        int mapIndex,
        float groundX,
        float groundZ,
        string? initialState = null,
        bool realTime = false,
        Vector3? targetWorld = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!float.IsFinite(groundX))
        {
            throw new ArgumentOutOfRangeException(nameof(groundX), groundX, "groundX must be finite.");
        }

        if (!float.IsFinite(groundZ))
        {
            throw new ArgumentOutOfRangeException(nameof(groundZ), groundZ, "groundZ must be finite.");
        }

        if (targetWorld is { } explicitTarget &&
            (!float.IsFinite(explicitTarget.X) || !float.IsFinite(explicitTarget.Y) || !float.IsFinite(explicitTarget.Z)))
        {
            throw new ArgumentOutOfRangeException(nameof(targetWorld), explicitTarget, "targetWorld must be finite.");
        }

        var origin = new Vector3(groundX, 0f, groundZ);
        bool requiresDepthReference = asset.Nodes.Any(node => node.DepthMask != LumenDepthMask.None);
        bool requiresTargetWorld = asset.Nodes.Any(node => node.Ribbon is { EndAtTarget: true });

        return new LumenPlayRequest
        {
            EffectId = asset.EffectId,
            MapIndex = mapIndex,
            OriginWorld = origin,
            TargetWorld = targetWorld ?? (requiresTargetWorld
                ? new Vector3(origin.X + DefaultBeamTargetOffsetCells, 0f, origin.Z)
                : null),
            DepthReferenceZ = requiresDepthReference ? origin.Z : null,
            InitialState = string.IsNullOrEmpty(initialState) ? null : initialState,
            RealTime = realTime,
        };
    }

    private static (string PackageId, string Detail) SplitSource(string source)
    {
        int colon = source.IndexOf(':');
        if (colon < 0)
        {
            return (string.Empty, source);
        }

        return (source[..colon], source[(colon + 1)..]);
    }

    private static string SourceKindKeywords(LumenEffectPreviewSourceKind kind) => kind switch
    {
        LumenEffectPreviewSourceKind.Json => "json",
        LumenEffectPreviewSourceKind.XmlDef => "xml def lumeneffectdef",
        LumenEffectPreviewSourceKind.Code => "code c#",
        LumenEffectPreviewSourceKind.ExternalProvider => "rimunity external provider",
        _ => string.Empty,
    };
}
