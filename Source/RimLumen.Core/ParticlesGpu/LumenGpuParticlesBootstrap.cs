using System;
using System.IO;
using RimLumen.Core.Contracts;
using Verse;
using Waylith;
using Waylith.Rendering;

namespace RimLumen.Core.ParticlesGpu;

[StaticConstructorOnStartup]
public static class LumenGpuParticlesBootstrap
{
    static LumenGpuParticlesBootstrap()
    {
        string assemblyDirectory = Path.GetDirectoryName(typeof(LumenGpuParticlesBootstrap).Assembly.Location)!;
        ShaderSourceLoader.RegisterShaderSourceDirectory(Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "Shaders")));
        var engine = new LumenGpuParticleEngine();
        LumenHostRegistry.RegisterGpuParticleEngine(engine);
        EngineApplication.Instance.BeforeGraphicsShutdown += _ =>
        {
            LumenHostRegistry.UnregisterContributor(engine);
            engine.Dispose();
        };
    }
}
