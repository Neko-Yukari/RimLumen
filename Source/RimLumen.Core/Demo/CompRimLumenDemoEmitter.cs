using System;
using System.Collections.Generic;
using RimLumen.Core.Api;
using RimLumen.Core.Contracts;
using RimWorld;
using Verse;

namespace RimLumen.Core.Demo;

public enum DemoEmitterKind
{
    Firework,
    Fountain,
    StreetLamp,
    DistortionPylon,
}

public sealed class CompProperties_RimLumenDemoEmitter : CompProperties
{
    public DemoEmitterKind kind = DemoEmitterKind.Firework;

    public CompProperties_RimLumenDemoEmitter()
    {
        compClass = typeof(CompRimLumenDemoEmitter);
    }
}

// Drives the optional demo buildings. Every effect is played with realTime = false, so the play handle
// follows the game clock: the firework trail, burst and light all freeze mid-air while the game is
// paused and scale with 1x/2x/3x, with no per-frame work on this component beyond the tick counter.
// The fountain, the street lamp and the distortion pylon keep one steady looping effect alive; only the fountain
// can be toggled. The pylon also re-fires its shock ring on a tick timer and turns its orbit by the game tick.
public sealed class CompRimLumenDemoEmitter : ThingComp
{
    // 3.2 s between automatic launches; the visible lifetime comes from the firework asset itself.
    private const int AutoLaunchIntervalTicks = 192;
    private const int TicksPerSecond = 60;
    private static readonly int FireworkVisibleTicks =
        (int)MathF.Ceiling(RimLumenDemoEffects.FireworkVisibleSeconds * TicksPerSecond);
    private const int MaxConcurrentFireworks = 3;
    // 2.5 s between pylon shock rings; one orbit every 90 ticks (1.5 s at 1x).
    private const int PylonPulseIntervalTicks = 150;
    private const int PylonOrbitTicks = 90;

    private sealed class ActiveLaunch
    {
        public readonly ILumenEffectHandle Handle;
        public int AgeTicks;

        public ActiveLaunch(ILumenEffectHandle handle) => Handle = handle;
    }

    private readonly List<ActiveLaunch> launches = new();
    private ILumenEffectHandle? steady;
    private bool autoLaunch = true;
    private bool fountainActive = true;
    private int autoLaunchTicks;
    private int pylonPulseTicks;
    private int variantCursor;

    private CompProperties_RimLumenDemoEmitter Props => (CompProperties_RimLumenDemoEmitter)props;

    public override void CompTick()
    {
        AdvanceLaunches();
        if (Props.kind == DemoEmitterKind.Firework)
        {
            if (!autoLaunch) return;
            if (++autoLaunchTicks < AutoLaunchIntervalTicks) return;
            autoLaunchTicks = 0;
            LaunchFirework();
            return;
        }

        if (fountainActive && steady is not { IsAlive: true }) StartSteady();
        if (Props.kind != DemoEmitterKind.DistortionPylon || ++pylonPulseTicks < PylonPulseIntervalTicks) return;
        pylonPulseTicks = 0;
        steady?.Trigger(RimLumenDemoEffects.PylonShockRingNode);
    }

    public override void PostSpawnSetup(bool respawningAfterLoad)
    {
        base.PostSpawnSetup(respawningAfterLoad);
        // The lamp and the pylon have no toggle: a Play that failed in an earlier session is retried on every spawn.
        if (Props.kind is DemoEmitterKind.StreetLamp or DemoEmitterKind.DistortionPylon ||
            (Props.kind == DemoEmitterKind.Fountain && fountainActive)) StartSteady();
    }

    public override void PostDeSpawn(Map map, DestroyMode mode)
    {
        ReleaseAll();
        base.PostDeSpawn(map, mode);
    }

    public override void PostDestroy(DestroyMode mode, Map previousMap)
    {
        ReleaseAll();
        base.PostDestroy(mode, previousMap);
    }

    public override void PostExposeData()
    {
        base.PostExposeData();
        Scribe_Values.Look(ref autoLaunch, "rimLumenDemoAutoLaunch", true);
        Scribe_Values.Look(ref fountainActive, "rimLumenDemoFountainActive", true);
    }

    public override IEnumerable<Gizmo> CompGetGizmosExtra()
    {
        if (Props.kind == DemoEmitterKind.Firework)
        {
            var launchCommand = new Command_Action
            {
                defaultLabel = "立即发射烟花 (Launch Firework)",
                defaultDesc = "立刻在建筑上方放一发烟花；自动连放开启时也会按节奏继续发射。\n" +
                              "Launch one firework now. Automatic launches keep running when they are on.",
                icon = parent.def.uiIcon,
                action = LaunchFirework,
            };
            if (launches.Count >= MaxConcurrentFireworks)
                launchCommand.Disable("已达到同时发射上限 (Max concurrent fireworks active)");
            yield return launchCommand;
            yield return new Command_Action
            {
                defaultLabel = autoLaunch
                    ? "自动连放：开 (Auto-launch: on)"
                    : "自动连放：关 (Auto-launch: off)",
                defaultDesc = "开启后每 3.2 秒自动发射一发烟花。\n" +
                              "Launches one firework every 3.2 seconds while enabled.",
                icon = parent.def.uiIcon,
                action = () => autoLaunch = !autoLaunch,
            };
            yield break;
        }

        if (Props.kind != DemoEmitterKind.Fountain) yield break;
        yield return new Command_Action
        {
            defaultLabel = fountainActive
                ? "喷泉开关：开 (Fountain Active: on)"
                : "喷泉开关：关 (Fountain Active: off)",
            defaultDesc = "关闭后喷泉立即停止喷水并释放粒子。\n" +
                          "Turning it off stops the jet and releases its particles immediately.",
            icon = parent.def.uiIcon,
            action = ToggleFountain,
        };
    }

    private void ToggleFountain()
    {
        fountainActive = !fountainActive;
        if (fountainActive)
        {
            StartSteady();
            return;
        }

        ReleaseSteady();
    }

    private void LaunchFirework()
    {
        if (launches.Count >= MaxConcurrentFireworks || parent.Map is null) return;

        RimLumenDemoEffects.EnsureRegistered();
        string effectId = RimLumenDemoEffects.FireworkEffectIds[
            variantCursor++ % RimLumenDemoEffects.FireworkEffectIds.Length];
        ILumenEffectHandle handle = RimLumenApi.Play(new LumenPlayRequest
        {
            EffectId = effectId,
            MapIndex = parent.Map.uniqueID,
            OriginWorld = EffectOrigin,
            RealTime = false,
        });
        if (!handle.IsAlive) return;

        launches.Add(new ActiveLaunch(handle));
    }

    private void StartSteady()
    {
        if (parent.Map is null) return;

        RimLumenDemoEffects.EnsureRegistered();
        System.Numerics.Vector3 origin = EffectOrigin;
        ILumenEffectHandle handle = RimLumenApi.Play(new LumenPlayRequest
        {
            EffectId = Props.kind switch
            {
                DemoEmitterKind.Fountain => RimLumenDemoEffects.ParticleFountainId,
                DemoEmitterKind.DistortionPylon => RimLumenDemoEffects.DistortionPylonId,
                _ => RimLumenDemoEffects.StreetLampId,
            },
            MapIndex = parent.Map.uniqueID,
            OriginWorld = origin,
            // The pylon's orbit: the root yaw follows the game tick, so it stops while paused and speeds up at 2x/3x.
            Follow = Props.kind == DemoEmitterKind.DistortionPylon
                ? (out System.Numerics.Vector3 position, out float rotationDegrees) =>
                {
                    position = origin;
                    rotationDegrees = Find.TickManager.TicksGame % PylonOrbitTicks * (360f / PylonOrbitTicks);
                    return true;
                }
                : null,
            RealTime = false,
        });
        if (!handle.IsAlive)
        {
            fountainActive = false;
            return;
        }

        steady = handle;
        fountainActive = true;
    }

    // Ensures: retires fireworks that have shown their whole 2.75 s arc; Stop(true) lets the last embers
    //          and the light fade finish on the service instead of cutting them off.
    private void AdvanceLaunches()
    {
        for (int i = launches.Count - 1; i >= 0; i--)
        {
            ActiveLaunch launch = launches[i];
            if (launch.Handle.IsAlive && ++launch.AgeTicks < FireworkVisibleTicks) continue;
            launch.Handle.Stop(allowFadeOut: true);
            launches.RemoveAt(i);
        }
    }

    private void ReleaseAll()
    {
        for (int i = 0; i < launches.Count; i++)
        {
            launches[i].Handle.Stop(allowFadeOut: false);
            launches[i].Handle.Dispose();
        }

        launches.Clear();
        ReleaseSteady();
    }

    private void ReleaseSteady()
    {
        if (steady is null) return;
        steady.Stop(allowFadeOut: false);
        steady.Dispose();
        steady = null;
    }

    private System.Numerics.Vector3 EffectOrigin
    {
        get
        {
            UnityEngine.Vector3 drawPos = parent.DrawPos;
            return new System.Numerics.Vector3(drawPos.x, 0f, drawPos.z);
        }
    }
}
