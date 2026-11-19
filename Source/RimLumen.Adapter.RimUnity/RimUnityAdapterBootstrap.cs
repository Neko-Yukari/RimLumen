using System;
using System.IO;
using System.Linq;
using HarmonyLib;
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
        var dispose = typeof(Map).GetMethod(nameof(Map.Dispose), Type.EmptyTypes)
            ?? throw new MissingMethodException(typeof(Map).FullName, nameof(Map.Dispose));
        new Harmony("NekoYukari.RimLumen.RimUnity").Patch(dispose,
            prefix: new HarmonyMethod(typeof(RimUnityAdapterBootstrap), nameof(MapRemoved)));
        LumenSceneBootstrap.EnsureInitialized();
        LumenHostRegistry.RegisterAssetProvider(provider);
        LumenHostRegistry.RegisterVfxService(new RimUnityHybridVfxService(
            LumenHostRegistry.VfxService!, provider, new RimUnityLegacyFallbackPlayer()));
        Log.Message($"[RimLumen.Adapter.RimUnity] Registered {provider.PackageCount} RimUnity package(s)");
    }

    private static void MapRemoved(Map __instance) => RimUnityLegacyFallbackPlayer.ExternalHandle.ReleaseMap(__instance.uniqueID);
}
