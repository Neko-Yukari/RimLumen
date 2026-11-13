using System;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Rendering.Probes.Contributors;

// P1-active: draws (a) an emissive quad with HDR intensity > 1.0 (bloom + tonemap measurement) and
// (b) a soft-particle quad whose depth straddles the vanilla scene depth (occlusion + feathering measurement),
// both into the RGBA16F target with read-only scene depth.
public sealed class SyntheticHdrQuadContributor : ILumenFrameContributor, IDisposable
{
    // Requires: hdrIntensity > 1; quads given in normalized screen space [0,1]^2 (origin top-left).
    public SyntheticHdrQuadContributor(float hdrIntensity, NormalizedRect emissiveQuad, NormalizedRect softQuad)
    {
        throw new NotImplementedException();
    }

    public string Name => "RimLumen.Probe.SyntheticHdrQuad";

    public bool HasActiveWork => throw new NotImplementedException();

    public NormalizedRect EmissiveQuad => throw new NotImplementedException();
    public NormalizedRect SoftQuad => throw new NotImplementedException();
    public int DrawsRecorded => throw new NotImplementedException();

    public void Arm() => throw new NotImplementedException();
    public void Disarm() => throw new NotImplementedException();

    public void CaptureMainThread(in LumenCaptureContext context) => throw new NotImplementedException();

    // Ensures: no compute work (no-op).
    public void EnqueuePreCompute(in LumenComputeContext context) => throw new NotImplementedException();

    // Ensures: no buffer barriers (no-op).
    public void RecordBufferBarriers(in LumenBarrierContext context) => throw new NotImplementedException();

    // Requires: context.SceneColorDepthSource non-null for the soft quad; else the soft quad is skipped.
    public void RecordHdrDraws(in LumenHdrDrawContext context) => throw new NotImplementedException();

    public void Dispose() => throw new NotImplementedException();
}

public readonly record struct NormalizedRect(float X, float Y, float Width, float Height);
