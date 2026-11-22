using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Contracts;

namespace RimLumen.WebSim.Tests;

internal static class WebDirectXmlLoaderRulesSuite
{
    public static void Register(List<(string Name, Action Run)> cases)
    {
        cases.Add(("XmlRules.ScalarTypesAndDefaults", ScalarTypesAndDefaults));
        cases.Add(("XmlRules.IntPermissiveLogsWarning", IntPermissiveLogsWarning));
        cases.Add(("XmlRules.StringEmptyNullAndCData", StringEmptyNullAndCData));
        cases.Add(("XmlRules.EnumsAndFlagsStrict", EnumsAndFlagsStrict));
        cases.Add(("XmlRules.Vector3AndQuaternionParity", Vector3AndQuaternionParity));
        cases.Add(("XmlRules.ListCommentsEmptyAndInvalid", ListCommentsEmptyAndInvalid));
        cases.Add(("XmlRules.DuplicateCaseAndUnknownRejected", DuplicateCaseAndUnknownRejected));
        cases.Add(("XmlRules.AllSevenLegacyFieldsRejectedByCore", AllSevenLegacyFieldsRejectedByCore));
        cases.Add(("XmlRules.UnsupportedFeaturesRejectedLoudly", UnsupportedFeaturesRejectedLoudly));
    }

    private static string Wrap(string body) =>
        $"<Defs><RimLumen.Core.Assets.LumenEffectDef><defName>RuleTest</defName>{body}</RimLumen.Core.Assets.LumenEffectDef></Defs>";

    private static string WrapEmitter(string emitterBody, string extraNodes = "") => Wrap(
        $"<nodes><li><name>core</name><emitter>{emitterBody}</emitter></li>{extraNodes}</nodes>");

    private static void ScalarTypesAndDefaults()
    {
        EffectAsset asset = WebDirectXmlLoader.ParseSingle(WrapEmitter(
            "<maxParticles>256</maxParticles><duration>1.25</duration><looping>True</looping><worldSpaceSimulation>false</worldSpaceSimulation>"));
        EmitterSpec e = asset.Nodes[0].Emitter!;
        TestAssert.Equal(256, e.MaxParticles, "maxParticles");
        TestAssert.Equal(1.25f, e.Duration, "duration");
        TestAssert.True(e.Looping, "bool case-insensitive True");
        TestAssert.True(!e.WorldSpaceSimulation, "bool false");
        TestAssert.Equal(2f, asset.DefaultDuration, "defaultDuration default");
    }

    private static void IntPermissiveLogsWarning()
    {
        var warnings = new List<string>();
        Action<string>? prev = Verse.Log.WarningSink;
        Verse.Log.WarningSink = warnings.Add;
        try
        {
            EffectAsset asset = WebDirectXmlLoader.ParseSingle(WrapEmitter("<maxParticles>12.7</maxParticles>"));
            TestAssert.Equal(12, asset.Nodes[0].Emitter!.MaxParticles, "ParseIntPermissive float truncation");
            TestAssert.True(warnings.Count > 0 && warnings[0].Contains("Parsed 12.7 as int."), "ParseIntPermissive logs Verse.Log.Warning");
        }
        finally
        {
            Verse.Log.WarningSink = prev;
        }
    }

    private static void StringEmptyNullAndCData()
    {
        string xml = Wrap(
            "<nodes><li><name>n0</name><emitter><textureKey /><name><![CDATA[line1\\nraw]]></name><shapePosition>(0,0,0)</shapePosition></emitter></li></nodes>" +
            "<timelineEvents><li><time>0.2</time><eventName>ev\\n1</eventName></li></timelineEvents>");
        EffectAsset asset = WebDirectXmlLoader.ParseSingle(xml);
        TestAssert.Equal(null, asset.Nodes[0].Emitter!.TextureKey, "empty textureKey becomes null in ToEmitterSpec");
        TestAssert.Equal("line1\\nraw", asset.Nodes[0].Emitter!.Name, "CDATA keeps literal backslash-n");
        TestAssert.Equal("ev\n1", asset.TimelineEvents[0].EventName, "text replaces escaped \\n with newline");

        EffectAsset withNull = WebDirectXmlLoader.ParseSingle(Wrap(
            "<initialState IsNull=\"True\" /><nodes><li><name>n0</name><emitter><textureKey IsNull=\"true\" /></emitter></li></nodes>"));
        TestAssert.Equal(null, withNull.InitialState, "IsNull true");
        TestAssert.Equal(null, withNull.Nodes[0].Emitter!.TextureKey, "nullable string IsNull");

        var ex = TestAssert.Throws<XmlAssetParseException>(
            () => WebDirectXmlLoader.ParseSingle(WrapEmitter("<duration><![CDATA[1.5]]></duration>")),
            "CDATA on non-string rejected");
        TestAssert.Contains(ex.Message, "CDATA can only be used for strings", "CDATA non-string error");
    }

    private static void EnumsAndFlagsStrict()
    {
        EffectAsset asset = WebDirectXmlLoader.ParseSingle(WrapEmitter(
            "<environmentChannels>Explosion, Cursor</environmentChannels>" +
            "<subEmitters><li><trigger>Death</trigger><targetNodeIndex>1</targetNodeIndex><count>2</count><inherit>Color, Size</inherit></li></subEmitters>",
            "<li><name>child</name><emitter><rateOverTime>0</rateOverTime></emitter></li>"));
        TestAssert.Equal(LumenEnvironmentChannel.Explosion | LumenEnvironmentChannel.Cursor, asset.Nodes[0].Emitter!.EnvironmentChannels, "flags comma list");
        TestAssert.Equal(LumenSubEmitterInherit.Color | LumenSubEmitterInherit.Size, asset.Nodes[0].Emitter!.SubEmitters[0].Inherit, "subEmitter flags");

        EffectAsset none = WebDirectXmlLoader.ParseSingle(WrapEmitter("<environmentChannels>None</environmentChannels>"));
        TestAssert.Equal(LumenEnvironmentChannel.None, none.Nodes[0].Emitter!.EnvironmentChannels, "None flag");

        TestAssert.Throws<XmlAssetParseException>(
            () => WebDirectXmlLoader.ParseSingle(WrapEmitter("<shape>cone</shape>")),
            "wrong case enum rejected");

        var listFlagEx = TestAssert.Throws<XmlAssetParseException>(
            () => WebDirectXmlLoader.ParseSingle(WrapEmitter("<environmentChannels><li>Explosion</li></environmentChannels>")),
            "<li> flags rejected on byte enum");
        TestAssert.Contains(listFlagEx.Message, "comma-separated", "byte enum li error hints comma-separated");
    }

    private static void Vector3AndQuaternionParity()
    {
        EffectAsset asset = WebDirectXmlLoader.ParseSingle(WrapEmitter(
            "<shapeExtents>(1.5, 2.25, -3.5)</shapeExtents>" +
            "<shapeRotation>(0, 0.70710677, 0, 0.70710677)</shapeRotation>")
            .Replace("</nodes>", "<li><name>beam</name><ribbon><mode>Beam</mode><endLocal IsNull=\"true\" /><endAtTarget>true</endAtTarget><segments>2</segments></ribbon></li></nodes>"));
        TestAssert.Equal(new Vector3(1.5f, 2.25f, -3.5f), asset.Nodes[0].Emitter!.ShapeExtents, "Vector3 invariant culture");
        TestAssert.True(MathF.Abs(asset.Nodes[0].Emitter!.ShapeRotation.Y - 0.70710677f) < 1e-4f, "Quaternion parsed");
        TestAssert.Equal(null, asset.Nodes[1].Ribbon!.EndLocal, "Nullable Vector3? IsNull");

        TestAssert.Throws<XmlAssetParseException>(
            () => WebDirectXmlLoader.ParseSingle(WrapEmitter("<shapeExtents>(1, 2)</shapeExtents>")),
            "Vector3 with 2 components rejected");

        TestAssert.Throws<XmlAssetParseException>(
            () => WebDirectXmlLoader.ParseSingle(WrapEmitter("<shapeRotation>(0, 0, 0, 1, 5)</shapeRotation>")),
            "Quaternion with 5 components rejected");
    }

    private static void ListCommentsEmptyAndInvalid()
    {
        EffectAsset withComment = WebDirectXmlLoader.ParseSingle(WrapEmitter(
            "<forceFields><!-- comment ignored --><li><kind>GravityDrag</kind><strength>5</strength></li></forceFields>" +
            "<bursts /><subEmitters />"));
        TestAssert.Equal(1, withComment.Nodes[0].Emitter!.ForceFields.Length, "comment inside list ignored");
        TestAssert.Equal(0, withComment.Nodes[0].Emitter!.Bursts.Length, "empty <bursts /> becomes empty array");
        TestAssert.Equal(0, withComment.Nodes[0].Emitter!.SubEmitters.Length, "empty <subEmitters /> becomes empty array");

        var emptyCurveEx = TestAssert.Throws<XmlAssetParseException>(
            () => WebDirectXmlLoader.ParseSingle(WrapEmitter("<sizeOverLifetime />")),
            "empty <sizeOverLifetime /> parses as empty list and is rejected by Core");
        TestAssert.Contains(emptyCurveEx.Message, "at least one key is required", "empty curve list validation");

        var rawTextEx = TestAssert.Throws<XmlAssetParseException>(
            () => WebDirectXmlLoader.ParseSingle(Wrap("<nodes>bare_text</nodes>")),
            "raw text in list rejected");
        TestAssert.Contains(rawTextEx.Message, "<li>", "raw text in list mentions <li>");

        var nonLiEx = TestAssert.Throws<XmlAssetParseException>(
            () => WebDirectXmlLoader.ParseSingle(WrapEmitter("<forceFields><item><kind>GravityDrag</kind><strength>1</strength></item></forceFields>")),
            "non-li element rejected");
        TestAssert.Contains(nonLiEx.Message, "<li>", "non-li mentions <li>");
    }

    private static void DuplicateCaseAndUnknownRejected()
    {
        var dup = TestAssert.Throws<XmlAssetParseException>(
            () => WebDirectXmlLoader.ParseSingle(WrapEmitter("<duration>1</duration><duration>2</duration>")),
            "duplicate field rejected");
        TestAssert.Contains(dup.Message, "same field twice", "duplicate message");

        var caseMismatch = TestAssert.Throws<XmlAssetParseException>(
            () => WebDirectXmlLoader.ParseSingle(WrapEmitter("<maxparticles>100</maxparticles>")),
            "case-mismatch field rejected");
        TestAssert.Contains(caseMismatch.Message, "case-sensitive", "case mismatch message");
        TestAssert.Contains(caseMismatch.Message, "maxParticles", "names expected field");

        var unknown = TestAssert.Throws<XmlAssetParseException>(
            () => WebDirectXmlLoader.ParseSingle(WrapEmitter("<doesNotExist>42</doesNotExist>")),
            "unknown field rejected loudly");
        TestAssert.Contains(unknown.Message, "Unknown XML tag <doesNotExist>", "unknown tag error");
    }

    private static void AllSevenLegacyFieldsRejectedByCore()
    {
        AssertLegacy("<nodes><li><name>n</name><emitter><startColorLinear>(1,1,1,1)</startColorLinear></emitter></li></nodes>", "startColorLinear");
        AssertLegacy("<nodes><li><name>n</name><emitter><bloomWeight>1</bloomWeight></emitter></li></nodes>", "bloomWeight");
        AssertLegacy("<nodes><li><name>n</name><emitter><hdrMultiplier>2</hdrMultiplier></emitter></li></nodes>", "hdrMultiplier");
        AssertLegacy("<nodes><li><name>n</name><emitter><colorOverLifetime><li><time>0</time><rgbaLinear>(1,1,1,1)</rgbaLinear></li></colorOverLifetime></emitter></li></nodes>", "rgbaLinear");
        AssertLegacy("<nodes><li><name>n</name><pointLight><colorLinear>(1,1,1)</colorLinear></pointLight></li></nodes>", "colorLinear");
        AssertLegacy("<nodes><li><name>n</name><pointLight><colorRgb>(1,1,1)</colorRgb></pointLight></li></nodes>", "colorRgb");
        AssertLegacy("<nodes><li><name>n</name><pointLight><overlightRadius>2</overlightRadius></pointLight></li></nodes>", "overlightRadius");

        void AssertLegacy(string body, string expectedToken)
        {
            var ex = TestAssert.Throws<XmlAssetParseException>(() => WebDirectXmlLoader.ParseSingle(Wrap(body)), $"legacy {expectedToken}");
            TestAssert.Contains(ex.Message, expectedToken, $"legacy message contains {expectedToken}");
        }

        const string multiLineLegacy =
            "<Defs>\n" +
            "  <RimLumen.Core.Assets.LumenEffectDef>\n" +
            "    <defName>LineTest</defName>\n" +
            "    <nodes><li><name>n</name><emitter>\n" +
            "      <startColorLinear>(1,0,0,1)</startColorLinear>\n" +
            "    </emitter></li></nodes>\n" +
            "  </RimLumen.Core.Assets.LumenEffectDef>\n" +
            "</Defs>";
        var lineEx = TestAssert.Throws<XmlAssetParseException>(() => WebDirectXmlLoader.ParseSingle(multiLineLegacy), "multiline legacy");
        TestAssert.Equal(5, lineEx.LineNumber, "legacy XML error pinpoints offending child element line");
    }

    private static void UnsupportedFeaturesRejectedLoudly()
    {
        TestAssert.Throws<XmlAssetParseException>(
            () => WebDirectXmlLoader.ParseSingle("<Defs><RimLumen.Core.Assets.LumenEffectDef ParentName=\"BaseEffect\"><defName>A</defName><nodes><li><name>n</name><emitter /></li></nodes></RimLumen.Core.Assets.LumenEffectDef></Defs>"),
            "ParentName rejected");

        TestAssert.Throws<XmlAssetParseException>(
            () => WebDirectXmlLoader.ParseSingle("<Defs><RimLumen.Core.Assets.LumenEffectDef Abstract=\"True\"><defName>A</defName><nodes><li><name>n</name><emitter /></li></nodes></RimLumen.Core.Assets.LumenEffectDef></Defs>"),
            "Abstract rejected");

        TestAssert.Throws<XmlAssetParseException>(
            () => WebDirectXmlLoader.ParseSingle("<Patch><Operation Class=\"PatchOperationReplace\"><xpath>/Defs</xpath></Operation></Patch>"),
            "PatchOperation rejected");

        TestAssert.Throws<XmlAssetParseException>(
            () => WebDirectXmlLoader.ParseSingle("<Defs><RimLumen.Core.Assets.LumenEffectDef><defName>A</defName><nodes><li><name>n</name><emitter MayRequire=\"Ludeon.RimWorld.Royalty\" /></li></nodes></RimLumen.Core.Assets.LumenEffectDef></Defs>"),
            "MayRequire rejected");

        TestAssert.Throws<XmlAssetParseException>(
            () => WebDirectXmlLoader.ParseSingle("<Defs><ThingDef><defName>NotLumen</defName></ThingDef></Defs>"),
            "non-LumenEffectDef element rejected");
    }
}
