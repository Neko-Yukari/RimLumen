using System.Collections.Generic;

namespace RimLumen.Core.Rendering.Logic;

public static class FeatureOrderGuard
{
    // Capture runs in feature order; the map postfix must observe this frame's RimLumen payload.
    public static int ComputePreBackbufferInsertIndex(IReadOnlyList<string> featureTypeNames)
    {
        for (int i = 0; i < featureTypeNames.Count; i++)
            if (featureTypeNames[i] == "MapRenderFeature") return i;
        return -1;
    }
}
