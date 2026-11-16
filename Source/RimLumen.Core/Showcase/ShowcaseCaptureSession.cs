using System;
using System.Collections;
using UnityStubs.Rendering;
using Verse;
using Waylith;
using Waylith.UnityCompat;

namespace RimLumen.Core.Showcase;

internal static class ShowcaseCaptureSession
{
    private const int SettleFrames = 90;

    internal static void Run(Map map, string pngPath)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentException.ThrowIfNullOrEmpty(pngPath);
        Current.CameraDriver.StartCoroutine(CaptureAndShutdown(map, pngPath));
    }

    private static IEnumerator CaptureAndShutdown(Map map, string pngPath)
    {
        for (int i = 0; i < SettleFrames; i++)
        {
            yield return new UnityEngine.WaitForEndOfFrame();
        }

        bool armed = false;
        IScreenCaptureBackend? backend = null;
        try
        {
            backend = RequestCapture();
            armed = true;
        }
        catch (Exception ex)
        {
            Log.Error("[RimLumen] Showcase capture request failed: " + ex);
            ShutdownWithExitCode(map, exitCode: 1);
            yield break;
        }

        if (armed)
        {
            yield return new UnityEngine.WaitForEndOfFrame();
        }

        int exitCode = 1;
        try
        {
            ReadAndWriteCapture(backend!, pngPath, out int width, out int height);
            Log.Message($"[RimLumen] Showcase captured PNG ({width}x{height}): {pngPath}");
            exitCode = 0;
        }
        catch (Exception ex)
        {
            Log.Error("[RimLumen] Showcase capture write failed: " + ex);
        }
        finally
        {
            ShutdownWithExitCode(map, exitCode);
        }
    }

    internal static IScreenCaptureBackend RequestCapture()
    {
        IScreenCaptureBackend backend = ScreenCaptureBackend.Current ?? throw new InvalidOperationException("Screen capture backend unavailable.");
        if (!backend.RequestCapture()) throw new InvalidOperationException("Swapchain transfer readback unsupported.");
        return backend;
    }

    internal static byte[] ReadAndWriteCapture(IScreenCaptureBackend backend, string pngPath, out int width, out int height)
    {
        if (!backend.TryReadScreen(out byte[] rgba, out width, out height))
            throw new InvalidOperationException("TryReadScreen returned false for showcase capture.");
        ShowcasePngEncoder.WriteRgba(pngPath, rgba, width, height);
        return rgba;
    }

    private static void ShutdownWithExitCode(Map map, int exitCode)
    {
        try
        {
            ShowcaseStage.Clear(map);
            EngineApplication.Instance?.WaitForRenderThreadIdle();
        }
        catch (Exception ex)
        {
            Log.Error("[RimLumen] Showcase cleanup error during shutdown: " + ex);
            exitCode = 1;
        }

        if (UnityRuntime.Current is { } runtime)
        {
            runtime.App.ExitCode = exitCode;
        }
        Root.Shutdown();
    }
}
