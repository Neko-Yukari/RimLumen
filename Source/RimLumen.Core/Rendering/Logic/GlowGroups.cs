using System;

namespace RimLumen.Core.Rendering.Logic;

internal enum GlowGroup : byte
{
    Early,
    BeforeWeather,
    BeforeGravship,
    MapEnd
}

[Flags]
internal enum GlowGroupMask : byte
{
    None = 0,
    Early = 1,
    BeforeWeather = 2,
    BeforeGravship = 4,
    MapEnd = 8
}

internal readonly record struct GlowQueueRange(int MinInclusive, int MaxExclusive);

internal static class GlowGroups
{
    // Requires: effectiveQueue belongs to a drawable MapMain command (< int.MaxValue).
    // Ensures: partitions at 3152, 3169 and 3178; the remaining commands belong to MapEnd.
    internal static GlowGroup Classify(int effectiveQueue) => effectiveQueue switch
    {
        < 3152 => GlowGroup.Early,
        < 3169 => GlowGroup.BeforeWeather,
        < 3178 => GlowGroup.BeforeGravship,
        _ => GlowGroup.MapEnd
    };

    // Ensures: the group's half-open source range; MaxExclusive is also its sentinel queue.
    internal static GlowQueueRange Range(GlowGroup group) => group switch
    {
        GlowGroup.Early => new(int.MinValue, 3152),
        GlowGroup.BeforeWeather => new(3152, 3169),
        GlowGroup.BeforeGravship => new(3169, 3178),
        GlowGroup.MapEnd => new(3178, int.MaxValue),
        _ => throw new ArgumentOutOfRangeException(nameof(group))
    };

    // Ensures: the single presence bit for this group, derived from final captured draws.
    internal static GlowGroupMask Mask(GlowGroup group) => (GlowGroupMask)(1 << (int)group);

    // Requires: bloomMipLevels is the validated active-pack value; earlyWork includes compute-only work.
    // Ensures: Early follows earlyWork; later groups require their captured bit and positive mip levels.
    internal static bool ShouldSplit(GlowGroup group, GlowGroupMask captured, bool earlyWork, int bloomMipLevels)
        => group == GlowGroup.Early ? earlyWork : bloomMipLevels > 0 && (captured & Mask(group)) != 0;
}
