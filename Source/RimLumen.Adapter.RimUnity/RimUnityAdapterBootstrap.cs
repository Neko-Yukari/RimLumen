using System;
using System.IO;
using System.Linq;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;
using Verse;

namespace RimLumen.Adapter.RimUnity;

[StaticConstructorOnStartup]
public static class RimUnityAdapterBootstrap
{
    static RimUnityAdapterBootstrap()
    {
        var mods = LoadedModManager.RunningMods.ToArray();
        if (!mods.Any(mod => string.Equals(mod.PackageIdPlayerFacing, AdapterModuleMarker.TargetBridge, StringComparison.OrdinalIgnoreCase))) return;
        var provider = new RimUnityAssetProvider();
        foreach (var mod in mods)
        {
            string directory = Path.Combine(mod.RootDir, "1.7", "Data", "RimUnity");
            if (File.Exists(Path.Combine(directory, "catalog.json")))
                provider.RegisterPackageRoot(mod.PackageIdPlayerFacing, directory);
        }
        LumenSceneBootstrap.EnsureInitialized();
        LumenHostRegistry.RegisterAssetProvider(provider);
        LumenHostRegistry.RegisterVfxService(new RimUnityHybridVfxService(
            LumenHostRegistry.VfxService!, provider, new RimUnityLegacyFallbackPlayer()));
        Log.Message($"[RimLumen.Adapter.RimUnity] Registered {provider.PackageCount} RimUnity package(s)");
    }
}
