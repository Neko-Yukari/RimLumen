using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace RimLumen.WebSim;

public static class Program
{
    public static void Main() { }
}

[SupportedOSPlatform("browser")]
public static partial class WebSimApi
{
    private static readonly WebSimEngine Engine = new();

    [JSExport]
    internal static string Initialize() => Engine.Initialize();

    [JSExport]
    internal static string ListDemosJson() => Engine.ListDemosJson();

    [JSExport]
    internal static string GetDemoSource(string effectId, string format, bool compact)
        => Engine.GetDemoSource(effectId, format, compact);

    [JSExport]
    internal static string ValidateSource(string source, string format)
        => Engine.ValidateSource(source, format);

    [JSExport]
    internal static string ExportSource(string source, string fromFormat, string toFormat, bool compact)
        => Engine.ExportSource(source, fromFormat, toFormat, compact);

    [JSExport]
    internal static string LoadAndPlay(string source, string format, string initialState)
        => Engine.LoadAndPlay(source, format, string.IsNullOrEmpty(initialState) ? null : initialState);

    [JSExport]
    internal static string PlayDemo(string effectId)
        => Engine.PlayDemo(effectId);

    [JSExport]
    internal static string Restart()
        => Engine.Restart();

    [JSExport]
    internal static void Stop(bool allowFadeOut)
        => Engine.Stop(allowFadeOut);

    [JSExport]
    internal static string GoToState(string stateName)
        => Engine.GoToState(stateName);

    [JSExport]
    internal static void TriggerEvent(string name)
        => Engine.TriggerEvent(name);

    [JSExport]
    internal static string SetParam(string name, float value)
        => Engine.SetParam(name, value);

    [JSExport]
    internal static void SetParticleDensity(float density)
        => Engine.SetParticleDensity(density);

    [JSExport]
    internal static void SetTargetWorld(float x, float y, float z)
        => Engine.SetTargetWorld(x, y, z);

    [JSExport]
    internal static void SetOrbitMotion(bool enabled, float radius, float angularSpeed)
        => Engine.SetOrbitMotion(enabled, radius, angularSpeed);

    [JSExport]
    internal static string StepFrame(float dt, int viewportWidth, int viewportHeight, double[] viewColMajor, double[] projColMajor)
        => Engine.StepFrame(dt, viewportWidth, viewportHeight, viewColMajor, projColMajor);

    [JSExport]
    internal static byte[] GetSimVerticesBytes()
        => Engine.GetSimVerticesBytes();

    [JSExport]
    internal static byte[] GetSimIndicesBytes()
        => Engine.GetSimIndicesBytes();

    [JSExport]
    internal static byte[] GetRibbonVerticesBytes()
        => Engine.GetRibbonVerticesBytes();

    [JSExport]
    internal static byte[] GetRibbonIndicesBytes()
        => Engine.GetRibbonIndicesBytes();
}
