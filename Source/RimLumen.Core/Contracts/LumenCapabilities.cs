namespace RimLumen.Core.Contracts;

public enum LumenParticleTier : byte
{
    External = 0,
    L1_NativeCpu = 1,
    L2_GpuCompute = 2,
}

/// <summary>Separates last-recorded-frame facts from backend availability and shared capacity.</summary>
/// <param name="IsHdrActive">Whether the render host recorded HDR work for its last reported frame, not whether a renderer is installed.</param>
/// <param name="IsSceneDepthAvailable">Whether scene depth was available for that reported frame.</param>
/// <param name="IsGpuComputeAvailable">Whether a GPU compute backend is available; does not imply a dispatch in the reported frame.</param>
/// <param name="MaxGpuParticlesPerFrame">Total shared GPU particle slots (across all effects/maps), not live particles or per-emitter capacity; zero without a backend.</param>
public readonly record struct LumenCapabilities(
    bool IsHdrActive,
    bool IsSceneDepthAvailable,
    bool IsGpuComputeAvailable,
    int MaxGpuParticlesPerFrame);
