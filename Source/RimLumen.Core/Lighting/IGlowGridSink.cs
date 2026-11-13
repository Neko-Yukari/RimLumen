using Verse;

namespace RimLumen.Core.Lighting;

/// <summary>
/// Thin seam over <c>Verse.GlowGrid.RegisterCustomGlower</c> / <c>DeregisterCustomGlower</c>
/// (Assembly-CSharp:69647-69683) so the driver can be exercised without a live map.
/// </summary>
public interface IGlowGridSink
{
    // Requires: glowRadius/overlightRadius are finite; the caller has already clamped them to [0, 40]
    //           (GlowGrid clamps glowRadius itself but leaves overlightRadius untouched).
    // Ensures: returns a non-negative custom-glower handle that stays valid until Deregister.
    int Register(IntVec3 position, float glowRadius, ColorInt glowColor, float overlightRadius, bool isCavePlant = false);

    // Requires: handle came from Register on the same sink.
    // Ensures: removes the glower; unknown handles are ignored by GlowGrid.
    void Deregister(int handle);
}
