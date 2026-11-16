using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Xml;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class Phase3AssetBoundarySuite
{
    [LumenFact]
    public static void JsonAndXmlParseNewShapeFieldsAndBurstProbability()
    {
        EmitterSpec json = Parse("""
            "shape":"Donut", "billboardMode":"Local", "coneLength":3, "donutRadius":0.4,
            "radiusThickness":0.2, "boxThickness":[0.1,0.3,0.5], "arcDegrees":90,
            "arcSpread":0.25, "radiusSpread":0.5, "randomDirectionAmount":0.6,
            "sphericalDirectionAmount":0.7, "randomPositionAmount":2,
            "bursts":[{"time":0.25,"count":3,"probability":0.4},[0.5,2],[0.75,1,0.2]]
            """);
        var document = new XmlDocument();
        document.LoadXml("""
            <li><shape>Donut</shape><billboardMode>Local</billboardMode><coneLength>3</coneLength>
            <donutRadius>0.4</donutRadius><radiusThickness>0.2</radiusThickness><boxThickness>(0.1,0.3,0.5)</boxThickness>
            <arcDegrees>90</arcDegrees><arcSpread>0.25</arcSpread><radiusSpread>0.5</radiusSpread>
            <randomDirectionAmount>0.6</randomDirectionAmount><sphericalDirectionAmount>0.7</sphericalDirectionAmount>
            <randomPositionAmount>2</randomPositionAmount><bursts><li><time>0.25</time><count>3</count><probability>0.4</probability></li>
            <li><time>0.5</time><count>2</count></li><li><time>0.75</time><count>1</count><probability>0.2</probability></li></bursts></li>
            """);
        // Nested list elements force PostLoad; its profiler reads game Prefs even when no PostLoad method exists.
        bool profiling = Verse.DeepProfiler.enabled;
        LumenEmitterXml emitterXml;
        try
        {
            Verse.DeepProfiler.enabled = false;
            emitterXml = Verse.DirectXmlToObject.ObjectFromXml<LumenEmitterXml>(document.DocumentElement!, false);
        }
        finally
        {
            Verse.DeepProfiler.enabled = profiling;
        }
        EmitterSpec xml = ConvertXml(new LumenNodeXml { name = "root", emitter = emitterXml }).Nodes[0].Emitter!;
        Check(json.Shape == xml.Shape && json.BillboardMode == xml.BillboardMode, "enum mapping");
        Check(json.ConeLength == xml.ConeLength && json.DonutRadius == xml.DonutRadius && json.RadiusThickness == xml.RadiusThickness
            && json.BoxThickness == xml.BoxThickness && json.ArcDegrees == xml.ArcDegrees && json.ArcSpread == xml.ArcSpread
            && json.RadiusSpread == xml.RadiusSpread && json.RandomDirectionAmount == xml.RandomDirectionAmount
            && json.SphericalDirectionAmount == xml.SphericalDirectionAmount && json.RandomPositionAmount == xml.RandomPositionAmount, "shape values");
        Console.WriteLine($"burst roundtrip JSON=[{string.Join("; ", json.Bursts)}] XML=[{string.Join("; ", xml.Bursts)}]");
        Check(json.Bursts.Length == 3 && xml.Bursts.Length == 3 && json.Bursts[0] == xml.Bursts[0]
            && json.Bursts[1] == xml.Bursts[1] && json.Bursts[2] == xml.Bursts[2]
            && json.Bursts[0].Probability == 0.4f && json.Bursts[1].Probability == 1f && json.Bursts[2].Probability == 0.2f, "burst probabilities");
    }

    [LumenFact]
    public static void AppendedShapeAndBillboardNamesParseAcrossFormats()
    {
        string[] shapes = { "SphereShell", "Hemisphere", "HemisphereShell", "CircleEdgeXZ", "ConeShell", "ConeVolume", "ConeVolumeShell", "SingleSidedEdge", "BoxShell", "BoxEdge", "Donut", "Rectangle" };
        foreach (string shape in shapes)
        {
            var document = new XmlDocument();
            document.LoadXml($"<li><shape>{shape}</shape></li>");
            LumenEmitterXml xml = Verse.DirectXmlToObject.ObjectFromXml<LumenEmitterXml>(document.DocumentElement!, false);
            EmitterSpec parsedXml = ConvertXml(new LumenNodeXml { name = "root", emitter = xml }).Nodes[0].Emitter!;
            Check(Parse($"\"shape\":\"{shape}\"").Shape == parsedXml.Shape && parsedXml.Shape.ToString() == shape, shape);
        }
        foreach (string mode in new[] { "Vertical", "Local" })
        {
            var document = new XmlDocument();
            document.LoadXml($"<li><billboardMode>{mode}</billboardMode></li>");
            LumenEmitterXml xml = Verse.DirectXmlToObject.ObjectFromXml<LumenEmitterXml>(document.DocumentElement!, false);
            EmitterSpec parsedXml = ConvertXml(new LumenNodeXml { name = "root", emitter = xml }).Nodes[0].Emitter!;
            Check(Parse($"\"billboardMode\":\"{mode}\"").BillboardMode == parsedXml.BillboardMode && parsedXml.BillboardMode.ToString() == mode, mode);
        }
    }

    [LumenFact]
    public static void JsonRejectsNewScalarDomainsWithFieldPaths()
    {
        Reject(() => Parse("\"coneLength\":-0.1"), "coneLength");
        Reject(() => Parse("\"donutRadius\":1e100"), "donutRadius");
        Reject(() => Parse("\"radiusThickness\":1.1"), "radiusThickness");
        Reject(() => Parse("\"boxThickness\":[0,-0.1,0]"), "boxThickness");
        Reject(() => Parse("\"arcDegrees\":361"), "arcDegrees");
        Reject(() => Parse("\"arcSpread\":-0.1"), "arcSpread");
        Reject(() => Parse("\"radiusSpread\":1.1"), "radiusSpread");
        Reject(() => Parse("\"randomDirectionAmount\":-0.1"), "randomDirectionAmount");
        Reject(() => Parse("\"sphericalDirectionAmount\":1.1"), "sphericalDirectionAmount");
        Reject(() => Parse("\"randomPositionAmount\":-0.1"), "randomPositionAmount");
        Reject(() => Parse("\"bursts\":[{\"probability\":1.1}]"), "probability");
    }

    [LumenFact]
    public static void SharedValidationRejectsInvalidXmlAndProgrammaticValues()
    {
        Reject(() => ConvertXml(new LumenNodeXml { name = "root", localYawDegrees = float.NaN }), "localYawDegrees");
        Reject(() => ConvertXml(new LumenNodeXml { name = "root", emitter = new LumenEmitterXml { bursts = new List<LumenBurstXml> { new() { probability = float.NaN } } } }), "probability");
        Reject(() => Validate(new EffectNodeSpec { Name = "root", Emitter = new EmitterSpec { Name = "e", BoxThickness = new Vector3(0f, 0f, 1.01f) } }), "boxThickness");
        Reject(() => Validate(new EffectNodeSpec { Name = "root", Emitter = new EmitterSpec { Name = "e", Bursts = new[] { new ParticleBurstSpec(0f, 1, -0.1f) } } }), "probability");
    }

    [LumenFact]
    public static void RotationValidationDoesNotDependOnNonuniformNodeScale()
    {
        Quaternion unit = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f);
        Validate(new EffectNodeSpec { Name = "root", LocalScale = new Vector3(-2f, 0f, 3f), LocalRotation = unit });
        Reject(() => Validate(new EffectNodeSpec { Name = "root", LocalScale = new Vector3(-2f, 0f, 3f), LocalRotation = new Quaternion(0f, 0f, 0f, 2f) }), "localRotation");
        Reject(() => Validate(new EffectNodeSpec { Name = "root", LocalRotation = new Quaternion(float.NaN, 0f, 0f, 1f) }), "localRotation");
        EffectAsset parsed = LumenJsonAssetLoader.Parse("""{"nodes":[{"name":"root","localRotation":[0,0,0,2],"localScale":[-2,0,3]}]}""", "phase3-json");
        Check(parsed.Nodes[0].LocalRotation == Quaternion.Identity, "JSON normalizes local rotation");
    }

    [LumenFact]
    public static void BurstBeyondDurationIsRejectedAtEveryAssetBoundary()
    {
        CheckBurstBoundary(time: 1.25f, rejected: true);
    }

    [LumenFact]
    public static void BurstAtDurationIsRetainedAtEveryAssetBoundary()
    {
        CheckBurstBoundary(time: 1f, rejected: false);
    }

    private static void CheckBurstBoundary(float time, bool rejected)
    {
        string authoredTime = time.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Func<EmitterSpec>[] boundaries =
        {
            () => Parse("\"duration\":1,\"bursts\":[{\"time\":" + authoredTime + ",\"count\":2}]"),
            () => ConvertXml(new LumenNodeXml { name = "root", emitter = new LumenEmitterXml
            {
                duration = 1f, bursts = new List<LumenBurstXml> { new() { time = time, count = 2 } },
            } }).Nodes[0].Emitter!,
            () =>
            {
                var emitter = new EmitterSpec { Name = "e", Duration = 1f, Bursts = new[] { new ParticleBurstSpec(time, 2) } };
                Validate(new EffectNodeSpec { Name = "root", Emitter = emitter });
                return emitter;
            },
        };
        foreach (Func<EmitterSpec> boundary in boundaries)
        {
            if (rejected)
                Reject(() => boundary(), "node 0 ('root').emitter.bursts[0].time");
            else
            {
                EmitterSpec emitter = boundary();
                Check(emitter.Bursts.Length == 1 && emitter.Bursts[0].Time == time && emitter.Bursts[0].Count == 2,
                    "The endpoint burst must be retained by every asset boundary.");
            }
        }
    }

    private static EmitterSpec Parse(string fields)
        => LumenJsonAssetLoader.Parse("{\"nodes\":[{\"name\":\"root\",\"emitter\":{" + fields + "}}]}", "phase3-json").Nodes[0].Emitter!;

    private static EffectAsset ConvertXml(LumenNodeXml node)
        => new LumenEffectDef { defName = "phase3-xml", nodes = new List<LumenNodeXml> { node } }.ToEffectAsset();

    private static void Validate(EffectNodeSpec node)
        => LumenAssetRules.Validate(new EffectAsset { EffectId = "phase3-csharp", Nodes = new[] { node } }, "phase3-csharp");

    private static void Reject(Action action, string field)
    {
        try { action(); }
        catch (InvalidDataException error)
        {
            Check(error.Message.Contains("phase3", StringComparison.Ordinal) && error.Message.Contains(field, StringComparison.Ordinal), error.Message);
            return;
        }
        throw new InvalidOperationException($"Expected InvalidDataException naming {field}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
