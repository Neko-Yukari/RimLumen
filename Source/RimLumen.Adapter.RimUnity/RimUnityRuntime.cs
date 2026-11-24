using System;
using System.Reflection;
using Verse;

namespace RimLumen.Adapter.RimUnity;

/// <summary>
/// The RimUnity runtime that is actually running, standalone (<c>Chezhou.RimUnity</c>) or embedded
/// (<c>RimUnity.Embedded</c> inside another mod), and the only place that reads RimUnity members by reflection.
/// This type has no compile-time RimUnity reference, so it is safe to use when RimUnity is absent.
/// </summary>
internal sealed class RimUnityRuntime
{
    private const string RuntimeAssemblyName = "RimUnity";
    private const string ModTypeName = "RimUnity.RimUnityMod";

    private readonly PropertyInfo? bloomProperty;
    private readonly FieldInfo? mapHdrModeField;

    public ModContentPack Provider { get; }
    public string Version { get; }

    /// <summary>False when this RimUnity build lacks <c>RimUnityMod.Bloom.mapHdrMode</c>; a warning was logged once.</summary>
    public bool CanReadMapHdrMode => mapHdrModeField != null;

    private RimUnityRuntime(Assembly assembly, Type modType, ModContentPack provider)
    {
        Provider = provider;
        Version = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? assembly.GetName().Version!.ToString();
        bloomProperty = modType.GetProperty("Bloom", BindingFlags.Public | BindingFlags.Static);
        mapHdrModeField = bloomProperty?.PropertyType.GetField("mapHdrMode", BindingFlags.Public | BindingFlags.Instance);
        if (mapHdrModeField is not { FieldType.IsEnum: true })
        {
            mapHdrModeField = null;
            Log.Warning($"[RimLumen.Adapter.RimUnity] RimUnity {Version} (provided by {provider.Name}) has no enum field " +
                "RimUnityMod.Bloom.mapHdrMode; the Map HDR conflict notice is disabled.");
        }
    }

    /// <summary>
    /// RimUnity's own runtime selection adds the one selected <c>RimUnity</c> assembly to its provider's
    /// <c>loadedAssemblies</c> (standalone mod, or the embedding mod), so ownership there identifies both forms.
    /// </summary>
    public static RimUnityRuntime? Find()
    {
        foreach (var mod in LoadedModManager.RunningMods)
        {
            foreach (var assembly in mod.assemblies.loadedAssemblies)
            {
                if (assembly.GetName().Name != RuntimeAssemblyName) continue;
                var modType = assembly.GetType(ModTypeName);
                if (modType != null) return new RimUnityRuntime(assembly, modType, mod);
            }
        }
        return null;
    }

    /// <summary>
    /// Read-only: the current <c>MapHdrMode</c> name (<c>None</c>, <c>Legacy</c>, <c>Experiment</c>).
    /// A missing settings instance reads as <c>None</c>, the same fallback RimUnity's own hooks use.
    /// Only valid when <see cref="CanReadMapHdrMode"/> is true.
    /// </summary>
    public string ReadMapHdrMode()
    {
        object? settings = bloomProperty!.GetValue(null);
        return settings == null ? MapHdrNoticeRule.Off : mapHdrModeField!.GetValue(settings)!.ToString()!;
    }
}
