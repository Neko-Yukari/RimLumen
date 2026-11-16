using System;
using System.Collections.Generic;
using RimLumen.Core.Mod;
using RimWorld;
using Verse;

namespace RimLumen.Core.Demo;

// Publishes the optional demo buildings into the vanilla Joy build menu and
// without reflection: the ThingDefs are authored with canGenerateDefaultDesignator=false, so vanilla's
// DesignationCategoryDef.ResolveDesignators never adds them, and this registry owns the Designator_Build
// entries in DesignationCategoryDef.AllResolvedDesignators.
public static class DemoBuildingRegistry
{
    internal const string FireworkDefName = "RimLumen_FireworkLauncher";
    internal const string FountainDefName = "RimLumen_ParticleFountain";

    private static readonly string[] DemoDefNames = { FireworkDefName, FountainDefName };

    // One cached Designator_Build per def: the menu mutates the same list vanilla populated, so the
    // instances are reused across toggles instead of leaking a fresh designator per settings frame.
    private static readonly Dictionary<string, Designator_Build> CachedDesignators =
        new(StringComparer.Ordinal);

    // Requires: main thread.
    // Ensures: returns false and changes nothing when the def database is not loaded yet (offline tests,
    //          or the settings load that runs before XML defs resolve) or when no demo def resolves,
    //          otherwise makes every demo def's presence in the Joy category match <paramref name="enabled"/>.
    public static bool SyncWithSettings(bool enabled)
    {
        DesignationCategoryDef? joy = DefDatabase<DesignationCategoryDef>.GetNamedSilentFail("Joy");
        if (joy is null) return false;

        List<Designator> designators = joy.AllResolvedDesignators;
        bool synced = false;
        foreach (string defName in DemoDefNames)
        {
            synced |= SyncDesignator(designators, defName, enabled);
        }

        return synced;
    }

    private static bool SyncDesignator(List<Designator> designators, string defName, bool enabled)
    {
        ThingDef? def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
        if (def is null) return false;

        // Ideology asks this flag before a pawn is allowed to build a blueprint (Ideo.MembersCanBuild),
        // and vanilla read it during ResolveReferences, which has already run by now: mirroring the
        // feature state keeps the demo buildings buildable on non-classic ideos and hidden otherwise.
        def.canGenerateDefaultDesignator = enabled;

        if (!enabled)
        {
            for (int i = designators.Count - 1; i >= 0; i--)
            {
                if (designators[i] is Designator_Build build && ReferenceEquals(build.PlacingDef, def))
                {
                    designators.RemoveAt(i);
                }
            }

            return true;
        }

        foreach (Designator designator in designators)
        {
            if (designator is Designator_Build existing && ReferenceEquals(existing.PlacingDef, def)) return true;
        }

        if (!CachedDesignators.TryGetValue(defName, out Designator_Build? cached))
        {
            CachedDesignators[defName] = cached = new Designator_Build(def);
        }

        designators.Add(cached);
        return true;
    }
}

// Static constructors run from StaticConstructorOnStartupUtility.CallAll(), which LongEventHandler runs
// after every def's ResolveReferences (and therefore after Joy.ResolveDesignators has populated the list),
// so queueing here lands the sync on the finished list.
[StaticConstructorOnStartup]
internal static class DemoBuildingRegistryStartupHook
{
    static DemoBuildingRegistryStartupHook()
    {
        LongEventHandler.ExecuteWhenFinished(
            () => DemoBuildingRegistry.SyncWithSettings(RimLumenMod.Settings.EnableDemoBuildings));
    }
}
