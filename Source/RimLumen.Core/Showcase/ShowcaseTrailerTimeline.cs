using System;
using System.Collections.Generic;
using RimLumen.Core.Mod;

namespace RimLumen.Core.Showcase;

internal enum TrailerCueKind
{
    SegmentAStart,
    StartStation,
    LaunchFirework,
    Freeze,
    SegmentBStart,
    SwitchPack,
    ArrowsOn,
    End,
}

// Arg: station index for StartStation, firework slot for LaunchFirework, pack id for SwitchPack.
internal readonly record struct TrailerCue(float Time, TrailerCueKind Kind, int Index = 0, string? PackId = null);

// The fixed trailer storyboard; seconds count from the end of the lead-in. Camera X offsets are cells from the showcase
// arena centre, so a take reads the same on every map size.
internal static class ShowcaseTrailerTimeline
{
    internal const float LeadInSeconds = 1f;
    // Segment A pans until PanSeconds, then holds on the fireworks until the last one has faded (FireworkVisibleSeconds
    // after its launch) before the freeze frame.
    internal const float PanSeconds = 12f;
    internal const float SegmentAEnd = 13f;
    internal const float FreezeEnd = SegmentAEnd + 1f;
    internal const float SegmentBEnd = FreezeEnd + 10f;

    // Segment A pans at constant speed from PanStartX to PanEndX at zoom PanRootSize.
    internal const float PanStartX = -24f;
    internal const float PanEndX = 24f;
    internal const float PanRootSize = 9f;
    // A station starts when the camera centre is this many cells short of it, so it lights up on screen right of centre.
    internal const float StationLead = 6f;
    internal static readonly float[] StationOffsetsX = { -25f, -15f, -5f, 5f, 15f, 25f };

    internal const float FireworkTime = 9f;
    internal const float FireworkStagger = 0.12f;
    // Firework ground origins relative to the arena centre (cells); they climb towards +Z on screen.
    internal static readonly (float X, float Z)[] FireworkOffsets = { (14f, -4f), (18f, -2f), (22f, -5f), (26f, -3f), (30f, -4f) };

    // Segment B camera, relative to the arena centre, and its zoom.
    internal const float VortexCameraX = 0.5f;
    internal const float VortexCameraZ = 1.0f;
    // Vanilla's closest zoom (CameraMapConfig.sizeRange.min). Below it the camera drops under altitude 15 and its 0.5 near
    // plane clips the upper altitude layers: MoteOverhead (RimLumen's HDR layer) and the lighting overlay.
    internal const float VortexRootSize = 11f;

    internal static readonly TrailerCue[] Cues = Build();

    // Ensures: camera X offset of segment A at take time t, clamped to the pan's end points.
    internal static float PanX(float t) =>
        PanStartX + (PanEndX - PanStartX) * Math.Clamp(t / PanSeconds, 0f, 1f);

    private static TrailerCue[] Build()
    {
        var cues = new List<TrailerCue> { new(0f, TrailerCueKind.SegmentAStart) };
        float speed = (PanEndX - PanStartX) / PanSeconds;
        for (int i = 0; i < StationOffsetsX.Length; i++)
            cues.Add(new(Math.Max(0f, (StationOffsetsX[i] - StationLead - PanStartX) / speed), TrailerCueKind.StartStation, i));
        for (int i = 0; i < FireworkOffsets.Length; i++)
            cues.Add(new(FireworkTime + i * FireworkStagger, TrailerCueKind.LaunchFirework, i));
        cues.Add(new(SegmentAEnd, TrailerCueKind.Freeze));
        cues.Add(new(FreezeEnd, TrailerCueKind.SegmentBStart));
        cues.Add(new(FreezeEnd, TrailerCueKind.SwitchPack, PackId: ShaderPackCatalogIds.Standard));
        cues.Add(new(FreezeEnd + 2f, TrailerCueKind.SwitchPack, PackId: ShaderPackCatalogIds.Crisp));
        cues.Add(new(FreezeEnd + 4f, TrailerCueKind.SwitchPack, PackId: ShaderPackCatalogIds.Foggy));
        cues.Add(new(FreezeEnd + 5f, TrailerCueKind.ArrowsOn));
        cues.Add(new(SegmentBEnd, TrailerCueKind.End));
        // Stable: cues at the same time keep their listed order (segment start before its pack switch).
        TrailerCue[] sorted = cues.ToArray();
        int[] order = new int[sorted.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => sorted[a].Time != sorted[b].Time ? sorted[a].Time.CompareTo(sorted[b].Time) : a.CompareTo(b));
        return Array.ConvertAll(order, i => sorted[i]);
    }
}

// Built-in pack ids the trailer switches between (folder ids under 1.7/ShaderPacks plus the default).
internal static class ShaderPackCatalogIds
{
    internal const string Standard = ShaderPackCatalog.DefaultPackId;
    internal const string Crisp = "crisp";
    internal const string Foggy = "foggy";
}
