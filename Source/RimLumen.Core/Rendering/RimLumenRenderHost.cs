using System.IO;
using Waylith;
using Waylith.Rendering;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Rendering;

// Public runtime surface of the native HDR pipeline; other Core modules query it, never the engine directly.
public static class RimLumenRenderHost
{
    internal static RimLumenPipeline? Pipeline { get; private set; }

    internal static void SetFrameCapabilities(bool hdr, bool depth)
    {
        Capabilities = Capabilities with { IsHdrActive = hdr, IsSceneDepthAvailable = depth };
    }

    // Ensures: all-false / 0 until Install succeeds; updated by the pipeline (e.g. ValidationClean from probe runs).
    public static LumenCapabilities Capabilities { get; private set; }

    // Ensures: the passId registered via RenderPassRegistry.Register("RimLumenHdr"); 0 before Install.
    public static byte LumenRenderPassId { get; private set; }

    // Requires: main thread; modRootDirectory is the RimLumen mod root (contains 1.7/Shaders).
    // Ensures: idempotent. Headless (no GraphicsDevice) => no-op, Capabilities stay default.
    //   Otherwise registers 1.7/Shaders with ShaderSourceLoader, registers the pass id + layer route,
    //   inserts RimLumenPreBackbufferFeature (after MapRenderFeature, before PostFxFeature) and
    //   RimLumenCompositeFeature (immediately before the ImGui feature). 0 Harmony.
    public static void Install(string modRootDirectory)
    {
        if (LumenRenderPassId != 0 || EngineApplication.Instance?.GraphicsDevice == null)
            return;

        ShaderSourceLoader.RegisterShaderSourceDirectory(Path.Combine(modRootDirectory, "1.7", "Shaders"));
        byte passId = RenderPassRegistry.Register("RimLumenHdr");
        // UnityStubs LayerMask allocates named layers at 16..31; vanilla uses Default=0.
        // Reserve 15 in the otherwise unnamed 1..15 range without Unity/RimUnity coupling.
        LayerPassRouter.RegisterLayerMask(1 << 15, passId);
        LumenRenderPassId = passId;
        Pipeline = new RimLumenPipeline(EngineApplication.Instance);
        Pipeline.Attach();
        Capabilities = new LumenCapabilities(false, false, true, false, 0);
    }
}
