using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using RimLumen.Core.Api;
using RimLumen.Core.Assets;
using RimLumen.Core.Compat;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class NativeAssetBoundarySuite
{
    [LumenFact]
    public static void D2StartColorRenameJson()
        => RejectJson("\"emitter\": { \"startColorLinear\": [1,1,1,1] }", "startColorLinear", "renamed to startColor");

    [LumenFact]
    public static void D2StartColorRenameXml()
        => RejectLegacyEmitterXml("startColorLinear", "renamed to startColor");

    [LumenFact]
    public static void D2GradientColorRenameJson()
        => RejectJson("\"emitter\": { \"colorOverLifetime\": [{ \"time\": 0, \"rgbaLinear\": [1,1,1,1] }] }", "rgbaLinear", "renamed to color");

    [LumenFact]
    public static void D2GradientColorRenameXml()
    {
        var stop = new LumenGradientStopXml();
        SetLegacyXmlField(stop, "rgbaLinear");
        RejectXml(new LumenNodeXml { emitter = new LumenEmitterXml { colorOverLifetime = new List<LumenGradientStopXml> { stop } } }, "rgbaLinear", "renamed to color");
    }

    [LumenFact]
    public static void D2PointLightLinearRenameJson()
        => RejectJson("\"pointLight\": { \"colorLinear\": [1,1,1] }", "colorLinear", "renamed to color");

    [LumenFact]
    public static void D2PointLightLinearRenameXml() => RejectLegacyLightXml("colorLinear");

    [LumenFact]
    public static void D2PointLightRgbRenameJson()
        => RejectJson("\"pointLight\": { \"colorRgb\": [1,1,1] }", "colorRgb", "renamed to color");

    [LumenFact]
    public static void D2PointLightRgbRenameXml() => RejectLegacyLightXml("colorRgb");

    [LumenFact]
    public static void D3BloomWeightJson()
        => RejectJson("\"emitter\": { \"bloomWeight\": 0.5 }", "bloomWeight", "was removed");

    [LumenFact]
    public static void D3BloomWeightXml() => RejectLegacyEmitterXml("bloomWeight", "was removed");

    [LumenFact]
    public static void D3HdrMultiplierJson()
        => RejectJson("\"emitter\": { \"hdrMultiplier\": 2 }", "hdrMultiplier", "was removed");

    [LumenFact]
    public static void D3HdrMultiplierXml() => RejectLegacyEmitterXml("hdrMultiplier", "was removed");

    [LumenFact]
    public static void D3HdrMultiplierModExtension()
    {
        var extension = new LumenEmissiveModExtension();
        SetLegacyXmlField(extension, "hdrMultiplier");
        string error = extension.ConfigErrors().Single();
        NativeTestData.Check(error.Contains("hdrMultiplier", StringComparison.Ordinal) && error.Contains("was removed", StringComparison.Ordinal), error);
    }

    [LumenFact]
    public static void D8DurationMustBePositive()
        => RejectJson("\"emitter\": { \"duration\": 0 }", "duration", "> 0");

    [LumenFact]
    public static void D8DuplicateParticleScalarKeys()
        => RejectJson("\"emitter\": { \"sizeOverLifetime\": [[0,1],[0,2]] }", "sizeOverLifetime", "strictly increasing");

    [LumenFact]
    public static void D8DuplicateParticleGradientStops()
        => RejectJson("\"emitter\": { \"colorOverLifetime\": [[0,[1,1,1,1]],[0,[0,0,0,0]]] }", "colorOverLifetime", "strictly increasing");

    [LumenFact]
    public static void D8ParticleScalarNormalizedTime()
        => RejectJson("\"emitter\": { \"sizeOverLifetime\": [[-0.1,1]] }", "sizeOverLifetime", "[0,1]");

    [LumenFact]
    public static void D8ParticleGradientNormalizedTime()
        => RejectJson("\"emitter\": { \"colorOverLifetime\": [[1.1,[1,1,1,1]]] }", "colorOverLifetime", "[0,1]");

    [LumenFact]
    public static void D8PointLightNegativeSeconds()
        => RejectJson("\"pointLight\": { \"intensityOverTime\": [[-0.1,1]] }", "intensityOverTime", ">= 0");

    [LumenFact]
    public static void D8PointLightDuplicateSeconds()
        => RejectJson("\"pointLight\": { \"intensityOverTime\": [[2,1],[2,0]] }", "intensityOverTime", "strictly increasing");

    [LumenFact]
    public static void D8PointLightGlowRadiusBounds()
        => RejectJson("\"pointLight\": { \"glowRadius\": 40.1 }", "glowRadius", "[0,40]");

    [LumenFact]
    public static void D8PointLightOverlightRadiusBounds()
        => RejectJson("\"pointLight\": { \"overlightRadius\": -0.1 }", "overlightRadius", "[0,40]");

    [LumenFact]
    public static void D8PointLightColorBounds()
        => RejectJson("\"pointLight\": { \"color\": [1.1,0,0] }", "color", "[0,1]");

    [LumenFact]
    public static void D8XmlUsesSharedValidation()
        => RejectXml(new LumenNodeXml { emitter = new LumenEmitterXml { duration = 0f } }, "duration", "> 0");

    [LumenFact]
    public static void D8RegisterEffectUsesSharedValidation()
        => Reject(() => RimLumenApi.RegisterEffect(new EffectAsset
        {
            EffectId = "invalid.registration",
            Nodes = new[] { new EffectNodeSpec { Name = "root", Emitter = new EmitterSpec { Name = "n", Duration = 0f } } },
        }), "duration", "> 0");

    [LumenFact]
    public static void D8ProgrammaticCurvesRequireKeysAndFiniteData()
        => Reject(() => LumenAssetRules.Validate(new EffectAsset
        {
            EffectId = "invalid.curve",
            Nodes = new[] { new EffectNodeSpec { Name = "root", Emitter = new EmitterSpec { Name = "n", SizeOverLifetime = new ScalarCurve(Array.Empty<CurveKey>()) } } },
        }, "programmatic"), "sizeOverLifetime", "at least one");

    [LumenFact]
    public static void D8ProgrammaticCurveFiniteTangents()
        => Reject(() => ValidateEmitter(new EmitterSpec { Name = "n", SizeOverLifetime = new ScalarCurve(new[] { new CurveKey(0f, 1f, OutTangent: float.NaN) }) }), "sizeOverLifetime", "finite");

    [LumenFact]
    public static void D8ProgrammaticGradientFiniteColor()
        => Reject(() => ValidateEmitter(new EmitterSpec { Name = "n", ColorOverLifetime = ColorGradient.Constant(new Vector4(float.NaN)) }), "colorOverLifetime", "finite");

    [LumenFact]
    public static void D8ProgrammaticGradientRequiresStops()
        => Reject(() => ValidateEmitter(new EmitterSpec { Name = "n", ColorOverLifetime = new ColorGradient(Array.Empty<ColorStop>()) }), "colorOverLifetime", "at least one");

    [LumenFact]
    public static void D8ProgrammaticCurveFiniteMultiplier()
        => Reject(() => ValidateEmitter(new EmitterSpec { Name = "n", SizeOverLifetime = ScalarCurve.Constant(float.PositiveInfinity) }), "sizeOverLifetime", "finite");

    [LumenFact]
    public static void D8ProgrammaticEnumsMustBeDefined()
        => Reject(() => ValidateEmitter(new EmitterSpec { Name = "n", Shape = (LumenShapeKind)255 }), "shape", "unknown");

    [LumenFact]
    public static void D8PointLightSecondsAreNotNormalizedLifetime()
    {
        var asset = LumenJsonAssetLoader.Parse("""{ "nodes": [{ "name": "light", "pointLight": { "intensityOverTime": [[0,0],[2.2,1]] } }] }""", "seconds");
        NativeTestData.Close(1f, CurveEvaluator.Evaluate(asset.Nodes[0].PointLight!.Value.IntensityOverTime!, 2.2f), "point light seconds");
    }

    private static void ValidateEmitter(EmitterSpec emitter)
        => LumenAssetRules.Validate(new EffectAsset { EffectId = "boundary", Nodes = new[] { new EffectNodeSpec { Name = "root", Emitter = emitter } } }, "programmatic");

    private static void RejectLegacyEmitterXml(string key, string message)
    {
        var emitter = new LumenEmitterXml();
        SetLegacyXmlField(emitter, key);
        RejectXml(new LumenNodeXml { emitter = emitter }, key, message);
    }

    private static void RejectLegacyLightXml(string key)
    {
        var light = new LumenPointLightXml();
        SetLegacyXmlField(light, key);
        RejectXml(new LumenNodeXml { pointLight = light }, key, "renamed to color");
    }

    // DirectXmlToObject binds public and non-public instance fields; conversion is the host-free seam.
    private static void SetLegacyXmlField(object target, string key)
    {
        FieldInfo field = target.GetType().GetField(key, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing XML rejection sentinel for '{key}'.");
        field.SetValue(target, field.FieldType == typeof(string) ? "1" : Activator.CreateInstance(field.FieldType));
    }

    private static void RejectJson(string nodeContent, string field, string message)
        => Reject(() => LumenJsonAssetLoader.Parse($$"""{ "nodes": [{ "name": "root", {{nodeContent}} }] }""", "boundary"), field, message);

    private static void RejectXml(LumenNodeXml node, string field, string message)
        => Reject(() => new LumenEffectDef { defName = "boundary", nodes = new List<LumenNodeXml> { node } }.ToEffectAsset(), field, message);

    private static void Reject(Action action, string field, string message)
    {
        try
        {
            action();
        }
        catch (InvalidDataException error)
        {
            NativeTestData.Check(error.Message.Contains(field, StringComparison.Ordinal) && error.Message.Contains(message, StringComparison.Ordinal),
                $"Expected '{field}' and '{message}', got: {error.Message}");
            return;
        }
        throw new InvalidOperationException($"Expected rejection of '{field}': {message}.");
    }
}
