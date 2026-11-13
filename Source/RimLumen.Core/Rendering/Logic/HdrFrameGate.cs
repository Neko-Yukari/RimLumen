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
    // Requires: routedDrawCount >= 0.
    // Ensures: Skip iff !anyContributorActive && routedDrawCount == 0 && !probeForcesActive; else Render.
    public static HdrFrameMode Decide(bool anyContributorActive, int routedDrawCount, bool probeForcesActive)
    {
        throw new NotImplementedException();
    }
}
