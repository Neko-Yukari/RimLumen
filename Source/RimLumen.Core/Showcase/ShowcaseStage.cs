using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using RimLumen.Core.Contracts;
using RimLumen.Core.Lighting;
using RimLumen.Core.ParticlesGpu;
using RimLumen.Core.Rendering;
using RimLumen.Core.Scene;
using RimWorld;
using RimWorld.Planet;
using Verse;
using CurveKey = RimLumen.Core.Contracts.CurveKey;

namespace RimLumen.Core.Showcase;

public sealed record ShowcaseOptions(bool Enabled, string? CapturePngPath);

public sealed record ShowcaseStationInfo(
    int Index,
    string Title,
    string Subtitle,
    IntVec3 AnchorCell);

public static class ShowcaseOptionsParser
{
    // Ensures: returns true iff args contains "-rimlumen-showcase" (case-insensitive);
    //   also parses optional "-rimlumen-showcase-capture=<pngPath>" when present.
    public static bool TryParse(IReadOnlyList<string> args, out ShowcaseOptions options)
    {
        const string enableFlag = "-rimlumen-showcase";
        const string capturePrefix = "-rimlumen-showcase-capture=";
        bool enabled = false;
        string? capturePath = null;

        if (args is not null)
        {
            foreach (string arg in args)
            {
                if (string.IsNullOrEmpty(arg))
                    continue;
                if (arg.Equals(enableFlag, StringComparison.OrdinalIgnoreCase))
                {
                    enabled = true;
                }
                else if (capturePath is null && arg.StartsWith(capturePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string raw = arg.Substring(capturePrefix.Length);
                    if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
                    {
                        raw = raw.Substring(1, raw.Length - 2);
                    }
                    if (raw.Length > 0)
                    {
                        capturePath = raw;
                    }
                }
            }
        }

        if (!enabled)
        {
            options = new ShowcaseOptions(false, null);
            return false;
        }

        options = new ShowcaseOptions(true, capturePath);
        return true;
    }
}

public static class ShowcaseStage
{
    public const int ArenaWidth = 68;
    public const int ArenaHeight = 30;
    private static readonly int[] StationOffsetsX = { -25, -15, -5, 5, 15, 25 };

    private static readonly string[] StationEffectIds =
    {
        "rimlumen.showcase.station1_hdr",
        "rimlumen.showcase.station2_l1_stretched",
        "rimlumen.showcase.station3_orbit_light",
        "rimlumen.showcase.station4_hard",
        "rimlumen.showcase.station4_soft",
        "rimlumen.showcase.station5_gpu_12k",
    };

    private static readonly object StateLock = new();
    private static ActiveShowcaseState? activeState;

    internal sealed class ActiveShowcaseState
    {
        public required Map Map;
        public required IReadOnlyList<ShowcaseStationInfo> Stations;
        public required List<ILumenEffectHandle> Handles;
        public required List<Thing> SpawnedWalls;
        public required IntVec3 Station1Anchor;
        public required IntVec3 Station3Anchor;
        public required IntVec3 Station6Anchor;
        public UnityEngine.Material? LdrMaterial;
        public UnityEngine.Material? MoteGlowMaterial;
        public float OrbitTheta;
        public float PulseTime;
        public bool ShowLabels = true;
        // Player's EnableVanillaAdditiveBloom before the showcase forced it on; restored by Clear.
        public bool PreviousVanillaAdditiveBloom;
    }

    public static IntVec3 ComputeArenaCenter(IntVec3 mapCenter, IntVec3 mapSize)
    {
        int minX = Math.Min(35, mapSize.x / 2);
        int maxX = Math.Max(minX, mapSize.x - 35);
        int minZ = Math.Min(16, mapSize.z / 2);
        int maxZ = Math.Max(minZ, mapSize.z - 16);
        return new IntVec3(
            Math.Clamp(mapCenter.x, minX, maxX),
            0,
            Math.Clamp(mapCenter.z, minZ, maxZ));
    }

    public static CellRect ComputeArenaRect(IntVec3 arenaCenter, IntVec3 mapSize)
    {
        int minX = Math.Max(0, arenaCenter.x - 34);
        int maxX = Math.Min(mapSize.x - 1, arenaCenter.x + 33);
        int minZ = Math.Max(0, arenaCenter.z - 15);
        int maxZ = Math.Min(mapSize.z - 1, arenaCenter.z + 14);
        return CellRect.FromLimits(minX, minZ, maxX, maxZ);
    }

    public static IReadOnlyList<ShowcaseStationInfo> ComputeStationLayout(IntVec3 mapCenter, IntVec3 mapSize)
    {
        IntVec3 center = ComputeArenaCenter(mapCenter, mapSize);
        var specs = new (string Title, string Subtitle)[]
        {
            ("#1 LDR vs HDR泛光", "上: Transparent 8-bit | 下: L1 HDR 8.5x+Bloom"),
            ("#2 L1拉伸脉冲", "StretchedVelocity 锥形喷射 + 3D重力 + 渐变曲线"),
            ("#3 动态点光照地", "WorldSpace 拖尾 + 7.5格绕行点光驱动 GlowGrid"),
            ("#4 深度软粒子穿墙", "上: SoftFade=0 硬切花岗岩墙 | 下: SoftFade=1.4 柔化"),
            ("#5 L2 GPU 12k旋涡", "12,000 GPU粒子: Curl湍流 + 涡旋 + 中心吸引"),
            ("#6 原版发光自动HDR", "EnableVanillaAdditiveBloom + MoteGlow 自动HDR"),
        };

        var stations = new ShowcaseStationInfo[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            int x = Math.Clamp(center.x + StationOffsetsX[i], 0, Math.Max(0, mapSize.x - 1));
            int z = Math.Clamp(center.z, 0, Math.Max(0, mapSize.z - 1));
            stations[i] = new ShowcaseStationInfo(i + 1, specs[i].Title, specs[i].Subtitle, new IntVec3(x, 0, z));
        }
        return stations;
    }

    // Requires: main thread; map is an active playing map with initialized GlowGrid and SkyManager.
    // Ensures: idempotently clears/rebuilds a centered 68x30 dark paved arena at midnight, leaves the game running at
    //   normal speed (a game that was paused is set to Normal; the stations follow RimWorld pause and 1x/2x/3x speed
    //   afterwards), frames the camera over all 6 left-to-right stations, and spawns all 6 live showcase stations via
    //   LumenSceneBootstrap / RimLumenRenderHost, remembering the player's EnableVanillaAdditiveBloom for Clear. A showcase still
    //   active on another map is cleared first, so its handles and walls are never orphaned.
    public static IReadOnlyList<ShowcaseStationInfo> BuildOrRebuild(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);

        Map? otherMap;
        lock (StateLock)
        {
            otherMap = activeState?.Map;
        }
        if (otherMap is not null && !ReferenceEquals(otherMap, map))
        {
            Clear(otherMap);
        }
        Clear(map);
        LumenSceneBootstrap.EnsureInitialized();
        RuntimeHelpers.RunClassConstructor(typeof(LumenGpuParticlesBootstrap).TypeHandle);
        EnsureMapComponents(map);

        IntVec3 center = ComputeArenaCenter(map.Center, map.Size);
        CellRect arena = ComputeArenaRect(center, map.Size);
        IReadOnlyList<ShowcaseStationInfo> stations = ComputeStationLayout(map.Center, map.Size);

        PrepareArenaTerrain(map, center, arena);
        SetDayPercent(map, 0.0f);

        // Station time follows the game clock (dt = 0 while paused, TickRateMultiplier otherwise), so the showcase only needs
        // a running clock to animate; it never forces a pause. Window/long-event force pauses are left alone.
        if (Find.TickManager is { CurTimeSpeed: TimeSpeed.Paused } tickManager)
        {
            tickManager.CurTimeSpeed = TimeSpeed.Normal;
        }
        ScreenFader.SetColor(UnityEngine.Color.clear);
        Find.CameraDriver?.SetRootPosAndSize(new UnityEngine.Vector3(center.x + 0.5f, 0f, center.z + 0.5f), 17.5f);

        var walls = SpawnStation4Walls(map, stations[3].AnchorCell);
        var handles = SpawnAllStationEffects(map, stations, out ActiveShowcaseState state, walls);

        lock (StateLock)
        {
            // Clear restored any earlier showcase's saved value above, so the current value is the player's original one.
            state.PreviousVanillaAdditiveBloom = RimLumenRenderHost.EnableVanillaAdditiveBloom;
            activeState = state;
        }
        RimLumenRenderHost.EnableVanillaAdditiveBloom = true;

        Log.Message($"[RimLumen] Showcase built: map={map.uniqueID}, center={center}, stations={stations.Count}, handles={handles.Count}");
        return stations;
    }

    // Requires: main thread; map is non-null.
    // Ensures: stops all active showcase effect handles, restores RimLumenRenderHost.EnableVanillaAdditiveBloom to the value
    //   saved when the showcase was built,
    //   and removes dynamic showcase quads without leaving orphan lights or particles.
    public static void Clear(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);

        ActiveShowcaseState? prior;
        lock (StateLock)
        {
            if (activeState is null || !ReferenceEquals(activeState.Map, map))
            {
                return;
            }
            prior = activeState;
            activeState = null;
        }

        foreach (ILumenEffectHandle handle in prior.Handles)
        {
            handle.Stop(allowFadeOut: false);
        }
        prior.Handles.Clear();

        foreach (Thing wall in prior.SpawnedWalls)
        {
            if (wall.Spawned && !wall.Destroyed)
            {
                wall.Destroy(DestroyMode.Vanish);
            }
        }
        prior.SpawnedWalls.Clear();

        if (LumenSceneBootstrap.IsInitialized)
        {
            foreach (string id in StationEffectIds)
            {
                LumenSceneBootstrap.Catalog.Unregister(id);
            }
        }

        RimLumenRenderHost.EnableVanillaAdditiveBloom = prior.PreviousVanillaAdditiveBloom;
    }

    public static bool ToggleLabels(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        lock (StateLock)
        {
            if (activeState is null || !ReferenceEquals(activeState.Map, map))
            {
                return false;
            }
            activeState.ShowLabels = !activeState.ShowLabels;
            return activeState.ShowLabels;
        }
    }

    public static bool ToggleMidnightNoon(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        float current = GenLocalDate.DayPercent(map);
        bool isDark = current < 0.25f || current > 0.75f;
        float target = isDark ? 0.5f : 0.0f;
        SetDayPercent(map, target);
        return !isDark;
    }

    internal static bool TryGetActiveState(Map map, out ActiveShowcaseState? state)
    {
        lock (StateLock)
        {
            if (activeState is not null && ReferenceEquals(activeState.Map, map))
            {
                state = activeState;
                return true;
            }
        }
        state = null;
        return false;
    }

    private static void EnsureMapComponents(Map map)
    {
        var lighting = map.GetComponent<LumenMapLightingComponent>();
        if (lighting is null)
        {
            lighting = new LumenMapLightingComponent(map);
            map.components.Add(lighting);
            lighting.FinalizeInit();
        }
        else if (!LumenMapLightingRegistry.TryResolve(map.uniqueID, out _))
        {
            lighting.FinalizeInit();
        }

        if (map.GetComponent<LumenShowcaseMapComponent>() is null)
        {
            map.components.Add(new LumenShowcaseMapComponent(map));
        }
    }

    private static void PrepareArenaTerrain(Map map, IntVec3 center, CellRect arena)
    {
        IntVec3 safeEdgeCell = arena.minZ >= 3
            ? new IntVec3(center.x, 0, arena.minZ - 3)
            : new IntVec3(center.x, 0, Math.Min(map.Size.z - 1, arena.maxZ + 3));

        foreach (IntVec3 cell in arena)
        {
            List<Thing> things = cell.GetThingList(map);
            if (things.Count > 0)
            {
                Thing[] snapshot = things.ToArray();
                foreach (Thing thing in snapshot)
                {
                    if (thing is Pawn pawn)
                    {
                        pawn.Position = safeEdgeCell;
                        pawn.Notify_Teleported(endCurrentJob: false, resetTweenedPos: true);
                    }
                    else if (thing.Spawned && !thing.Destroyed)
                    {
                        thing.Destroy(DestroyMode.Vanish);
                    }
                }
            }

            map.roofGrid.SetRoof(cell, null);
            map.terrainGrid.SetTerrain(cell, TerrainDefOf.TileSandstone);
            map.fogGrid.Unfog(cell);
        }
    }

    private static void SetDayPercent(Map map, float targetDayPercent)
    {
        float current = GenLocalDate.DayPercent(map);
        float delta = targetDayPercent - current;
        if (delta < 0f)
        {
            delta += 1f;
        }
        if (delta > 0.005f && delta < 0.995f)
        {
            int ticks = (int)MathF.Round(delta * GenDate.TicksPerDay);
            if (ticks > 0)
            {
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + ticks);
            }
        }
        map.skyManager.SkyManagerUpdate();
        map.mapDrawer.WholeMapChanged((ulong)MapMeshFlagDefOf.GroundGlow | (ulong)MapMeshFlagDefOf.Things | (ulong)MapMeshFlagDefOf.Terrain);
    }

    private static List<Thing> SpawnStation4Walls(Map map, IntVec3 anchor4)
    {
        var walls = new List<Thing>(12);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = 3; dz <= 4; dz++)
            {
                SpawnWallCell(map, new IntVec3(anchor4.x + dx, 0, anchor4.z + dz), walls);
                SpawnWallCell(map, new IntVec3(anchor4.x + dx, 0, anchor4.z - dz), walls);
            }
        }
        return walls;
    }

    private static void SpawnWallCell(Map map, IntVec3 cell, List<Thing> walls)
    {
        if (!cell.InBounds(map))
        {
            return;
        }
        Thing wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.BlocksGranite);
        GenSpawn.Spawn(wall, cell, map);
        walls.Add(wall);
    }

    private static List<ILumenEffectHandle> SpawnAllStationEffects(
        Map map,
        IReadOnlyList<ShowcaseStationInfo> stations,
        out ActiveShowcaseState state,
        List<Thing> spawnedWalls)
    {
        float yMote = AltitudeLayer.MoteOverhead.AltitudeFor();
        float yWall = AltitudeLayer.Building.AltitudeFor();

        var catalog = LumenSceneBootstrap.Catalog;
        var service = LumenSceneBootstrap.Service;

        catalog.Register(CreateStation1HdrAsset(yMote));
        catalog.Register(CreateStation2StretchedAsset(yMote));
        catalog.Register(CreateStation3OrbitAsset(yMote));
        catalog.Register(CreateStation4MistAsset(StationEffectIds[3], yWall, softFadeDistance: 0f));
        catalog.Register(CreateStation4MistAsset(StationEffectIds[4], yWall, softFadeDistance: 1.4f));
        catalog.Register(CreateStation5VortexAsset(yMote));

        var anchor1 = stations[0].AnchorCell;
        var anchor2 = stations[1].AnchorCell;
        var anchor3 = stations[2].AnchorCell;
        var anchor4 = stations[3].AnchorCell;
        var anchor5 = stations[4].AnchorCell;
        var anchor6 = stations[5].AnchorCell;

        var handles = new List<ILumenEffectHandle>(6);
        var builtState = new ActiveShowcaseState
        {
            Map = map,
            Stations = stations,
            Handles = handles,
            SpawnedWalls = spawnedWalls,
            Station1Anchor = anchor1,
            Station3Anchor = anchor3,
            Station6Anchor = anchor6,
            LdrMaterial = MaterialPool.MatFrom(BaseContent.WhiteTex, ShaderDatabase.Mote, new UnityEngine.Color(1f, 0.42f, 0.08f, 1f)),
            MoteGlowMaterial = MaterialPool.MatFrom("Other/ForceField", ShaderDatabase.MoteGlow, new UnityEngine.Color(1f, 0.38f, 0.78f, 1f)),
        };

        handles.Add(service.Play(new LumenPlayRequest
        {
            EffectId = StationEffectIds[0],
            MapIndex = map.uniqueID,
            OriginWorld = new Vector3(anchor1.x + 0.5f, 0f, anchor1.z - 3.5f),
        }));

        handles.Add(service.Play(new LumenPlayRequest
        {
            EffectId = StationEffectIds[1],
            MapIndex = map.uniqueID,
            OriginWorld = new Vector3(anchor2.x + 0.5f, 0f, anchor2.z + 0.5f),
        }));

        handles.Add(service.Play(new LumenPlayRequest
        {
            EffectId = StationEffectIds[2],
            MapIndex = map.uniqueID,
            OriginWorld = new Vector3(anchor3.x + 0.5f + 3.4f, 0f, anchor3.z + 0.5f),
            Follow = (out Vector3 worldPosition, out float rotationDegrees) =>
            {
                float theta = builtState.OrbitTheta;
                worldPosition = new Vector3(
                    anchor3.x + 0.5f + MathF.Cos(theta) * 3.4f,
                    0f,
                    anchor3.z + 0.5f + MathF.Sin(theta) * 3.4f);
                rotationDegrees = -theta * (180f / MathF.PI);
                return true;
            },
        }));

        handles.Add(service.Play(new LumenPlayRequest
        {
            EffectId = StationEffectIds[3],
            MapIndex = map.uniqueID,
            OriginWorld = new Vector3(anchor4.x + 0.5f, 0f, anchor4.z + 4.0f),
        }));

        handles.Add(service.Play(new LumenPlayRequest
        {
            EffectId = StationEffectIds[4],
            MapIndex = map.uniqueID,
            OriginWorld = new Vector3(anchor4.x + 0.5f, 0f, anchor4.z - 3.0f),
        }));

        handles.Add(service.Play(new LumenPlayRequest
        {
            EffectId = StationEffectIds[5],
            MapIndex = map.uniqueID,
            OriginWorld = new Vector3(anchor5.x + 0.5f, 0f, anchor5.z + 0.5f),
        }));

        state = builtState;
        return handles;
    }

    private static EffectAsset CreateStation1HdrAsset(float yMote) => new()
    {
        EffectId = StationEffectIds[0],
        DefaultDuration = 4f,
        Nodes = new[]
        {
            new EffectNodeSpec
            {
                Name = "hdr_core",
                LocalPosition = new Vector3(0f, yMote, 0f),
                Emitter = new EmitterSpec
                {
                    Name = "hdr_core",
                    PreferredTier = LumenParticleTier.L1_NativeCpu,
                    MaxParticles = 14,
                    Duration = 2f,
                    Looping = true,
                    RateOverTime = 9f,
                    Shape = LumenShapeKind.CircleXZ,
                    ShapeExtents = new Vector3(0.22f, 0f, 0.22f),
                    StartLifetimeMin = 1.2f,
                    StartLifetimeMax = 1.6f,
                    StartSpeedMin = 0.02f,
                    StartSpeedMax = 0.08f,
                    StartSizeMin = 1.35f,
                    StartSizeMax = 1.75f,
                    StartColorLinear = new Vector4(1.0f, 0.34f, 0.04f, 1.0f),
                    HdrIntensity = 3.2f,
                    SizeOverLifetime = new ScalarCurve(new[]
                    {
                        new CurveKey(0f, 0.75f),
                        new CurveKey(0.5f, 1.0f),
                        new CurveKey(1f, 0.75f),
                    }),
                    ColorOverLifetime = new ColorGradient(new[]
                    {
                        new ColorStop(0f, new Vector4(1f, 1f, 1f, 0f)),
                        new ColorStop(0.2f, new Vector4(1f, 1f, 1f, 1f)),
                        new ColorStop(0.8f, new Vector4(1f, 1f, 1f, 1f)),
                        new ColorStop(1f, new Vector4(1f, 1f, 1f, 0f)),
                    }),
                },
            },
        },
    };

    private static EffectAsset CreateStation2StretchedAsset(float yMote) => new()
    {
        EffectId = StationEffectIds[1],
        DefaultDuration = 1.8f,
        Nodes = new[]
        {
            new EffectNodeSpec
            {
                Name = "stretched_sparks",
                // Pitch the cone axis (local +Y) onto screen-up +Z.
                LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f),
                LocalPosition = new Vector3(0f, yMote, -2.5f),
                Emitter = new EmitterSpec
                {
                    Name = "stretched_sparks",
                    PreferredTier = LumenParticleTier.L1_NativeCpu,
                    MaxParticles = 240,
                    Duration = 1.8f,
                    Looping = true,
                    WorldSpaceSimulation = true,
                    RateOverTime = 22f,
                    Bursts = new[] { (0.0f, 34), (0.9f, 34) },
                    Shape = LumenShapeKind.Cone,
                    ConeAngleDegrees = 46f,
                    StartLifetimeMin = 0.75f,
                    StartLifetimeMax = 1.15f,
                    StartSpeedMin = 7.0f,
                    StartSpeedMax = 10.5f,
                    StartSizeMin = 0.11f,
                    StartSizeMax = 0.18f,
                    StartColorLinear = Vector4.One,
                    HdrIntensity = 2.4f,
                    BillboardMode = LumenBillboardMode.StretchedVelocity,
                    VelocityStretchFactor = 0.22f,
                    SizeOverLifetime = new ScalarCurve(new[]
                    {
                        new CurveKey(0f, 0.6f),
                        new CurveKey(0.15f, 1.0f),
                        new CurveKey(1f, 0.2f),
                    }),
                    ColorOverLifetime = new ColorGradient(new[]
                    {
                        new ColorStop(0f, new Vector4(1.0f, 0.92f, 0.55f, 1.0f)),
                        new ColorStop(0.45f, new Vector4(1.0f, 0.38f, 0.06f, 0.95f)),
                        new ColorStop(1f, new Vector4(0.65f, 0.06f, 0.01f, 0.0f)),
                    }),
                    ForceFields = new[]
                    {
                        new ForceFieldSpec(ForceFieldKind.GravityDrag, new Vector3(0f, 0f, -1f), new Vector3(0.6f, 0f, 0f), 0f, 9.5f),
                    },
                },
            },
        },
    };

    private static EffectAsset CreateStation3OrbitAsset(float yMote) => new()
    {
        EffectId = StationEffectIds[2],
        DefaultDuration = 4f,
        Nodes = new[]
        {
            new EffectNodeSpec
            {
                Name = "arcane_orb",
                LocalPosition = new Vector3(0f, yMote, 0f),
                Emitter = new EmitterSpec
                {
                    Name = "arcane_orb",
                    PreferredTier = LumenParticleTier.L1_NativeCpu,
                    MaxParticles = 240,
                    Duration = 2f,
                    Looping = true,
                    WorldSpaceSimulation = true,
                    RateOverTime = 95f,
                    Shape = LumenShapeKind.Sphere,
                    ShapeExtents = new Vector3(0.28f, 0.28f, 0.28f),
                    StartLifetimeMin = 0.65f,
                    StartLifetimeMax = 1.05f,
                    StartSpeedMin = 0.15f,
                    StartSpeedMax = 0.55f,
                    StartSizeMin = 0.35f,
                    StartSizeMax = 0.70f,
                    StartColorLinear = new Vector4(0.15f, 0.75f, 1.0f, 1.0f),
                    HdrIntensity = 5.5f,
                    SizeOverLifetime = new ScalarCurve(new[]
                    {
                        new CurveKey(0f, 1.0f),
                        new CurveKey(1f, 0.1f),
                    }),
                    ColorOverLifetime = new ColorGradient(new[]
                    {
                        new ColorStop(0f, new Vector4(1f, 1f, 1f, 1f)),
                        new ColorStop(0.6f, new Vector4(0.4f, 0.9f, 1f, 0.7f)),
                        new ColorStop(1f, new Vector4(0.1f, 0.4f, 1f, 0f)),
                    }),
                },
            },
            new EffectNodeSpec
            {
                Name = "arcane_light",
                LocalPosition = Vector3.Zero,
                PointLight = new PointLightSpec(9.5f, 4.5f, new Vector3(0.30f, 0.92f, 1.0f), ScalarCurve.Constant(2.2f)),
            },
        },
    };

    private static EffectAsset CreateStation4MistAsset(string effectId, float yWall, float softFadeDistance) => new()
    {
        EffectId = effectId,
        DefaultDuration = 4f,
        Nodes = new[]
        {
            new EffectNodeSpec
            {
                Name = "depth_mist",
                LocalPosition = new Vector3(-0.35f, yWall + 0.15f, 0f),
                LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.52f),
                Emitter = new EmitterSpec
                {
                    Name = effectId,
                    PreferredTier = LumenParticleTier.L2_GpuCompute,
                    MaxParticles = 900,
                    Duration = 4f,
                    Looping = true,
                    WorldSpaceSimulation = false,
                    RateOverTime = 360f,
                    Bursts = new[] { (0f, 900) },
                    Shape = LumenShapeKind.Box,
                    ShapeExtents = new Vector3(3.1f, 0.04f, 1.25f),
                    StartLifetimeMin = 2.2f,
                    StartLifetimeMax = 3.2f,
                    StartSpeedMin = 0.01f,
                    StartSpeedMax = 0.05f,
                    StartSizeMin = 0.55f,
                    StartSizeMax = 0.85f,
                    StartColorLinear = new Vector4(0.25f, 1.0f, 0.60f, 0.92f),
                    HdrIntensity = 2.4f,
                    SoftParticleFadeDistance = softFadeDistance,
                    ColorOverLifetime = new ColorGradient(new[]
                    {
                        new ColorStop(0f, new Vector4(0.4f, 1.0f, 0.6f, 0f)),
                        new ColorStop(0.25f, new Vector4(0.4f, 1.0f, 0.6f, 0.9f)),
                        new ColorStop(0.75f, new Vector4(1.0f, 0.85f, 0.35f, 0.9f)),
                        new ColorStop(1f, new Vector4(1.0f, 0.85f, 0.35f, 0f)),
                    }),
                },
            },
        },
    };

    private static EffectAsset CreateStation5VortexAsset(float yMote) => new()
    {
        EffectId = StationEffectIds[5],
        DefaultDuration = 6f,
        Nodes = new[]
        {
            new EffectNodeSpec
            {
                Name = "gpu_vortex_12k",
                LocalPosition = new Vector3(0f, yMote, 0f),
                Emitter = new EmitterSpec
                {
                    Name = "gpu_vortex_12k",
                    PreferredTier = LumenParticleTier.L2_GpuCompute,
                    MaxParticles = 12_000,
                    Duration = 6f,
                    Looping = true,
                    WorldSpaceSimulation = false,
                    RateOverTime = 4_000f,
                    Bursts = new[] { (0f, 8_000) },
                    Shape = LumenShapeKind.Box,
                    ShapeExtents = new Vector3(2.4f, 0.35f, 2.4f),
                    StartLifetimeMin = 2.0f,
                    StartLifetimeMax = 3.0f,
                    StartSpeedMin = 0.2f,
                    StartSpeedMax = 0.6f,
                    StartSizeMin = 0.08f,
                    StartSizeMax = 0.15f,
                    StartColorLinear = Vector4.One,
                    HdrIntensity = 1.45f,
                    SoftParticleFadeDistance = 0.5f,
                    ColorOverLifetime = new ColorGradient(new[]
                    {
                        new ColorStop(0f, new Vector4(0.65f, 0.18f, 1.0f, 0.0f)),
                        new ColorStop(0.20f, new Vector4(0.65f, 0.18f, 1.0f, 0.85f)),
                        new ColorStop(0.60f, new Vector4(0.12f, 0.80f, 1.0f, 0.80f)),
                        new ColorStop(1f, new Vector4(1.0f, 0.72f, 0.18f, 0.0f)),
                    }),
                    ForceFields = new[]
                    {
                        new ForceFieldSpec(ForceFieldKind.CurlTurbulence, Vector3.Zero, Vector3.Zero, 7.5f, 1.8f, 0.85f),
                        new ForceFieldSpec(ForceFieldKind.Vortex, Vector3.UnitY, new Vector3(0.55f, 0f, 0f), 7.5f, 4.8f),
                        new ForceFieldSpec(ForceFieldKind.PointAttractor, Vector3.Zero, Vector3.Zero, 7.5f, 2.4f),
                    },
                },
            },
        },
    };
}

public sealed class LumenShowcaseMapComponent : MapComponent, IDisposable
{
    private bool disposed;

    public LumenShowcaseMapComponent(Map map) : base(map)
    {
    }

    // Ensures: drives per-frame showcase motion (orbiting dynamic point-light orb at station #3, pulsing mote at #6) on the
    //   game clock, and submits the comparison quads at station #1 (vanilla 8-bit LDR reference) and station #6 (vanilla MoteGlow).
    public override void MapComponentUpdate()
    {
        if (!ShowcaseStage.TryGetActiveState(map, out var state) || state is null)
        {
            return;
        }

        // Same clock as the particle contributors: frozen while the game is paused, scaled by 1x/2x/3x otherwise.
        float dt = MathF.Max(0f, MathF.Min(0.1f, UnityEngine.Time.unscaledDeltaTime));
        TickManager? tickManager = Current.Game?.tickManager;
        dt = tickManager != null && tickManager.Paused ? 0f : dt * (tickManager?.TickRateMultiplier ?? 1f);
        state.OrbitTheta = (state.OrbitTheta + 1.6f * dt) % (MathF.PI * 2f);
        state.PulseTime += dt;

        float yMote = AltitudeLayer.MoteOverhead.AltitudeFor();
        if (state.LdrMaterial is not null)
        {
            var ldrPos = new UnityEngine.Vector3(state.Station1Anchor.x + 0.5f, yMote, state.Station1Anchor.z + 4.5f);
            var ldrMatrix = UnityEngine.Matrix4x4.TRS(ldrPos, UnityEngine.Quaternion.identity, new UnityEngine.Vector3(2.6f, 1f, 2.6f));
            UnityEngine.Graphics.DrawMesh(MeshPool.plane10, ldrMatrix, state.LdrMaterial, 0);
        }

        if (state.MoteGlowMaterial is not null)
        {
            var compatPos = new UnityEngine.Vector3(state.Station6Anchor.x + 0.5f, yMote, state.Station6Anchor.z + 0.5f);
            float outerScale = 3.2f + 0.9f * MathF.Sin(state.PulseTime * 2.4f);
            float innerScale = 2.0f + 0.6f * MathF.Cos(state.PulseTime * 3.1f);
            var outerRot = UnityEngine.Quaternion.AngleAxis(state.PulseTime * 25f, UnityEngine.Vector3.up);
            var innerRot = UnityEngine.Quaternion.AngleAxis(-state.PulseTime * 45f, UnityEngine.Vector3.up);

            UnityEngine.Graphics.DrawMesh(MeshPool.plane10,
                UnityEngine.Matrix4x4.TRS(compatPos, outerRot, new UnityEngine.Vector3(outerScale, 1f, outerScale)),
                state.MoteGlowMaterial, 0);
            UnityEngine.Graphics.DrawMesh(MeshPool.plane10,
                UnityEngine.Matrix4x4.TRS(compatPos + new UnityEngine.Vector3(0f, 0.02f, 0f), innerRot, new UnityEngine.Vector3(innerScale, 1f, innerScale)),
                state.MoteGlowMaterial, 0);
        }
    }

    // Ensures: draws toggleable world-space station captions (#1..#6) above each station when showcase is active.
    public override void MapComponentOnGUI()
    {
        if (!ShowcaseStage.TryGetActiveState(map, out var state) || state is null || !state.ShowLabels)
        {
            return;
        }
        if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap != map || WorldRendererUtility.WorldSelected)
        {
            return;
        }

        GameFont prevFont = Text.Font;
        UnityEngine.TextAnchor prevAnchor = Text.Anchor;
        UnityEngine.Color prevColor = UnityEngine.GUI.color;
        try
        {
            Text.Font = GameFont.Small;
            Text.Anchor = UnityEngine.TextAnchor.MiddleCenter;
            const string banner = "[RimLumen Showcase] #1..#6 从左到右实时运行中 (DevMode -> RimLumen 可开关对照)";
            float bannerW = MathF.Min(UI.screenWidth - 40f, Text.CalcSize(banner).x + 28f);
            var bannerRect = new UnityEngine.Rect((UI.screenWidth - bannerW) * 0.5f, 112f, bannerW, 28f);
            Widgets.DrawBoxSolidWithOutline(bannerRect,
                new UnityEngine.Color(0.05f, 0.07f, 0.12f, 0.85f),
                new UnityEngine.Color(0.35f, 0.72f, 1.0f, 0.65f), 1);
            UnityEngine.GUI.color = new UnityEngine.Color(0.92f, 0.96f, 1.0f, 1f);
            Widgets.Label(bannerRect, banner);

            Text.Font = GameFont.Tiny;
            foreach (ShowcaseStationInfo station in state.Stations)
            {
                IntVec3 labelCell = new(station.AnchorCell.x, 0, Math.Min(map.Size.z - 1, station.AnchorCell.z + 8));
                DrawWorldPill(labelCell, station.Title);
            }
            DrawWorldPill(new IntVec3(state.Station1Anchor.x, 0, state.Station1Anchor.z + 6), "上: 8-bit 封顶无泛光");
            DrawWorldPill(new IntVec3(state.Station1Anchor.x, 0, state.Station1Anchor.z - 6), "下: RGBA16F 炽核+橘金泛光");
            IntVec3 anchor4 = state.Stations[3].AnchorCell;
            DrawWorldPill(new IntVec3(anchor4.x, 0, anchor4.z + 6), "上: 硬切穿墙 (fade=0)");
            DrawWorldPill(new IntVec3(anchor4.x, 0, anchor4.z - 6), "下: 软粒子柔化 (fade=1.4)");
        }
        finally
        {
            Text.Font = prevFont;
            Text.Anchor = prevAnchor;
            UnityEngine.GUI.color = prevColor;
        }
    }

    private static void DrawWorldPill(IntVec3 cell, string text)
    {
        UnityEngine.Vector2 screenPos = GenMapUI.LabelDrawPosFor(cell);
        if (screenPos.x < -120f || screenPos.x > UI.screenWidth + 120f || screenPos.y < 0f || screenPos.y > UI.screenHeight)
        {
            return;
        }
        float w = Text.CalcSize(text).x + 14f;
        var pillRect = new UnityEngine.Rect(screenPos.x - w * 0.5f, screenPos.y - 10f, w, 20f);
        Widgets.DrawBoxSolidWithOutline(pillRect,
            new UnityEngine.Color(0.05f, 0.07f, 0.12f, 0.82f),
            new UnityEngine.Color(0.35f, 0.65f, 0.95f, 0.55f), 1);
        UnityEngine.GUI.color = UnityEngine.Color.white;
        Widgets.Label(pillRect, text);
    }

    // Ensures: releases all showcase handles and restores global flags when the map unloads.
    public override void MapRemoved() => Dispose();

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        ShowcaseStage.Clear(map);
    }
}
