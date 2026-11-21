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
}

internal sealed record LumenEffectPreviewEntry(
    string EffectId,
    EffectAsset Asset,
    LumenEffectPreviewSourceKind SourceKind,
    string PackageId,
    string SourceDetail);

internal static class LumenEffectPreviewCatalog
{
    internal const float DefaultBeamTargetOffsetCells = 6f;

    internal static IReadOnlyList<LumenEffectPreviewEntry> BuildEntries(InMemoryAssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        LumenScannedAsset[] scanned = catalog.GetScannedSnapshot();
        var scannedById = new Dictionary<string, LumenScannedAsset>(scanned.Length, StringComparer.Ordinal);
        foreach (LumenScannedAsset item in scanned)
        {
            scannedById[item.EffectId] = item;
        }

        EffectAsset[] snapshot = catalog.GetSnapshot();
        var entries = new List<LumenEffectPreviewEntry>(snapshot.Length);
        foreach (EffectAsset asset in snapshot)
        {
            if (scannedById.TryGetValue(asset.EffectId, out LumenScannedAsset scannedItem))
            {
                (string packageId, string detail) = SplitSource(scannedItem.Source);
                LumenEffectPreviewSourceKind kind = detail.StartsWith("LumenEffectDef/", StringComparison.Ordinal)
                    ? LumenEffectPreviewSourceKind.XmlDef
                    : LumenEffectPreviewSourceKind.Json;
                entries.Add(new LumenEffectPreviewEntry(asset.EffectId, asset, kind, packageId, detail));
            }
            else
            {
                entries.Add(new LumenEffectPreviewEntry(
                    asset.EffectId,
                    asset,
                    LumenEffectPreviewSourceKind.Code,
                    string.Empty,
                    string.Empty));
            }
        }

        entries.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.EffectId, b.EffectId));
        return entries;
    }

    internal static IReadOnlyList<LumenEffectPreviewEntry> FilterEntries(
        IReadOnlyList<LumenEffectPreviewEntry> entries,
        string? searchText)
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
            bool matchesAll = true;
            for (int t = 0; t < tokens.Length; t++)
            {
                string token = tokens[t];
                bool matchesToken =
                    entry.EffectId.Contains(token, StringComparison.OrdinalIgnoreCase)
                    || entry.PackageId.Contains(token, StringComparison.OrdinalIgnoreCase)
                    || entry.SourceDetail.Contains(token, StringComparison.OrdinalIgnoreCase)
                    || kindKeywords.Contains(token, StringComparison.OrdinalIgnoreCase);

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

    internal static LumenPlayRequest BuildPlayRequest(
        EffectAsset asset,
        int mapIndex,
        float groundX,
        float groundZ,
        string? initialState = null)
    {
        var origin = new Vector3(groundX, 0f, groundZ);
        bool needsDepth = asset.Nodes.Any(node => node.DepthMask != LumenDepthMask.None);
        bool needsTarget = asset.Nodes.Any(node => node.Ribbon is { EndAtTarget: true });

        return new LumenPlayRequest
        {
            EffectId = asset.EffectId,
            MapIndex = mapIndex,
            OriginWorld = origin,
            TargetWorld = needsTarget ? new Vector3(origin.X + DefaultBeamTargetOffsetCells, 0f, origin.Z) : null,
            DepthReferenceZ = needsDepth ? origin.Z : null,
            InitialState = string.IsNullOrEmpty(initialState) ? null : initialState,
        };
    }

    private static (string PackageId, string Detail) SplitSource(string source)
    {
        int colon = source.IndexOf(':');
        return colon < 0 ? (string.Empty, source) : (source[..colon], source[(colon + 1)..]);
    }

    private static string SourceKindKeywords(LumenEffectPreviewSourceKind kind) => kind switch
    {
        LumenEffectPreviewSourceKind.Json => "json",
        LumenEffectPreviewSourceKind.XmlDef => "xml def lumeneffectdef",
        LumenEffectPreviewSourceKind.Code => "code c#",
        _ => string.Empty,
    };
}
