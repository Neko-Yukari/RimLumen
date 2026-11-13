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
    private static readonly object Sync = new();
    private static InMemoryAssetCatalog? catalog;
    private static CpuParticleSimulator? simulator;
    private static LumenVfxService? service;

    // Ensures: creates the singleton instances on first call, registers Catalog as an asset provider,
    //          registers Service as the VFX service, and registers Service and then Simulator as frame
    //          contributors (Service first: handle transforms must be updated before the simulation).
    //          Later calls return the same instances and register nothing twice.
    public static void EnsureInitialized()
    {
        lock (Sync)
        {
            if (IsInitialized) return;
            var newCatalog = new InMemoryAssetCatalog();
            var newSimulator = new CpuParticleSimulator();
            var newService = new LumenVfxService(newSimulator);
            LumenHostRegistry.RegisterAssetProvider(newCatalog);
            LumenHostRegistry.RegisterVfxService(newService);
            LumenHostRegistry.RegisterContributor(newService);
            LumenHostRegistry.RegisterContributor(newSimulator);
            catalog = newCatalog;
            simulator = newSimulator;
            service = newService;
        }
    }

    public static bool IsInitialized => service is not null;
    public static InMemoryAssetCatalog Catalog => catalog ?? throw new InvalidOperationException("Call EnsureInitialized first.");

    public static CpuParticleSimulator Simulator => simulator ?? throw new InvalidOperationException("Call EnsureInitialized first.");

    public static LumenVfxService Service => service ?? throw new InvalidOperationException("Call EnsureInitialized first.");
}

/// <summary>
/// RimWorld entry point: runs the bootstrap when the game finishes loading assemblies.
/// </summary>
[Verse.StaticConstructorOnStartup]
public static class LumenSceneStartupHook
{
    static LumenSceneStartupHook() => LumenSceneBootstrap.EnsureInitialized();
}
