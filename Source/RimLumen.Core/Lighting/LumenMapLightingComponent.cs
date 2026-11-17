using System;
using System.Collections.Generic;
using Verse;

namespace RimLumen.Core.Lighting;

/// <summary>
/// Owns the per-map <see cref="MapGlowerDriver"/> and its lifecycle. RimWorld creates one
/// <see cref="MapComponent"/> per map, so this component is the map-unload anchor that guarantees
/// every custom glower is deregistered. It implements <see cref="IDisposable"/> as well as
/// <c>MapRemoved</c> because <c>Map.Dispose</c> (AC:77526) only disposes components that implement
/// IDisposable and never calls MapRemoved when the player returns to the main menu.
/// </summary>
public sealed class LumenMapLightingComponent : MapComponent, IDisposable
{
    private readonly MapGlowerDriver driver;
    private bool disposed;

    // Requires: map is non-null. Uses a VerseGlowGridSink over map.glowGrid.
    public LumenMapLightingComponent(Map map) : base(map) => driver = new MapGlowerDriver(new VerseGlowGridSink(map));

    public MapGlowerDriver Driver => driver;

    // Ensures: publishes this map's driver in LumenMapLightingRegistry (idempotent).
    public override void FinalizeInit() => LumenMapLightingRegistry.Register(map.uniqueID, driver);

    // Ensures: deregisters every glower (Driver.Dispose) and removes the map from the registry.
    public override void MapRemoved() => Dispose();

    // Ensures: same teardown as MapRemoved, idempotent, so Map.Dispose also releases every glower.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        driver.Dispose();
        LumenMapLightingRegistry.Unregister(map.uniqueID, driver);
    }

    // Ensures: no-op; RimLumen lights are runtime-only and never persisted in the save.
    public override void ExposeData() { }
}

/// <summary>
/// Exact RimWorld Map.uniqueID lookup for <see cref="MapGlowerDriver"/> instances.
/// Play resolves its implicit current-map request before accessing this registry; registration order
/// never determines which map receives an effect's lights.
/// </summary>
public static class LumenMapLightingRegistry
{
    private static readonly Dictionary<int, MapGlowerDriver> drivers = new();

    // Requires: mapUniqueId is the Verse.Map.uniqueID (-1 is a valid key for maps that are not saved yet).
    // Ensures: a later Register for the same key replaces only that map's driver.
    public static void Register(int mapUniqueId, MapGlowerDriver driver)
    {
        drivers[mapUniqueId] = driver;
    }

    // Ensures: removes the entry only when it still holds that exact driver instance; idempotent.
    public static void Unregister(int mapUniqueId, MapGlowerDriver driver)
    {
        if (drivers.TryGetValue(mapUniqueId, out var current) && ReferenceEquals(current, driver))
        {
            drivers.Remove(mapUniqueId);
        }
    }

    // Ensures: resolves only the exact map key; an unloaded map never migrates its lights onto another
    //          map. Even negative keys are exact lookups, never aliases for the current/last map.
    public static bool TryResolve(int mapIndex, out MapGlowerDriver? driver)
        => drivers.TryGetValue(mapIndex, out driver);

    // Ensures: number of registered maps.
    public static int Count => drivers.Count;
}
