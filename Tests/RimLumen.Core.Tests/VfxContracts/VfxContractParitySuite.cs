using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Xml;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Tests.VfxContracts;

[LumenTestSuite]
public static class VfxContractParitySuite
{
    [LumenFact]
    public static void EveryNewFieldParsesIdenticallyFromCSharpJsonAndXml()
    {
        EffectAsset csharp = FullCSharp();
        LumenAssetRules.Validate(csharp, "csharp");
        string expected = Describe(csharp.Nodes);
        Same(expected, Describe(LumenJsonAssetLoader.Parse(FullJson).Nodes), "JSON");
        Same(expected, Describe(Xml("vfx.parity", FullXml).Nodes), "XML");
    }

    [LumenFact]
    public static void OmittedFieldsTakeTheSameDefaultsInEveryFormat()
    {
        var csharp = new EffectAsset
        {
            EffectId = "vfx.defaults",
            Nodes = new[]
            {
                new EffectNodeSpec { Name = "plain", Emitter = new EmitterSpec { Name = "plain" } },
                new EffectNodeSpec { Name = "trail", Ribbon = new RibbonSpec { Mode = LumenRibbonMode.Trail, Lifetime = 1f, MinVertexDistance = 0.1f, MaxPoints = 8 } },
                new EffectNodeSpec { Name = "haze", Distortion = new DistortionSpec { Kind = LumenDistortionKind.HeatHaze, Radius = 2f, Strength = 0.1f, Frequency = 1f } },
            },
        };
        string expected = Describe(csharp.Nodes);
        Same(expected, Describe(LumenJsonAssetLoader.Parse("""
            { "effectId": "vfx.defaults", "nodes": [
              { "name": "plain", "emitter": {} },
              { "name": "trail", "ribbon": { "mode": "Trail", "lifetime": 1, "minVertexDistance": 0.1, "maxPoints": 8 } },
              { "name": "haze", "distortion": { "kind": "HeatHaze", "radius": 2, "strength": 0.1, "frequency": 1 } } ] }
            """).Nodes), "JSON");
        Same(expected, Describe(Xml("vfx.defaults",
            "<li><name>plain</name><emitter /></li>",
            "<li><name>trail</name><ribbon><mode>Trail</mode><lifetime>1</lifetime><minVertexDistance>0.1</minVertexDistance><maxPoints>8</maxPoints></ribbon></li>",
            "<li><name>haze</name><distortion><kind>HeatHaze</kind><radius>2</radius><strength>0.1</strength><frequency>1</frequency></distortion></li>").Nodes), "XML");
    }

    // A. Environment forces
    [LumenFact] public static void RejectsUnknownEnvironmentChannelBits() => RejectJson(Emitter("\"environmentChannels\": 4"), "environmentChannels", "unknown LumenEnvironmentChannel bits");
    [LumenFact] public static void RejectsNegativeEnvironmentForceScale() => RejectJson(Emitter("\"environmentForceScale\": -1"), "environmentForceScale", ">= 0");
    [LumenFact] public static void RejectsRingWaveWithoutSpeed() => RejectJson(Emitter(RingWave("[0,0.5,0]", 6)), "forceFields[0].parameters.x", "> 0");
    [LumenFact] public static void RejectsRingWaveWithoutShellThickness() => RejectJson(Emitter(RingWave("[4,0,0]", 6)), "forceFields[0].parameters.y", "> 0");
    [LumenFact] public static void RejectsRingWaveWithoutEndRadius() => RejectJson(Emitter(RingWave("[4,0.5,0]", 0)), "forceFields[0].radius", "> 0");

    // B. Collision
    [LumenFact] public static void RejectsUnknownCollisionMode() => RejectJson(Emitter("\"collision\": { \"mode\": 7 }"), "collision.mode", "unknown LumenCollisionMode");
    [LumenFact] public static void RejectsBounceAboveOne() => RejectJson(Emitter("\"collision\": { \"mode\": \"Bounce\", \"bounce\": 1.5 }"), "collision.bounce", "[0,1]");
    [LumenFact] public static void RejectsNegativeDampen() => RejectJson(Emitter("\"collision\": { \"mode\": \"Bounce\", \"dampen\": -0.1 }"), "collision.dampen", "[0,1]");
    [LumenFact] public static void RejectsLifetimeLossAboveOne() => RejectJson(Emitter("\"collision\": { \"mode\": \"Kill\", \"lifetimeLoss\": 2 }"), "collision.lifetimeLoss", "[0,1]");

    // C. Sub-emitters
    [LumenFact] public static void RejectsSubEmitterTargetOutOfRange() => RejectJson(Source("\"targetNodeIndex\": 5, \"count\": 1") + "," + Spark, "subEmitters[0].targetNodeIndex", "must be in [0, 2)");
    [LumenFact] public static void RejectsSubEmitterTargetingItsOwnNode() => RejectJson(Source("\"targetNodeIndex\": 0, \"count\": 1") + "," + Spark, "subEmitters[0].targetNodeIndex", "must differ from the source");
    [LumenFact] public static void RejectsSubEmitterTargetWithoutEmitter() => RejectJson(Source("\"targetNodeIndex\": 1, \"count\": 1") + ", { \"name\": \"empty\" }", "subEmitters[0].targetNodeIndex", "has no emitter");
    [LumenFact] public static void RejectsSubEmitterTargetWithRate() => RejectJson(Source("\"targetNodeIndex\": 1, \"count\": 1") + ", { \"name\": \"spark\", \"emitter\": { \"rateOverTime\": 3 } }", "subEmitters[0].targetNodeIndex", "rateOverTime 0 and no bursts");
    [LumenFact] public static void RejectsSubEmitterTargetWithBursts() => RejectJson(Source("\"targetNodeIndex\": 1, \"count\": 1") + ", { \"name\": \"spark\", \"emitter\": { \"bursts\": [[0, 4]] } }", "subEmitters[0].targetNodeIndex", "rateOverTime 0 and no bursts");
    [LumenFact] public static void RejectsSubEmitterTierMismatch() => RejectJson(Source("\"targetNodeIndex\": 1, \"count\": 1") + ", { \"name\": \"spark\", \"emitter\": { \"preferredTier\": \"L1_NativeCpu\" } }", "subEmitters[0].targetNodeIndex", "preferredTier");
    [LumenFact] public static void RejectsSubEmitterCycle() => RejectJson(
        "{ \"name\": \"a\", \"emitter\": { \"subEmitters\": [{ \"trigger\": \"Death\", \"targetNodeIndex\": 1, \"count\": 1 }] } }, " +
        "{ \"name\": \"b\", \"emitter\": { \"subEmitters\": [{ \"trigger\": \"Death\", \"targetNodeIndex\": 0, \"count\": 1 }] } }",
        "emitter.subEmitters", "cycle");
    [LumenFact] public static void RejectsNegativeSubEmitterCount() => RejectJson(Source("\"targetNodeIndex\": 1, \"count\": -1") + "," + Spark, "subEmitters[0].count", ">= 0");
    [LumenFact] public static void RejectsSubEmitterProbabilityAboveOne() => RejectJson(Source("\"targetNodeIndex\": 1, \"count\": 1, \"probability\": 1.5") + "," + Spark, "subEmitters[0].probability", "[0,1]");
    [LumenFact] public static void RejectsUnknownSubEmitterTrigger() => RejectJson("{ \"name\": \"source\", \"emitter\": { \"subEmitters\": [{ \"trigger\": 9, \"targetNodeIndex\": 1, \"count\": 1 }] } }," + Spark, "subEmitters[0].trigger", "unknown LumenSubEmitterTrigger");
    [LumenFact] public static void RejectsUnknownSubEmitterInheritBits() => RejectJson(Source("\"targetNodeIndex\": 1, \"count\": 1, \"inherit\": 8") + "," + Spark, "subEmitters[0].inherit", "unknown LumenSubEmitterInherit bits");
    [LumenFact] public static void RejectsCollisionTriggerWithoutCollision() => RejectJson("{ \"name\": \"source\", \"emitter\": { \"subEmitters\": [{ \"trigger\": \"Collision\", \"targetNodeIndex\": 1, \"count\": 1 }] } }," + Spark, "subEmitters[0].trigger", "collision.mode");
    [LumenFact] public static void RequiresSubEmitterCountJson() => RejectJson("{ \"name\": \"source\", \"emitter\": { \"subEmitters\": [{ \"trigger\": \"Death\", \"targetNodeIndex\": 1 }] } }," + Spark, "subEmitters[0].count", "required");
    [LumenFact] public static void RequiresSubEmitterTriggerXml() => RejectXml("<li><name>source</name><emitter><subEmitters><li><targetNodeIndex>1</targetNodeIndex><count>1</count></li></subEmitters></emitter></li>", "subEmitters[0].trigger", "required");

    // D. Distortion
    [LumenFact] public static void RequiresDistortionKindJson() => RejectJson(Distortion("\"radius\": 2, \"strength\": 0.1"), "distortion.kind", "required");
    [LumenFact] public static void RequiresDistortionRadiusXml() => RejectXml("<li><name>fx</name><distortion><kind>HeatHaze</kind><strength>0.1</strength><frequency>1</frequency></distortion></li>", "distortion.radius", "required");
    [LumenFact] public static void RejectsUnknownDistortionKind() => RejectJson(Distortion("\"kind\": 9, \"radius\": 2, \"strength\": 0.1"), "distortion.kind", "unknown LumenDistortionKind");
    [LumenFact] public static void RejectsNonPositiveDistortionRadius() => RejectJson(Distortion(Haze("\"radius\": 0, \"strength\": 0.1")), "distortion.radius", "> 0");
    [LumenFact] public static void RejectsNonPositiveDistortionStrength() => RejectJson(Distortion(Haze("\"radius\": 2, \"strength\": 0")), "distortion.strength", "> 0");
    [LumenFact] public static void RejectsRingWaveWithoutThickness() => RejectJson(Distortion(Ring("\"speed\": 6, \"duration\": 1")), "distortion.thickness", "required finite > 0 for RingWave");
    [LumenFact] public static void RejectsRingWaveWithoutSpeedField() => RejectJson(Distortion(Ring("\"thickness\": 0.5, \"duration\": 1")), "distortion.speed", "required finite > 0 for RingWave");
    [LumenFact] public static void RejectsRingWaveWithoutDuration() => RejectJson(Distortion(Ring("\"thickness\": 0.5, \"speed\": 6")), "distortion.duration", "> 0");
    [LumenFact] public static void RejectsRingWaveWithFrequency() => RejectJson(Distortion(Ring("\"thickness\": 0.5, \"speed\": 6, \"duration\": 1, \"frequency\": 2")), "distortion.frequency", "not used by RingWave");
    [LumenFact] public static void RejectsRippleWithoutFrequency() => RejectJson(Distortion("\"kind\": \"Ripple\", \"radius\": 3, \"strength\": 0.2, \"thickness\": 0.3, \"speed\": 2"), "distortion.frequency", "required finite > 0 for Ripple");
    [LumenFact] public static void RejectsHeatHazeWithThickness() => RejectJson(Distortion(Haze("\"radius\": 2, \"strength\": 0.1, \"thickness\": 0.5")), "distortion.thickness", "not used by HeatHaze");
    [LumenFact] public static void RejectsHeatHazeWithSpeed() => RejectJson(Distortion(Haze("\"radius\": 2, \"strength\": 0.1, \"speed\": 1")), "distortion.speed", "not used by HeatHaze");
    [LumenFact] public static void RejectsNegativeDistortionDuration() => RejectJson(Distortion(Haze("\"radius\": 2, \"strength\": 0.1, \"duration\": -1")), "distortion.duration", ">= 0");
    [LumenFact] public static void RejectsNegativeStrengthOverTime() => RejectJson(Distortion(Haze("\"radius\": 2, \"strength\": 0.1, \"strengthOverTime\": [[0, -1]]")), "distortion.strengthOverTime[0]", ">= 0");

    // E. Ribbon
    [LumenFact] public static void RequiresRibbonModeXml() => RejectXml("<li><name>fx</name><ribbon><width>1</width></ribbon></li>", "ribbon.mode", "required");
    [LumenFact] public static void RejectsUnknownRibbonMode() => RejectJson(Ribbon("\"mode\": 5"), "ribbon.mode", "unknown LumenRibbonMode");
    [LumenFact] public static void RejectsTrailWithoutLifetime() => RejectJson(Ribbon("\"mode\": \"Trail\", \"minVertexDistance\": 0.1, \"maxPoints\": 8"), "ribbon.lifetime", "required finite > 0 for Trail");
    [LumenFact] public static void RejectsTrailWithoutMinVertexDistance() => RejectJson(Ribbon("\"mode\": \"Trail\", \"lifetime\": 1, \"maxPoints\": 8"), "ribbon.minVertexDistance", "required finite > 0 for Trail");
    [LumenFact] public static void RejectsTrailWithOnePoint() => RejectJson(Ribbon(Trail("\"maxPoints\": 1")), "ribbon.maxPoints", "> 1");
    [LumenFact] public static void RejectsTrailWithSegments() => RejectJson(Ribbon(Trail("\"maxPoints\": 8, \"segments\": 4")), "ribbon.segments", "not used by Trail");
    [LumenFact] public static void RejectsTrailWithEndpoint() => RejectJson(Ribbon(Trail("\"maxPoints\": 8, \"endLocal\": [0,0,5]")), "ribbon.endLocal/endAtTarget", "not used by Trail");
    [LumenFact] public static void RejectsTrailWithNoise() => RejectJson(Ribbon(Trail("\"maxPoints\": 8, \"noiseAmplitude\": 0.2")), "ribbon.noiseAmplitude", "not used by Trail");
    [LumenFact] public static void RejectsBeamWithoutEndpoint() => RejectJson(Ribbon("\"mode\": \"Beam\", \"segments\": 1"), "ribbon.endLocal/endAtTarget", "exactly one");
    [LumenFact] public static void RejectsBeamWithBothEndpoints() => RejectJson(Ribbon("\"mode\": \"Beam\", \"segments\": 1, \"endLocal\": [0,0,5], \"endAtTarget\": true"), "ribbon.endLocal/endAtTarget", "exactly one");
    [LumenFact] public static void RejectsBeamWithoutSegments() => RejectJson(Ribbon("\"mode\": \"Beam\", \"endAtTarget\": true"), "ribbon.segments", ">= 1 for Beam");
    [LumenFact] public static void RejectsBeamWithTrailLifetime() => RejectJson(Ribbon(Beam("\"lifetime\": 1")), "ribbon.lifetime", "not used by Beam");
    [LumenFact] public static void RejectsBeamWithNegativeNoise() => RejectJson(Ribbon(Beam("\"noiseFrequency\": -1")), "ribbon.noiseFrequency", ">= 0");
    [LumenFact] public static void RejectsNonPositiveRibbonWidth() => RejectJson(Ribbon(Beam("\"width\": 0")), "ribbon.width", "> 0");
    [LumenFact] public static void RejectsNegativeWidthOverLength() => RejectJson(Ribbon(Beam("\"widthOverLength\": [[0, -1]]")), "ribbon.widthOverLength[0]", ">= 0");
    [LumenFact] public static void RejectsWidthOverLengthOutsideNormalizedLength() => RejectJson(Ribbon(Beam("\"widthOverLength\": [[1.5, 1]]")), "ribbon.widthOverLength[0].time", "[0,1]");
    [LumenFact] public static void RejectsColorOverLengthAlphaAboveOne() => RejectJson(Ribbon(Beam("\"colorOverLength\": [[0, [1,1,1,2]]]")), "ribbon.colorOverLength[0].color", "alpha");
    [LumenFact] public static void RejectsNegativeRibbonHdrIntensity() => RejectJson(Ribbon(Beam("\"hdrIntensity\": -1")), "ribbon.hdrIntensity", ">= 0");
    [LumenFact] public static void RejectsNegativeRibbonSoftFade() => RejectJson(Ribbon(Beam("\"softParticleFadeDistance\": -1")), "ribbon.softParticleFadeDistance", ">= 0");
    [LumenFact] public static void RejectsUnknownRibbonTextureMode() => RejectJson(Ribbon(Beam("\"textureMode\": 4")), "ribbon.textureMode", "unknown LumenRibbonTextureMode");
    [LumenFact] public static void RejectsUnknownRibbonAlignment() => RejectJson(Ribbon(Beam("\"alignment\": 4")), "ribbon.alignment", "unknown LumenRibbonAlignment");

    private const string Spark = "{ \"name\": \"spark\", \"emitter\": {} }";

    private static string Emitter(string fields) => $"{{ \"name\": \"source\", \"emitter\": {{ {fields} }} }}";
    private static string RingWave(string parameters, float radius) => $"\"forceFields\": [{{ \"kind\": \"RingWave\", \"parameters\": {parameters}, \"radius\": {radius.ToString(CultureInfo.InvariantCulture)}, \"strength\": 2 }}]";
    private static string Source(string sub) => $"{{ \"name\": \"source\", \"emitter\": {{ \"subEmitters\": [{{ \"trigger\": \"Death\", {sub} }}] }} }}";
    private static string Distortion(string fields) => $"{{ \"name\": \"fx\", \"distortion\": {{ {fields} }} }}";
    private static string Ring(string fields) => $"\"kind\": \"RingWave\", \"radius\": 4, \"strength\": 0.3, {fields}";
    private static string Haze(string fields) => $"\"kind\": \"HeatHaze\", \"frequency\": 1, {fields}";
    private static string Ribbon(string fields) => $"{{ \"name\": \"fx\", \"ribbon\": {{ {fields} }} }}";
    private static string Trail(string fields) => $"\"mode\": \"Trail\", \"lifetime\": 1, \"minVertexDistance\": 0.1, {fields}";
    private static string Beam(string fields) => $"\"mode\": \"Beam\", \"endAtTarget\": true, \"segments\": 1, {fields}";

    private static void RejectJson(string nodes, string field, string message)
        => Reject(() => LumenJsonAssetLoader.Parse($"{{ \"effectId\": \"vfx.reject\", \"nodes\": [{nodes}] }}"), "vfx.reject", field, message);

    private static void RejectXml(string node, string field, string message)
        => Reject(() => Xml("vfx.reject", node), "vfx.reject", field, message);

    private static void Reject(Action action, string asset, string field, string message)
    {
        try
        {
            action();
        }
        catch (InvalidDataException error)
        {
            if (!error.Message.Contains(asset, StringComparison.Ordinal) || !error.Message.Contains(field, StringComparison.Ordinal) ||
                !error.Message.Contains(message, StringComparison.Ordinal))
                throw new InvalidOperationException($"Expected '{asset}', '{field}' and '{message}', got: {error.Message}");
            return;
        }
        throw new InvalidOperationException($"Expected rejection of '{field}': {message}.");
    }

    private static EffectAsset Xml(string defName, params string[] nodes)
    {
        var list = new List<LumenNodeXml>();
        foreach (string node in nodes)
        {
            var document = new XmlDocument();
            document.LoadXml(node);
            list.Add((LumenNodeXml)LoadXml(typeof(LumenNodeXml), document.DocumentElement!));
        }
        return new LumenEffectDef { defName = defName, nodes = list }.ToEffectAsset();
    }

    // Offline, DirectXmlToObject drops every list item: its per-item PostLoad calls DeepProfiler.Start, which throws
    // NullReferenceException without the game host. Lists and the DTOs holding them are therefore assembled here, and
    // every object and value inside them is still parsed by the real loader.
    private static object LoadXml(Type type, XmlElement element)
    {
        var deferred = new List<(FieldInfo Field, XmlElement Element)>();
        foreach (XmlElement child in element.ChildNodes.OfType<XmlElement>().ToList())
        {
            FieldInfo? field = type.GetField(child.Name);
            if (field is null || !(IsList(field.FieldType) || field.FieldType.Namespace == typeof(LumenNodeXml).Namespace))
                continue;
            deferred.Add((field, child));
            element.RemoveChild(child);
        }

        object value = typeof(Verse.DirectXmlToObject).GetMethod(nameof(Verse.DirectXmlToObject.ObjectFromXml))!
            .MakeGenericMethod(type).Invoke(null, new object[] { element, false })!;
        foreach ((FieldInfo field, XmlElement child) in deferred)
        {
            if (!IsList(field.FieldType))
            {
                field.SetValue(value, LoadXml(field.FieldType, child));
                continue;
            }
            var list = (System.Collections.IList)Activator.CreateInstance(field.FieldType)!;
            foreach (XmlElement item in child.ChildNodes.OfType<XmlElement>())
                list.Add(LoadXml(field.FieldType.GetGenericArguments()[0], item));
            field.SetValue(value, list);
        }
        return value;
    }

    private static bool IsList(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);

    private static void Same(string expected, string actual, string format)
    {
        if (expected != actual)
            throw new InvalidOperationException($"{format} differs from C#.\nC#:   {expected}\n{format}: {actual}");
    }

    // Canonical text of a contract value: public properties in declaration order, arrays element by element.
    private static string Describe(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        Enum or bool or int or uint => value.ToString()!,
        Vector3 v => $"({Describe(v.X)},{Describe(v.Y)},{Describe(v.Z)})",
        Vector4 v => $"({Describe(v.X)},{Describe(v.Y)},{Describe(v.Z)},{Describe(v.W)})",
        Quaternion q => $"({Describe(q.X)},{Describe(q.Y)},{Describe(q.Z)},{Describe(q.W)})",
        Array array => "[" + string.Join(", ", array.Cast<object?>().Select(Describe)) + "]",
        _ => value.GetType().Name + "{" + string.Join(", ", value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => $"{property.Name}={Describe(property.GetValue(value))}")) + "}",
    };

    private static EffectAsset FullCSharp() => new()
    {
        EffectId = "vfx.parity",
        Nodes = new[]
        {
            new EffectNodeSpec
            {
                Name = "source",
                Emitter = new EmitterSpec
                {
                    Name = "source", RateOverTime = 5f,
                    EnvironmentChannels = LumenEnvironmentChannel.Cursor, EnvironmentForceScale = 0.5f,
                    Collision = new CollisionSpec { Mode = LumenCollisionMode.Bounce, Bounce = 0.4f, Dampen = 0.2f, LifetimeLoss = 0.1f, KillUnderRoof = true },
                    SubEmitters = new[] { new SubEmitterSpec(LumenSubEmitterTrigger.Collision, 1, 3, 0.5f, LumenSubEmitterInherit.Color | LumenSubEmitterInherit.Size) },
                    ForceFields = new[] { new ForceFieldSpec(ForceFieldKind.RingWave, new Vector3(1f, 0f, 2f), new Vector3(4f, 0.5f, 0f), 6f, -2f) },
                },
            },
            new EffectNodeSpec { Name = "spark", Emitter = new EmitterSpec { Name = "spark", EnvironmentChannels = LumenEnvironmentChannel.None } },
            new EffectNodeSpec
            {
                Name = "ring",
                Distortion = new DistortionSpec
                {
                    Kind = LumenDistortionKind.RingWave, Radius = 4f, Strength = 0.3f, Thickness = 0.5f, Speed = 6f, Duration = 0.8f,
                    StrengthOverTime = new ScalarCurve(new[] { new CurveKey(0f, 1f), new CurveKey(0.8f, 0f) }),
                },
            },
            new EffectNodeSpec
            {
                Name = "ripple",
                Distortion = new DistortionSpec { Kind = LumenDistortionKind.Ripple, Radius = 3f, Strength = 0.2f, Thickness = 0.3f, Speed = 2f, Frequency = 2f },
            },
            new EffectNodeSpec
            {
                Name = "haze",
                Distortion = new DistortionSpec { Kind = LumenDistortionKind.HeatHaze, Radius = 2f, Strength = 0.1f, Frequency = 1.5f, Duration = 3f },
            },
            new EffectNodeSpec
            {
                Name = "trail",
                Ribbon = new RibbonSpec
                {
                    Mode = LumenRibbonMode.Trail, Lifetime = 0.6f, MinVertexDistance = 0.1f, MaxPoints = 32, Width = 0.3f,
                    WidthOverLength = new ScalarCurve(new[] { new CurveKey(0f, 1f), new CurveKey(1f, 0f) }),
                    ColorOverLength = new ColorGradient(new[] { new ColorStop(0f, new Vector4(1f, 0.5f, 0f, 1f)), new ColorStop(1f, new Vector4(1f, 0f, 0f, 0f)) }),
                    HdrIntensity = 2f, TextureKey = "Lumen/Trail", BlendMode = LumenBlendMode.AlphaBlend,
                    TextureMode = LumenRibbonTextureMode.Tile, Alignment = LumenRibbonAlignment.Ground, SoftParticleFadeDistance = 0.25f,
                },
            },
            new EffectNodeSpec
            {
                Name = "beam",
                Ribbon = new RibbonSpec { Mode = LumenRibbonMode.Beam, EndLocal = new Vector3(0f, 0f, 5f), Segments = 8, NoiseAmplitude = 0.2f, NoiseFrequency = 10f },
            },
            new EffectNodeSpec { Name = "zap", Ribbon = new RibbonSpec { Mode = LumenRibbonMode.Beam, EndAtTarget = true, Segments = 1 } },
        },
    };

    private const string FullJson = """
        { "effectId": "vfx.parity", "nodes": [
          { "name": "source", "emitter": {
              "rateOverTime": 5, "environmentChannels": "Cursor", "environmentForceScale": 0.5,
              "collision": { "mode": "Bounce", "bounce": 0.4, "dampen": 0.2, "lifetimeLoss": 0.1, "killUnderRoof": true },
              "subEmitters": [{ "trigger": "Collision", "targetNodeIndex": 1, "count": 3, "probability": 0.5, "inherit": "Color, Size" }],
              "forceFields": [{ "kind": "RingWave", "centerOrAxis": [1,0,2], "parameters": [4,0.5,0], "radius": 6, "strength": -2 }] } },
          { "name": "spark", "emitter": { "environmentChannels": "None" } },
          { "name": "ring", "distortion": { "kind": "RingWave", "radius": 4, "strength": 0.3, "thickness": 0.5, "speed": 6, "duration": 0.8,
              "strengthOverTime": [[0,1],[0.8,0]] } },
          { "name": "ripple", "distortion": { "kind": "Ripple", "radius": 3, "strength": 0.2, "thickness": 0.3, "speed": 2, "frequency": 2 } },
          { "name": "haze", "distortion": { "kind": "HeatHaze", "radius": 2, "strength": 0.1, "frequency": 1.5, "duration": 3 } },
          { "name": "trail", "ribbon": { "mode": "Trail", "lifetime": 0.6, "minVertexDistance": 0.1, "maxPoints": 32, "width": 0.3,
              "widthOverLength": [[0,1],[1,0]], "colorOverLength": [[0,[1,0.5,0,1]],[1,[1,0,0,0]]], "hdrIntensity": 2,
              "textureKey": "Lumen/Trail", "blendMode": "AlphaBlend", "textureMode": "Tile", "alignment": "Ground", "softParticleFadeDistance": 0.25 } },
          { "name": "beam", "ribbon": { "mode": "Beam", "endLocal": [0,0,5], "segments": 8, "noiseAmplitude": 0.2, "noiseFrequency": 10 } },
          { "name": "zap", "ribbon": { "mode": "Beam", "endAtTarget": true, "segments": 1 } } ] }
        """;

    private static readonly string[] FullXml =
    {
        "<li><name>source</name><emitter><rateOverTime>5</rateOverTime><environmentChannels>Cursor</environmentChannels>" +
        "<environmentForceScale>0.5</environmentForceScale><collision><mode>Bounce</mode><bounce>0.4</bounce><dampen>0.2</dampen>" +
        "<lifetimeLoss>0.1</lifetimeLoss><killUnderRoof>true</killUnderRoof></collision><subEmitters><li><trigger>Collision</trigger>" +
        "<targetNodeIndex>1</targetNodeIndex><count>3</count><probability>0.5</probability><inherit>Color, Size</inherit></li></subEmitters>" +
        "<forceFields><li><kind>RingWave</kind><centerOrAxis>(1,0,2)</centerOrAxis><parameters>(4,0.5,0)</parameters><radius>6</radius>" +
        "<strength>-2</strength></li></forceFields></emitter></li>",
        "<li><name>spark</name><emitter><environmentChannels>None</environmentChannels></emitter></li>",
        "<li><name>ring</name><distortion><kind>RingWave</kind><radius>4</radius><strength>0.3</strength><thickness>0.5</thickness>" +
        "<speed>6</speed><duration>0.8</duration><strengthOverTime><li><time>0</time><value>1</value></li><li><time>0.8</time><value>0</value></li>" +
        "</strengthOverTime></distortion></li>",
        "<li><name>ripple</name><distortion><kind>Ripple</kind><radius>3</radius><strength>0.2</strength><thickness>0.3</thickness>" +
        "<speed>2</speed><frequency>2</frequency></distortion></li>",
        "<li><name>haze</name><distortion><kind>HeatHaze</kind><radius>2</radius><strength>0.1</strength><frequency>1.5</frequency>" +
        "<duration>3</duration></distortion></li>",
        "<li><name>trail</name><ribbon><mode>Trail</mode><lifetime>0.6</lifetime><minVertexDistance>0.1</minVertexDistance><maxPoints>32</maxPoints>" +
        "<width>0.3</width><widthOverLength><li><time>0</time><value>1</value></li><li><time>1</time><value>0</value></li></widthOverLength>" +
        "<colorOverLength><li><time>0</time><color>(1,0.5,0,1)</color></li><li><time>1</time><color>(1,0,0,0)</color></li></colorOverLength>" +
        "<hdrIntensity>2</hdrIntensity><textureKey>Lumen/Trail</textureKey><blendMode>AlphaBlend</blendMode><textureMode>Tile</textureMode>" +
        "<alignment>Ground</alignment><softParticleFadeDistance>0.25</softParticleFadeDistance></ribbon></li>",
        "<li><name>beam</name><ribbon><mode>Beam</mode><endLocal>(0,0,5)</endLocal><segments>8</segments><noiseAmplitude>0.2</noiseAmplitude>" +
        "<noiseFrequency>10</noiseFrequency></ribbon></li>",
        "<li><name>zap</name><ribbon><mode>Beam</mode><endAtTarget>true</endAtTarget><segments>1</segments></ribbon></li>",
    };
}
