using System;
using Verse;

namespace RimLumen.Core.Lighting;

/// <summary>
/// <see cref="IGlowGridSink"/> over the live map's <c>GlowGrid</c>.
/// The grid is created during map initialization, so it is resolved on every call rather than cached.
/// </summary>
public sealed class VerseGlowGridSink : IGlowGridSink
{
    private readonly Map map;

    // Requires: map is non-null.
    public VerseGlowGridSink(Map map) => this.map = map;

    // Requires: map.glowGrid is created (always true once the map is running; false for a bare
    //           `new Map()` used offline).
    // Ensures: forwards to GlowGrid.RegisterCustomGlower and returns its handle.
    // Ensures: throws InvalidOperationException when map.glowGrid is still null, rather than silently
    //          pretending the light exists.
    public int Register(IntVec3 position, float glowRadius, ColorInt glowColor, float overlightRadius, bool isCavePlant = false)
    {
        var grid = map.glowGrid ?? throw new InvalidOperationException("The map's GlowGrid has not been initialized.");
        return grid.RegisterCustomGlower(position, glowRadius, glowColor, overlightRadius, isCavePlant);
    }

    // Ensures: forwards to GlowGrid.DeregisterCustomGlower; no-op when map.glowGrid is null (nothing
    //          was ever registered in that state).
    public void Deregister(int handle) => map.glowGrid?.DeregisterCustomGlower(handle);
}
