using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Waylith;
using Waylith.Rendering;
using RimLumen.Core.Contracts;
using RimLumen.Core.Rendering.Logic;

namespace RimLumen.Core.Rendering;

// Public runtime surface of the native HDR pipeline; other Core modules query it, never the engine directly.
public static class RimLumenRenderHost
{
    internal static RimLumenPipeline? Pipeline { get; private set; }

    public static bool EnableVanillaAdditiveBloom { get; set; } = true;

    // Ensures: stored value is within [0.1, 1.0]; the simulators read it without re-clamping.
    public static float ParticleDensityScale
    {
        get => particleDensityScale;
        set => particleDensityScale = System.Math.Clamp(value, 0.1f, 1.0f);
    }

    private static float particleDensityScale = 1.0f;

    public static ShaderPackSpec ActiveShaderPack { get; private set; } = ShaderPackSpec.Default;

    public static string? LastShaderPackError { get; private set; }

    public static ShaderPackRuntimeParams ShaderPackParams { get; } = new();

    public static bool IsMapLightFieldActive => ActiveShaderPack.RequiresMapLightField && !FailSafeTripped;

    internal static bool IsDefaultShaderPack(ShaderPackSpec spec) => ReferenceEquals(spec, ShaderPackSpec.Default);

    // A custom pack is data, not RimLumen itself: its failures revert the pack instead of detaching the pipeline.
    internal static bool ShouldRevertPackOnFailure(ShaderPackSpec spec) => !IsDefaultShaderPack(spec);

    private static int failSafeTripped;

    internal static bool FailSafeTripped => Volatile.Read(ref failSafeTripped) != 0;

    // Ensures: idempotent, any thread; returns true only for the call that tripped it. Capabilities read all-false, every pipeline
    //   frame hook no-ops from now on, and the pipeline's features leave FrameRenderer on the main thread (queued via
    //   RenderToMainThread), so a render-thread trip never edits the feature list itself.
    internal static bool TripFailSafe()
    {
        if (Interlocked.Exchange(ref failSafeTripped, 1) != 0) return false;
        Capabilities = default;
        Pipeline?.DetachAfterFailSafe();
        return true;
    }

    internal static void SetFrameCapabilities(bool hdr, bool depth)
    {
        Capabilities = Capabilities with { IsHdrActive = hdr, IsSceneDepthAvailable = depth };
    }

    // Ensures: all-false / 0 until Install succeeds; updated by the pipeline while it records.
    public static LumenCapabilities Capabilities { get; private set; }

    // Ensures: the passId registered via RenderPassRegistry.Register("RimLumenHdr"); 0 before Install.
    public static byte LumenRenderPassId { get; private set; }

    // Requires: main thread; modRootDirectory is the RimLumen mod root (contains 1.7/Shaders).
    // Ensures: idempotent. Headless (no GraphicsDevice) => no-op, Capabilities stay default.
    //   Otherwise registers 1.7/Shaders with ShaderSourceLoader, registers the pass id + layer route,
    //   captures before MapRenderFeature and records the chain at the map's 3152 scene-grab split.
    public static void Install(string modRootDirectory)
    {
        if (LumenRenderPassId != 0 || EngineApplication.Instance?.GraphicsDevice == null)
            return;

        RimLumenMapHooks.Arm();
        ShaderSourceLoader.RegisterShaderSourceDirectory(Path.Combine(modRootDirectory, "1.7", "Shaders"));
        byte passId = RenderPassRegistry.Register("RimLumenHdr");
        // UnityStubs LayerMask allocates named layers at 16..31; vanilla uses Default=0.
        // Reserve 15 in the otherwise unnamed 1..15 range without Unity/RimUnity coupling.
        LayerPassRouter.RegisterLayerMask(1 << 15, passId);
        LumenRenderPassId = passId;
        Pipeline = new RimLumenPipeline(EngineApplication.Instance);
        Pipeline.Attach();
        Capabilities = new LumenCapabilities(false, false, true, 0);
    }

    // Requires: main thread.
    // Ensures: validates DAG and precompiles all custom fragment shaders through Slang before swapping
    //   the live runtime. On any failure, records LastShaderPackError, never trips fail-safe, keeps the
    //   already-active pack, and only installs the built-in default when no runtime is live yet.
    public static bool ReloadShaderPack(ShaderPackSpec? spec)
    {
        ShaderPackSpec candidate = spec ?? ShaderPackSpec.Default;
        try
        {
            ShaderPackGraphValidator.Validate(candidate);
            ShaderSourceLoader.ClearLayoutCaches();
            PrecompileCustomShaders(candidate);

            if (Pipeline is { } pipeline)
            {
                pipeline.App.WaitForRenderThreadIdle();
                pipeline.App.Device.WaitIdle();
                var nextRuntime = new ShaderPackRuntime(pipeline.App, candidate);
                try
                {
                    pipeline.ReplaceShaderPack(nextRuntime);
                }
                catch
                {
                    nextRuntime.Dispose();
                    throw;
                }
            }

            ActiveShaderPack = candidate;
            LastShaderPackError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastShaderPackError = ex.ToString();
            RimLumenPipeline? pipeline = Pipeline;
            if (pipeline is null)
            {
                LogError($"[RimLumen] ShaderPack reload failed; no pipeline is installed: {ex.Message}");
            }
            else if (pipeline.IsDisposed)
            {
                // A disposed pipeline owns no GPU objects to swap and cannot record; installing a runtime here
                // would leak the new allocations and log a misleading "fallback installed" message.
                LogError($"[RimLumen] ShaderPack reload failed; pipeline already disposed, nothing to install: {ex.Message}");
            }
            else if (pipeline.HasShaderPackRuntime)
            {
                LogError($"[RimLumen] ShaderPack reload failed; keeping active pack '{ActiveShaderPack.Id}': {ex.Message}");
            }
            else
            {
                LogError($"[RimLumen] ShaderPack reload failed and no runtime was live; installing the built-in default pack: {ex.Message}");
                try
                {
                    pipeline.App.WaitForRenderThreadIdle();
                    pipeline.App.Device.WaitIdle();
                    var fallback = new ShaderPackRuntime(pipeline.App, ShaderPackSpec.Default);
                    try
                    {
                        pipeline.ReplaceShaderPack(fallback);
                    }
                    catch
                    {
                        fallback.Dispose();
                        throw;
                    }
                    ActiveShaderPack = ShaderPackSpec.Default;
                }
                catch (Exception fallbackError)
                {
                    LastShaderPackError += Environment.NewLine + "Fallback also failed: " + fallbackError;
                    LogError($"[RimLumen] Default ShaderPack could not be installed; detaching RimLumen features: {fallbackError}");
                    TripFailSafe();
                }
            }
            return false;
        }
    }

    // Render thread (via RenderToMainThread) or main thread. Records the failure and swaps the pack back to the
    // built-in default when the failed pack is still the active one; never detaches the pipeline for pack data.
    internal static void ReportRuntimePackFailure(ShaderPackSpec failedSpec, Exception ex)
    {
        LastShaderPackError = ex.ToString();
        if (Pipeline is not { } pipeline)
            return;
        pipeline.App.FrameRenderer.RenderToMainThread.Enqueue(() =>
        {
            LogError($"[RimLumen] ShaderPack '{failedSpec.Id}' failed while recording; reverting to the built-in default pack: {ex}");
            RevertFailedPackToDefault(failedSpec);
        });
    }

    // Requires: main thread. No-op when another pack has been installed since the failure.
    internal static bool RevertFailedPackToDefault(ShaderPackSpec failedSpec)
    {
        if (!ReferenceEquals(ActiveShaderPack, failedSpec))
            return false;
        if (Pipeline is not { IsDisposed: false } pipeline)
        {
            ActiveShaderPack = ShaderPackSpec.Default;
            return true;
        }
        try
        {
            pipeline.App.WaitForRenderThreadIdle();
            pipeline.App.Device.WaitIdle();
            var fallback = new ShaderPackRuntime(pipeline.App, ShaderPackSpec.Default);
            try
            {
                pipeline.ReplaceShaderPack(fallback);
            }
            catch
            {
                fallback.Dispose();
                throw;
            }
            ActiveShaderPack = ShaderPackSpec.Default;
            return true;
        }
        catch (Exception ex)
        {
            LastShaderPackError = (LastShaderPackError is null ? string.Empty : LastShaderPackError + Environment.NewLine)
                + "Fallback to the default pack failed: " + ex;
            LogError($"[RimLumen] Default ShaderPack could not be installed; detaching RimLumen features: {ex}");
            TripFailSafe();
            return false;
        }
    }

    private static void PrecompileCustomShaders(ShaderPackSpec spec)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIfPresent(paths, spec.BloomPrefilterShaderPath);
        AddIfPresent(paths, spec.BloomDownShaderPath);
        AddIfPresent(paths, spec.BloomUpShaderPath);
        AddIfPresent(paths, spec.CompositeShaderPath);
        foreach (ShaderPackPassSpec pass in spec.Passes)
            AddIfPresent(paths, pass.FragmentShaderPath);

        foreach (string path in paths)
        {
            string source = ShaderSourceLoader.ReadShaderSource(path);
            RuntimeShaderCompiler.Compile(
                source,
                path,
                "main",
                RuntimeShaderLanguage.Glsl,
                RuntimeShaderStage.Fragment,
                out _);
        }
    }

    private static void AddIfPresent(HashSet<string> paths, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            paths.Add(path);
    }

    // Verse.Log needs a live host: offline callers (no EngineApplication yet) skip the message instead of raising.
    internal static void LogWarning(string message) => PostLog(Verse.Log.Warning, message);

    internal static void LogError(string message) => PostLog(Verse.Log.Error, message);

    private static void PostLog(Action<string> write, string message)
    {
        if (EngineApplication.Instance is null) return;
        write(message);
    }
}
