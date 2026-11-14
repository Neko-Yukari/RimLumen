using RimLumen.Core.Rendering;
using RimWorld;
using Verse;

namespace RimLumen.Core.Lighting;

public sealed class SectionLayer_RimLumenLightField : SectionLayer
{
    public SectionLayer_RimLumenLightField(Section section)
        : base(section)
    {
        relevantChangeTypes = (ulong)MapMeshFlagDefOf.Roofs
            | (ulong)MapMeshFlagDefOf.GroundGlow
            | (ulong)MapMeshFlagDefOf.FogOfWar
            | (ulong)MapMeshFlagDefOf.Buildings;
    }

    public override bool Visible => RimLumenRenderHost.IsMapLightFieldActive;

    public override void DrawLayer()
    {
    }

    public override void Regenerate()
    {
        if (!RimLumenRenderHost.IsMapLightFieldActive)
            return;
        MapLightFieldCollector.UpdateSection(section.map, section.CellRect);
    }
}
