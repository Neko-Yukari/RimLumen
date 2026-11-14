namespace RimLumen.Core.Contracts;

public enum LumenParticleTier : byte
{
    L0_ExternalCpu = 0,
    L1_NativeCpu = 1,
    L2_GpuCompute = 2,
}

public readonly record struct LumenCapabilities(
    bool IsHdrActive,
    bool IsSceneDepthAvailable,
    bool IsGpuComputeAvailable,
    int MaxGpuParticlesPerFrame);
