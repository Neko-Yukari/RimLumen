using System;
using System.Linq;
#if !RIMLUMEN_SHIP_RELEASE
using RimLumen.Core.Rendering.Logic;
using RimLumen.Core.Rendering.Probes;
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
        // Command line first: VerboseDiagnostics must be set before Install creates the first Vulkan objects,
        // otherwise their OwnedVkObject attribution lines would be skipped under -rimlumen-probe.
        string[] commandLineArgs = Environment.GetCommandLineArgs();
        bool hasProbeOptions = ProbeOptionsParser.TryParse(commandLineArgs, out ProbeOptions? options);
        if (hasProbeOptions && !options!.BenchmarkOnly) RimLumenRenderHost.VerboseDiagnostics = true;
#endif
        try
        {
            RimLumenRenderHost.Install(root);
            RimLumen.Core.Scene.LumenSceneBootstrap.EnsureInitialized();
            RimLumen.Core.ParticlesGpu.LumenGpuParticlesBootstrap.EnsureInitialized();
            int loaded = RimLumen.Core.Assets.LumenModAssetScanner.ScanRunningMods(RimLumen.Core.Scene.LumenSceneBootstrap.Catalog);
            Log.Message($"[RimLumen] Installed: pass={RimLumenRenderHost.LumenRenderPassId}, layer=15, nativeAssets={loaded}, vanillaGlow={RimLumenRenderHost.EnableVanillaAdditiveBloom}");
        }
#if !RIMLUMEN_SHIP_RELEASE
        catch (Exception ex) when (!hasProbeOptions)
#else
        catch (Exception ex)
#endif
        {
            Log.Error($"[RimLumen] Startup fail-safe tripped; disabling RimLumen for this session: {ex}");
            RimLumenRenderHost.TripFailSafe();
            return;
        }
#if !RIMLUMEN_SHIP_RELEASE
        if (hasProbeOptions)
        {
            EngineApplication app = EngineApplication.Instance;
            int settleFrames = 0;
            void Update()
            {
                if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || LongEventHandler.ShouldWaitForEvent)
                {
                    settleFrames = 0;
                    return;
                }
                if (settleFrames++ < 2) return;
                app.OnUpdate -= Update;
                ProbeSession.Run(root, options!);
            }
            app.OnUpdate += Update;
        }
        else if (ShowcaseOptionsParser.TryParse(commandLineArgs, out ShowcaseOptions showcaseOptions))
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
