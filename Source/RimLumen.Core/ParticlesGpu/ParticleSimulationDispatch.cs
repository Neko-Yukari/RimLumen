namespace RimLumen.Core.ParticlesGpu;

internal static class ParticleSimulationDispatch
{
    internal const int GroupWidth = 64;
    internal const int SingleGroupCapacity = 1024;

    internal static int BlockCount(int capacity) => (capacity - 1) / GroupWidth + 1;
    internal static long ScratchBytes(int capacity) => (long)BlockCount(capacity) * sizeof(uint);
    internal static bool UsesParallelScan(int capacity) => capacity > SingleGroupCapacity;
}
