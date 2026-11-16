using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using RimLumen.Core.Mod;
using RimLumen.Core.Rendering;
using RimWorld;
using UnityStubs.Rendering;
using Verse;
using Waylith;
using Waylith.UnityCompat;

namespace RimLumen.Core.Showcase;

internal static class ShowcaseGlowABSession
{
    private const string ArgumentPrefix = "-rimlumen-glow-ab=";
    private const string BloomPrefix = "-rimlumen-glow-ab-bloom=";
    // Optional (intensity, threshold, knee) override of the Standard pack's default bloom; knee is optional.
    private static (float Intensity, float Threshold, float? Knee)? bloomOverride;
    private const float CameraSize = 12f;

    internal static bool TryStart(IReadOnlyList<string> args)
    {
        string[] requests = args.Where(arg => arg.StartsWith(ArgumentPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (requests.Length == 0) return false;
        try
        {
            string outputDir = requests[0].Substring(ArgumentPrefix.Length);
            if (requests.Length != 1 || string.IsNullOrWhiteSpace(outputDir))
                throw new ArgumentException("Glow A/B requires one output directory.");
            if (args.Any(arg => arg.StartsWith("-rimlumen-validation-save=", StringComparison.OrdinalIgnoreCase) ||
                                arg.StartsWith("-rimlumen-showcase-capture=", StringComparison.OrdinalIgnoreCase) ||
                                arg.StartsWith("-rimlumen-showcase-bench=", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Glow A/B cannot be combined with validation-save, capture or benchmark.");
            string? bloom = args.FirstOrDefault(arg => arg.StartsWith(BloomPrefix, StringComparison.OrdinalIgnoreCase));
            if (bloom != null)
            {
                string[] parts = bloom.Substring(BloomPrefix.Length).Split(',');
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var style = System.Globalization.NumberStyles.Float;
                float knee = 0f;
                if (parts.Length is not (2 or 3) ||
                    !float.TryParse(parts[0], style, inv, out float intensity) ||
                    !float.TryParse(parts[1], style, inv, out float threshold) ||
                    (parts.Length == 3 && !float.TryParse(parts[2], style, inv, out knee)))
                    throw new ArgumentException("Glow A/B bloom override must be 'intensity,threshold[,knee]': " + bloom);
                bloomOverride = (intensity, threshold, parts.Length == 3 ? knee : null);
            }
            outputDir = Path.GetFullPath(outputDir);
            Directory.CreateDirectory(outputDir);
            EngineApplication app = EngineApplication.Instance;
            void UpdateGlowAB()
            {
                if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || LongEventHandler.AnyEventNowOrWaiting)
                    return;
                app.OnUpdate -= UpdateGlowAB;
                try { Current.CameraDriver.StartCoroutine(RunAndShutdown(Find.CurrentMap, outputDir)); }
                catch (Exception ex)
                {
                    Log.Error("[RimLumen] Glow A/B could not start: " + ex);
                    Shutdown(1);
                }
            }
            app.OnUpdate += UpdateGlowAB;
        }
        catch (Exception ex)
        {
            Log.Error("[RimLumen] Glow A/B arguments failed: " + ex);
            Shutdown(1);
        }
        return true;
    }

    private static IEnumerator RunAndShutdown(Map map, string outputDir)
    {
        IEnumerator sequence = RunSequence(map, outputDir);
        int exitCode = 1;
        while (true)
        {
            bool moved;
            try
            {
                // Crashlanded pods can deliver colonists after the first map-ready frame.
                foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned.ToArray()) pawn.DeSpawn();
                foreach (Window window in Find.WindowStack.Windows) window.drawInScreenshotMode = false;
                moved = sequence.MoveNext();
            }
            catch (Exception ex)
            {
                Log.Error("[RimLumen] Glow A/B failed: " + ex);
                break;
            }
            if (!moved) { exitCode = 0; break; }
            yield return sequence.Current;
        }
        Shutdown(exitCode);
    }

    private static IEnumerator RunSequence(Map map, string outputDir)
    {
        IntVec3 center = map.Center;
        CellRect arena = CellRect.CenteredOn(center, 15, 15);
        ScreenFader.SetColor(UnityEngine.Color.clear);
        Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
        Find.UIRoot.screenshotMode.Active = true;
        foreach (Window window in Find.WindowStack.Windows) window.drawInScreenshotMode = false;
        foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned.ToArray()) pawn.DeSpawn();
        foreach (IntVec3 cell in arena) map.roofGrid.SetRoof(cell, null);
        foreach (IntVec3 cell in arena)
        {
            foreach (Thing thing in cell.GetThingList(map).ToArray())
                if (thing.Spawned && !thing.Destroyed && thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);
            map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
            map.fogGrid.Unfog(cell);
        }
        map.roofCollapseBufferResolver.CollapseRoofsMarkedToCollapse();
        // Clear both sides of the vanilla weather transition so random quicktest rain cannot extinguish the sample.
        map.weatherManager.TransitionTo(WeatherDefOf.Clear);
        map.weatherManager.TransitionTo(WeatherDefOf.Clear);
        float delta = 22f / 24f - GenLocalDate.DayPercent(map);
        if (delta < 0f) delta += 1f;
        Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + (int)MathF.Round(delta * GenDate.TicksPerDay));
        map.skyManager.SkyManagerUpdate();
        if (!RimLumenRenderHost.ReloadShaderPack(ShaderPackCatalog.StandardEntry.Spec))
            throw new InvalidOperationException("Standard ShaderPack reload failed: " + RimLumenRenderHost.LastShaderPackError);
        ShaderPackCatalog.ApplyOptionsToRuntime(ShaderPackCatalog.StandardEntry, null, RimLumenRenderHost.ShaderPackParams);
        if (bloomOverride is { } b)
        {
            RimLumenRenderHost.ShaderPackParams.BloomIntensity = b.Intensity;
            RimLumenRenderHost.ShaderPackParams.BloomThreshold = b.Threshold;
            if (b.Knee is { } k) RimLumenRenderHost.ShaderPackParams.BloomKnee = k;
        }
        Current.CameraDriver.JumpToCurrentMapLoc(center);
        Current.CameraDriver.SetRootSize(CameraSize);
        // Let SetRootSize apply the camera transform before locking out camera motion.
        yield return new UnityEngine.WaitForEndOfFrame();
        Current.CameraDriver.enabled = false;

        for (int x = -4; x <= -1; x++)
            for (int z = -1; z <= 1; z++) StartFire(map, center + new IntVec3(x, 0, z));
        StartFire(map, center + new IntVec3(6, 0, 0));
        Log.Message($"[RimLumen] Glow A/B scene: arena={arena}; cluster=({center.x - 4},{center.z - 1})..({center.x - 1},{center.z + 1}) (12 fires); isolated=({center.x + 6},{center.z}); fireSize=0.9; cameraCenter={center}; rootSize={CameraSize}; all spawned pawns despawned; Standard bloom intensity={RimLumenRenderHost.ShaderPackParams.BloomIntensity} threshold={RimLumenRenderHost.ShaderPackParams.BloomThreshold} knee={RimLumenRenderHost.ShaderPackParams.BloomKnee}; localHour={GenLocalDate.DayPercent(map) * 24f:F3}.");

        var wait = Stopwatch.StartNew();
        while (CountFireGlow(map) == 0)
        {
            if (wait.Elapsed.TotalSeconds > 30) throw new TimeoutException("No live FireGlow fleck appeared within 30 seconds.");
            if (Find.TickManager.CurTimeSpeed != TimeSpeed.Normal || Find.TickManager.Paused)
                throw new InvalidOperationException("Glow A/B simulation is not running at normal speed.");
            yield return new UnityEngine.WaitForEndOfFrame();
        }
        // FireGlow fades in over 0.75 s (Fleck_Visual.xml); let it reach full alpha, then pause so the three captures
        // share one simulation state and on-vs-on2 isolates capture noise from fleck lifetime changes.
        int settleTick = Find.TickManager.TicksGame + 180;
        while (Find.TickManager.TicksGame < settleTick)
        {
            if (wait.Elapsed.TotalSeconds > 60) throw new TimeoutException("Simulation did not advance 180 ticks.");
            yield return new UnityEngine.WaitForEndOfFrame();
        }
        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        Log.Message($"[RimLumen] Glow A/B ready: live FireGlow={CountFireGlow(map)} via FleckManager.Systems/EnumerateFlecks; waited={wait.Elapsed.TotalSeconds:F3}s; paused at tick {Find.TickManager.TicksGame}.");

        var captures = new List<byte[]>(3);
        int width = 0, height = 0;
        CellRect roi = default;
        string[] names = ["glow_on", "glow_off", "glow_on2"];
        for (int step = 0; step < names.Length; step++)
        {
            bool enabled = step != 1;
            RimLumenRenderHost.EnableVanillaAdditiveBloom = enabled;
            for (int frame = 0; frame < 30; frame++) yield return new UnityEngine.WaitForEndOfFrame();
            if (!Find.TickManager.Paused || LongEventHandler.AnyEventNowOrWaiting)
                throw new InvalidOperationException("Glow A/B simulation resumed or a long event is pending.");
            if (CountFireGlow(map) == 0) throw new InvalidOperationException("No live FireGlow at " + names[step]);
            IScreenCaptureBackend backend = ShowcaseCaptureSession.RequestCapture();
            yield return new UnityEngine.WaitForEndOfFrame();
            byte[] rgba = ShowcaseCaptureSession.ReadAndWriteCapture(backend, Path.Combine(outputDir, names[step] + ".png"), out int w, out int h);
            if (step > 0 && (width != w || height != h)) throw new InvalidOperationException("Capture dimensions changed during Glow A/B.");
            width = w;
            height = h;
            if (step == 0) roi = ProjectClusterRect(center, width, height);
            captures.Add(rgba);
            LogPipelineState(names[step], map);
            if (RimLumenRenderHost.EnableVanillaAdditiveBloom != enabled)
                throw new InvalidOperationException("Vanilla additive compatibility disabled itself during " + names[step]);
        }

        byte[] diff = new byte[captures[0].Length];
        for (int i = 0; i < diff.Length; i++)
            diff[i] = i % 4 == 3 ? (byte)255 : (byte)Math.Min(255, Math.Abs(captures[0][i] - captures[1][i]) * 8);
        ShowcasePngEncoder.WriteRgba(Path.Combine(outputDir, "diff.png"), diff, width, height);
        Log.Message($"[RimLumen] Glow A/B pixel rects (top-down, inclusive): frame=(0,0)..({width - 1},{height - 1}); cluster={roi}; diff.png=8x abs RGB, opaque alpha. Fractions use any RGB channel >2/255; metrics use normalized RGBA.");
        foreach ((string label, byte[] other) in new[] { ("on-vs-off", captures[1]), ("on-vs-on2", captures[2]) })
        {
            LogDifference(label + " full", captures[0], other, width, new CellRect(0, 0, width, height));
            LogDifference(label + " cluster", captures[0], other, width, roi);
        }
        Log.Message("[RimLumen] Glow A/B complete: " + outputDir);
    }

    private static void StartFire(Map map, IntVec3 cell)
    {
        // A wooden floor is the vanilla fuel source; bare soil fire dies at its first complex calculation.
        map.terrainGrid.SetTerrain(cell, TerrainDefOf.WoodPlankFloor);
        if (!FireUtility.TryStartFireIn(cell, map, 0.9f, null))
            throw new InvalidOperationException("Could not start vanilla fire at " + cell);
    }

    private static int CountFireGlow(Map map) => map.flecks.Systems
        .SelectMany(system => system.EnumerateFlecks())
        .Count(fleck => fleck is FleckThrown thrown && thrown.baseData.def == FleckDefOf.FireGlow && !thrown.baseData.EndOfLife &&
                        CellRect.CenteredOn(map.Center, 15, 15).Contains(thrown.baseData.spawnPosition.ToIntVec3()));

    private static CellRect ProjectClusterRect(IntVec3 center, int width, int height)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (int x in new[] { -7, 3 })
            foreach (int z in new[] { -5, 6 })
            {
                UnityEngine.Vector3 screen = Find.Camera.WorldToScreenPoint(new UnityEngine.Vector3(center.x + x, AltitudeLayer.MoteOverhead.AltitudeFor(), center.z + z));
                minX = Math.Min(minX, screen.x);
                minY = Math.Min(minY, screen.y);
                maxX = Math.Max(maxX, screen.x);
                maxY = Math.Max(maxY, screen.y);
            }
        // Waylith UnityCompat WorldToScreenPoint already returns a top-down Y, as does TryReadScreen.
        CellRect rect = CellRect.FromLimits(Math.Max(0, (int)MathF.Floor(minX)), Math.Max(0, (int)MathF.Floor(minY)),
            Math.Min(width - 1, (int)MathF.Ceiling(maxX)), Math.Min(height - 1, (int)MathF.Ceiling(maxY)));
        if (rect.Width <= 0 || rect.Height <= 0) throw new InvalidOperationException("Cluster projected outside the capture.");
        return rect;
    }

    private static void LogPipelineState(string label, Map map)
    {
        if (RimLumenRenderHost.FailSafeTripped) throw new InvalidOperationException("RimLumen render fail-safe is tripped.");
        if (RimLumenRenderHost.Pipeline is not { } pipeline) throw new InvalidOperationException("RimLumen pipeline is not installed.");
        PipelineFrame? frame = pipeline.Captured;
        string state = frame == null ? "captured=null (no published HDR/glow frame)" :
            $"captureIndex={frame.Index}, glowGroups={frame.GlowGroupsCaptured}, renderHdr={frame.RenderHdr}, bloomMips={frame.BloomMipLevels}";
        Log.Message($"[RimLumen] Glow A/B {label}: enabled={RimLumenRenderHost.EnableVanillaAdditiveBloom}, liveFireGlow={CountFireGlow(map)}, ticks={Find.TickManager.TicksGame}, {state}. Existing captured payload's immutable values; draw counts not read because render-thread disposal returns and clears pooled draw lists.");
    }

    private static void LogDifference(string label, byte[] first, byte[] second, int width, CellRect rect)
    {
        long[] sums = new long[4];
        int[] maxima = new int[4];
        int changed = 0;
        foreach (IntVec3 pixel in rect)
        {
            int index = (pixel.z * width + pixel.x) * 4;
            bool overThreshold = false;
            for (int channel = 0; channel < 4; channel++)
            {
                int delta = Math.Abs(first[index + channel] - second[index + channel]);
                sums[channel] += delta;
                maxima[channel] = Math.Max(maxima[channel], delta);
                if (channel < 3 && delta > 2) overThreshold = true;
            }
            if (overThreshold) changed++;
        }
        int pixels = rect.Width * rect.Height;
        string means = string.Join(",", sums.Select(sum => ((double)sum / pixels / 255).ToString("F8", CultureInfo.InvariantCulture)));
        string max = string.Join(",", maxima.Select(value => ((double)value / 255).ToString("F8", CultureInfo.InvariantCulture)));
        Log.Message(FormattableString.Invariant($"[RimLumen] Glow A/B {label}: pixels={pixels}; meanAbsRGBA=({means}); maxAbsRGBA=({max}); fractionRGBAbove2of255={(double)changed / pixels:F8} ({changed}/{pixels})."));
    }

    private static void Shutdown(int exitCode)
    {
        try { EngineApplication.Instance?.WaitForRenderThreadIdle(); }
        catch (Exception ex)
        {
            Log.Error("[RimLumen] Glow A/B shutdown failed: " + ex);
            exitCode = 1;
        }
        if (UnityRuntime.Current is { } runtime) runtime.App.ExitCode = exitCode;
        Root.Shutdown();
    }
}
