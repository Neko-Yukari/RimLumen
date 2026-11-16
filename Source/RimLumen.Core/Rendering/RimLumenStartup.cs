using System;
using System.Linq;
#if !RIMLUMEN_SHIP_RELEASE
using RimLumen.Core.Showcase;
#endif
using Verse;
using Waylith;

namespace RimLumen.Core.Rendering;

[StaticConstructorOnStartup]
internal static class RimLumenStartup
{
    static RimLumenStartup()
    {
        // PackageIdPlayerFacing is the About.xml id; PackageId carries a "_steam" postfix for Workshop copies.
        ModContentPack? self = LoadedModManager.RunningMods.FirstOrDefault(mod =>
            string.Equals(mod.PackageIdPlayerFacing, "NekoYukari.RimLumen", StringComparison.OrdinalIgnoreCase));
        if (self == null)
        {
            Log.Error("[RimLumen] Running mod 'NekoYukari.RimLumen' not found by PackageIdPlayerFacing; RimLumen is disabled for this session.");
            return;
        }
        string root = self.RootDir;
#if !RIMLUMEN_SHIP_RELEASE
        string[] commandLineArgs = Environment.GetCommandLineArgs();
#endif
        try
        {
            RimLumenRenderHost.Install(root);
            RimLumen.Core.Scene.LumenSceneBootstrap.EnsureInitialized();
            RimLumen.Core.ParticlesGpu.LumenGpuParticlesBootstrap.EnsureInitialized();
            int loaded = RimLumen.Core.Assets.LumenModAssetScanner.ScanRunningMods(RimLumen.Core.Scene.LumenSceneBootstrap.Catalog);
            Log.Message($"[RimLumen] Installed: pass={RimLumenRenderHost.LumenRenderPassId}, layer=15, nativeAssets={loaded}, vanillaGlow={RimLumenRenderHost.EnableVanillaAdditiveBloom}");
        }
        catch (Exception ex)
        {
            Log.Error($"[RimLumen] Startup fail-safe tripped; disabling RimLumen for this session: {ex}");
            RimLumenRenderHost.TripFailSafe();
            return;
        }
#if !RIMLUMEN_SHIP_RELEASE
        if (ShowcaseValidationSave.TryStart(commandLineArgs)) return;
        if (ShowcaseBenchmarkSession.TryParseOutputPath(commandLineArgs, out string benchmarkJsonPath))
        {
            EngineApplication app = EngineApplication.Instance;
            int settleFrames = 0;
            void UpdateBenchmark()
            {
                if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || LongEventHandler.ShouldWaitForEvent)
                    return;
                // Let two frames render on the loaded map before the measured ladder starts.
                if (settleFrames++ < 2) return;
                app.OnUpdate -= UpdateBenchmark;
                ShowcaseBenchmarkSession.Run(Find.CurrentMap, benchmarkJsonPath);
            }
            app.OnUpdate += UpdateBenchmark;
            return;
        }
        if (ShowcaseOptionsParser.TryParse(commandLineArgs, out ShowcaseOptions showcaseOptions))
        {
            EngineApplication app = EngineApplication.Instance;
            void UpdateShowcase()
            {
                if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || LongEventHandler.ShouldWaitForEvent)
                    return;
                app.OnUpdate -= UpdateShowcase;
                Map map = Find.CurrentMap;
                if (string.IsNullOrEmpty(showcaseOptions.CapturePngPath))
                {
                    Prefs.DevMode = true;
                }
                ShowcaseStage.BuildOrRebuild(map);
                if (!string.IsNullOrEmpty(showcaseOptions.CapturePngPath))
                {
                    ShowcaseCaptureSession.Run(map, showcaseOptions.CapturePngPath);
                }
            }
            app.OnUpdate += UpdateShowcase;
        }
#endif
    }
}
