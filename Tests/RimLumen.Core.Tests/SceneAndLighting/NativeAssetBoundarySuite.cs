using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Xml;
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
    public static void D8DescendingParticleScalarKeys()
        => RejectJson("\"emitter\": { \"sizeOverLifetime\": [[0,1],[0.5,2],[0.4,3]] }", "sizeOverLifetime", "non-decreasing");

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
    public static void D8PointLightDescendingSeconds()
        => RejectJson("\"pointLight\": { \"intensityOverTime\": [[2,1],[3,2],[2.5,0]] }", "intensityOverTime", "non-decreasing");

    [LumenFact]
    public static void D8PointLightGlowRadiusBounds()
        => RejectJson("\"pointLight\": { \"glowRadius\": 40.1 }", "glowRadius", "[0,40]");

    [LumenFact]
    public static void D8PointLightOverlightRadiusIsRemoved()
        => RejectJson("\"pointLight\": { \"overlightRadius\": 0 }", "overlightRadius", "unknown property");

    [LumenFact]
    public static void D8PointLightNegativeIntensity()
        => RejectJson("\"pointLight\": { \"intensity\": -0.5 }", "pointLight", "intensity must be >= 0");

    [LumenFact]
    public static void D8PointLightNegativeCurveKey()
        => RejectJson("\"pointLight\": { \"intensityOverTime\": [[0, 1], [1, -0.5]] }", "intensityOverTime[1]", "value must be >= 0");

    [LumenFact]
    public static void D8PointLightOverlightRadiusIsRemovedXml()
    {
        var light = new LumenPointLightXml();
        SetLegacyXmlField(light, "overlightRadius");
        RejectXml(new LumenNodeXml { pointLight = light }, "overlightRadius", "removed");
    }

    [LumenFact]
    public static void D8PointLightColorBounds()
        => RejectJson("\"pointLight\": { \"color\": [1.1,0,0] }", "color", "[0,1]");

    [LumenFact]
    public static void D8PointLightCastShadowsMustBeBoolean()
    {
        RejectJson("\"pointLight\": { \"castShadows\": \"false\" }", "castShadows", "true/false");
        RejectJson("\"pointLight\": { \"castShadows\": 0 }", "castShadows", "true/false");
    }

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

    [LumenFact]
    public static void F1XmlNumericSpacePlaneMustBeDefined()
    {
        var document = new XmlDocument();
        document.LoadXml("<li><spacePlane>1</spacePlane></li>");
        LumenNodeXml node = Verse.DirectXmlToObject.ObjectFromXml<LumenNodeXml>(document.DocumentElement!, false);
        RejectXml(node, "spacePlane", "unknown");
    }

    [LumenFact]
    public static void F1XmlNumericPreferredTierMustBeDefined()
        => RejectXml(new LumenNodeXml { emitter = new LumenEmitterXml { preferredTier = Enum.Parse<LumenParticleTier>("255") } }, "preferredTier", "unknown");

    [LumenFact]
    public static void F1XmlNumericForceKindMustBeDefined()
        => RejectXml(new LumenNodeXml { emitter = new LumenEmitterXml { forceFields = new List<LumenForceFieldXml>
        {
            new() { kind = Enum.Parse<ForceFieldKind>("255"), strength = 1f },
        } } }, "forceFields[0].kind", "unknown");

    [LumenFact]
    public static void F1XmlExistingEmitterEnumGuardsRejectNumericValues()
    {
        RejectXml(new LumenNodeXml { emitter = new LumenEmitterXml { shape = Enum.Parse<LumenShapeKind>("255") } }, "shape", "unknown");
        RejectXml(new LumenNodeXml { emitter = new LumenEmitterXml { blendMode = Enum.Parse<LumenBlendMode>("255") } }, "blendMode", "unknown");
        RejectXml(new LumenNodeXml { emitter = new LumenEmitterXml { billboardMode = Enum.Parse<LumenBillboardMode>("255") } }, "billboardMode", "unknown");
    }

    [LumenFact]
    public static void F1JsonNumericEnumsRejectThroughTheSharedBoundary()
    {
        RejectJson("\"spacePlane\": 1", "spacePlane", "unknown");
        RejectJson("\"emitter\": { \"preferredTier\": 255 }", "preferredTier", "unknown");
        RejectJson("\"emitter\": { \"shape\": 255 }", "shape", "unknown");
        RejectJson("\"emitter\": { \"blendMode\": 255 }", "blendMode", "unknown");
        RejectJson("\"emitter\": { \"billboardMode\": 255 }", "billboardMode", "unknown");
        RejectJson("\"emitter\": { \"forceFields\": [{ \"kind\": 255, \"strength\": 1 }] }", "forceFields[0].kind", "unknown");
    }

    [LumenFact]
    public static void MeshBillboardIsRejectedByBothParsersWithItsFieldPath()
    {
        const string field = "node 0 ('root').emitter.billboardMode";
        RejectJson("\"emitter\": { \"billboardMode\": \"Mesh\" }", field, "Mesh billboards are not supported");
        RejectXml(new LumenNodeXml { name = "root", emitter = new LumenEmitterXml { billboardMode = LumenBillboardMode.Mesh } },
            field, "Mesh billboards are not supported");
    }

    [LumenFact]
    public static void F8JsonForceStrengthIsRequiredAndNamesAsset()
        => RejectJson("\"emitter\": { \"forceFields\": [{ \"kind\": \"GravityDrag\" }] }", "forceFields[0].strength", "required");

    [LumenFact]
    public static void F8XmlForceStrengthIsRequiredAndNamesAsset()
        => RejectXml(new LumenNodeXml { emitter = new LumenEmitterXml { forceFields = new List<LumenForceFieldXml>
        {
            new() { kind = ForceFieldKind.GravityDrag },
        } } }, "forceFields[0].strength", "required");

    [LumenFact]
    public static void F8RealXmlLoaderPreservesPresentNullableStrength()
    {
        var document = new XmlDocument();
        document.LoadXml("<li><kind>GravityDrag</kind><centerOrAxis>(2,0,0)</centerOrAxis><strength>1</strength></li>");
        LumenForceFieldXml field = Verse.DirectXmlToObject.ObjectFromXml<LumenForceFieldXml>(document.DocumentElement!, false);
        EffectAsset asset = new LumenEffectDef
        {
            defName = "nullable-strength",
            nodes = new List<LumenNodeXml> { new() { emitter = new LumenEmitterXml
            {
                forceFields = new List<LumenForceFieldXml> { field },
            } } },
        }.ToEffectAsset();
        NativeTestData.Close(2f, ParticleForceMath.Acceleration(asset.Nodes[0].Emitter!, Matrix4x4.Identity, Vector3.Zero, out _).X,
            "real XML present strength reaches force evaluation unchanged");
    }

    [LumenFact]
    public static void F8ExplicitNonfiniteStrengthIsNotReportedMissing()
    {
        RejectJson("\"emitter\": { \"forceFields\": [{ \"kind\": \"GravityDrag\", \"strength\": 1e100 }] }", "strength", "finite");
        RejectXml(new LumenNodeXml { emitter = new LumenEmitterXml { forceFields = new List<LumenForceFieldXml>
        {
            new() { kind = ForceFieldKind.GravityDrag, strength = float.NaN },
        } } }, "strength", "finite");
    }

    [LumenFact]
    public static void PhaseAExistingColorDomainRejectsInvalidJson()
    {
        RejectJson("\"emitter\": { \"startColor\": [-1,1,1,1] }", "startColor", "finite");
        RejectJson("\"emitter\": { \"hdrIntensity\": -1 }", "hdrIntensity", "finite");
        RejectJson("\"emitter\": { \"colorOverLifetime\": [[0,[1,1,1,1.1]]] }", "colorOverLifetime", "finite");
    }

    [LumenFact]
    public static void PhaseAJsonColourDomainNamesFieldAndAsset()
    {
        RejectJson("\"emitter\": { \"startColor\": [1,1,1,1.5] }", "startColor", "[0,1]");
        RejectJson("\"emitter\": { \"materialTint\": [1,-1,1,1] }", "materialTint", ">= 0");
        RejectJson("\"emitter\": { \"materialTint\": [1,1,1,-0.1] }", "materialTint", "[0,1]");
        RejectJson("\"emitter\": { \"colorOverLifetime\": [[0,[-1,1,1,1]]] }", "colorOverLifetime", ">= 0");
        RejectJson("\"emitter\": { \"hdrIntensity\": 1e100 }", "hdrIntensity", "finite");
    }

    [LumenFact]
    public static void PhaseAXmlColourDomainNamesFieldAndAsset()
    {
        RejectXml(new LumenNodeXml { emitter = new LumenEmitterXml { startColor = "(1,1,1,1.5)" } }, "startColor", "[0,1]");
        RejectXml(new LumenNodeXml { emitter = new LumenEmitterXml { materialTint = "(1,-1,1,1)" } }, "materialTint", ">= 0");
        RejectXml(new LumenNodeXml { emitter = new LumenEmitterXml { materialTint = "(1,1,1,NaN)" } }, "materialTint", "finite");
        RejectXml(new LumenNodeXml { emitter = new LumenEmitterXml { hdrIntensity = -1f } }, "hdrIntensity", ">= 0");
        RejectXml(new LumenNodeXml { emitter = new LumenEmitterXml { colorOverLifetime = new List<LumenGradientStopXml> { new() { color = "(1,1,-1,1)" } } } }, "colorOverLifetime", ">= 0");
    }

    [LumenFact]
    public static void PhaseAProgrammaticColourDomainIsValidated()
    {
        Reject(() => ValidateEmitter(new EmitterSpec { Name = "n", StartColor = new Vector4(-1f, 1f, 1f, 1f) }), "startColor", ">= 0");
        Reject(() => ValidateEmitter(new EmitterSpec { Name = "n", MaterialTint = new Vector4(1f, 1f, 1f, 2f) }), "materialTint", "[0,1]");
        Reject(() => ValidateEmitter(new EmitterSpec { Name = "n", HdrIntensity = float.NaN }), "hdrIntensity", "finite");
        Reject(() => ValidateEmitter(new EmitterSpec { Name = "n", ColorSpace = (LumenColorSpace)255 }), "colorSpace", "unknown");
    }

    [LumenFact]
    public static void PhaseAProviderRejectsInvalidColourOnceWithIdentity()
    {
        var asset = new EffectAsset
        {
            EffectId = "provider.material",
            Nodes = new[] { new EffectNodeSpec { Name = "root", Emitter = new EmitterSpec { Name = "n", MaterialTint = new Vector4(-1f, 1f, 1f, 1f) } } },
        };
        var provider = new InMemoryAssetCatalog();
        var cache = new LumenAssetValidationCache();
        NativeTestData.Check(!cache.TryValidate(asset, provider, out string? error), "Invalid provider colour must be rejected");
        NativeTestData.Check(error is not null && error.Contains(asset.EffectId, StringComparison.Ordinal) &&
            error.Contains(nameof(InMemoryAssetCatalog), StringComparison.Ordinal) && error.Contains("materialTint", StringComparison.Ordinal), error ?? "Missing diagnostic");
        NativeTestData.Check(!cache.TryValidate(asset, provider, out string? repeated) && repeated is null, "Repeated resolution must not emit another diagnostic");
    }

    [LumenFact]
    public static void PhaseAJsonLinearMaterialParses()
    {
        EmitterSpec emitter = LumenJsonAssetLoader.Parse("""{ "nodes": [{ "name": "root", "emitter": { "colorSpace": "Linear", "materialTint": [0.5,0.25,2,0.5] } }] }""", "linear").Nodes[0].Emitter!;
        NativeTestData.Check(emitter.ColorSpace == LumenColorSpace.Linear, $"Expected Linear, got {emitter.ColorSpace}");
        NativeTestData.Check(emitter.MaterialTint == new Vector4(0.5f, 0.25f, 2f, 0.5f), $"Expected authored tint, got {emitter.MaterialTint}");
        RejectJson("\"emitter\": { \"colorSpace\": 255 }", "colorSpace", "unknown");
    }

    [LumenFact]
    public static void PhaseAXmlLinearMaterialParsesThroughRealLoader()
    {
        var document = new XmlDocument();
        document.LoadXml("<li><colorSpace>Linear</colorSpace><materialTint>(0.5, 0.25, 2, 0.5)</materialTint></li>");
        LumenEmitterXml xml = Verse.DirectXmlToObject.ObjectFromXml<LumenEmitterXml>(document.DocumentElement!, false);
        EmitterSpec emitter = new LumenEffectDef { defName = "linear-xml", nodes = new List<LumenNodeXml> { new() { emitter = xml } } }
            .ToEffectAsset().Nodes[0].Emitter!;
        NativeTestData.Check(emitter.ColorSpace == LumenColorSpace.Linear, $"Expected Linear, got {emitter.ColorSpace}");
        NativeTestData.Check(emitter.MaterialTint == new Vector4(0.5f, 0.25f, 2f, 0.5f), $"Expected authored tint, got {emitter.MaterialTint}");
        RejectXml(new LumenNodeXml { emitter = new LumenEmitterXml { colorSpace = Enum.Parse<LumenColorSpace>("255") } }, "colorSpace", "unknown");
    }

    [LumenFact]
    public static void PhaseAXmlRawRgbDefaultsAlphaAndPreservesHdrAcrossFields()
    {
        LumenNodeXml node = LoadXmlNode("""
            <li><emitter><startColor>(2,3,4)</startColor><materialTint>(0.5,0.25,2)</materialTint></emitter>
            <pointLight><color>(0.5,0.25,1)</color></pointLight></li>
            """);
        node.emitter!.colorOverLifetime = new List<LumenGradientStopXml> { LoadXml<LumenGradientStopXml>("<li><time>0</time><color>(4,3,2)</color></li>") };
        EffectAsset asset = new LumenEffectDef { defName = "raw-rgb", nodes = new List<LumenNodeXml> { node } }.ToEffectAsset();
        EmitterSpec emitter = asset.Nodes[0].Emitter!;
        NativeTestData.Check(emitter.StartColor == new Vector4(2f, 3f, 4f, 1f), "RGB startColour must preserve HDR and default alpha1");
        NativeTestData.Check(emitter.MaterialTint == new Vector4(0.5f, 0.25f, 2f, 1f), "RGB materialTint must preserve HDR and default alpha1");
        NativeTestData.Check(emitter.ColorOverLifetime.Stops[0].Rgba == new Vector4(4f, 3f, 2f, 1f), "RGB gradient must preserve HDR and default alpha1");
        NativeTestData.Check(asset.Nodes[0].PointLight!.Value.ColorRgb == new Vector3(0.5f, 0.25f, 1f), "RGB point-light colour must remain normalized");
    }

    [LumenFact]
    public static void PhaseAXmlRawRgbaPreservesVertexAndGradientAlpha()
    {
        LumenNodeXml node = LoadXmlNode("""
            <li><emitter><startColor>(2,3,4,0.25)</startColor></emitter></li>
            """);
        node.emitter!.colorOverLifetime = new List<LumenGradientStopXml> { LoadXml<LumenGradientStopXml>("<li><time>0</time><color>(4,3,2,0.5)</color></li>") };
        EmitterSpec emitter = new LumenEffectDef { defName = "raw-rgba", nodes = new List<LumenNodeXml> { node } }.ToEffectAsset().Nodes[0].Emitter!;
        NativeTestData.Check(emitter.StartColor == new Vector4(2f, 3f, 4f, 0.25f), "RGBA startColour must retain all values");
        NativeTestData.Check(emitter.ColorOverLifetime.Stops[0].Rgba == new Vector4(4f, 3f, 2f, 0.5f), "RGBA gradient must retain all values");
    }

    [LumenFact]
    public static void PhaseAXmlMalformedColoursNameAssetAndField()
    {
        RejectXmlText("<li><emitter><startColor>1,2,3</startColor></emitter></li>", "startColor", "expected");
        RejectXmlText("<li><emitter><materialTint>(1,2)</materialTint></emitter></li>", "materialTint", "components");
        RejectXmlColour(new LumenNodeXml { emitter = new LumenEmitterXml { colorOverLifetime = new List<LumenGradientStopXml>
        {
            LoadXml<LumenGradientStopXml>("<li><time>0</time><color>(1,2,3,4,5)</color></li>"),
        } } }, "colorOverLifetime[0].color", "components");
        RejectXmlText("<li><emitter><materialTint IsNull=\"true\" /></emitter></li>", "materialTint", "null");
        RejectXmlText("<li><pointLight><color>(x,0,0)</color></pointLight></li>", "pointLight.color", "float");
    }

    [LumenFact]
    public static void PhaseAXmlPointLightRawBytesAreRejectedByExistingDomain()
        => RejectXmlText("<li><pointLight><color>(255,128,0)</color></pointLight></li>", "pointLight", "[0,1]");

    private static LumenNodeXml LoadXmlNode(string xml) => LoadXml<LumenNodeXml>(xml);

    private static T LoadXml<T>(string xml)
    {
        var document = new XmlDocument();
        document.LoadXml(xml);
        return Verse.DirectXmlToObject.ObjectFromXml<T>(document.DocumentElement!, false);
    }

    private static void RejectXmlText(string xml, string field, string message)
        => RejectXmlColour(LoadXmlNode(xml), field, message);

    private static void RejectXmlColour(LumenNodeXml node, string field, string message)
    {
        InvalidDataException? error = null;
        try
        {
            new LumenEffectDef { defName = "xml-colour-boundary", nodes = new List<LumenNodeXml> { node } }.ToEffectAsset();
        }
        catch (InvalidDataException caught) { error = caught; }
        NativeTestData.Check(error is not null && error.Message.Contains("xml-colour-boundary", StringComparison.Ordinal) &&
            error.Message.Contains(field, StringComparison.Ordinal) && error.Message.Contains(message, StringComparison.Ordinal), error?.Message ?? $"Expected XML colour rejection of {field}");
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
            if (message == "required")
                NativeTestData.Check(error.Message.Contains("boundary", StringComparison.Ordinal), $"Missing asset identity: {error.Message}");
            return;
        }
        throw new InvalidOperationException($"Expected rejection of '{field}': {message}.");
    }
}
