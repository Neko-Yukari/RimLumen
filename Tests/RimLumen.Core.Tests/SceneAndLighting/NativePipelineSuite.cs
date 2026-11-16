using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Rendering;
using RimLumen.Core.Scene;
using UColor = UnityEngine.Color;
using UVector3 = UnityEngine.Vector3;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class NativePipelineSuite
{
    [LumenFact]
    public static void JsonConciseDocumentUsesContractDefaults()
    {
        var asset = LumenJsonAssetLoader.Parse(ConciseJson, "Spells/Fireball");
        NativeTestData.Check(asset.EffectId == "Spells/Fireball", $"Fallback effect id, got '{asset.EffectId}'");
        NativeTestData.Close(2f, asset.DefaultDuration, "defaultDuration");
        NativeTestData.Check(asset.Nodes.Length == 1, "One node");
        EffectNodeSpec node = asset.Nodes[0];
        NativeTestData.Check(node.Name == "spark" && node.ParentIndex == -1, "Node identity");
        NativeTestData.Check(node.SpacePlane == LumenSpacePlane.GroundXZ, "Default space plane");
        EmitterSpec emitter = node.Emitter ?? throw new InvalidOperationException("Emitter missing");
        NativeTestData.Check(emitter.Name == "spark", "Emitter name falls back to the node name");
        NativeTestData.Check(emitter.MaxParticles == 1024 && emitter.Duration == 1f && !emitter.Looping, "Particle budget/duration/looping defaults");
        NativeTestData.Check(emitter.WorldSpaceSimulation, "World-space simulation default");
        NativeTestData.Close(8f, emitter.RateOverTime, "rateOverTime");
        NativeTestData.Check(emitter.Shape == LumenShapeKind.Point, "Shape default");
        NativeTestData.Check(emitter.PreferredTier == LumenParticleTier.L2_GpuCompute, "Tier default");
        NativeTestData.Check(!emitter.KillBelowFloor, "killBelowFloor defaults to false");
        NativeTestData.Check(emitter.TextureKey is null && emitter.FlipbookTilesX == 1 && emitter.FlipbookTilesY == 1, "Texture/flipbook defaults");
        NativeTestData.Check(emitter.Bursts.Length == 0 && emitter.ForceFields.Length == 0, "No bursts or force fields by default");
        NativeTestData.Close(1f, CurveEvaluator.Evaluate(emitter.SizeOverLifetime, 0.37f), "Default size curve");
        NativeTestData.Close(1f, GradientEvaluator.Evaluate(emitter.ColorOverLifetime, 0.37f).X, "Default colour gradient");
        NativeTestData.Check(asset.TimelineEvents.Length == 0, "No timeline events by default");
    }

    [LumenFact]
    public static void RemovedSpacePlaneNamesFailAtTheParser()
    {
        // The removed values 1 and 2, and a name outside the two remaining planes.
        foreach (string plane in new[] { "1", "2", "\"Upright\"" })
        {
            try
            {
                LumenJsonAssetLoader.Parse($$"""{ "nodes": [ { "name": "n", "spacePlane": {{plane}} } ] }""", "removed.plane");
            }
            catch (InvalidDataException error)
            {
                NativeTestData.Check(error.Message.Contains("spacePlane"), $"error names the field: {error.Message}");
                NativeTestData.Check(error.Message.Contains("(known: GroundXZ, Vertical3D)"),
                    $"error lists the remaining planes: {error.Message}");
                continue;
            }
            throw new InvalidOperationException($"spacePlane {plane} must be rejected");
        }
    }

    [LumenFact]
    public static void JsonFullDocumentMapsEveryField()
    {
        var asset = LumenJsonAssetLoader.Parse(FullJson);
        NativeTestData.Check(asset.EffectId == "spells/fireball.full", "Explicit effect id");
        NativeTestData.Close(3.5f, asset.DefaultDuration, "defaultDuration");
        NativeTestData.Check(asset.Nodes.Length == 2, "Two nodes");
        EffectNodeSpec node = asset.Nodes[0];
        NativeTestData.Check(node.Name == "core" && node.ParentIndex == -1, "Node identity");
        NativeTestData.Check(node.SpacePlane == LumenSpacePlane.Vertical3D, "spacePlane name decoding");
        NativeTestData.Close(1f, node.LocalPosition.X, "localPosition.x");
        NativeTestData.Close(3f, node.LocalPosition.Z, "localPosition.z");
        NativeTestData.Close(0.5f, node.LocalScale.X, "localScale.x");
        NativeTestData.Close(2f, node.LocalScale.Z, "localScale.z");
        Vector3 rotated = Vector3.Transform(Vector3.UnitX, node.LocalRotation);
        NativeTestData.Check(rotated.Length() > 0.9f && rotated.Z < -0.9f, "localYawDegrees rotates about +Y (x -> -z)");
        NativeTestData.Check(asset.Nodes[1].ParentIndex == 0, "Child parent index");

        EmitterSpec emitter = node.Emitter ?? throw new InvalidOperationException("Emitter missing");
        NativeTestData.Check(emitter.Name == "flame" && emitter.MaxParticles == 128, "Emitter name and budget");
        NativeTestData.Close(1.5f, emitter.Duration, "duration");
        NativeTestData.Check(emitter.Looping && !emitter.WorldSpaceSimulation, "looping / worldSpaceSimulation");
        NativeTestData.Close(12f, emitter.RateOverTime, "rateOverTime");
        NativeTestData.Check(emitter.Bursts.Length == 2, "Two bursts");
        NativeTestData.Close(0.25f, emitter.Bursts[0].Time, "Object burst time");
        NativeTestData.Check(emitter.Bursts[0].Count == 3 && emitter.Bursts[1].Count == 4, "Burst counts");
        NativeTestData.Check(emitter.Shape == LumenShapeKind.Cone, "Integer enum decoding (3 = Cone)");
        NativeTestData.Close(2f, emitter.ShapeExtents.Y, "shapeExtents object form");
        NativeTestData.Close(40f, emitter.ConeAngleDegrees, "coneAngleDegrees");
        NativeTestData.Close(2.5f, emitter.StartLifetimeMin, "startLifetime shorthand min");
        NativeTestData.Close(2.5f, emitter.StartLifetimeMax, "startLifetime shorthand max");
        NativeTestData.Close(4f, emitter.StartSpeedMax, "startSpeed shorthand");
        NativeTestData.Close(0.5f, emitter.StartSizeMin, "startSize shorthand");
        NativeTestData.Check(emitter.StartColorLinear == new Vector4(1f, 0.5f, 0.25f, 0.75f), "startColorLinear RGBA");
        NativeTestData.Close(0.75f, CurveEvaluator.Evaluate(emitter.SizeOverLifetime, 0.5f), "sizeOverLifetime keys");
        NativeTestData.Close(0.5f, GradientEvaluator.Evaluate(emitter.ColorOverLifetime, 0.5f).Y, "colorOverLifetime stops");
        NativeTestData.Check(emitter.ForceFields.Length == 1, "One force field");
        ForceFieldSpec field = emitter.ForceFields[0];
        NativeTestData.Check(field.Kind == ForceFieldKind.Vortex, "Force field kind");
        NativeTestData.Close(3f, field.Radius, "Force field radius");
        NativeTestData.Close(2f, field.Strength, "Force field strength");
        NativeTestData.Close(0.5f, field.Frequency, "Force field frequency");
        NativeTestData.Check(emitter.TextureKey == "Effects/Flame", "textureKey");
        NativeTestData.Check(emitter.FlipbookTilesX == 2 && emitter.FlipbookTilesY == 2, "Flipbook grid");
        NativeTestData.Close(3f, emitter.FlipbookCycles, "flipbookCycles");
        NativeTestData.Check(emitter.BlendMode == LumenBlendMode.AlphaBlend, "blendMode");
        NativeTestData.Check(emitter.BillboardMode == LumenBillboardMode.Horizontal, "billboardMode");
        NativeTestData.Close(1.5f, emitter.VelocityStretchFactor, "velocityStretchFactor");
        NativeTestData.Close(2f, emitter.LengthScale, "lengthScale");
        NativeTestData.Close(0.25f, emitter.SoftParticleFadeDistance, "softParticleFadeDistance");
        NativeTestData.Close(3f, emitter.HdrIntensity, "hdrIntensity");
        NativeTestData.Close(0.5f, emitter.BloomWeight, "bloomWeight");
        NativeTestData.Check(emitter.PreferredTier == LumenParticleTier.L1_NativeCpu, "preferredTier");
        NativeTestData.Check(emitter.KillBelowFloor, "killBelowFloor");

        PointLightSpec light = node.PointLight ?? throw new InvalidOperationException("Point light missing");
        NativeTestData.Close(6f, light.GlowRadius, "glowRadius");
        NativeTestData.Close(1.5f, light.OverlightRadius, "overlightRadius");
        NativeTestData.Check(light.ColorRgb == new Vector3(1f, 0.5f, 0.25f), "colorRgb");
        NativeTestData.Check(light.IntensityOverTime is not null, "Intensity rides on the curve multiplier");
        NativeTestData.Close(2f, CurveEvaluator.Evaluate(light.IntensityOverTime!, 0.1f), "intensity");

        NativeTestData.Check(asset.TimelineEvents.Length == 1, "One timeline event");
        NativeTestData.Check(asset.TimelineEvents[0].EventName == "burst", "Timeline event name");
        NativeTestData.Close(0.4f, asset.TimelineEvents[0].TimeSeconds, "Timeline event time");
        NativeTestData.Check(asset.TimelineEvents[0].TargetNodeIndex == 0, "Timeline event target");
    }

    [LumenFact]
    public static void JsonRejectsInvalidOrUnknownInput()
    {
        NativeTestData.Throws<InvalidDataException>(() => LumenJsonAssetLoader.Parse("{ not json"), "malformed JSON");
        NativeTestData.Throws<InvalidDataException>(() => LumenJsonAssetLoader.Parse("""{ "nodes": [] }""", "x"), "empty nodes");
        NativeTestData.Throws<InvalidDataException>(() => LumenJsonAssetLoader.Parse("""{ "nodes": [ { "name": "n" } ] }"""), "missing effect id");
        NativeTestData.Throws<InvalidDataException>(
            () => LumenJsonAssetLoader.Parse("""{ "nodes": [ { "name": "n", "emitter": { "maxParticles": 0 } } ] }""", "x"),
            "non-positive maxParticles");
        NativeTestData.Throws<InvalidDataException>(
            () => LumenJsonAssetLoader.Parse("""{ "nodes": [ { "name": "n", "emitter": { "startLifetimeMin": 2, "startLifetimeMax": 1 } } ] }""", "x"),
            "inverted lifetime range");
        NativeTestData.Throws<InvalidDataException>(
            () => LumenJsonAssetLoader.Parse("""{ "nodes": [ { "name": "n", "emitter": { "startLifetimeMin": 0, "startLifetimeMax": 1 } } ] }""", "x"),
            "non-positive lifetime");
        NativeTestData.Throws<InvalidDataException>(
            () => LumenJsonAssetLoader.Parse("""{ "nodes": [ { "name": "n", "emitter": { "flipbookTilesX": 0 } } ] }""", "x"),
            "flipbook grid below 1");
        NativeTestData.Throws<InvalidDataException>(
            () => LumenJsonAssetLoader.Parse("""{ "nodes": [ { "name": "n", "parentIndex": 0 } ] }""", "x"),
            "self parent");
        NativeTestData.Throws<InvalidDataException>(
            () => LumenJsonAssetLoader.Parse("""{ "nodes": [ { "name": "a" }, { "name": "b", "parentIndex": 5 } ] }""", "x"),
            "forward parent");
        NativeTestData.Throws<InvalidDataException>(
            () => LumenJsonAssetLoader.Parse("""{ "nodes": [ { "name": "n", "emitterr": { } } ] }""", "x"),
            "typo guard");
        NativeTestData.Throws<InvalidDataException>(
            () => LumenJsonAssetLoader.Parse("""{ "nodes": [ { "name": "n", "spacePlane": "HeightLayer" } ] }""", "x"),
            "unknown enum member");
        NativeTestData.Throws<InvalidDataException>(
            () => LumenJsonAssetLoader.Parse("""{ "nodes": [ { "name": "n" } ], "timelineEvents": [ { "time": 1, "eventName": "x", "targetNodeIndex": 3 } ] }""", "x"),
            "timeline target out of range");
    }

    [LumenFact]
    public static void LoadFileDerivesIdFromDataRimLumenPath()
    {
        string root = NativeTestData.NewTempDirectory();
        try
        {
            string dataDirectory = Path.Combine(root, "1.7", "Data", "RimLumen");
            string nested = Path.Combine(dataDirectory, "Spells");
            Directory.CreateDirectory(nested);
            string file = Path.Combine(nested, "Fireball.lumen.json");
            File.WriteAllText(file, """{ "nodes": [ { "name": "spark" } ] }""");
            NativeTestData.Check(LumenJsonAssetLoader.LoadFile(file).EffectId == "Spells/Fireball", "Relative id under Data/RimLumen");
            NativeTestData.Check(LumenJsonAssetLoader.LoadFile(file, "pkg.mod").EffectId == "Spells/Fireball", "Package id does not change the id");

            string top = Path.Combine(dataDirectory, "Simple.json");
            File.WriteAllText(top, """{ "nodes": [ { "name": "spark" } ] }""");
            NativeTestData.Check(LumenJsonAssetLoader.LoadFile(top).EffectId == "Simple", "Top-level file keeps its stem");

            string explicitId = Path.Combine(dataDirectory, "Renamed.lumen.json");
            File.WriteAllText(explicitId, """{ "effectId": "custom.id", "nodes": [ { "name": "spark" } ] }""");
            NativeTestData.Check(LumenJsonAssetLoader.LoadFile(explicitId).EffectId == "custom.id", "Explicit effectId wins");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LumenFact]
    public static void ScanDirectoryRegistersCanonicalShortAndRelativeIds()
    {
        string root = NativeTestData.NewTempDirectory();
        try
        {
            string dataDirectory = Path.Combine(root, "Data", "RimLumen");
            Directory.CreateDirectory(Path.Combine(dataDirectory, "Spells"));
            File.WriteAllText(Path.Combine(dataDirectory, "Spells", "Fireball.lumen.json"),
                """{ "nodes": [ { "name": "spark", "emitter": { "rateOverTime": 8 } } ] }""");
            File.WriteAllText(Path.Combine(dataDirectory, "Simple.json"),
                """{ "effectId": "simple.effect", "nodes": [ { "name": "spark" } ] }""");

            var catalog = new InMemoryAssetCatalog();
            int registered = LumenModAssetScanner.ScanDirectory(catalog, "nekoyukari.rimlumen", dataDirectory);
            NativeTestData.Check(registered == 2, $"Two files registered, got {registered}");
            NativeTestData.CheckResolves(catalog, "Spells/Fireball");
            NativeTestData.CheckResolves(catalog, "nekoyukari.rimlumen:Spells/Fireball");
            NativeTestData.CheckResolves(catalog, "simple.effect");
            NativeTestData.CheckResolves(catalog, "nekoyukari.rimlumen:simple.effect");
            NativeTestData.CheckResolves(catalog, "Simple");
            NativeTestData.CheckResolves(catalog, "nekoyukari.rimlumen:Simple");
            NativeTestData.Check(!catalog.TryResolveEffect("missing.effect", out _), "Unknown ids stay unresolved");

            NativeTestData.Check(LumenModAssetScanner.ScanDirectory(catalog, "nekoyukari.rimlumen", Path.Combine(root, "absent")) == 0,
                "A missing directory registers nothing");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LumenFact]
    public static void ScanDirectoryBareIdIsFirstWriterWinsAcrossMods()
    {
        string root = NativeTestData.NewTempDirectory();
        try
        {
            string dataA = Path.Combine(root, "A", "Data", "RimLumen");
            string dataB = Path.Combine(root, "B", "Data", "RimLumen");
            Directory.CreateDirectory(dataA);
            Directory.CreateDirectory(dataB);
            File.WriteAllText(Path.Combine(dataA, "Fireball.lumen.json"), """{ "nodes": [ { "name": "from_a" } ] }""");
            File.WriteAllText(Path.Combine(dataB, "Fireball.lumen.json"), """{ "nodes": [ { "name": "from_b" } ] }""");

            var catalog = new InMemoryAssetCatalog();
            LumenModAssetScanner.ScanDirectory(catalog, "mod.a", dataA);
            LumenModAssetScanner.ScanDirectory(catalog, "mod.b", dataB);

            NativeTestData.Check(catalog.TryResolveEffect("Fireball", out EffectAsset? bare) && bare!.Nodes[0].Name == "from_a",
                "Bare 'Fireball' stays with the first mod");
            NativeTestData.Check(catalog.TryResolveEffect("mod.a:Fireball", out EffectAsset? qualifiedA) && qualifiedA!.Nodes[0].Name == "from_a",
                "mod.a:Fireball resolves to mod A's asset");
            NativeTestData.Check(catalog.TryResolveEffect("mod.b:Fireball", out EffectAsset? qualifiedB) && qualifiedB!.Nodes[0].Name == "from_b",
                "mod.b:Fireball resolves to mod B's asset");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LumenFact]
    public static void ScanDirectoryPropagatesUnlessErrorHandlerGiven()
    {
        string root = NativeTestData.NewTempDirectory();
        try
        {
            string dataDirectory = Path.Combine(root, "Data", "RimLumen");
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Combine(dataDirectory, "Broken.lumen.json"), """{ "nodes": [ ] }""");
            File.WriteAllText(Path.Combine(dataDirectory, "Good.lumen.json"), """{ "nodes": [ { "name": "spark" } ] }""");

            var catalog = new InMemoryAssetCatalog();
            NativeTestData.Throws<InvalidDataException>(
                () => LumenModAssetScanner.ScanDirectory(catalog, "pkg", dataDirectory),
                "fail-loud for a malformed mod file");

            var tolerant = new InMemoryAssetCatalog();
            var failures = new List<string>();
            int registered = LumenModAssetScanner.ScanDirectory(tolerant, "pkg", dataDirectory, (file, error) =>
                failures.Add(Path.GetFileName(file) + ": " + error.Message));
            NativeTestData.Check(registered == 1, $"Healthy file still loads, got {registered}");
            NativeTestData.Check(failures.Count == 1 && failures[0].StartsWith("Broken.lumen.json:", StringComparison.Ordinal),
                "Broken file is reported once with its name");
            NativeTestData.CheckResolves(tolerant, "Good");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LumenFact]
    public static void EffectDefConvertsWithoutGraphicsDevice()
    {
        var def = new LumenEffectDef
        {
            defName = "RimLumen.XmlCheck",
            nodes = new List<LumenNodeXml>
            {
                new()
                {
                    name = "root",
                    localPosition = new UVector3(1f, 2f, 3f),
                    heightOffset = 0.5f,
                    localYawDegrees = 90f,
                    localScale = 2f,
                    emitter = new LumenEmitterXml
                    {
                        name = "flame",
                        preferredTier = LumenParticleTier.L1_NativeCpu,
                        maxParticles = 32,
                        duration = 2f,
                        looping = false,
                        rateOverTime = 6f,
                        bursts = new List<LumenBurstXml> { new() { time = 0.25f, count = 2 } },
                        startLifetimeMin = 1f,
                        startLifetimeMax = 2f,
                        startSpeedMin = 1f,
                        startSpeedMax = 3f,
                        startSizeMin = 0.5f,
                        startSizeMax = 1f,
                        startColorLinear = new UColor(1f, 0.5f, 0.25f, 1f),
                        hdrIntensity = 2f,
                        bloomWeight = 0.5f,
                        shape = LumenShapeKind.Cone,
                        shapeExtents = new UVector3(1f, 1f, 1f),
                        coneAngleDegrees = 30f,
                        billboardMode = LumenBillboardMode.StretchedVelocity,
                        velocityStretchFactor = 2f,
                        lengthScale = 1.5f,
                        blendMode = LumenBlendMode.Premultiplied,
                        softParticleFadeDistance = 0.3f,
                        textureKey = "Effects/Flame",
                        flipbookTilesX = 2,
                        flipbookTilesY = 2,
                        flipbookCycles = 2f,
                        worldSpaceSimulation = false,
                        killBelowFloor = true,
                        sizeOverLifetime = new List<LumenCurveKeyXml> { new() { time = 0f, value = 1f }, new() { time = 1f, value = 0.5f } },
                        colorOverLifetime = new List<LumenGradientStopXml>
                        {
                            new() { time = 0f, rgbaLinear = new UColor(1f, 1f, 1f, 1f) },
                            new() { time = 1f, rgbaLinear = new UColor(0f, 0f, 0f, 0f) },
                        },
                        forceFields = new List<LumenForceFieldXml>
                        {
                            new() { kind = ForceFieldKind.Vortex, centerOrAxis = new UVector3(0f, 1f, 0f), radius = 3f, strength = 2f, frequency = 0.5f },
                        },
                    },
                    pointLight = new LumenPointLightXml
                    {
                        colorLinear = new UColor(1f, 0.5f, 0.25f, 1f),
                        radiusCells = 6f,
                        overlightRadius = 1.5f,
                        intensity = 2f,
                    },
                },
                new() { name = "child", parentIndex = 0, localPosition = new UVector3(0f, 1f, 0f) },
            },
        };

        EffectAsset asset = def.ToEffectAsset("nekoyukari.rimlumen");
        NativeTestData.Check(asset.EffectId == "RimLumen.XmlCheck", "EffectId defaults to defName");
        NativeTestData.Check(asset.Nodes.Length == 2 && asset.Nodes[1].ParentIndex == 0, "Node hierarchy");
        EffectNodeSpec node = asset.Nodes[0];
        NativeTestData.Close(1f, node.LocalPosition.X, "localPosition.x");
        NativeTestData.Close(2.5f, node.LocalPosition.Y, "heightOffset adds altitude");
        NativeTestData.Close(3f, node.LocalPosition.Z, "localPosition.z");
        NativeTestData.Close(2f, node.LocalScale.Y, "localScale");
        Vector3 rotated = Vector3.Transform(Vector3.UnitX, node.LocalRotation);
        NativeTestData.Check(rotated.Z < -0.9f, "localYawDegrees rotates about +Y");

        EmitterSpec emitter = node.Emitter ?? throw new InvalidOperationException("Emitter missing");
        NativeTestData.Check(emitter.Name == "flame" && emitter.MaxParticles == 32 && !emitter.Looping, "Emitter identity");
        NativeTestData.Check(emitter.PreferredTier == LumenParticleTier.L1_NativeCpu, "preferredTier");
        NativeTestData.Close(6f, emitter.RateOverTime, "rateOverTime");
        NativeTestData.Check(emitter.Bursts.Length == 1 && emitter.Bursts[0].Count == 2, "bursts");
        NativeTestData.Check(emitter.Shape == LumenShapeKind.Cone && emitter.ConeAngleDegrees == 30f, "shape/cone angle");
        NativeTestData.Check(emitter.StartColorLinear == new Vector4(1f, 0.5f, 0.25f, 1f), "startColorLinear");
        NativeTestData.Close(0.75f, CurveEvaluator.Evaluate(emitter.SizeOverLifetime, 0.5f), "sizeOverLifetime");
        NativeTestData.Close(0.5f, GradientEvaluator.Evaluate(emitter.ColorOverLifetime, 0.5f).X, "colorOverLifetime");
        NativeTestData.Check(emitter.ForceFields.Length == 1 && emitter.ForceFields[0].Kind == ForceFieldKind.Vortex, "force field");
        NativeTestData.Check(emitter.TextureKey == "Effects/Flame" && emitter.FlipbookTilesX == 2 && emitter.FlipbookCycles == 2f, "texture/flipbook");
        NativeTestData.Check(emitter.BlendMode == LumenBlendMode.Premultiplied && emitter.BillboardMode == LumenBillboardMode.StretchedVelocity, "blend/billboard");
        NativeTestData.Check(!emitter.WorldSpaceSimulation && emitter.LengthScale == 1.5f, "local space/length scale");
        NativeTestData.Check(emitter.KillBelowFloor, "XML killBelowFloor");

        PointLightSpec light = node.PointLight ?? throw new InvalidOperationException("Point light missing");
        NativeTestData.Close(6f, light.GlowRadius, "glowRadius");
        NativeTestData.Close(1.5f, light.OverlightRadius, "overlightRadius");
        NativeTestData.Check(light.ColorRgb == new Vector3(1f, 0.5f, 0.25f), "point light colour");
        NativeTestData.Close(2f, CurveEvaluator.Evaluate(light.IntensityOverTime!, 0f), "point light intensity");

        var invalidParent = new LumenEffectDef { defName = "bad.parent", nodes = new List<LumenNodeXml> { new() { name = "root", parentIndex = 0 } } };
        NativeTestData.Throws<InvalidDataException>(() => invalidParent.ToEffectAsset(), "self parent");
        var invalidEmitter = new LumenEffectDef
        {
            defName = "bad.emitter",
            nodes = new List<LumenNodeXml> { new() { name = "root", emitter = new LumenEmitterXml { maxParticles = 0 } } },
        };
        NativeTestData.Throws<InvalidDataException>(() => invalidEmitter.ToEffectAsset(), "non-positive maxParticles");
        NativeTestData.Throws<InvalidDataException>(() => new LumenEffectDef { defName = "empty" }.ToEffectAsset(), "no nodes");
    }

    [LumenFact]
    public static void PauseFreezesScaledTimeAndRealTimeKeepsAdvancing()
    {
        var asset = new EffectAsset
        {
            EffectId = "native.pause",
            Nodes = new[]
            {
                new EffectNodeSpec
                {
                    Name = "fire",
                    Emitter = new EmitterSpec
                    {
                        Name = "flame", PreferredTier = LumenParticleTier.L1_NativeCpu, RateOverTime = 4f,
                        MaxParticles = 32, StartLifetimeMin = 10f, StartLifetimeMax = 10f,
                        StartSpeedMin = 1f, StartSpeedMax = 1f,
                    },
                },
            },
        };

        var frozenService = NativeTestData.Service(asset);
        using var frozen = frozenService.Play(new LumenPlayRequest { EffectId = asset.EffectId });
        NativeTestData.Step(frozenService, 0.5f, 0.5f);
        int frozenCount = frozenService.Simulator.LiveParticleCount;
        NativeTestData.Check(frozenCount == 2, $"Half a second at rate 4 spawns two particles, got {frozenCount}");
        float frozenZ = frozenService.Simulator.LiveParticles[0].Position.Z;
        float frozenAge = frozenService.Simulator.LiveParticles[0].Age;
        float frozenPlayback = ((EffectInstanceHandle)frozen).PlaybackTime;

        for (int frame = 0; frame < 12; frame++)
        {
            NativeTestData.Step(frozenService, 0f, 1f / 60f);
        }

        NativeTestData.Check(frozenService.Simulator.LiveParticleCount == frozenCount, "No spawns while paused");
        NativeTestData.Check(frozenService.Simulator.LiveParticles[0].Position.Z == frozenZ, "Scaled-time particle did not move");
        NativeTestData.Check(frozenService.Simulator.LiveParticles[0].Age == frozenAge, "Scaled-time particle did not age");
        NativeTestData.Check(((EffectInstanceHandle)frozen).PlaybackTime == frozenPlayback, "Playback time frozen");
        NativeTestData.Check(frozen.IsAlive, "Handle stays alive while paused");
        NativeTestData.Check(frozenService.Simulator.Vertices.Length == frozenCount * 4, "Paused particles still emit vertices");

        var realTimeService = NativeTestData.Service(asset);
        using var realTime = realTimeService.Play(new LumenPlayRequest { EffectId = asset.EffectId, RealTime = true });
        NativeTestData.Step(realTimeService, 0.5f, 0.5f);
        NativeTestData.Check(realTimeService.Simulator.LiveParticleCount == frozenCount, "Same spawn at the same start");
        float startZ = realTimeService.Simulator.LiveParticles[0].Position.Z;
        for (int frame = 0; frame < 6; frame++)
        {
            NativeTestData.Step(realTimeService, 0f, 1f / 60f);
        }

        NativeTestData.Close(startZ + 0.1f, realTimeService.Simulator.LiveParticles[0].Position.Z, "Real-time particle moved on unscaled time");
        NativeTestData.Close(frozenPlayback + 0.1f, ((EffectInstanceHandle)realTime).PlaybackTime, "Real-time playback advanced");
        NativeTestData.Check(realTimeService.Simulator.LiveParticleCount == frozenCount, "0.1s at rate 4 adds no particle");
    }

    [LumenFact]
    public static void RealTimeIgnoresScaledTimeScale()
    {
        float ScaledDistance(bool realTime, float deltaTime, float unscaledDeltaTime)
        {
            var asset = new EffectAsset
            {
                EffectId = "native.timescale",
                Nodes = new[]
                {
                    new EffectNodeSpec
                    {
                        Name = "shot",
                        Emitter = new EmitterSpec
                        {
                            Name = "streak", PreferredTier = LumenParticleTier.L1_NativeCpu, MaxParticles = 4,
                            Bursts = new[] { (0f, 1) }, StartLifetimeMin = 10f, StartLifetimeMax = 10f,
                            StartSpeedMin = 1f, StartSpeedMax = 1f,
                        },
                    },
                },
            };
            var service = NativeTestData.Service(asset);
            using var handle = service.Play(new LumenPlayRequest { EffectId = asset.EffectId, RealTime = realTime });
            NativeTestData.Step(service, deltaTime, unscaledDeltaTime);
            NativeTestData.Step(service, deltaTime, unscaledDeltaTime);
            NativeTestData.Check(service.Simulator.LiveParticleCount == 1, "Burst spawned exactly one particle");
            return service.Simulator.LiveParticles[0].Position.Z;
        }

        float normal = ScaledDistance(realTime: false, deltaTime: 0.1f, unscaledDeltaTime: 0f);
        float doubleSpeed = ScaledDistance(realTime: false, deltaTime: 0.2f, unscaledDeltaTime: 0f);
        float realTimeAtDoubleSpeed = ScaledDistance(realTime: true, deltaTime: 0.2f, unscaledDeltaTime: 0.1f);
        NativeTestData.Close(0.1f, normal, "1x game speed");
        NativeTestData.Close(0.2f, doubleSpeed, "2x game speed");
        NativeTestData.Close(normal, realTimeAtDoubleSpeed, "Real-time effect ignores the 2x game speed");
    }

    [LumenFact]
    public static void SubBatchesGroupByTextureAndBlendAndCoverEveryIndex()
    {
        var simulator = new CpuParticleSimulator();
        int alpha = simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "alpha", MaxParticles = 32, TextureKey = "tex/a", BlendMode = LumenBlendMode.Additive,
            StartLifetimeMin = 10f, StartLifetimeMax = 10f,
        }, Matrix4x4.Identity, emitting: false);
        int beta = simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "beta", MaxParticles = 32, TextureKey = "tex/b", BlendMode = LumenBlendMode.AlphaBlend,
            StartLifetimeMin = 10f, StartLifetimeMax = 10f,
        }, Matrix4x4.Identity, emitting: false);
        int gamma = simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "gamma", MaxParticles = 32, TextureKey = "tex/a", BlendMode = LumenBlendMode.Additive,
            StartLifetimeMin = 10f, StartLifetimeMax = 10f,
        }, Matrix4x4.Identity, emitting: false);
        simulator.EmitBurst(alpha, 2);
        simulator.EmitBurst(beta, 3);
        simulator.EmitBurst(gamma, 1);
        NativeTestData.Step(simulator, 0f, 0f);

        int particles = simulator.LiveParticleCount;
        NativeTestData.Check(particles == 6, $"Six live particles, got {particles}");
        NativeTestData.Check(simulator.Vertices.Length == particles * 4, "Four vertices per particle");
        NativeTestData.Check(simulator.Indices.Length == particles * 6, "Six indices per particle");
        IReadOnlyList<L1SubBatch> subBatches = simulator.SubBatches;
        NativeTestData.Check(subBatches.Count == 2, $"Two (texture, blend) groups, got {subBatches.Count}");
        NativeTestData.Check(subBatches[0].TextureKey == "tex/a" && subBatches[0].BlendMode == LumenBlendMode.Additive, "First group is tex/a additive");
        NativeTestData.Check(subBatches[1].TextureKey == "tex/b" && subBatches[1].BlendMode == LumenBlendMode.AlphaBlend, "Second group is tex/b alpha");

        int offset = 0;
        foreach (L1SubBatch subBatch in subBatches)
        {
            NativeTestData.Check(subBatch.IndexStart == offset, $"Sub-batch starts at {offset}, got {subBatch.IndexStart}");
            NativeTestData.Check(subBatch.IndexCount > 0 && subBatch.IndexCount % 6 == 0, "Whole quads only");
            offset += subBatch.IndexCount;
        }

        NativeTestData.Check(offset == particles * 6, $"Sub-batches cover every index: {offset} vs {particles * 6}");
        NativeTestData.Check(subBatches[0].IndexCount == 18, $"tex/a group holds three particles, got {subBatches[0].IndexCount / 6}");
        NativeTestData.Check(subBatches[1].IndexCount == 18, $"tex/b group holds three particles, got {subBatches[1].IndexCount / 6}");

        for (int i = 0; i < simulator.Indices.Length; i++)
        {
            NativeTestData.Check(simulator.Indices[i] < simulator.Vertices.Length, "Index stays inside the vertex buffer");
        }

        int firstGroupMaxQuad = 0;
        for (int i = subBatches[0].IndexStart; i < subBatches[0].IndexStart + subBatches[0].IndexCount; i++)
        {
            firstGroupMaxQuad = Math.Max(firstGroupMaxQuad, (int)(simulator.Indices[i] / 4));
        }

        NativeTestData.Check(firstGroupMaxQuad == 2, $"First group owns quads 0..2, highest {firstGroupMaxQuad}");
        int secondGroupMinQuad = int.MaxValue;
        for (int i = subBatches[1].IndexStart; i < subBatches[1].IndexStart + subBatches[1].IndexCount; i++)
        {
            secondGroupMinQuad = Math.Min(secondGroupMinQuad, (int)(simulator.Indices[i] / 4));
        }

        NativeTestData.Check(secondGroupMinQuad == 3, $"Second group starts at quad 3, got {secondGroupMinQuad}");

        simulator.ReleaseEmitter(alpha, immediate: true);
        simulator.ReleaseEmitter(beta, immediate: true);
        simulator.ReleaseEmitter(gamma, immediate: true);
        NativeTestData.Check(simulator.SubBatches.Count == 0 && simulator.Indices.Length == 0, "No particles means no sub-batches");
    }

    [LumenFact]
    public static void SubBatchesSplitOnSoftParticleFadeDistance()
    {
        var simulator = new CpuParticleSimulator();
        int sharp = simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "sharp", MaxParticles = 8, TextureKey = "tex/fade", SoftParticleFadeDistance = 0f,
            StartLifetimeMin = 10f, StartLifetimeMax = 10f,
        }, Matrix4x4.Identity, emitting: false);
        int soft = simulator.AcquireEmitter(new EmitterSpec
        {
            Name = "soft", MaxParticles = 8, TextureKey = "tex/fade", SoftParticleFadeDistance = 0.4f,
            StartLifetimeMin = 10f, StartLifetimeMax = 10f,
        }, Matrix4x4.Identity, emitting: false);
        simulator.EmitBurst(sharp, 1);
        simulator.EmitBurst(soft, 1);
        NativeTestData.Step(simulator, 0f, 0f);
        IReadOnlyList<L1SubBatch> subBatches = simulator.SubBatches;
        NativeTestData.Check(subBatches.Count == 2, $"Different soft fade distances need separate batches, got {subBatches.Count}");
        NativeTestData.Check(subBatches[0].SoftParticleFadeDistance == 0f && subBatches[1].SoftParticleFadeDistance == 0.4f,
            "Each sub-batch carries its own soft fade distance");
    }

    [LumenFact]
    public static void DensityThinsBurstsAndRates()
    {
        float original = RimLumenRenderHost.ParticleDensityScale;
        try
        {
            RimLumenRenderHost.ParticleDensityScale = 0.5f;
            var simulator = new CpuParticleSimulator();
            int burst = simulator.AcquireEmitter(new EmitterSpec
            {
                Name = "burst", MaxParticles = 64, StartLifetimeMin = 10f, StartLifetimeMax = 10f,
            }, Matrix4x4.Identity, emitting: false);
            simulator.EmitBurst(burst, 10);
            NativeTestData.Step(simulator, 0f, 0f);
            NativeTestData.Check(simulator.LiveParticleCount == 5, $"Half density turns a 10 burst into 5, got {simulator.LiveParticleCount}");

            int single = simulator.AcquireEmitter(new EmitterSpec
            {
                Name = "single", MaxParticles = 8, StartLifetimeMin = 10f, StartLifetimeMax = 10f,
            }, Matrix4x4.Identity, emitting: false);
            simulator.EmitBurst(single, 1);
            NativeTestData.Step(simulator, 0f, 0f);
            NativeTestData.Check(simulator.GetLiveParticleCount(single) == 1, "A single-particle burst never disappears");

            var rateSimulator = new CpuParticleSimulator();
            rateSimulator.AcquireEmitter(new EmitterSpec
            {
                Name = "rate", MaxParticles = 32, RateOverTime = 4f, Looping = true, Duration = 1f,
                StartLifetimeMin = 10f, StartLifetimeMax = 10f,
            }, Matrix4x4.Identity);
            NativeTestData.Step(rateSimulator, 1f, 1f);
            NativeTestData.Check(rateSimulator.LiveParticleCount == 2, $"Half density halves a 4/s rate, got {rateSimulator.LiveParticleCount}");
        }
        finally
        {
            RimLumenRenderHost.ParticleDensityScale = original;
        }
    }

    [LumenFact]
    public static void XmlAndJsonShareTheSameEmitterDefaults()
    {
        var def = new LumenEffectDef
        {
            defName = "RimLumen.Defaults",
            nodes = new List<LumenNodeXml> { new() { name = "node", emitter = new LumenEmitterXml() } },
        };
        EffectAsset fromXml = def.ToEffectAsset();
        EffectAsset fromJson = LumenJsonAssetLoader.Parse("""{ "nodes": [ { "name": "node", "emitter": { } } ] }""", "RimLumen.Defaults");
        EmitterSpec xml = fromXml.Nodes[0].Emitter ?? throw new InvalidOperationException("XML emitter missing");
        EmitterSpec json = fromJson.Nodes[0].Emitter ?? throw new InvalidOperationException("JSON emitter missing");

        NativeTestData.Check(xml.PreferredTier == json.PreferredTier, $"preferredTier default: XML {xml.PreferredTier} vs JSON {json.PreferredTier}");
        NativeTestData.Check(xml.MaxParticles == json.MaxParticles, $"maxParticles default: XML {xml.MaxParticles} vs JSON {json.MaxParticles}");
        NativeTestData.Close(json.Duration, xml.Duration, "duration default");
        NativeTestData.Check(xml.Looping == json.Looping, "looping default");
        NativeTestData.Close(json.RateOverTime, xml.RateOverTime, "rateOverTime default");
        NativeTestData.Close(json.StartLifetimeMin, xml.StartLifetimeMin, "startLifetimeMin default");
        NativeTestData.Close(json.StartLifetimeMax, xml.StartLifetimeMax, "startLifetimeMax default");
        NativeTestData.Close(json.StartSpeedMin, xml.StartSpeedMin, "startSpeedMin default");
        NativeTestData.Close(json.StartSpeedMax, xml.StartSpeedMax, "startSpeedMax default");
        NativeTestData.Close(json.StartSizeMin, xml.StartSizeMin, "startSizeMin default");
        NativeTestData.Close(json.StartSizeMax, xml.StartSizeMax, "startSizeMax default");
        NativeTestData.Check(xml.StartColorLinear == json.StartColorLinear, "startColorLinear default");
        NativeTestData.Close(json.HdrIntensity, xml.HdrIntensity, "hdrIntensity default");
        NativeTestData.Close(json.BloomWeight, xml.BloomWeight, "bloomWeight default");
        NativeTestData.Check(xml.Shape == json.Shape, "shape default");
        NativeTestData.Close(json.ShapeExtents.X, xml.ShapeExtents.X, "shapeExtents default");
        NativeTestData.Close(json.ConeAngleDegrees, xml.ConeAngleDegrees, "coneAngleDegrees default");
        NativeTestData.Check(xml.BillboardMode == json.BillboardMode, "billboardMode default");
        NativeTestData.Close(json.VelocityStretchFactor, xml.VelocityStretchFactor, "velocityStretchFactor default");
        NativeTestData.Close(json.LengthScale, xml.LengthScale, "lengthScale default");
        NativeTestData.Check(xml.BlendMode == json.BlendMode, "blendMode default");
        NativeTestData.Close(json.SoftParticleFadeDistance, xml.SoftParticleFadeDistance, "softParticleFadeDistance default");
        NativeTestData.Check(xml.FlipbookTilesX == json.FlipbookTilesX && xml.FlipbookTilesY == json.FlipbookTilesY, "flipbook grid default");
        NativeTestData.Close(json.FlipbookCycles, xml.FlipbookCycles, "flipbookCycles default");
        NativeTestData.Check(xml.WorldSpaceSimulation == json.WorldSpaceSimulation, "worldSpaceSimulation default");
        NativeTestData.Check(xml.KillBelowFloor == json.KillBelowFloor, "killBelowFloor default");
        NativeTestData.Close(fromJson.DefaultDuration, fromXml.DefaultDuration, "defaultDuration");
    }

    private const string ConciseJson = """
    {
      // hand-authored .lumen.json: only the non-default fields
      "nodes": [
        { "name": "spark", "emitter": { "rateOverTime": 8 } }
      ]
    }
    """;

    private const string FullJson = """
    {
      "effectId": "spells/fireball.full",
      "defaultDuration": 3.5,
      "nodes": [
        {
          "name": "core",
          "parentIndex": -1,
          "spacePlane": "Vertical3D",
          "localPosition": [1, 2, 3],
          "localYawDegrees": 90,
          "localScale": [0.5, 0.75, 2],
          "emitter": {
            "name": "flame",
            "maxParticles": 128,
            "duration": 1.5,
            "looping": true,
            "worldSpaceSimulation": false,
            "rateOverTime": 12,
            "bursts": [ { "time": 0.25, "count": 3 }, [0.5, 4] ],
            "shape": 3,
            "shapeExtents": { "x": 1, "y": 2, "z": 3 },
            "coneAngleDegrees": 40,
            "startLifetime": 2.5,
            "startSpeed": 4,
            "startSize": 0.5,
            "startColorLinear": [1, 0.5, 0.25, 0.75],
            "sizeOverLifetime": [[0, 1], [1, 0.5]],
            "colorOverLifetime": [[0, [1, 1, 1, 1]], [1, [0, 0, 0, 0]]],
            "forceFields": [ { "kind": "Vortex", "centerOrAxis": [0, 1, 0], "radius": 3, "strength": 2, "frequency": 0.5 } ],
            "textureKey": "Effects/Flame",
            "flipbookTilesX": 2,
            "flipbookTilesY": 2,
            "flipbookCycles": 3,
            "blendMode": "AlphaBlend",
            "billboardMode": "Horizontal",
            "velocityStretchFactor": 1.5,
            "lengthScale": 2,
            "softParticleFadeDistance": 0.25,
            "hdrIntensity": 3,
            "bloomWeight": 0.5,
            "preferredTier": "L1_NativeCpu",
            "killBelowFloor": true
          },
          "pointLight": { "glowRadius": 6, "overlightRadius": 1.5, "colorRgb": [1, 0.5, 0.25], "intensity": 2 }
        },
        { "name": "child", "parentIndex": 0, "localPosition": [0, 1, 0] }
      ],
      "timelineEvents": [ { "time": 0.4, "eventName": "burst", "targetNodeIndex": 0 } ]
    }
    """;
}

internal static class NativeTestData
{
    public static LumenVfxService Service(EffectAsset asset)
    {
        var catalog = new InMemoryAssetCatalog();
        catalog.Register(asset);
        LumenHostRegistry.RegisterAssetProvider(catalog);
        return new LumenVfxService(new CpuParticleSimulator())
        {
            GpuEngineProvider = static () => null,
            GlowerDriverProvider = static _ => null,
        };
    }

    public static void Step(LumenVfxService service, float deltaTime, float unscaledDeltaTime)
    {
        var context = new LumenCaptureContext
        {
            FrameIndex = 1, DeltaTime = deltaTime, UnscaledDeltaTime = unscaledDeltaTime,
            ViewportWidth = 640, ViewportHeight = 480,
            ViewMatrix = Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity, LumenRenderPassId = 0,
        };
        service.CaptureMainThread(context);
        service.Simulator.CaptureMainThread(context);
    }

    public static void Step(CpuParticleSimulator simulator, float deltaTime, float unscaledDeltaTime)
        => simulator.CaptureMainThread(new LumenCaptureContext
        {
            FrameIndex = 1, DeltaTime = deltaTime, UnscaledDeltaTime = unscaledDeltaTime,
            ViewportWidth = 640, ViewportHeight = 480,
            ViewMatrix = Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity, LumenRenderPassId = 0,
        });

    public static string NewTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "rimlumen-native-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void CheckResolves(InMemoryAssetCatalog catalog, string effectId)
    {
        if (!catalog.TryResolveEffect(effectId, out EffectAsset? asset) || asset is null)
        {
            throw new InvalidOperationException($"Expected '{effectId}' to resolve.");
        }
    }

    public static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Close(float expected, float actual, string what)
    {
        if (!(MathF.Abs(expected - actual) <= 1e-4f * MathF.Max(1f, MathF.Abs(expected))))
        {
            throw new InvalidOperationException($"{what}: expected {expected}, got {actual}.");
        }
    }

    public static void Throws<T>(Action action, string what) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"{what}: expected {typeof(T).Name}.");
    }
}
