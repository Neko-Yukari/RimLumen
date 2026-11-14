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
    private static readonly object gate = new();
    private static LumenGpuParticleEngine? engine;
    private static bool initialized;
    private static bool disposed;

    static LumenGpuParticlesBootstrap() => EnsureInitialized();

    // Requires: main thread; EngineApplication.Instance exists.
    // Ensures: idempotent; registers the shader directory and the GPU particle engine exactly once.
    public static void EnsureInitialized()
    {
        lock (gate)
        {
            if (initialized) return;
            initialized = true;
            string assemblyDirectory = Path.GetDirectoryName(typeof(LumenGpuParticlesBootstrap).Assembly.Location)!;
            ShaderSourceLoader.RegisterShaderSourceDirectory(Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "Shaders")));
            engine = new LumenGpuParticleEngine();
            LumenHostRegistry.RegisterGpuParticleEngine(engine);
            EngineApplication.Instance.BeforeGraphicsShutdown += Shutdown;
            EngineApplication.Instance.OnShutdown += Quiesce;
        }
    }

    private static void Quiesce()
    {
        var app = EngineApplication.Instance;
        app.WaitForRenderThreadIdle();
        app.GraphicsDevice?.WaitIdleAndRun(() => { });
        Shutdown(app);
    }

    private static void Shutdown(EngineApplication app)
    {
        if (disposed) return;
        disposed = true;
        app.OnShutdown -= Quiesce;
        app.BeforeGraphicsShutdown -= Shutdown;
        if (engine is not null)
        {
            LumenHostRegistry.UnregisterContributor(engine);
            engine.Dispose();
        }
        Log.Message("[RimLumen] GPU particle shutdown completed.");
    }
}
