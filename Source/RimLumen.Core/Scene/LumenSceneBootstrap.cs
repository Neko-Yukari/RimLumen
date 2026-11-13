using System;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Scene;

/// <summary>
/// Composition root: wires the native asset catalog, the L1 simulator and the VFX service into
/// <see cref="LumenHostRegistry"/>. Idempotent and safe to call from a static constructor or a test.
/// </summary>
public static class LumenSceneBootstrap
{
    // Ensures: creates the singleton instances on first call, registers Catalog as an asset provider,
    //          registers Service as the VFX service, and registers Service and then Simulator as frame
    //          contributors (Service first: handle transforms must be updated before the simulation).
    //          Later calls return the same instances and register nothing twice.
    public static void EnsureInitialized() => throw new NotImplementedException();

    public static bool IsInitialized => throw new NotImplementedException();
    public static InMemoryAssetCatalog Catalog => throw new NotImplementedException();

    public static CpuParticleSimulator Simulator => throw new NotImplementedException();

    public static LumenVfxService Service => throw new NotImplementedException();
}

/// <summary>
/// RimWorld entry point: runs the bootstrap when the game finishes loading assemblies.
/// </summary>
[Verse.StaticConstructorOnStartup]
public static class LumenSceneStartupHook
{
    static LumenSceneStartupHook() => LumenSceneBootstrap.EnsureInitialized();
}
