using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimLumen.Core.Lighting;

// XML: <li Class="RimLumen.Core.Lighting.CompProperties_RimLumenLogicalLight"><glowRadius>6</glowRadius></li>
public sealed class CompProperties_RimLumenLogicalLight : CompProperties
{
    // Cells, finite and in (0, 40] like CompProperties_Glower.glowRadius.
    public float glowRadius = 6f;

    public CompProperties_RimLumenLogicalLight()
    {
        compClass = typeof(CompRimLumenLogicalLight);
    }

    public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
    {
        foreach (string error in base.ConfigErrors(parentDef)) yield return error;
        if (!LogicalLight.IsValidRadius(glowRadius))
            yield return $"{parentDef.defName}: CompProperties_RimLumenLogicalLight.glowRadius must be finite and in (0, {GlowGrid.MaxLightRadius}], got {glowRadius}.";
    }
}

// Gameplay-only light: raises GlowGrid.GroundGlowAt (mood, work speed, plant growth, the light readout) exactly
// like a white, non-overlit CompGlower of the same radius, walls included, but never enters GlowGrid itself, so
// VisualGlowAt, the vanilla LightOverlay and RimLumen's map light field stay dark. Pair it with a RimLumen
// pointLight effect for the visible pool. It is lit under CompGlower's rules (power, flick, fuel, schedule and
// every other IThingGlower) and reacts to the same comp signals.
public sealed class CompRimLumenLogicalLight : ThingComp
{
    private LogicalGlowGrid? registeredIn;

    public CompProperties_RimLumenLogicalLight Props => (CompProperties_RimLumenLogicalLight)props;

    // CompGlower.ShouldBeLitNow.
    private bool ShouldBeLitNow
    {
        get
        {
            if (!parent.Spawned || !FlickUtility.WantsToBeOn(parent)) return false;
            if (parent is IThingGlower thingGlower && !thingGlower.ShouldBeLitNow()) return false;
            foreach (ThingComp comp in parent.AllComps)
            {
                if (comp is IThingGlower compGlower && !compGlower.ShouldBeLitNow()) return false;
            }

            return true;
        }
    }

    public override void PostSpawnSetup(bool respawningAfterLoad)
    {
        if (!parent.BeingTransportedOnGravship) Refresh();
    }

    public override void PostSwapMap() => Refresh();

    public override void PostMapInit() => Refresh();

    public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
    {
        base.PostDeSpawn(map, mode);
        Refresh();
    }

    public override void ReceiveCompSignal(string signal)
    {
        switch (signal)
        {
            case "PowerTurnedOn":
            case "PowerTurnedOff":
            case "FlickedOn":
            case "FlickedOff":
            case "Refueled":
            case "RanOutOfFuel":
            case "ScheduledOn":
            case "ScheduledOff":
            case "MechClusterDefeated":
            case "Hacked":
            case "RitualTargetChanged":
            case "CrateContentsChanged":
            case "Vacuum":
                Refresh();
                break;
        }
    }

    // Ensures: the light is registered at the parent's current cell in exactly the current map's field while
    //          ShouldBeLitNow holds, and in no field otherwise.
    private void Refresh()
    {
        LogicalGlowGrid? target = ShouldBeLitNow ? LogicalLightHooks.GridOf(parent.Map) : null;
        if (registeredIn != null && registeredIn != target) registeredIn.Remove(parent.thingIDNumber);
        target?.Set(parent.thingIDNumber, new LogicalLight(parent.Position.x, parent.Position.z, Props.glowRadius));
        registeredIn = target;
    }
}
