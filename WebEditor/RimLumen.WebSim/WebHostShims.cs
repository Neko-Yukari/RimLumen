using System;
using System.Collections.Generic;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;

namespace Verse
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class StaticConstructorOnStartupAttribute : Attribute { }

    public class Def
    {
        public string? defName;
        public ModContentPack? modContentPack;
    }

    public sealed class ModContentPack
    {
        public string PackageIdPlayerFacing => string.Empty;
        public string Name => string.Empty;
        public string RootDir => string.Empty;
    }

    public static class LoadedModManager
    {
        public static IEnumerable<ModContentPack> RunningMods => Array.Empty<ModContentPack>();
    }

    public static class DefDatabase<T>
    {
        public static IReadOnlyList<T> AllDefsListForReading => Array.Empty<T>();
    }

    public static class Log
    {
        public static Action<string>? WarningSink;
        public static Action<string>? ErrorSink;
        public static void Warning(string message) => WarningSink?.Invoke(message);
        public static void Error(string message) => ErrorSink?.Invoke(message);
    }

    public enum AltitudeLayer
    {
        Projectile = 0,
        MoteOverhead = 1,
    }

    public static class Altitudes
    {
        public static float AltitudeFor(AltitudeLayer layer) => layer == AltitudeLayer.MoteOverhead ? 0.5f : 0f;
    }

    public struct IntVec3
    {
        public int x;
        public int y;
        public int z;
    }

    public sealed class Map
    {
        public int uniqueID;
        public bool Disposed;
        public IntVec3 Size;
    }

    public sealed class WindowStack
    {
        public bool MouseObscuredNow;
    }

    public static class Find
    {
        public static Map? CurrentMap => null;
        public static WindowStack? WindowStack => null;
    }

    public sealed class TickManager
    {
        public int TicksGame;
    }

    public sealed class Game
    {
        public TickManager? tickManager;
    }

    public static class Current
    {
        public static Game? Game => null;
    }

    public static class UI
    {
        public static UnityEngine.Vector3 MouseMapPosition() => default;
    }
}

namespace UnityEngine
{
    public struct Vector3
    {
        public float x;
        public float y;
        public float z;
        public static readonly Vector3 one = new() { x = 1f, y = 1f, z = 1f };
    }

    public struct Quaternion
    {
        public float x;
        public float y;
        public float z;
        public float w;
        public static readonly Quaternion identity = new() { w = 1f };
    }

    public static class Time
    {
        public static double realtimeSinceStartupAsDouble => 0d;
    }

    public static class Screen
    {
        public static int width => 0;
        public static int height => 0;
    }

    public static class Input
    {
        public static Vector3 mousePosition => default;
    }
}

namespace Waylith
{
    public sealed class EngineApplication
    {
        public static EngineApplication? Instance => null;
    }
}

namespace Waylith.Rendering
{
    public readonly struct FrameContext { }
    public sealed class RenderTexture { }
}

namespace RimLumen.Core.Rendering
{
    public static class RimLumenRenderHost
    {
        private static float particleDensityScale = 1.0f;

        public static float ParticleDensityScale
        {
            get => particleDensityScale;
            set
            {
                if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
                particleDensityScale = Math.Clamp(value, 0.1f, 1.0f);
            }
        }

        public static LumenCapabilities Capabilities => default;

        internal static void LogWarning(string message) => Verse.Log.Warning(message);
        internal static void LogError(string message) => Verse.Log.Error(message);
    }
}

namespace RimLumen.Core.ParticleWorld
{
    internal static class ParticleCollisionFields
    {
        internal static ParticleMapGrid? ActiveGrid { get; set; }

        internal static ParticleMapGrid? Capture(int mapId, CollisionSpec spec)
        {
            if (mapId < 0 || (spec.Mode == LumenCollisionMode.None && !spec.KillUnderRoof)) return null;
            return ActiveGrid is { } grid && grid.MapId == mapId ? grid : null;
        }
    }
}
