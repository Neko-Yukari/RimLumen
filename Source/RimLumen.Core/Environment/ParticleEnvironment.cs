using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Rendering;
using Verse;

namespace RimLumen.Core.ParticleWorld;

internal readonly record struct HostMouseSample(Vector2 WorldGroundXZ, Vector2 ScreenHeightPosition);

internal readonly record struct FrozenExplosionPulse(
    uint Id,
    byte Slot,
    LumenEnvironmentForceShape Shape,
    Vector2 CenterXZ,
    float Radius,
    float AbsStrength,
    float WaveRadius,
    float ShellHalfThickness);

// Host inputs and sustained/transient environment sources are captured once per frame on the main thread
// before effect handles, L1, or L2 run. Idle frames do zero mouse/window reads and zero allocations.
internal static class ParticleEnvironment
{
    internal const int MaxPulsesPerMap = 8;
    internal const int MaxSustainedSourcesPerMap = 8;
    private const int MaxForcesPerMap = MaxPulsesPerMap + MaxSustainedSourcesPerMap;
    private const long CapWarningCooldownMs = 10_000;
    private const LumenEnvironmentChannel KnownChannels = LumenEnvironmentChannel.Explosion | LumenEnvironmentChannel.Cursor;

    private readonly struct PulseEntry
    {
        public required uint Id { get; init; }
        public required byte Slot { get; init; }
        public required LumenEnvironmentChannel Channel { get; init; }
        public required LumenEnvironmentForceShape Shape { get; init; }
        public required Vector3 CenterWorld { get; init; }
        public required float Radius { get; init; }
        public required float Strength { get; init; }
        public required float Duration { get; init; }
        public required float ShellHalfThickness { get; init; }
        public required bool RealTime { get; init; }
        public required double StartSeconds { get; init; }
    }

    internal sealed class InertEnvironmentSource : ILumenEnvironmentSource
    {
        private readonly LumenEnvironmentChannel channel;
        private readonly bool followHostMouse;

        public InertEnvironmentSource(int mapIndex, LumenEnvironmentChannel channel, bool followHostMouse)
        {
            MapIndex = mapIndex;
            this.channel = channel;
            this.followHostMouse = followHostMouse;
        }

        public bool IsAlive => false;
        public int MapIndex { get; }

        public void SetPosition(Vector3 worldPosition)
        {
            if (followHostMouse)
                throw new InvalidOperationException("Cannot call SetPosition on an environment source with FollowHostMouse = true.");
            if (!IsFinite(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition), "Position components must be finite.");
        }

        public void SetStrength(float strength)
        {
            if (!float.IsFinite(strength))
                throw new ArgumentOutOfRangeException(nameof(strength), "Strength must be finite.");
            if (channel == LumenEnvironmentChannel.None && strength != 0f)
                throw new InvalidOperationException("Cannot set non-zero strength on an environment source with Channel = None.");
        }

        public void SetRadius(float radius)
        {
            if (channel != LumenEnvironmentChannel.None ? !(radius > 0f) || !float.IsFinite(radius) : !float.IsFinite(radius) || radius < 0f)
                throw new ArgumentOutOfRangeException(nameof(radius), channel != LumenEnvironmentChannel.None
                    ? "Radius must be finite and > 0 when Channel is non-zero."
                    : "Radius must be finite and >= 0 when Channel is None.");
        }

        public void Dispose() { }
    }

    private sealed class SustainedSource : ILumenEnvironmentSource
    {
        public required int MapIndex { get; init; }
        public required LumenEnvironmentChannel Channel { get; init; }
        public required LumenEnvironmentForceShape Shape { get; init; }
        public required bool FollowHostMouse { get; init; }
        public required bool TriggersCursorEvents { get; init; }
        public Vector3 PositionWorld;
        public float Radius;
        public float Strength;
        public bool Alive = true;
        public bool HasPreviousWorld;
        public Vector3 PreviousWorld;
        public Vector2 SmoothedVelocity;

        public bool IsAlive => Alive;

        public void SetPosition(Vector3 worldPosition)
        {
            if (FollowHostMouse)
                throw new InvalidOperationException("Cannot call SetPosition on an environment source with FollowHostMouse = true.");
            if (!IsFinite(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition), "Position components must be finite.");
            if (Alive) PositionWorld = worldPosition;
        }

        public void SetStrength(float strength)
        {
            if (!float.IsFinite(strength))
                throw new ArgumentOutOfRangeException(nameof(strength), "Strength must be finite.");
            if (Channel == LumenEnvironmentChannel.None && strength != 0f)
                throw new InvalidOperationException("Cannot set non-zero strength on an environment source with Channel = None.");
            if (Alive) Strength = strength;
        }

        public void SetRadius(float radius)
        {
            if (Channel != LumenEnvironmentChannel.None ? !(radius > 0f) || !float.IsFinite(radius) : !float.IsFinite(radius) || radius < 0f)
                throw new ArgumentOutOfRangeException(nameof(radius), Channel != LumenEnvironmentChannel.None
                    ? "Radius must be finite and > 0 when Channel is non-zero."
                    : "Radius must be finite and >= 0 when Channel is None.");
            if (Alive) Radius = radius;
        }

        public void Dispose()
        {
            if (!Alive) return;
            Alive = false;
            RemoveSustainedSource(this);
        }
    }

    private sealed class MapEnvironmentState
    {
        public readonly PulseEntry[] Pulses = new PulseEntry[MaxPulsesPerMap];
        public int PulseCount;
        public byte FreePulseSlots = 0xFF;

        public readonly List<SustainedSource> SustainedSources = new(MaxSustainedSourcesPerMap);
        public int FollowMouseSourceCount;
        public int FollowMouseCursorEventCount;
        public int CursorEventSubscribers;

        public bool HasPreviousHostMouse;
        public Vector2 PreviousHostMouseScreen;
        public Vector2 HostMouseVelocity;

        private readonly LumenEnvironmentForce[] allForces = new LumenEnvironmentForce[MaxForcesPerMap];
        private readonly LumenEnvironmentForce[] explosionForces = new LumenEnvironmentForce[MaxForcesPerMap];
        private readonly LumenEnvironmentForce[] cursorForces = new LumenEnvironmentForce[MaxForcesPerMap];
        private readonly LumenCursorSnapshot[] cursors = new LumenCursorSnapshot[MaxSustainedSourcesPerMap + 1];
        private readonly FrozenExplosionPulse[] explosionPulses = new FrozenExplosionPulse[MaxPulsesPerMap];
        private int allForceCount;
        private int explosionForceCount;
        private int cursorForceCount;
        private int cursorCount;
        private int explosionPulseCount;

        public bool HasAnyFrozen => allForceCount != 0 || cursorCount != 0 || explosionPulseCount != 0;

        public void ClearFrozen()
        {
            allForceCount = 0;
            explosionForceCount = 0;
            cursorForceCount = 0;
            cursorCount = 0;
            explosionPulseCount = 0;
        }

        public void AddForce(in LumenEnvironmentForce force)
        {
            allForces[allForceCount++] = force;
            if ((force.Channel & LumenEnvironmentChannel.Explosion) != 0)
                explosionForces[explosionForceCount++] = force;
            if ((force.Channel & LumenEnvironmentChannel.Cursor) != 0)
                cursorForces[cursorForceCount++] = force;
        }

        public void AddCursor(in LumenCursorSnapshot cursor) => cursors[cursorCount++] = cursor;

        public void AddExplosionPulse(in FrozenExplosionPulse pulse) => explosionPulses[explosionPulseCount++] = pulse;

        public ReadOnlyMemory<LumenEnvironmentForce> SelectForces(LumenEnvironmentChannel channels) => (channels & KnownChannels) switch
        {
            LumenEnvironmentChannel.None => default,
            LumenEnvironmentChannel.Explosion => explosionForces.AsMemory(0, explosionForceCount),
            LumenEnvironmentChannel.Cursor => cursorForces.AsMemory(0, cursorForceCount),
            _ => allForces.AsMemory(0, allForceCount),
        };

        public ReadOnlySpan<LumenCursorSnapshot> Cursors => cursors.AsSpan(0, cursorCount);

        public ReadOnlySpan<FrozenExplosionPulse> ExplosionPulses => explosionPulses.AsSpan(0, explosionPulseCount);

        public void RemovePulseAt(int index)
        {
            FreePulseSlots |= (byte)(1 << Pulses[index].Slot);
            PulseCount--;
            for (int i = index; i < PulseCount; i++)
                Pulses[i] = Pulses[i + 1];
        }
    }

    private static readonly Dictionary<int, MapEnvironmentState> Maps = new();
    private static ulong? capturedFrame;
    private static double simClock;
    private static double realClock;
    private static uint nextPulseId;
    private static int totalActivePulses;
    private static int totalSustainedSources;
    private static int totalCursorSubscribers;
    private static bool hasFrozenSnapshots;
    private static bool hasWarnedSourceCap;
    private static long lastSourceCapWarningMs;

    internal static void ValidatePulseRequest(in LumenEnvironmentPulseRequest request, out int mapIndex)
    {
        mapIndex = LumenPlayMap.ResolveMapIndex(request.MapIndex,
            request.MapIndex == -1 ? Find.CurrentMap?.uniqueID : null);
        if (request.Channel == LumenEnvironmentChannel.None || (request.Channel & ~KnownChannels) != 0)
            throw new ArgumentException($"Channel must be a non-zero combination of known LumenEnvironmentChannel flags, got {request.Channel}.", nameof(request));
        if (!Enum.IsDefined(request.Shape))
            throw new ArgumentOutOfRangeException(nameof(request), request.Shape, "Unknown LumenEnvironmentForceShape.");
        if (!IsFinite(request.CenterWorld))
            throw new ArgumentOutOfRangeException(nameof(request), "CenterWorld components must be finite.");
        if (!(request.Radius > 0f) || !float.IsFinite(request.Radius))
            throw new ArgumentOutOfRangeException(nameof(request), request.Radius, "Radius must be finite and > 0.");
        if (!float.IsFinite(request.Strength))
            throw new ArgumentOutOfRangeException(nameof(request), request.Strength, "Strength must be finite.");
        if (!(request.Duration > 0f) || !float.IsFinite(request.Duration))
            throw new ArgumentOutOfRangeException(nameof(request), request.Duration, "Duration must be finite and > 0.");
        if (request.Shape == LumenEnvironmentForceShape.SphericalWave)
        {
            if (!(request.ShellHalfThickness > 0f) || !float.IsFinite(request.ShellHalfThickness))
                throw new ArgumentOutOfRangeException(nameof(request), request.ShellHalfThickness, "ShellHalfThickness must be finite and > 0 for SphericalWave.");
        }
        else if (request.ShellHalfThickness != 0f)
        {
            throw new ArgumentException($"ShellHalfThickness is not used by {request.Shape} and must be 0, got {request.ShellHalfThickness}.", nameof(request));
        }
    }

    internal static void ValidateSourceRequest(in LumenEnvironmentSourceRequest request, out int mapIndex)
    {
        mapIndex = LumenPlayMap.ResolveMapIndex(request.MapIndex,
            request.MapIndex == -1 ? Find.CurrentMap?.uniqueID : null);
        if ((request.Channel & ~KnownChannels) != 0)
            throw new ArgumentException($"Unknown LumenEnvironmentChannel bits in {request.Channel}.", nameof(request));
        if (request.Shape is not LumenEnvironmentForceShape.HorizontalRadial and not LumenEnvironmentForceShape.SphericalRadial)
            throw new ArgumentException($"Sustained environment sources only support HorizontalRadial or SphericalRadial, got {request.Shape}.", nameof(request));
        if (!IsFinite(request.InitialPositionWorld))
            throw new ArgumentOutOfRangeException(nameof(request), "InitialPositionWorld components must be finite.");
        if (request.FollowHostMouse && request.InitialPositionWorld != Vector3.Zero)
            throw new ArgumentException("InitialPositionWorld must remain Vector3.Zero when FollowHostMouse is true.", nameof(request));

        if (request.Channel == LumenEnvironmentChannel.None)
        {
            if (!request.TriggersCursorEvents)
                throw new ArgumentException("A sustained source with Channel = None must have TriggersCursorEvents = true.", nameof(request));
            if (!float.IsFinite(request.Radius) || request.Radius < 0f)
                throw new ArgumentOutOfRangeException(nameof(request), request.Radius, "Radius must be finite and >= 0.");
            if (request.Strength != 0f)
                throw new ArgumentException("Strength must be 0 when Channel is None.", nameof(request));
        }
        else
        {
            if (!(request.Radius > 0f) || !float.IsFinite(request.Radius))
                throw new ArgumentOutOfRangeException(nameof(request), request.Radius, "Radius must be finite and > 0 when Channel is non-zero.");
            if (!float.IsFinite(request.Strength))
                throw new ArgumentOutOfRangeException(nameof(request), request.Strength, "Strength must be finite.");
        }
    }

    internal static void EmitPulse(int mapIndex, in LumenEnvironmentPulseRequest request)
    {
        double simNow = Current.Game?.tickManager is { } tm ? tm.TicksGame / 60.0 : simClock;
        double realNow = Waylith.EngineApplication.Instance is not null ? UnityEngine.Time.realtimeSinceStartupAsDouble : realClock;
        MapEnvironmentState state = GetOrCreateMap(mapIndex);

        for (int i = state.PulseCount - 1; i >= 0; i--)
        {
            PulseEntry existing = state.Pulses[i];
            double now = existing.RealTime ? realNow : simNow;
            if (now > existing.StartSeconds + existing.Duration)
            {
                state.RemovePulseAt(i);
                totalActivePulses--;
            }
        }

        if (state.PulseCount == MaxPulsesPerMap)
        {
            state.RemovePulseAt(0);
            totalActivePulses--;
        }

        byte slot = (byte)BitOperations.TrailingZeroCount(state.FreePulseSlots);
        state.FreePulseSlots &= (byte)~(1 << slot);
        uint id = unchecked(++nextPulseId);
        if (id == 0u) id = unchecked(++nextPulseId);

        state.Pulses[state.PulseCount++] = new PulseEntry
        {
            Id = id,
            Slot = slot,
            Channel = request.Channel,
            Shape = request.Shape,
            CenterWorld = request.CenterWorld,
            Radius = request.Radius,
            Strength = request.Strength,
            Duration = request.Duration,
            ShellHalfThickness = request.ShellHalfThickness,
            RealTime = request.RealTime,
            StartSeconds = request.RealTime ? realNow : simNow,
        };
        totalActivePulses++;
    }

    internal static ILumenEnvironmentSource CreateSource(int mapIndex, in LumenEnvironmentSourceRequest request)
    {
        MapEnvironmentState state = GetOrCreateMap(mapIndex);
        if (state.SustainedSources.Count >= MaxSustainedSourcesPerMap)
        {
            WarnSourceCap(mapIndex);
            return new InertEnvironmentSource(mapIndex, request.Channel, request.FollowHostMouse);
        }

        var source = new SustainedSource
        {
            MapIndex = mapIndex,
            Channel = request.Channel,
            Shape = request.Shape,
            FollowHostMouse = request.FollowHostMouse,
            TriggersCursorEvents = request.TriggersCursorEvents,
            PositionWorld = request.InitialPositionWorld,
            Radius = request.Radius,
            Strength = request.Strength,
        };
        state.SustainedSources.Add(source);
        if (source.FollowHostMouse)
        {
            state.FollowMouseSourceCount++;
            if (source.TriggersCursorEvents) state.FollowMouseCursorEventCount++;
        }
        totalSustainedSources++;
        return source;
    }

    private static void RemoveSustainedSource(SustainedSource source)
    {
        if (!Maps.TryGetValue(source.MapIndex, out MapEnvironmentState? state)) return;
        if (!state.SustainedSources.Remove(source)) return;
        if (source.FollowHostMouse)
        {
            state.FollowMouseSourceCount--;
            if (source.TriggersCursorEvents) state.FollowMouseCursorEventCount--;
        }
        totalSustainedSources--;
    }

    internal static void RegisterCursorSubscriber(int mapIndex)
    {
        MapEnvironmentState state = GetOrCreateMap(mapIndex);
        state.CursorEventSubscribers++;
        totalCursorSubscribers++;
    }

    internal static void UnregisterCursorSubscriber(int mapIndex)
    {
        if (!Maps.TryGetValue(mapIndex, out MapEnvironmentState? state) || state.CursorEventSubscribers <= 0)
            return;
        state.CursorEventSubscribers--;
        totalCursorSubscribers--;
    }

    internal static bool NeedsHostMouse(int currentMapId)
        => currentMapId >= 0
            && Maps.TryGetValue(currentMapId, out MapEnvironmentState? state)
            && (state.FollowMouseSourceCount > 0 || state.CursorEventSubscribers > 0);

    internal static void BeginFrame(in LumenCaptureContext context)
    {
        if (capturedFrame == context.FrameIndex) return;
        double? hostSim = Current.Game?.tickManager is { } tm ? tm.TicksGame / 60.0 : null;
        double? hostReal = Waylith.EngineApplication.Instance is not null ? UnityEngine.Time.realtimeSinceStartupAsDouble : null;
        HostMouseSample? hostMouse = NeedsHostMouse(context.CurrentMapIndex) ? TryReadHostMouse(context.CurrentMapIndex) : null;
        CaptureFrame(in context, hostSim, hostReal, hostMouse);
    }

    // Host-input boundary: freezes clocks, active pulses, sustained forces, and smoothed cursors once per FrameIndex.
    internal static void CaptureFrame(
        in LumenCaptureContext context,
        double? hostSimSeconds,
        double? hostRealSeconds,
        HostMouseSample? hostMouse)
    {
        capturedFrame = context.FrameIndex;
        float simDt = MathF.Max(0f, context.DeltaTime);
        float realDt = context.UnscaledDeltaTime > 0f ? context.UnscaledDeltaTime : simDt;
        double prevSim = simClock;
        double prevReal = realClock;
        simClock = hostSimSeconds ?? (simClock + simDt);
        realClock = hostRealSeconds ?? (realClock + realDt);
        double stepSim = hostSimSeconds.HasValue ? simDt : (simClock - prevSim);
        double stepReal = hostRealSeconds.HasValue ? realDt : (realClock - prevReal);

        if (totalActivePulses == 0 && totalSustainedSources == 0 && totalCursorSubscribers == 0)
        {
            if (hasFrozenSnapshots)
            {
                foreach (MapEnvironmentState state in Maps.Values) state.ClearFrozen();
                hasFrozenSnapshots = false;
            }
            return;
        }

        hasFrozenSnapshots = false;
        Matrix4x4 viewProjection = context.ProjectionMatrix * context.ViewMatrix;

        foreach (var (mapId, state) in Maps)
        {
            state.ClearFrozen();

            // 1. Transient pulses (oldest to newest; remove pulses whose interval ended before this frame).
            int p = 0;
            while (p < state.PulseCount)
            {
                PulseEntry pulse = state.Pulses[p];
                double now = pulse.RealTime ? realClock : simClock;
                double step = pulse.RealTime ? stepReal : stepSim;
                double elapsed = Math.Max(0.0, now - pulse.StartSeconds);
                if (elapsed > pulse.Duration && (now - step) >= pulse.StartSeconds + pulse.Duration)
                {
                    state.RemovePulseAt(p);
                    totalActivePulses--;
                    continue;
                }

                float clampedElapsed = (float)Math.Min(elapsed, pulse.Duration);
                float waveRadius = pulse.Shape == LumenEnvironmentForceShape.SphericalWave
                    ? pulse.Radius * (clampedElapsed / pulse.Duration)
                    : 0f;

                if (elapsed <= pulse.Duration && pulse.Strength != 0f)
                {
                    state.AddForce(new LumenEnvironmentForce(
                        pulse.Channel, pulse.Shape, pulse.CenterWorld,
                        pulse.Radius, pulse.Strength, waveRadius, pulse.ShellHalfThickness));
                }

                if ((pulse.Channel & LumenEnvironmentChannel.Explosion) != 0)
                {
                    state.AddExplosionPulse(new FrozenExplosionPulse(
                        pulse.Id, pulse.Slot, pulse.Shape,
                        new Vector2(pulse.CenterWorld.X, pulse.CenterWorld.Z),
                        pulse.Radius, MathF.Abs(pulse.Strength), waveRadius, pulse.ShellHalfThickness));
                }

                if (elapsed > pulse.Duration)
                {
                    state.RemovePulseAt(p);
                    totalActivePulses--;
                    continue;
                }

                p++;
            }

            // 2. Host mouse on the currently rendered map.
            bool hasValidHostMouse = mapId == context.CurrentMapIndex && hostMouse.HasValue;
            if (hasValidHostMouse && (state.FollowMouseSourceCount > 0 || state.CursorEventSubscribers > 0))
            {
                HostMouseSample sample = hostMouse!.Value;
                state.HostMouseVelocity = CursorVelocityMath.StepScreenVelocity(
                    state.PreviousHostMouseScreen,
                    sample.ScreenHeightPosition,
                    state.HasPreviousHostMouse,
                    state.HostMouseVelocity,
                    realDt);
                state.PreviousHostMouseScreen = sample.ScreenHeightPosition;
                state.HasPreviousHostMouse = true;

                if (state.CursorEventSubscribers > 0 || state.FollowMouseCursorEventCount > 0)
                {
                    state.AddCursor(new LumenCursorSnapshot(
                        sample.WorldGroundXZ,
                        state.HostMouseVelocity,
                        state.HostMouseVelocity.Length()));
                }
            }
            else
            {
                state.HasPreviousHostMouse = false;
                state.HostMouseVelocity = Vector2.Zero;
            }

            // 3. Sustained environment sources / custom cursors.
            bool isRenderedMap = context.CurrentMapIndex < 0 || mapId == context.CurrentMapIndex;
            for (int i = 0; i < state.SustainedSources.Count; i++)
            {
                SustainedSource src = state.SustainedSources[i];
                if (src.FollowHostMouse)
                {
                    if (hasValidHostMouse && src.Channel != LumenEnvironmentChannel.None && src.Strength != 0f)
                    {
                        Vector2 xz = hostMouse!.Value.WorldGroundXZ;
                        state.AddForce(new LumenEnvironmentForce(
                            src.Channel, src.Shape, new Vector3(xz.X, 0f, xz.Y),
                            src.Radius, src.Strength, 0f, 0f));
                    }
                    continue;
                }

                if (src.Channel != LumenEnvironmentChannel.None && src.Strength != 0f)
                {
                    state.AddForce(new LumenEnvironmentForce(
                        src.Channel, src.Shape, src.PositionWorld,
                        src.Radius, src.Strength, 0f, 0f));
                }

                if (src.TriggersCursorEvents)
                {
                    Vector2 velocity = Vector2.Zero;
                    if (isRenderedMap)
                    {
                        velocity = CursorVelocityMath.StepWorldCursorVelocity(
                            src.PreviousWorld,
                            src.PositionWorld,
                            src.HasPreviousWorld,
                            src.SmoothedVelocity,
                            in viewProjection,
                            context.ViewportWidth,
                            context.ViewportHeight,
                            realDt,
                            out bool projected);
                        src.HasPreviousWorld = projected;
                        src.PreviousWorld = src.PositionWorld;
                        src.SmoothedVelocity = velocity;
                    }
                    else
                    {
                        src.HasPreviousWorld = false;
                        src.SmoothedVelocity = Vector2.Zero;
                    }

                    state.AddCursor(new LumenCursorSnapshot(
                        new Vector2(src.PositionWorld.X, src.PositionWorld.Z),
                        velocity,
                        velocity.Length()));
                }
            }

            hasFrozenSnapshots |= state.HasAnyFrozen;
        }
    }

    internal static ReadOnlyMemory<LumenEnvironmentForce> ForMap(int mapId, LumenEnvironmentChannel channels)
        => Maps.TryGetValue(mapId, out MapEnvironmentState? state) ? state.SelectForces(channels) : default;

    internal static ReadOnlySpan<LumenCursorSnapshot> CursorsForMap(int mapId)
        => Maps.TryGetValue(mapId, out MapEnvironmentState? state) ? state.Cursors : default;

    internal static ReadOnlySpan<FrozenExplosionPulse> ExplosionPulsesForMap(int mapId)
        => Maps.TryGetValue(mapId, out MapEnvironmentState? state) ? state.ExplosionPulses : default;

    internal static void ForgetMap(int mapId)
    {
        if (!Maps.Remove(mapId, out MapEnvironmentState? state)) return;
        totalActivePulses -= state.PulseCount;
        totalSustainedSources -= state.SustainedSources.Count;
        totalCursorSubscribers -= state.CursorEventSubscribers;
        foreach (SustainedSource src in state.SustainedSources)
            src.Alive = false;
        state.SustainedSources.Clear();
    }

    private static MapEnvironmentState GetOrCreateMap(int mapId)
    {
        if (!Maps.TryGetValue(mapId, out MapEnvironmentState? state))
            Maps.Add(mapId, state = new MapEnvironmentState());
        return state;
    }

    private static HostMouseSample? TryReadHostMouse(int currentMapId)
    {
        Map? currentMap = Find.CurrentMap;
        if (currentMap is null || currentMap.Disposed || currentMap.uniqueID != currentMapId)
            return null;
        if (Find.WindowStack is { MouseObscuredNow: true })
            return null;
        int screenW = UnityEngine.Screen.width;
        int screenH = UnityEngine.Screen.height;
        if (screenW <= 0 || screenH <= 0)
            return null;
        UnityEngine.Vector3 mousePx = UnityEngine.Input.mousePosition;
        if (!float.IsFinite(mousePx.x) || !float.IsFinite(mousePx.y) ||
            mousePx.x < 0f || mousePx.x > screenW || mousePx.y < 0f || mousePx.y > screenH)
            return null;
        UnityEngine.Vector3 world = UI.MouseMapPosition();
        if (!float.IsFinite(world.x) || !float.IsFinite(world.z) ||
            world.x < 0f || world.x >= currentMap.Size.x || world.z < 0f || world.z >= currentMap.Size.z)
            return null;
        return new HostMouseSample(
            new Vector2(world.x, world.z),
            CursorVelocityMath.ScreenPixelsToHeightUnits(new Vector2(mousePx.x, mousePx.y), screenH));
    }

    private static void WarnSourceCap(int mapIndex)
    {
        long now = Environment.TickCount64;
        if (hasWarnedSourceCap && now - lastSourceCapWarningMs < CapWarningCooldownMs)
            return;
        hasWarnedSourceCap = true;
        lastSourceCapWarningMs = now;
        RimLumenRenderHost.LogWarning(
            $"[RimLumen] Map {mapIndex} reached MaxSustainedSourcesPerMap ({MaxSustainedSourcesPerMap}); returning an inert environment source (throttled to once per 10s).");
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
