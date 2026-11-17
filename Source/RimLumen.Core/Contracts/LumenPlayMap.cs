using System;

namespace RimLumen.Core.Contracts;

internal static class LumenPlayMap
{
    // Requires: currentMapUniqueId is sampled at the Play host seam, not during later frames.
    // Ensures: every successful Play owns one non-negative RimWorld Map.uniqueID. With no current map,
    //          an implicit request throws: keeping -1 would make particles visible on every map and could
    //          attach lights to an unrelated map. Explicit map requests need no current-map host state.
    internal static int ResolveMapIndex(int requestedMapIndex, int? currentMapUniqueId)
    {
        if (requestedMapIndex >= 0) return requestedMapIndex;
        if (requestedMapIndex != -1)
            throw new ArgumentOutOfRangeException(nameof(requestedMapIndex), "MapIndex must be a RimWorld map uniqueID or -1 for the current map.");
        return currentMapUniqueId is >= 0 ? currentMapUniqueId.Value
            : throw new InvalidOperationException("RimLumen Play requires a current RimWorld map when MapIndex is -1; pass an explicit map uniqueID instead.");
    }
}
