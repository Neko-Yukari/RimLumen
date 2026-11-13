using System.Collections.Generic;
using Verse;

namespace RimLumen.Core.Lighting;

/// <summary>
/// Owns the per-map <see cref="MapGlowerDriver"/> and its lifecycle. RimWorld creates one
/// <see cref="MapComponent"/> per map, so this component is the map-unload anchor that guarantees
/// every custom glower is deregistered.
/// </summary>
public sealed class LumenMapLightingComponent : MapComponent
{
    private readonly MapGlowerDriver driver;

    // Requires: map is non-null. Uses a VerseGlowGridSink over map.glowGrid.
    public LumenMapLightingComponent(Map map) : base(map) => driver = new MapGlowerDriver(new VerseGlowGridSink(map));

    // Requires: map and sink are non-null. Injection seam for tests and alternate backends.
    public LumenMapLightingComponent(Map map, IGlowGridSink sink) : base(map) => driver = new MapGlowerDriver(sink);

    public MapGlowerDriver Driver => driver;

    // Ensures: publishes this map's driver in LumenMapLightingRegistry (idempotent).
    public override void FinalizeInit() => LumenMapLightingRegistry.Register(map.uniqueID, driver);

    // Ensures: deregisters every glower (Driver.Dispose) and removes the map from the registry.
    public override void MapRemoved()
    {
        driver.Dispose();
        LumenMapLightingRegistry.Unregister(map.uniqueID, driver);
    }

    // Ensures: no-op; RimLumen lights are runtime-only and never persisted in the save.
    public override void ExposeData() { }
}

/// <summary>
/// Map-id keyed registry of <see cref="MapGlowerDriver"/> instances. Negative or unknown map indices
/// resolve to the most recently registered map, because <see cref="Contracts.LumenPlayRequest.MapIndex"/>
/// defaults to -1 for "whatever map is being played".
/// </summary>
public static class LumenMapLightingRegistry
{
    private static readonly Dictionary<int, MapGlowerDriver> drivers = new();
    private static readonly List<int> registrationOrder = new();

    // Requires: mapUniqueId is the Verse.Map.uniqueID (-1 is a valid key for maps that are not saved yet).
    // Ensures: a later Register for the same key replaces the driver and makes it the most recent one.
    public static void Register(int mapUniqueId, MapGlowerDriver driver)
    {
        drivers[mapUniqueId] = driver;
        registrationOrder.Remove(mapUniqueId);
        registrationOrder.Add(mapUniqueId);
    }

    // Ensures: removes the entry only when it still holds that exact driver instance; idempotent.
    public static void Unregister(int mapUniqueId, MapGlowerDriver driver)
    {
        if (drivers.TryGetValue(mapUniqueId, out var current) && ReferenceEquals(current, driver))
        {
            drivers.Remove(mapUniqueId);
            registrationOrder.Remove(mapUniqueId);
        }
    }

    // Ensures: true with the driver for mapIndex, or with the most recently registered driver when
    //          mapIndex is negative or unknown; false with driver == null when nothing is registered.
    public static bool TryResolve(int mapIndex, out MapGlowerDriver? driver)
    {
        if (drivers.TryGetValue(mapIndex, out driver))
        {
            return true;
        }
        if (registrationOrder.Count == 0)
        {
            return false;
        }
        driver = drivers[registrationOrder[^1]];
        return true;
    }

    // Ensures: number of registered maps.
    public static int Count => drivers.Count;
}
