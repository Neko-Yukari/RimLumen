using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;
using Verse;

namespace RimLumen.Adapter.RimUnity;

/// <summary>
/// This assembly is always loaded (see <c>LoadFolders.xml</c>) because an embedded RimUnity has no package id to gate on.
/// The static constructor touches no RimUnity type until <see cref="RimUnityRuntime.Find"/> confirms a running runtime.
/// </summary>
[StaticConstructorOnStartup]
public static class RimUnityAdapterBootstrap
{
    static RimUnityAdapterBootstrap()
    {
        var runtime = RimUnityRuntime.Find();
        if (runtime != null) Register(runtime);
    }

    // NoInlining keeps RimUnity-referencing types out of the static constructor's JIT when RimUnity is absent.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Register(RimUnityRuntime runtime)
    {
        var mods = LoadedModManager.RunningMods.ToArray();
        var provider = new RimUnityAssetProvider();
        foreach (var mod in mods)
        {
            string directory = Path.Combine(mod.RootDir, "1.7", "Data", "RimUnity");
            if (File.Exists(Path.Combine(directory, "catalog.json")))
                provider.RegisterPackageRoot(mod.PackageIdPlayerFacing, directory);
        }
        var dispose = typeof(Map).GetMethod(nameof(Map.Dispose), Type.EmptyTypes)
            ?? throw new MissingMethodException(typeof(Map).FullName, nameof(Map.Dispose));
        var harmony = new Harmony("NekoYukari.RimLumen.RimUnity");
        harmony.Patch(dispose, prefix: new HarmonyMethod(typeof(RimUnityAdapterBootstrap), nameof(MapRemoved)));
        if (runtime.CanReadMapHdrMode) RimUnityMapHdrNotice.Install(runtime, harmony);
        LumenSceneBootstrap.EnsureInitialized();
        LumenHostRegistry.RegisterAssetProvider(provider);
        LumenHostRegistry.RegisterVfxService(new RimUnityHybridVfxService(
            LumenHostRegistry.VfxService!, provider, new RimUnityLegacyFallbackPlayer()));
        Log.Message($"[RimLumen.Adapter.RimUnity] RimUnity {runtime.Version} provided by {runtime.Provider.Name}; " +
            $"registered {provider.PackageCount} RimUnity package(s)");
    }

    private static void MapRemoved(Map __instance) => RimUnityLegacyFallbackPlayer.ExternalHandle.ReleaseMap(__instance.uniqueID);
}
