using System;
using System.Linq;
using RimLumen.Core.Rendering.Logic;
using RimLumen.Core.Rendering.Probes;
using Verse;
using Waylith;

namespace RimLumen.Core.Rendering;

[StaticConstructorOnStartup]
internal static class RimLumenStartup
{
    static RimLumenStartup()
    {
        string root = LoadedModManager.RunningMods.Single(mod =>
            string.Equals(mod.PackageId, "NekoYukari.RimLumen", StringComparison.OrdinalIgnoreCase)).RootDir;
        RimLumenRenderHost.Install(root);
        Log.Message($"[RimLumen] Installed: pass={RimLumenRenderHost.LumenRenderPassId}, layer=15, thread={Environment.CurrentManagedThreadId}");
        if (ProbeOptionsParser.TryParse(Environment.GetCommandLineArgs(), out ProbeOptions? options))
        {
            EngineApplication app = EngineApplication.Instance;
            void Update()
            {
                if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || LongEventHandler.ShouldWaitForEvent)
                    return;
                app.OnUpdate -= Update;
                ProbeSession.Run(root, options!);
            }
            app.OnUpdate += Update;
        }
    }
}
