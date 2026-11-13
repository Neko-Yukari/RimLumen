using Verse;

namespace RimLumen.Core.Compat;

// Per-def opt-in/out and intensity for the optional vanilla additive HDR redraw
// (RimLumenRenderHost.EnableVanillaAdditiveBloom, default off).
public class LumenEmissiveModExtension : DefModExtension
{
    public bool EnableHdrBloom = true;
    public float HdrMultiplier = 2.0f;
}
