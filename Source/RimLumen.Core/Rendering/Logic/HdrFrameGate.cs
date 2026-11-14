using System;

namespace RimLumen.Core.Rendering.Logic;

public enum HdrFrameMode : byte
{
    // Zero barriers, zero draws, zero clears: pixel-identical to RimLumen not installed.
    Skip = 0,
    Render = 1,
}

public static class HdrFrameGate
{
    // A frame may exist only to advance background compute: it needs barriers, but no HDR work.
    public static bool ShouldRenderHdr(int routedDrawCount, bool hasDirectHdrContributor)
        => routedDrawCount > 0 || hasDirectHdrContributor;

    public static bool ShouldRenderHdr(int routedDrawCount, bool hasDirectHdrContributor, bool runOnEmptyFrames)
        => runOnEmptyFrames || ShouldRenderHdr(routedDrawCount, hasDirectHdrContributor);

    // Requires: routedDrawCount >= 0.
    // Ensures: Skip iff !anyContributorActive && routedDrawCount == 0 && !probeForcesActive; else Render.
    public static HdrFrameMode Decide(bool anyContributorActive, int routedDrawCount, bool probeForcesActive)
    {
        return anyContributorActive || routedDrawCount > 0 || probeForcesActive
            ? HdrFrameMode.Render
            : HdrFrameMode.Skip;
    }

    public static HdrFrameMode Decide(bool anyContributorActive, int routedDrawCount, bool probeForcesActive, bool runOnEmptyFrames)
    {
        return runOnEmptyFrames
            ? HdrFrameMode.Render
            : Decide(anyContributorActive, routedDrawCount, probeForcesActive);
    }
}
