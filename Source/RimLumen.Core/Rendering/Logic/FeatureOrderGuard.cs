using System;
using System.Collections.Generic;

namespace RimLumen.Core.Rendering.Logic;

// Pure index arithmetic over FrameRenderer.Features type names; no engine access.
public static class FeatureOrderGuard
{
    // Requires: featureTypeNames is the current feature order (Type.Name); selfIndex is the
    //   composite feature's index in that list, or -1 when it is not inserted.
    // Ensures: returns -1 when no ImGui feature exists (first name containing "ImGui", ordinal
    //   ignore-case) or self already sits immediately before it; otherwise returns the index to pass
    //   to InsertFeature AFTER RemoveFeature(self) (when selfIndex >= 0) so self lands right before ImGui.
    public static int ComputeCompositeReinsertIndex(IReadOnlyList<string> featureTypeNames, int selfIndex)
    {
        throw new NotImplementedException();
    }

    // Requires: featureTypeNames is the current feature order (Type.Name), without the RimLumen pre-backbuffer feature.
    // Ensures: returns indexOf("MapRenderFeature") + 1 when "PostFxFeature" exists after it; otherwise -1.
    public static int ComputePreBackbufferInsertIndex(IReadOnlyList<string> featureTypeNames)
    {
        throw new NotImplementedException();
    }
}
