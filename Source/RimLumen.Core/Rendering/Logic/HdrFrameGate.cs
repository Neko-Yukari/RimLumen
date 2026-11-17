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

    // Distortion draws need the chain for their composite even when the HDR layer stays empty.
    public static bool ShouldRenderHdr(int routedDrawCount, bool hasDirectHdrContributor, bool runOnEmptyFrames,
        int distortionDrawCount)
        => runOnEmptyFrames || distortionDrawCount > 0 || ShouldRenderHdr(routedDrawCount, hasDirectHdrContributor);

    // Requires: routedDrawCount >= 0.
    // Ensures: Skip iff !anyContributorActive && routedDrawCount == 0; else Render.
    public static HdrFrameMode Decide(bool anyContributorActive, int routedDrawCount)
    {
        return anyContributorActive || routedDrawCount > 0
            ? HdrFrameMode.Render
            : HdrFrameMode.Skip;
    }

    // Ensures: Render when runOnEmptyFrames or any distortion draw exists, else the two-argument decision.
    public static HdrFrameMode Decide(bool anyContributorActive, int routedDrawCount, int distortionDrawCount,
        bool runOnEmptyFrames)
    {
        return runOnEmptyFrames || distortionDrawCount > 0
            ? HdrFrameMode.Render
            : Decide(anyContributorActive, routedDrawCount);
    }
}
