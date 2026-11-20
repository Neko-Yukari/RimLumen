using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Contracts;
using Verse;

namespace RimLumen.Core.ParticleWorld;

// Host inputs are captured once for both backends. Explosion time is the game tick clock, so pulses neither
// age during pause nor linger until the next effect is played when there were no active particle contributors.
internal static class ParticleEnvironment
{
    private readonly record struct Pulse(int MapId, Vector3 Center, float Radius, int StartTick);
    private static readonly List<Pulse> Pulses = new();
    private static readonly Dictionary<int, MapEnvironmentSources> SourcesByMap = new();
    private static ulong? capturedFrame;
    internal const int MaxPulsesPerMap = 8;
    internal static ParticleCursorMode CursorMode;
    internal static float CursorRadius = 5f;
    internal static float CursorStrength = 4f;

    internal static void RecordExplosion(int mapId, Vector3 center, float radius)
    {
        if (radius <= 0f) return;
        int tick = Find.TickManager.TicksGame;
        RemoveExpiredPulses(tick);
        int count = 0, oldest = -1;
        for (int i = 0; i < Pulses.Count; i++)
            if (Pulses[i].MapId == mapId) { if (oldest < 0) oldest = i; count++; }
        if (count == MaxPulsesPerMap) Pulses.RemoveAt(oldest);
        Pulses.Add(new(mapId, center, radius, tick));
    }

    private static void RemoveExpiredPulses(int tick)
    {
        for (int i = Pulses.Count - 1; i >= 0; i--)
            if (tick - Pulses[i].StartTick > 30) Pulses.RemoveAt(i);
    }

    internal static void BeginFrame(ulong frameIndex, int currentMapId)
    {
        if (capturedFrame == frameIndex) return;
        Vector3? cursor = null;
        if (CursorMode != ParticleCursorMode.Off && currentMapId >= 0)
        {
            UnityEngine.Vector3 mouse = UI.MouseMapPosition();
            cursor = new(mouse.x, 0f, mouse.z);
        }
        CaptureSources(frameIndex, currentMapId, cursor, Pulses.Count != 0 ? Find.TickManager.TicksGame : 0);
    }

    // Host input boundary: freeze the sampled cursor and game tick before either simulator visits its emitters.
    internal static void CaptureSources(ulong frameIndex, int currentMapId, Vector3? cursor, int tick)
    {
        capturedFrame = frameIndex;
        foreach (MapEnvironmentSources sources in SourcesByMap.Values) sources.Clear();
        RemoveExpiredPulses(tick);
        foreach (Pulse pulse in Pulses)
            Sources(pulse.MapId).Add(ParticleEnvironmentMath.Explosion(pulse.Center, pulse.Radius,
                (tick - pulse.StartTick) / 60f));
        if (cursor is Vector3 position)
            Sources(currentMapId).Add(new(LumenEnvironmentChannel.Cursor, position, CursorRadius,
                CursorMode == ParticleCursorMode.Attract ? -CursorStrength : CursorStrength, 0f, 0f));
    }

    // Main-thread views are stable for all L1/L2 captures of this frame, not for render-thread recording.
    internal static ReadOnlyMemory<ParticleEnvironmentSource> ForMap(int mapId, LumenEnvironmentChannel channels) =>
        SourcesByMap.TryGetValue(mapId, out var sources) ? sources.Select(channels) : default;

    private static MapEnvironmentSources Sources(int mapId)
    {
        if (!SourcesByMap.TryGetValue(mapId, out var sources)) SourcesByMap.Add(mapId, sources = new());
        return sources;
    }

    internal static void ForgetMap(int mapId)
    {
        Pulses.RemoveAll(p => p.MapId == mapId);
        SourcesByMap.Remove(mapId);
    }
}

// BeginFrame appends all explosions, then at most one cursor. Each channel mask selects a contiguous slice.
internal sealed class MapEnvironmentSources
{
    private readonly ParticleEnvironmentSource[] items = new ParticleEnvironmentSource[ParticleEnvironment.MaxPulsesPerMap + 1];
    private int explosions, count;

    internal void Clear() => explosions = count = 0;

    internal void Add(ParticleEnvironmentSource source)
    {
        items[count++] = source;
        if (source.Channel == LumenEnvironmentChannel.Explosion) explosions++;
    }

    internal ReadOnlyMemory<ParticleEnvironmentSource> Select(LumenEnvironmentChannel channels)
    {
        int start = (channels & LumenEnvironmentChannel.Explosion) != 0 ? 0 : explosions;
        int end = (channels & LumenEnvironmentChannel.Cursor) != 0 ? count : explosions;
        return items.AsMemory(start, end - start);
    }
}
