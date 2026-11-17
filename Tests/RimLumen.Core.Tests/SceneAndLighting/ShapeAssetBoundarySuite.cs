using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class ShapeAssetBoundarySuite
{
    [LumenFact]
    public static void JsonAndXmlNormalizeTheSameNonidentityShapeTransform()
    {
        EmitterSpec json = Parse(""" "shapePosition": [3,4,5], "shapeRotation": [0,0,2,2], "shapeScale": [-2,0,3]""");
        EmitterSpec xml = ConvertXml(new LumenEmitterXml
        {
            shapePosition = new UnityEngine.Vector3(3f, 4f, 5f),
            shapeRotation = new UnityEngine.Quaternion(0f, 0f, 2f, 2f),
            shapeScale = new UnityEngine.Vector3(-2f, 0f, 3f),
        });
        // +90 degrees about Z: mirrored X maps to -Y, flattened Y contributes nothing; offset is applied last.
        Near(new Vector3(3f, 2f, 14f), TransformPosition(json, new Vector3(1f, 2f, 3f)));
        Near(new Vector3(3f, 2f, 14f), TransformPosition(xml, new Vector3(1f, 2f, 3f)));
        Near(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, json.ShapeRotation));
        Near(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, xml.ShapeRotation));
    }

    [LumenFact]
    public static void JsonObjectRotationAndUniformScaleReuseExistingFormats()
    {
        EmitterSpec emitter = Parse(""" "shapePosition": { "x": 3, "y": 4, "z": 5 }, "shapeRotation": { "z": 2, "w": 2 }, "shapeScale": -2""");
        Near(new Vector3(7f, 2f, -1f), TransformPosition(emitter, new Vector3(1f, 2f, 3f)));
    }

    [LumenFact]
    public static void OmittedShapeTransformsPreserveAuthoredSamplePosition()
    {
        Vector3 sample = new(2f, -3f, 4f);
        Near(sample, TransformPosition(Parse(""), sample));
        Near(sample, TransformPosition(ConvertXml(new LumenEmitterXml()), sample));
    }

    [LumenFact]
    public static void JsonRejectsDegenerateOrMalformedQuaternion()
    {
        Reject(() => Parse(""" "shapeRotation": [0,0,0,0]"""), "shapeRotation");
        Reject(() => Parse(""" "shapeRotation": [0,0,1]"""), "shapeRotation");
    }

    [LumenFact]
    public static void JsonRejectsNonfiniteShapeFields()
    {
        Reject(() => Parse(""" "shapePosition": [1e100,0,0]"""), "shapePosition");
        Reject(() => Parse(""" "shapeScale": [0,1e100,1]"""), "shapeScale");
        Reject(() => Parse(""" "shapeRotation": [0,0,0,1e100]"""), "shapeRotation");
    }

    [LumenFact]
    public static void XmlRejectsDegenerateOrNonfiniteQuaternion()
    {
        Reject(() => ConvertXml(new LumenEmitterXml { shapeRotation = new UnityEngine.Quaternion(0f, 0f, 0f, 0f) }), "shapeRotation");
        Reject(() => ConvertXml(new LumenEmitterXml { shapeRotation = new UnityEngine.Quaternion(float.NaN, 0f, 0f, 1f) }), "shapeRotation");
    }

    [LumenFact]
    public static void XmlRejectsNonfinitePositionAndScale()
    {
        Reject(() => ConvertXml(new LumenEmitterXml { shapePosition = new UnityEngine.Vector3(float.PositiveInfinity, 0f, 0f) }), "shapePosition");
        Reject(() => ConvertXml(new LumenEmitterXml { shapeScale = new UnityEngine.Vector3(1f, float.NaN, 1f) }), "shapeScale");
    }

    [LumenFact]
    public static void ProgrammaticUnitRotationAndMirroredFlattenedScaleAreAccepted()
    {
        var emitter = new EmitterSpec
        {
            Name = "shape", ShapePosition = new Vector3(3f, 4f, 5f),
            ShapeRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
            ShapeScale = new Vector3(-2f, 0f, 3f),
        };
        LumenAssetRules.Validate(Asset(emitter), "shape-programmatic");
        Near(new Vector3(3f, 2f, 14f), TransformPosition(emitter, new Vector3(1f, 2f, 3f)));
    }

    [LumenFact]
    public static void ProgrammaticRotationRejectsNonunitWithoutNormalization()
    {
        var emitter = new EmitterSpec { Name = "shape", ShapeRotation = new Quaternion(0f, 0f, 2f, 2f) };
        Reject(() => LumenAssetRules.Validate(Asset(emitter), "shape-programmatic"), "shapeRotation");
    }

    [LumenFact]
    public static void ProgrammaticUnitToleranceAllowsRoundoffButRejectsScaledRotation()
    {
        LumenAssetRules.Validate(Asset(new EmitterSpec { Name = "roundoff", ShapeRotation = new Quaternion(0f, 0f, 0f, 1.000001f) }), "shape-programmatic");
        Reject(() => LumenAssetRules.Validate(Asset(new EmitterSpec { Name = "scaled", ShapeRotation = new Quaternion(0f, 0f, 0f, 1.00001f) }), "shape-programmatic"), "shapeRotation");
    }

    [LumenFact]
    public static void ProgrammaticRotationRejectsDegenerateAndNonfinite()
    {
        Reject(() => LumenAssetRules.Validate(Asset(new EmitterSpec { Name = "shape", ShapeRotation = new Quaternion(0f, 0f, 0f, 0f) }), "shape-programmatic"), "shapeRotation");
        Reject(() => LumenAssetRules.Validate(Asset(new EmitterSpec { Name = "shape", ShapeRotation = new Quaternion(float.NaN, 0f, 0f, 1f) }), "shape-programmatic"), "shapeRotation");
    }

    [LumenFact]
    public static void ProgrammaticPositionAndScaleRejectNonfinite()
    {
        Reject(() => LumenAssetRules.Validate(Asset(new EmitterSpec { Name = "shape", ShapePosition = new Vector3(float.NaN) }), "shape-programmatic"), "shapePosition");
        Reject(() => LumenAssetRules.Validate(Asset(new EmitterSpec { Name = "shape", ShapeScale = new Vector3(float.NegativeInfinity) }), "shape-programmatic"), "shapeScale");
    }

    private static EmitterSpec Parse(string fields)
        => LumenJsonAssetLoader.Parse("{ \"nodes\": [{ \"name\": \"root\", \"emitter\": {" + fields + "} }] }", "shape-json").Nodes[0].Emitter!;

    private static EmitterSpec ConvertXml(LumenEmitterXml emitter)
        => new LumenEffectDef
        {
            defName = "shape-xml",
            nodes = new List<LumenNodeXml> { new() { name = "root", emitter = emitter } },
        }.ToEffectAsset().Nodes[0].Emitter!;

    private static EffectAsset Asset(EmitterSpec emitter)
        => new() { EffectId = "shape-csharp", Nodes = new[] { new EffectNodeSpec { Name = "root", Emitter = emitter } } };

    private static Vector3 TransformPosition(EmitterSpec emitter, Vector3 sample)
        => Vector3.Transform(emitter.ShapeScale * sample, emitter.ShapeRotation) + emitter.ShapePosition;

    private static void Near(Vector3 expected, Vector3 actual)
    {
        if (!float.IsFinite(actual.LengthSquared()) || Vector3.Distance(expected, actual) > 1e-5f)
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static void Reject(Action action, string field)
    {
        try { action(); }
        catch (InvalidDataException error)
        {
            if (!error.Message.Contains(field, StringComparison.Ordinal))
                throw new InvalidOperationException($"Expected error naming {field}, got: {error.Message}");
            return;
        }
        throw new InvalidOperationException($"Expected InvalidDataException for {field}.");
    }
}
