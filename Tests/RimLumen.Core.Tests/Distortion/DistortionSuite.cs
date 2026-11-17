using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Xml.Linq;
using RimLumen.Core.Contracts;
using RimLumen.Core.Distortion;
using RimLumen.Core.Mod;
using RimLumen.Core.Rendering;
using RimLumen.Core.Rendering.Logic;
using Waylith.Rendering;

namespace RimLumen.Core.Tests.Distortion;

[LumenTestSuite]
public static class DistortionSuite
{
    private static readonly DistortionSpec Ring = new()
    {
        Kind = LumenDistortionKind.RingWave, Radius = 6f, Strength = 0.5f, Thickness = 1f, Speed = 4f, Duration = 1.5f,
    };

    private static readonly DistortionSpec Ripple = new()
    {
        Kind = LumenDistortionKind.Ripple, Radius = 8f, Strength = 1f, Thickness = 0.5f, Speed = 2f, Frequency = 1f,
    };

    private static readonly DistortionSpec Haze = new()
    {
        Kind = LumenDistortionKind.HeatHaze, Radius = 3f, Strength = 0.3f, Frequency = 2f, Duration = 3f,
    };

    [LumenFact]
    public static void VisibilityFollowsStartDurationAndActivity()
    {
        Check(!DistortionTimeline.IsVisible(Ring, started: false, 0f, active: true), "nothing shows before the first restart");
        Check(DistortionTimeline.IsVisible(Ring, true, 0f, true), "a started wave shows at t=0");
        Check(DistortionTimeline.IsVisible(Ring, true, 1.4f, false), "a launched wave finishes after its node deactivates");
        Check(!DistortionTimeline.IsVisible(Ring, true, 1.5f, true), "a wave ends at Duration even while active");
        Check(DistortionTimeline.IsVisible(Ripple, true, 100f, true), "Duration 0 shows while active");
        Check(!DistortionTimeline.IsVisible(Ripple, true, 0.1f, false), "Duration 0 stops with its node");
        Check(DistortionTimeline.IsVisible(Haze, true, 2.9f, false), "a timed haze finishes its Duration");
        Check(!DistortionTimeline.IsVisible(Haze, true, 3f, true), "a timed haze ends at Duration");
    }

    [LumenFact]
    public static void RingWaveExpandsAtSpeedAndRampsInOverOneThickness()
    {
        DistortionFrame early = DistortionTimeline.Evaluate(Ring, 0.1f, 1f);
        Near(new Vector4(0.4f, 0f, 0f, 0f), early.RingRadii);
        Near(new Vector4(0.2f, 0f, 0f, 0f), early.RingAmplitudes);
        Near(0f, early.HazeAmplitude);
        DistortionFrame full = DistortionTimeline.Evaluate(Ring, 0.5f, 2f);
        Near(new Vector4(2f, 0f, 0f, 0f), full.RingRadii);
        Near(new Vector4(1f, 0f, 0f, 0f), full.RingAmplitudes);
        DistortionSpec faded = new()
        {
            Kind = Ring.Kind, Radius = Ring.Radius, Strength = Ring.Strength, Thickness = Ring.Thickness, Speed = Ring.Speed,
            Duration = Ring.Duration, StrengthOverTime = new ScalarCurve(new[] { new CurveKey(0f, 1f), new CurveKey(1f, 0.25f) }, 1f),
        };
        Near(new Vector4(0.125f, 0f, 0f, 0f), DistortionTimeline.Evaluate(faded, 1.2f, 1f).RingAmplitudes);
    }

    [LumenFact]
    public static void RippleTrailsDecayingAlternatingRingsBehindTheFront()
    {
        DistortionFrame first = DistortionTimeline.Evaluate(Ripple, 0.1f, 1f);
        Near(new Vector4(0.2f, 0f, 0f, 0f), first.RingRadii);
        Near(new Vector4(0.4f, 0f, 0f, 0f), first.RingAmplitudes);
        DistortionFrame second = DistortionTimeline.Evaluate(Ripple, 1f, 1f);
        Near(new Vector4(2f, 1f, 0f, 0f), second.RingRadii);
        Near(new Vector4(1f, -0.5f, 0f, 0f), second.RingAmplitudes);
        DistortionFrame train = DistortionTimeline.Evaluate(Ripple, 2f, 1f);
        Near(new Vector4(4f, 3f, 2f, 1f), train.RingRadii);
        Near(new Vector4(1f, -0.5f, 0.25f, -0.125f), train.RingAmplitudes);
    }

    [LumenFact]
    public static void HeatHazeHasNoRingsAndEvolvesItsNoiseWithTime()
    {
        DistortionFrame frame = DistortionTimeline.Evaluate(Haze, 0.5f, 2f);
        Near(Vector4.Zero, frame.RingRadii);
        Near(Vector4.Zero, frame.RingAmplitudes);
        Near(0.6f, frame.HazeAmplitude);
        Near(0.5f, frame.HazePhase);
    }

    [LumenFact]
    public static void BackendInstancesDrainUntilTheirTimelineEnds()
    {
        var backend = new LumenDistortionBackend();
        Check(!backend.HasActiveWork, "an empty backend is idle");
        int ring = backend.CreateInstance(Node(Ring), Frame(false, 0f), realTime: false, seed: 1);
        int ripple = backend.CreateInstance(Node(Ripple), Frame(false, 0f), realTime: false, seed: 2);
        Check(ring != ripple && backend.IsInstanceAlive(ring) && backend.HasActiveWork, "created instances are alive");
        backend.RestartInstance(ring);
        backend.RestartInstance(ripple);
        backend.UpdateInstance(ring, Frame(true, 1f));
        backend.UpdateInstance(ripple, Frame(true, 1f));

        backend.DestroyInstance(ripple, immediate: false);
        Check(!backend.IsInstanceAlive(ripple), "a Duration-0 node has nothing left to show once it drains");
        backend.DestroyInstance(ring, immediate: false);
        Check(backend.IsInstanceAlive(ring), "a launched ring keeps draining");
        backend.UpdateInstance(ring, Frame(false, 0.4f));
        Check(backend.IsInstanceAlive(ring), "the ring lives until t reaches Duration");
        backend.UpdateInstance(ring, Frame(false, 0.1f));
        Check(!backend.IsInstanceAlive(ring) && !backend.HasActiveWork, "the ring dies at Duration");
        backend.DestroyInstance(ring, immediate: true);

        int restarted = backend.CreateInstance(Node(Ring), Frame(false, 0f), realTime: false, seed: 3);
        backend.RestartInstance(restarted);
        backend.UpdateInstance(restarted, Frame(true, 1.4f));
        backend.RestartInstance(restarted);
        backend.DestroyInstance(restarted, immediate: false);
        backend.UpdateInstance(restarted, Frame(false, 1.4f));
        Check(backend.IsInstanceAlive(restarted), "a restart rewinds the timeline to 0");
        backend.DestroyInstance(restarted, immediate: true);
        Check(!backend.IsInstanceAlive(restarted), "an immediate destroy releases at once");
    }

    [LumenFact]
    public static void FrameGateRunsTheChainOnlyWhenDistortionIsDrawn()
    {
        Equal(HdrFrameMode.Skip, HdrFrameGate.Decide(false, 0, 0, false));
        Equal(HdrFrameMode.Render, HdrFrameGate.Decide(false, 0, 1, false));
        Equal(false, HdrFrameGate.ShouldRenderHdr(0, false, false, 0));
        Equal(true, HdrFrameGate.ShouldRenderHdr(0, false, false, 2));
        Check(!RimLumenPipeline.HasDirectHdrContributor([new LumenDistortionBackend()]),
            "the distortion backend appends draws to the distortion pass, never direct HDR draws");
    }

    [LumenFact]
    public static void ValidatorAcceptsTheDistortionInputAndKeepsRejectingUnknownSources()
    {
        ShaderPackGraphValidator.ExecutionPlan implicitComposite = ShaderPackGraphValidator.ExecutionPlan.Compile(new ShaderPackSpec());
        Check(implicitComposite.Passes[^1].Inputs.Contains(new ShaderPackGraphValidator.PlannedInput(6, ShaderPackGraphValidator.InputKind.Distortion)),
            "the default composite binds distortion at binding 6");
        ShaderPackGraphValidator.ExecutionPlan plan = ShaderPackGraphValidator.ExecutionPlan.Compile(Composite("distortion", 3));
        Equal(ShaderPackGraphValidator.InputKind.Distortion, plan.Passes[^1].Inputs[0].Kind);
        Check(!ShaderPackGraphValidator.TryValidate(Composite("distortions", 3), out _), "unknown sources still fail");
        Check(!ShaderPackGraphValidator.TryValidate(new ShaderPackSpec
        {
            Targets = new[] { new ShaderPackTargetSpec("Distortion", ShaderPackTargetFormat.R8G8B8A8UNorm, 1f, ShaderPackTargetFilter.Linear, false) },
        }, out _), "distortion is a reserved name");
    }

    [LumenFact]
    public static void BuiltInPacksBindDistortionInTheirComposite()
    {
        string packsRoot = Path.Combine(RepoRoot(), "1.7", "ShaderPacks");
        IReadOnlyList<ShaderPackEntry> packs = ShaderPackCatalog.ScanRoots([(packsRoot, "built-in")]);
        foreach (string id in new[] { "atmospheric", "cinematic", "crisp", "vibrant" })
        {
            ShaderPackEntry entry = packs.Single(p => p.Id == id);
            ShaderPackGraphValidator.ExecutionPlan plan = ShaderPackGraphValidator.ExecutionPlan.Compile(entry.Spec);
            Check(plan.Passes[^1].Inputs.Any(input => input.Kind == ShaderPackGraphValidator.InputKind.Distortion && input.Binding == 6),
                $"{id} composite must bind distortion at binding 6");
        }
    }

    [LumenFact]
    public static void DistortionShadersCompileThroughRealSlang()
    {
        WithSlangLoaded(() =>
        {
            CompileFile("Shaders/Distortion/LumenDistortion.vert.glsl", RuntimeShaderStage.Vertex);
            CompileFile("Shaders/Distortion/LumenDistortion.frag.glsl", RuntimeShaderStage.Fragment);
            CompileFile("Shaders/PostFx/RimLumenComposite.frag.glsl", RuntimeShaderStage.Fragment);
            foreach (string pack in new[] { "Vibrant", "Crisp", "Cinematic", "Atmospheric" })
                CompileFile($"ShaderPacks/{pack}/Composite.frag.glsl", RuntimeShaderStage.Fragment);
        });
    }

    private static ShaderPackSpec Composite(string source, int binding) => new()
    {
        Passes = new[]
        {
            new ShaderPackPassSpec("Composite", null, "backbuffer",
                new[] { new ShaderPackInputBinding(binding, source) }, ShaderPackBlendMode.PremultipliedAlpha),
        },
    };

    private static EffectNodeSpec Node(DistortionSpec spec) => new() { Name = "wave", Distortion = spec };

    private static LumenNodeFeatureFrame Frame(bool active, float deltaTime) =>
        new(new LumenEmitterTransform(Matrix4x4.Identity, Quaternion.Identity), null, 7, 1f, active, deltaTime, Vector3.Zero);

    private static void CompileFile(string relativePath, RuntimeShaderStage stage)
    {
        string path = Path.Combine(RepoRoot(), "1.7", relativePath);
        byte[] spirv = RuntimeShaderCompiler.Compile(ShaderSourceLoader.ReadShaderSource(path), path, "main", RuntimeShaderLanguage.Glsl, stage);
        Check(spirv.Length >= 20 && BitConverter.ToUInt32(spirv) == 0x07230203u, $"{relativePath}: invalid SPIR-V.");
    }

    private static void WithSlangLoaded(Action action)
    {
        string gameRoot = Environment.GetEnvironmentVariable("GameRoot")
            ?? XDocument.Load(Path.Combine(RepoRoot(), "Directory.Build.props")).Descendants("GameRoot").Single().Value;
        string nativePath = Path.Combine(gameRoot, "slang.dll");
        nint ResolveSlang(Assembly assembly, string name)
        {
            if (assembly.GetName().Name != "Brutal.Slang" || name != "slang")
                return 0;
            return NativeLibrary.Load(nativePath, assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
        }
        nint optimizer = NativeLibrary.Load(Path.Combine(gameRoot, "slang-glslang.dll"), typeof(DistortionSuite).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveSlang;
        try
        {
            action();
        }
        finally
        {
            AssemblyLoadContext.Default.ResolvingUnmanagedDll -= ResolveSlang;
            NativeLibrary.Free(optimizer);
        }
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Cannot locate repository root above test output.");
    }

    private static void Near(float expected, float actual)
    {
        if (MathF.Abs(expected - actual) > 1e-5f)
            throw new InvalidOperationException($"Expected {expected}; actual {actual}.");
    }

    private static void Near(Vector4 expected, Vector4 actual)
    {
        if (Vector4.Distance(expected, actual) > 1e-5f)
            throw new InvalidOperationException($"Expected {expected}; actual {actual}.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; actual {actual}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
