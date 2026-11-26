using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using LudeonTK;
using RimLumen.Core.Contracts;
using RimLumen.Core.Demo;
using RimLumen.Core.Mod;
using RimLumen.Core.Rendering;
using RimLumen.Core.Scene;
using RimWorld;
using Verse;
using Waylith;

namespace RimLumen.Core.Showcase;

// One trailer take (ShowcaseTrailerTimeline): rebuilds the showcase idle, holds the first frame for the lead-in, then plays
// segment A (pan + stations + fireworks), a frozen second and segment B (the vortex beside walls with pack switches and
// force arrows). Every segment boundary logs "[RimLumen trailer] ..." with wall-clock and take time for the cut list.
// Music, pack, arrows, camera, time speed, screenshot mode and window visibility are restored afterwards; nothing is saved
// to disk. The arena terrain and time of day stay as the showcase leaves them, and a showcase active before the take is gone.
internal static class ShowcaseTrailerSession
{
    private const string VortexEffectId = "rimlumen.showcase.trailer_vortex";
    private static Take? active;

    // Requires: main thread, playing on map.
    // Ensures: starts a take unless one is running (then logs and returns false).
    internal static bool TryStart(Map map)
    {
        if (active is not null)
        {
            Log.Warning("[RimLumen trailer] A take is already running.");
            return false;
        }
        var take = new Take(map);
        active = take;
        try
        {
            Current.CameraDriver.StartCoroutine(Run(take));
        }
        catch
        {
            active = null;
            throw;
        }
        return true;
    }

    private static IEnumerator Run(Take take)
    {
        IEnumerator? body = null;
        try
        {
            take.Prepare();
            body = take.Play();
        }
        catch (Exception ex)
        {
            Log.Error("[RimLumen trailer] Take setup failed: " + ex);
        }

        // A coroutine cannot catch around a yield, so failures are caught at the MoveNext boundary.
        while (body is not null)
        {
            bool moved;
            try
            {
                moved = body.MoveNext();
            }
            catch (Exception ex)
            {
                Log.Error("[RimLumen trailer] Take failed: " + ex);
                break;
            }
            if (!moved) break;
            yield return body.Current;
        }

        try
        {
            take.Restore();
        }
        catch (Exception ex)
        {
            Log.Error("[RimLumen trailer] Take cleanup failed: " + ex);
        }
    }

    private sealed class Take
    {
        private readonly Map map;
        private readonly TickManager tickManager;
        private readonly EngineApplication app = EngineApplication.Instance;
        private readonly Dictionary<Window, bool> windowVisibility = new();
        private readonly List<ILumenEffectHandle> handles = new();
        private readonly List<Thing> walls = new();
        private readonly Stopwatch clock = new();
        private IntVec3 center;

        private bool prepared;
        private bool finished;
        private TimeSpeed savedTimeSpeed;
        private bool savedCameraDriver;
        private UnityEngine.Vector3 savedCameraPosition;
        private float savedRootSize;
        private bool savedScreenshotMode;
        private float savedMusicVolume;
        private string savedPackId = ShaderPackCatalog.DefaultPackId;
        private bool savedVanillaAdditiveBloom;
        private bool savedArrows;

        internal Take(Map map)
        {
            this.map = map;
            tickManager = Find.TickManager;
        }

        internal void Prepare()
        {
            RimLumenSettings settings = RimLumenMod.Settings;
            IReadOnlyList<ShaderPackEntry> packs = ShaderPackCatalog.GetAvailablePacks();
            foreach (string id in new[] { ShaderPackCatalogIds.Standard, ShaderPackCatalogIds.Crisp, ShaderPackCatalogIds.Foggy })
                if (!string.Equals(ShaderPackCatalog.FindOrDefault(id, packs).Id, id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Built-in shader pack '{id}' is unavailable.");

            // A showcase forces EnableVanillaAdditiveBloom on; clearing it first restores the player's value before it is saved.
            ShowcaseStage.ClearActive();
            savedTimeSpeed = tickManager.CurTimeSpeed;
            savedCameraDriver = Current.CameraDriver.enabled;
            savedCameraPosition = Find.Camera.transform.position;
            savedRootSize = Current.CameraDriver.RootSize;
            savedScreenshotMode = Find.UIRoot.screenshotMode.Active;
            savedMusicVolume = Prefs.VolumeMusic;
            savedPackId = settings.SelectedShaderPackId;
            savedVanillaAdditiveBloom = RimLumenRenderHost.EnableVanillaAdditiveBloom;
            savedArrows = ShowcaseForceArrows.Enabled;
            prepared = true;

            // Dialog_Debug forces a pause, which would freeze the take; it is the window this action was started from.
            Find.WindowStack.TryRemove(typeof(Dialog_Debug), doCloseSound: false);
            // In-memory only: the setter applies the volume; Prefs.Save is never called.
            Prefs.VolumeMusic = 0f;
            Find.UIRoot.screenshotMode.Active = true;
            ScreenFader.SetColor(UnityEngine.Color.clear);
            app.OnUpdate += OnUpdate;
            HideWindows();

            // Compile every pack once now, so the switches inside the take reuse cached shaders instead of hitching.
            // The last one, Foggy, is segment A's pack.
            foreach (string id in new[] { ShaderPackCatalogIds.Standard, ShaderPackCatalogIds.Crisp, ShaderPackCatalogIds.Foggy })
                SwitchPack(id, log: false);
            // Build the arrow renderer now, so a failure stops the take before it starts and the arrows cue does not hitch.
            if (!ShowcaseForceArrows.SetEnabled(true)) throw new InvalidOperationException("Force arrows failed to start.");
            ShowcaseForceArrows.SetEnabled(false);

            ShowcaseStage.BuildOrRebuild(map, startStations: false);
            ShowcaseStage.TryGetActiveState(map, out ShowcaseStage.ActiveShowcaseState? state);
            state!.ShowLabels = false;
            center = ShowcaseStage.ComputeArenaCenter(map.Center, map.Size);
            RimLumenDemoEffects.EnsureRegistered();
            LumenSceneBootstrap.Catalog.Register(CreateVortexAsset());
            tickManager.CurTimeSpeed = TimeSpeed.Normal;
            Current.CameraDriver.enabled = false;
            PlaceCamera(ShowcaseTrailerTimeline.PanX(0f), 0f, ShowcaseTrailerTimeline.PanRootSize);
        }

        // Leaving the game or map mid-take ends the coroutine with the play scene, so Restore never runs; this restores the
        // state that outlives the game.
        private void OnUpdate()
        {
            if (Current.ProgramState != ProgramState.Playing || !Find.Maps.Contains(map))
            {
                if (Finish())
                {
                    RestoreGlobals();
                    Log.Warning("[RimLumen trailer] Map left during the take; restored pack, arrows and music only.");
                }
                return;
            }
            HideWindows();
        }

        // ScreenshotMode hides letters/messages/alerts and the dev toolbar; windows opt out individually.
        private void HideWindows()
        {
            foreach (Window window in Find.WindowStack.Windows)
            {
                if (!windowVisibility.ContainsKey(window)) windowVisibility.Add(window, window.drawInScreenshotMode);
                window.drawInScreenshotMode = false;
            }
        }

        internal IEnumerator Play()
        {
            Mark("lead-in start", 0f);
            var leadIn = Stopwatch.StartNew();
            while (leadIn.Elapsed.TotalSeconds < ShowcaseTrailerTimeline.LeadInSeconds)
            {
                if (finished) yield break;
                yield return new UnityEngine.WaitForEndOfFrame();
            }

            clock.Start();
            int next = 0;
            TrailerCue[] cues = ShowcaseTrailerTimeline.Cues;
            bool segmentB = false;
            while (!finished)
            {
                float t = (float)clock.Elapsed.TotalSeconds;
                while (next < cues.Length && cues[next].Time <= t)
                {
                    TrailerCue cue = cues[next++];
                    if (cue.Kind == TrailerCueKind.End)
                    {
                        Mark("end", t);
                        yield break;
                    }
                    if (cue.Kind == TrailerCueKind.SegmentBStart) segmentB = true;
                    Apply(cue, t);
                }
                if (segmentB)
                    PlaceCamera(ShowcaseTrailerTimeline.VortexCameraX, ShowcaseTrailerTimeline.VortexCameraZ, ShowcaseTrailerTimeline.VortexRootSize);
                else
                    PlaceCamera(ShowcaseTrailerTimeline.PanX(MathF.Min(t, ShowcaseTrailerTimeline.SegmentAEnd)), 0f,
                        ShowcaseTrailerTimeline.PanRootSize);
                yield return new UnityEngine.WaitForEndOfFrame();
            }
        }

        private void Apply(TrailerCue cue, float t)
        {
            switch (cue.Kind)
            {
                case TrailerCueKind.SegmentAStart:
                    Mark("segment A start", t);
                    break;
                case TrailerCueKind.StartStation:
                    ShowcaseStage.StartStation(map, cue.Index);
                    break;
                case TrailerCueKind.LaunchFirework:
                    (float x, float z) = ShowcaseTrailerTimeline.FireworkOffsets[cue.Index];
                    handles.Add(LumenSceneBootstrap.Service.Play(new LumenPlayRequest
                    {
                        EffectId = RimLumenDemoEffects.FireworkEffectIds[cue.Index % RimLumenDemoEffects.FireworkEffectIds.Length],
                        MapIndex = map.uniqueID,
                        OriginWorld = new Vector3(center.x + 0.5f + x, 0f, center.z + 0.5f + z),
                    }));
                    break;
                case TrailerCueKind.Freeze:
                    tickManager.Pause();
                    Mark("freeze start", t);
                    break;
                case TrailerCueKind.SegmentBStart:
                    StopHandles();
                    ShowcaseStage.Clear(map);
                    BuildVortexScene();
                    tickManager.CurTimeSpeed = TimeSpeed.Normal;
                    Mark("segment B start", t);
                    break;
                case TrailerCueKind.SwitchPack:
                    SwitchPack(cue.PackId!, log: true, t);
                    break;
                case TrailerCueKind.ArrowsOn:
                    if (!ShowcaseForceArrows.SetEnabled(true)) throw new InvalidOperationException("Force arrows failed to start.");
                    Mark("force arrows on", t);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(cue), cue.Kind, null);
            }
        }

        // The player's hot-switch path (RimLumenMod pack menu): settings id + ApplyToRuntime, without writing settings.
        private void SwitchPack(string id, bool log, float t = 0f)
        {
            RimLumenSettings settings = RimLumenMod.Settings;
            settings.SelectedShaderPackId = id;
            settings.ApplyToRuntime();
            if (!string.Equals(RimLumenRenderHost.ActiveShaderPack.Id, id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Shader pack '{id}' did not become active: {RimLumenRenderHost.LastShaderPackError}");
            if (log) Mark("pack " + id, t);
        }

        // A two-cell-deep granite wall behind the vortex and a short return on its right: the vortex overlaps both, so the
        // soft-particle fade shows where the particles meet the walls.
        private void BuildVortexScene()
        {
            for (int dx = -3; dx <= 3; dx++)
                for (int dz = 2; dz <= 3; dz++)
                    SpawnWall(new IntVec3(center.x + dx, 0, center.z + dz));
            for (int dz = -2; dz <= 1; dz++)
                SpawnWall(new IntVec3(center.x + 3, 0, center.z + dz));
            handles.Add(LumenSceneBootstrap.Service.Play(new LumenPlayRequest
            {
                EffectId = VortexEffectId,
                MapIndex = map.uniqueID,
                OriginWorld = new Vector3(center.x + 0.5f, 0f, center.z + 1.5f),
            }));
        }

        private void SpawnWall(IntVec3 cell)
        {
            if (!cell.InBounds(map)) return;
            Thing wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.BlocksGranite);
            GenSpawn.Spawn(wall, cell, map);
            walls.Add(wall);
        }

        private void PlaceCamera(float offsetX, float offsetZ, float rootSize) =>
            Current.CameraDriver.SetRootPosAndSize(
                new UnityEngine.Vector3(center.x + 0.5f + offsetX, 0f, center.z + 0.5f + offsetZ), rootSize);

        private void StopHandles()
        {
            foreach (ILumenEffectHandle handle in handles) handle.Stop(allowFadeOut: false);
            handles.Clear();
        }

        // Ends the take once: drops the per-frame hook and frees the slot for the next take. False if already ended.
        private bool Finish()
        {
            if (finished) return false;
            finished = true;
            app.OnUpdate -= OnUpdate;
            active = null;
            return true;
        }

        internal void Restore()
        {
            if (!Finish() || !prepared) return;
            StopHandles();
            foreach (Thing wall in walls)
                if (wall.Spawned && !wall.Destroyed) wall.Destroy(DestroyMode.Vanish);
            walls.Clear();
            ShowcaseStage.Clear(map);
            RestoreGlobals();
            foreach (KeyValuePair<Window, bool> entry in windowVisibility)
                entry.Key.drawInScreenshotMode = entry.Value;
            Find.UIRoot.screenshotMode.Active = savedScreenshotMode;
            Current.CameraDriver.enabled = savedCameraDriver;
            Current.CameraDriver.SetRootPosAndSize(savedCameraPosition, savedRootSize);
            tickManager.CurTimeSpeed = savedTimeSpeed;
            Log.Message("[RimLumen trailer] restored");
        }

        // State that outlives the game: the vortex asset, arrows, the selected pack, the bloom flag and the music volume.
        private void RestoreGlobals()
        {
            if (LumenSceneBootstrap.IsInitialized) LumenSceneBootstrap.Catalog.Unregister(VortexEffectId);
            ShowcaseForceArrows.SetEnabled(savedArrows);
            RimLumenSettings settings = RimLumenMod.Settings;
            settings.SelectedShaderPackId = savedPackId;
            settings.ApplyToRuntime();
            // ApplyToRuntime re-applies the settings scalars; the runtime flag may have been toggled by a DebugAction.
            RimLumenRenderHost.EnableVanillaAdditiveBloom = savedVanillaAdditiveBloom;
            Prefs.VolumeMusic = savedMusicVolume;
        }

        private static void Mark(string what, float takeSeconds) =>
            Log.Message($"[RimLumen trailer] {what} t={takeSeconds:F3}s at {DateTime.Now:HH:mm:ss.fff}");

        // Segment B: L2 vortex + curl turbulence + attractor + sideways wind/drag, soft-faded against the walls. The
        // GravityDrag direction is horizontal so its arrow survives the top-down camera (a vertical one projects to 0).
        private static EffectAsset CreateVortexAsset() => new()
        {
            EffectId = VortexEffectId,
            DefaultDuration = 10f,
            Nodes = new[]
            {
                new EffectNodeSpec
                {
                    Name = "trailer_vortex",
                    LocalPosition = new Vector3(0f, 0.6f, 0f),
                    Emitter = new EmitterSpec
                    {
                        Name = "trailer_vortex",
                        PreferredTier = LumenParticleTier.L2_GpuCompute,
                        MaxParticles = 9_000,
                        Duration = 10f,
                        Looping = true,
                        WorldSpaceSimulation = false,
                        RateOverTime = 3_000f,
                        Bursts = new[] { new ParticleBurstSpec(0f, 4_000) },
                        Shape = LumenShapeKind.Box,
                        ShapeExtents = new Vector3(2.2f, 0.3f, 2.2f),
                        StartLifetimeMin = 2.4f,
                        StartLifetimeMax = 3.2f,
                        StartSpeedMin = 0.2f,
                        StartSpeedMax = 0.5f,
                        StartSizeMin = 0.09f,
                        StartSizeMax = 0.17f,
                        StartColor = Vector4.One,
                        HdrIntensity = 1.6f,
                        // Same top-down altitude gap as station #4: 9 dims the vortex over the walls, not over the ground.
                        SoftParticleFadeDistance = 9f,
                        ColorOverLifetime = new ColorGradient(new[]
                        {
                            new ColorStop(0f, new Vector4(0.15f, 0.75f, 1.0f, 0.0f)),
                            new ColorStop(0.20f, new Vector4(0.15f, 0.75f, 1.0f, 0.85f)),
                            new ColorStop(0.65f, new Vector4(0.70f, 0.30f, 1.0f, 0.80f)),
                            new ColorStop(1f, new Vector4(1.0f, 0.65f, 0.20f, 0.0f)),
                        }),
                        ForceFields = new[]
                        {
                            new ForceFieldSpec(ForceFieldKind.Vortex, Vector3.UnitY, new Vector3(0.8f, 0.3f, 0f), 6f, 5.2f),
                            new ForceFieldSpec(ForceFieldKind.CurlTurbulence, Vector3.Zero, Vector3.Zero, 6f, 1.6f, 0.9f),
                            new ForceFieldSpec(ForceFieldKind.PointAttractor, Vector3.Zero, Vector3.Zero, 6f, 2.0f),
                            new ForceFieldSpec(ForceFieldKind.GravityDrag, Vector3.Normalize(new Vector3(1f, 0f, -0.5f)), new Vector3(0.6f, 0f, 0f), 0f, 1.2f),
                        },
                    },
                },
            },
        };
    }
}
