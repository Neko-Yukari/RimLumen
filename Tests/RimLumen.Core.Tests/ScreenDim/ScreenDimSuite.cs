using System;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.ScreenDim;
using RimLumen.Core.Tests.Distortion;
using Waylith.Rendering;

namespace RimLumen.Core.Tests.ScreenDim;

[LumenTestSuite]
public static class ScreenDimSuite
{
    [LumenFact]
    public static void OverlappingDimsCombineAsCoveragePerMap()
    {
        var backend = new LumenScreenDimBackend();
        int first = backend.CreateInstance(Node(new ScreenDimSpec { Strength = 0.5f }), Frame(7, false, 0f), realTime: false, seed: 1);
        int second = backend.CreateInstance(Node(new ScreenDimSpec { Strength = 0.5f }), Frame(7, false, 0f), realTime: false, seed: 2);
        int other = backend.CreateInstance(Node(new ScreenDimSpec { Strength = 0.8f }), Frame(9, false, 0f), realTime: false, seed: 3);
        Near(0f, backend.Coverage(7));
        backend.RestartInstance(first);
        backend.RestartInstance(second);
        backend.RestartInstance(other);
        backend.UpdateInstance(first, Frame(7, true, 0.1f));
        backend.UpdateInstance(second, Frame(7, true, 0.1f));
        backend.UpdateInstance(other, Frame(9, true, 0.1f));
        Near(0.75f, backend.Coverage(7));
        Near(0.8f, backend.Coverage(9));
        Near(0f, backend.Coverage(3));
    }

    [LumenFact]
    public static void CoverageSamplesTheCurveTimesIntensityCappedAtOne()
    {
        var backend = new LumenScreenDimBackend();
        var spec = new ScreenDimSpec { Strength = 0.5f, StrengthOverTime = new ScalarCurve(new[] { new CurveKey(0f, 0f), new CurveKey(1f, 1f) }) };
        int dim = backend.CreateInstance(Node(spec), Frame(7, false, 0f), realTime: false, seed: 1);
        backend.RestartInstance(dim);
        backend.UpdateInstance(dim, Frame(7, true, 0.5f));
        Near(0.25f, backend.Coverage(7));
        backend.UpdateInstance(dim, Frame(7, true, 0.5f, intensity: 4f));
        Near(1f, backend.Coverage(7));
    }

    [LumenFact]
    public static void TimedDimFadesOnItsCurveThroughDrainAndUntimedDimEndsWithIt()
    {
        var backend = new LumenScreenDimBackend();
        var spec = new ScreenDimSpec { Strength = 1f, Duration = 1f, StrengthOverTime = new ScalarCurve(new[] { new CurveKey(0f, 1f), new CurveKey(1f, 0f) }) };
        int timed = backend.CreateInstance(Node(spec), Frame(7, false, 0f), realTime: false, seed: 1);
        int untimed = backend.CreateInstance(Node(new ScreenDimSpec { Strength = 0.4f }), Frame(9, false, 0f), realTime: false, seed: 2);
        backend.RestartInstance(timed);
        backend.RestartInstance(untimed);
        backend.UpdateInstance(timed, Frame(7, true, 0.25f));
        backend.UpdateInstance(untimed, Frame(9, true, 0.25f));
        backend.DestroyInstance(untimed, immediate: false);
        Check(!backend.IsInstanceAlive(untimed), "a Duration-0 dim ends when its node drains");
        backend.DestroyInstance(timed, immediate: false);
        backend.UpdateInstance(timed, Frame(7, false, 0.25f));
        Near(0.5f, backend.Coverage(7));
        backend.UpdateInstance(timed, Frame(7, false, 0.5f));
        Check(!backend.IsInstanceAlive(timed) && !backend.HasActiveWork, "the timed dim is released at Duration");
        Near(0f, backend.Coverage(7));
    }

    [LumenFact]
    public static void PlayedScreenDimNodeReachesTheBackend()
    {
        var asset = new EffectAsset { EffectId = "screen-dim.play", Nodes = [Node(new ScreenDimSpec { Strength = 0.6f })] };
        var service = SceneAndLighting.VfxTestData.Service(asset);
        var backend = new LumenScreenDimBackend();
        LumenHostRegistry.RegisterNodeFeatureBackend(backend);
        try
        {
            using var handle = service.Play(new LumenPlayRequest { EffectId = asset.EffectId, MapIndex = 7 });
            SceneAndLighting.VfxTestData.Step(service, 0.1f);
            Near(0.6f, backend.Coverage(7));
            handle.Stop(true);
            SceneAndLighting.VfxTestData.Step(service, 0f);
            Check(!handle.IsAlive && !backend.HasActiveWork, "Stop(true) drains an untimed dim at once");
        }
        finally { LumenHostRegistry.UnregisterNodeFeatureBackend(backend); }
    }

    [LumenFact]
    public static void ScreenDimShadersCompileThroughRealSlang()
    {
        DistortionSuite.WithSlangLoaded(() =>
        {
            DistortionSuite.CompileFile("Shaders/ScreenDim/LumenScreenDim.vert.glsl", RuntimeShaderStage.Vertex);
            DistortionSuite.CompileFile("Shaders/ScreenDim/LumenScreenDim.frag.glsl", RuntimeShaderStage.Fragment);
        });
    }

    private static EffectNodeSpec Node(ScreenDimSpec spec) => new() { Name = "dim", ScreenDim = spec };

    private static LumenNodeFeatureFrame Frame(int map, bool active, float deltaTime, float intensity = 1f) =>
        new(new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), null, map, intensity, active, deltaTime, null);

    private static void Near(float expected, float actual)
    {
        if (MathF.Abs(expected - actual) > 1e-5f)
            throw new InvalidOperationException($"Expected {expected}; actual {actual}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
