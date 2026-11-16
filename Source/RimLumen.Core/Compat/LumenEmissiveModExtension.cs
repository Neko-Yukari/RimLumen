using System.Collections.Generic;
using Verse;

namespace RimLumen.Core.Compat;

// Per-def opt-in/out for the optional vanilla additive HDR redraw
// (RimLumenRenderHost.EnableVanillaAdditiveBloom, default off).
public class LumenEmissiveModExtension : DefModExtension
{
    public bool EnableHdrBloom = true;
    private string? hdrMultiplier = null;

    public override IEnumerable<string> ConfigErrors()
    {
        if (hdrMultiplier is not null)
            yield return "LumenEmissiveModExtension: 'hdrMultiplier' was removed.";
    }
}
