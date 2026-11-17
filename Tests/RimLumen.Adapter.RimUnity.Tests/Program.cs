using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimUnity.Serialization;

namespace RimLumen.Adapter.RimUnity.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
        string? filter = null;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--filter") filter = args[i + 1];
        (string Name, Action Run)[] suites =
        [
            ("WotF all seven translation and native/hybrid play", IntegrationTests.WotfIntegration),
            ("WotF tier report", IntegrationTests.WotfTierReport),
            ("Tier matrix and External ownership", TranslationTests.TierMatrix),
            ("Phase3 active export fields", TranslationTests.ActiveExportFields),
            ("Phase3 bounded imported bursts", TranslationTests.BoundedImportedBursts),
            ("Phase3 deferred weighted export classification", TranslationTests.DeferredWeightedExports),
            ("renderMode fallback ownership", TranslationTests.RenderModeFallback),
            ("Shape fidelity fallback reasons", TranslationTests.Shapes),
            ("Shape axis import", TranslationTests.ShapeAxis),
            ("Shape nonuniform scale import", TranslationTests.ShapeScale),
            ("R1 duplicate curve import", TranslationTests.DuplicateCurve),
            ("R1 mixed repeated scalar curve registration", TranslationTests.MixedRepeatedCurves),
            ("R2 adjacent Fixed gradient import", TranslationTests.AdjacentFixedGradient),
            ("R3 rejected hybrid ownership", IntegrationTests.RejectedHybrid),
            ("Canonical GravityDrag L1/L2 fields only", TranslationTests.Gravity),
            ("Canonical Vortex radial L1/L2", TranslationTests.VortexRadial),
            ("Transitive subEmitter ownership", TranslationTests.SubEmitterClosure),
            ("Curves, gradients and HDR", TranslationTests.Values),
            ("Scene colour Gamma import", TranslationTests.SceneColor),
            ("PhaseA Linear material factors", TranslationTests.LinearMaterial),
            ("PhaseA imported material domain", TranslationTests.MaterialDomain),
            ("PhaseA WotF Probe external handoff", IntegrationTests.WotfProbeMaterial),
            ("D20 per-particle lights ownership", TranslationTests.ParticleLights),
            ("Catalog required, gzip payloads, aliases, shared owner and traversal", FileTests.Catalog),
            ("RimUnity same-root reload refreshes catalogs and translations", FileTests.Reload),
            ("RUTX DXT1/BC7 mips, gzip and malformed payloads", FileTests.Textures),
            ("Core assembly architectural independence", IntegrationTests.CoreIndependence),
        ];
        int passed = 0, failed = 0;
        foreach (var suite in suites)
        {
            if (!string.IsNullOrEmpty(filter) && !suite.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            try { suite.Run(); passed++; Console.WriteLine("[PASS] " + suite.Name); }
            catch (Exception e) { failed++; Console.WriteLine("[FAIL] " + suite.Name + ": " + e); }
        }
        Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
        return failed == 0 ? 0 : 1;
    }
}

internal static class Check
{
    internal static void True(bool actual, string message)
    {
        if (!actual) throw new InvalidOperationException(message);
    }
    internal static void Equal<T>(T expected, T actual, string message) =>
        True(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected={expected}; actual={actual}");
    internal static void Near(float expected, float actual, string message, float tolerance = 0.00002f) =>
        True(float.IsFinite(actual) && MathF.Abs(expected - actual) <= tolerance, $"{message}: expected={expected}; actual={actual}");
    internal static void Reasons(IEnumerable<string> expected, IEnumerable<string> actual, string message) =>
        Equal(string.Join("|", expected.Order()), string.Join("|", actual.Order()), message);
    internal static void Throws<T>(Action action, string fragment) where T : Exception
    {
        try { action(); }
        catch (T e) { True(e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase), $"Expected {typeof(T).Name} containing '{fragment}', actual: {e.Message}"); return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name} containing '{fragment}', but call succeeded.");
    }
}

// Test inputs use the real export DTOs. Only the unavailable external runtime is spied.
internal static class Export
{
    internal static ValueData Number(float x) => new() { kind = "number", number = x };
    internal static ValueData Vector(params float[] values) => new() { kind = "vector", vector = values };
    internal static ValueData Array(params ValueData[] values) => new() { kind = "array", items = values.ToList() };
    internal static MemberData Member(string name, ValueData value) => new() { name = name, value = value };
    internal static ValueData Data(params (string Name, ValueData Value)[] fields) => new()
    { kind = "data", fields = fields.Select(f => Member(f.Name, f.Value)).ToList() };
    internal static ValueData Constant(float value) => Data(("mode", Number(0)), ("constant", Number(value)));
    internal static ComponentData Particles(int max = 32, float rate = 8, float lifetime = 1) => new()
    {
        id = "ps", type = "RimUnity.ParticleSystem", members =
        [Member("main", Data(("maxParticles", Number(max)), ("startLifetime", Constant(lifetime)),
            ("startSpeed", Constant(2)), ("duration", Number(5)), ("loop", Number(1)))),
         Member("emission", Data(("enabled", Number(1)), ("rateOverTime", Constant(rate)))),
         Member("shape", Data(("enabled", Number(0))))]
    };
    internal static NodeData Node(string id, ComponentData particles, string? parent = null) => new()
    {
        id = id, transformId = id + ".transform", name = id, parent = parent!, active = true,
        position = [0, 0, 0], rotation = [0, 0, 0, 1], scale = [1, 1, 1], components = [particles]
    };
    internal static PrefabData Prefab(ComponentData particles) => new() { nodes = [Node("root", particles)] };
}

internal sealed class TestDirectory : IDisposable
{
    internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "fixtures-" + Guid.NewGuid().ToString("N"));
    internal TestDirectory() => Directory.CreateDirectory(Root);
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
