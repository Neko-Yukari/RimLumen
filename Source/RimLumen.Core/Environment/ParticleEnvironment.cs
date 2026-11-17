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
    private static readonly List<ParticleEnvironmentSource> Sources = new();
    private static ulong? capturedFrame;
    private static Vector3 cursor;
    private static int cursorMap = -1;
    internal const int MaxPulsesPerMap = 8;
    internal static bool ExplosionsEnabled = true;
    internal static float ExplosionForceScale = 1f;
    internal static ParticleCursorMode CursorMode;
    internal static float CursorRadius = 5f;
    internal static float CursorStrength = 4f;

    internal static void RecordExplosion(int mapId, Vector3 center, float radius)
    {
        if (!ExplosionsEnabled || radius <= 0f) return;
        int tick = Find.TickManager.TicksGame;
        Pulses.RemoveAll(p => tick - p.StartTick > 30);
        int count = 0, oldest = -1;
        for (int i = 0; i < Pulses.Count; i++)
            if (Pulses[i].MapId == mapId) { if (oldest < 0) oldest = i; count++; }
        if (count == MaxPulsesPerMap) Pulses.RemoveAt(oldest);
        Pulses.Add(new(mapId, center, radius, tick));
    }

    internal static void BeginFrame(ulong frameIndex, int currentMapId)
    {
        if (capturedFrame == frameIndex) return;
        capturedFrame = frameIndex;
        if (Pulses.Count != 0)
        {
            int tick = Find.TickManager.TicksGame;
            Pulses.RemoveAll(p => tick - p.StartTick > 30);
        }
        cursorMap = -1;
        if (CursorMode == ParticleCursorMode.Off || currentMapId < 0) return;
        UnityEngine.Vector3 mouse = UI.MouseMapPosition();
        cursor = new(mouse.x, 0f, mouse.z);
        cursorMap = currentMapId;
    }

    // Consumed immediately during capture; the span is reused by the next emitter, never retained by a frame.
    internal static ReadOnlySpan<ParticleEnvironmentSource> ForMap(int mapId)
    {
        Sources.Clear();
        if (ExplosionsEnabled && Pulses.Count != 0)
        {
            int tick = Find.TickManager.TicksGame;
            foreach (Pulse pulse in Pulses)
                if (pulse.MapId == mapId)
                    Sources.Add(ParticleEnvironmentMath.Explosion(pulse.Center, pulse.Radius,
                        (tick - pulse.StartTick) / 60f, ExplosionForceScale));
        }
        if (mapId >= 0 && cursorMap == mapId)
            Sources.Add(new(LumenEnvironmentChannel.Cursor, cursor, CursorRadius,
                CursorMode == ParticleCursorMode.Attract ? -CursorStrength : CursorStrength, 0f, 0f));
        return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Sources);
    }

    internal static void ForgetMap(int mapId) => Pulses.RemoveAll(p => p.MapId == mapId);
}
