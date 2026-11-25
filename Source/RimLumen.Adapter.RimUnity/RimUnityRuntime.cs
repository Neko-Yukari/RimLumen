using System;
using System.Linq.Expressions;
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
    private readonly Func<bool> isMapHdrOff;
    private readonly Func<int> readEffectiveBloomLayerMask;

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
            // Fail closed for HDR takeover when this RimUnity version cannot report its Map HDR mode.
            isMapHdrOff = static () => false;
            readEffectiveBloomLayerMask = static () => ~0;
            Log.Warning($"[RimLumen.Adapter.RimUnity] RimUnity {Version} (provided by {provider.Name}) has no enum field " +
                "RimUnityMod.Bloom.mapHdrMode; the Map HDR conflict notice and external HDR takeover are disabled.");
            return;
        }
        isMapHdrOff = BuildIsMapHdrOff(bloomProperty!, mapHdrModeField, Version, provider.Name);
        readEffectiveBloomLayerMask = BuildReadEffectiveBloomLayerMask(bloomProperty!, Version, provider.Name);
    }

    private static Func<bool> BuildIsMapHdrOff(PropertyInfo bloom, FieldInfo field, string version, string providerName)
    {
        if (!Enum.TryParse(field.FieldType, MapHdrNoticeRule.Off, out object? offValue) || offValue is null)
        {
            Log.Warning($"[RimLumen.Adapter.RimUnity] RimUnity {version} (provided by {providerName}) enum " +
                $"{field.FieldType.FullName} has no '{MapHdrNoticeRule.Off}' value; assuming Map HDR may be active.");
            return static () => false;
        }
        var settings = Expression.Variable(bloom.PropertyType, "settings");
        var assign = Expression.Assign(settings, Expression.Property(null, bloom));
        var isNull = Expression.Equal(settings, Expression.Constant(null, bloom.PropertyType));
        var isOff = Expression.Equal(Expression.Field(settings, field), Expression.Constant(offValue, field.FieldType));
        var body = Expression.Block([settings], assign, Expression.OrElse(isNull, isOff));
        return Expression.Lambda<Func<bool>>(body).Compile();
    }

    private static Func<int> BuildReadEffectiveBloomLayerMask(PropertyInfo bloom, string version, string providerName)
    {
        PropertyInfo? effectiveMask = bloom.PropertyType.GetProperty("EffectiveLayerMask",
            BindingFlags.Public | BindingFlags.Instance);
        if (effectiveMask is null || effectiveMask.PropertyType != typeof(int) || effectiveMask.GetMethod is null)
        {
            Log.Warning($"[RimLumen.Adapter.RimUnity] RimUnity {version} (provided by {providerName}) has no int property " +
                "RimUnityMod.Bloom.EffectiveLayerMask; assuming all layers may be claimed by RimUnity Bloom.");
            return static () => ~0;
        }
        var settings = Expression.Variable(bloom.PropertyType, "settings");
        var assign = Expression.Assign(settings, Expression.Property(null, bloom));
        var value = Expression.Condition(
            Expression.Equal(settings, Expression.Constant(null, bloom.PropertyType)),
            Expression.Constant(0),
            Expression.Property(settings, effectiveMask));
        var body = Expression.Block([settings], assign, value);
        return Expression.Lambda<Func<int>>(body).Compile();
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

    /// <summary>Zero-allocation per-frame check for whether RimUnity's own Map HDR mode is off (<c>None</c>).</summary>
    public bool IsMapHdrOff() => isMapHdrOff();

    /// <summary>Zero-allocation per-frame read of RimUnity's independent per-layer Bloom mask (<c>EffectiveLayerMask</c>).</summary>
    public int ReadEffectiveBloomLayerMask() => readEffectiveBloomLayerMask();
}
