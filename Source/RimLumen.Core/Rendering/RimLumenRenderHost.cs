using System;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Rendering;

// Public runtime surface of the native HDR pipeline; other Core modules query it, never the engine directly.
public static class RimLumenRenderHost
{
    // Ensures: all-false / 0 until Install succeeds; updated by the pipeline (e.g. ValidationClean from probe runs).
    public static LumenCapabilities Capabilities => throw new NotImplementedException();

    // Ensures: the passId registered via RenderPassRegistry.Register("RimLumenHdr"); 0 before Install.
    public static byte LumenRenderPassId => throw new NotImplementedException();

    // Requires: main thread; modRootDirectory is the RimLumen mod root (contains 1.7/Shaders).
    // Ensures: idempotent. Headless (no GraphicsDevice) => no-op, Capabilities stay default.
    //   Otherwise registers 1.7/Shaders with ShaderSourceLoader, registers the pass id + layer route,
    //   inserts RimLumenPreBackbufferFeature (after MapRenderFeature, before PostFxFeature) and
    //   RimLumenCompositeFeature (immediately before the ImGui feature). 0 Harmony.
    public static void Install(string modRootDirectory)
    {
        throw new NotImplementedException();
    }
}
