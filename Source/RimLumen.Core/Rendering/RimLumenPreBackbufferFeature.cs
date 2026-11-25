using System;
using System.Collections.Generic;
using Waylith.Graphics;
using Waylith.Rendering;

namespace RimLumen.Core.Rendering;

internal sealed class RimLumenPreBackbufferFeature(RimLumenPipeline owner) : IRenderFeature, IEarlyMainThreadRenderFeature
{
    public bool IsActive => owner.Captured != null;
    public IReadOnlyList<IRenderPass> Passes { get; } = Array.Empty<IRenderPass>();

    public void BeginMainThreadFrame(in FrameTiming timing)
    {
        if (RimLumenRenderHost.FailSafeTripped) return;
        try { owner.BeginCapture(in timing); }
        catch (Exception ex) { owner.TripRenderFailSafe(ex); }
    }

    public object? CaptureFrame(in FrameTiming timing)
    {
        if (!RimLumenRenderHost.FailSafeTripped)
        {
            try { return owner.Capture(in timing); }
            catch (Exception ex) { owner.TripRenderFailSafe(ex); }
        }
        owner.RestorePendingExternalHdrCommandsIfMapActive();
        owner.Captured = null;
        return null;
    }

    public void OnRenderTargetRebuilt(IRenderTarget target) => owner.MapChain.OnRenderTargetRebuilt(target);
}
