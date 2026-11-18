using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Xml;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.ParticlesGpu;
using RimLumen.Core.Ribbons;
using RimLumen.Core.Scene;

namespace RimLumen.Core.Tests.SceneAndLighting;

// LumenDepthMask through the public Play flow: the asset boundary, the play request, and the per-vertex / per-emitter
// values L1, ribbons and L2 hand to their shaders. The north/south discard itself runs only on the GPU.
[LumenTestSuite]
public static class DepthMaskSuite
{
    private static readonly Matrix4x4 TopDown = new(
        1f, 0f, 0f, 0f,
        0f, 0f, 1f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 0f, 1f);

    // MoteOverhead minus Projectile: the eye-distance margin a north fragment needs above the scene.
    private static float Lift => Verse.Altitudes.AltitudeFor(Verse.AltitudeLayer.MoteOverhead)
        - Verse.Altitudes.AltitudeFor(Verse.AltitudeLayer.Projectile);

    [LumenFact]
    public static void JsonAndXmlParseTheNodeDepthMask()
    {
        EffectAsset json = LumenJsonAssetLoader.Parse(
            """{ "nodes": [{ "name": "slash", "depthMask": "NorthBehindReference", "emitter": {} }] }""", "mask-json");
        Check(json.Nodes[0].DepthMask == LumenDepthMask.NorthBehindReference, $"JSON depthMask: {json.Nodes[0].DepthMask}");
        var document = new XmlDocument();
        document.LoadXml("<li><depthMask>NorthBehindReference</depthMask><emitter /></li>");
        LumenNodeXml node = Verse.DirectXmlToObject.ObjectFromXml<LumenNodeXml>(document.DocumentElement!, false);
        EffectAsset xml = new LumenEffectDef { defName = "mask-xml", nodes = new List<LumenNodeXml> { node } }.ToEffectAsset();
        Check(xml.Nodes[0].DepthMask == LumenDepthMask.NorthBehindReference, $"XML depthMask: {xml.Nodes[0].DepthMask}");
    }

    [LumenFact]
    public static void DepthMaskOnlyAppliesToGroundNodesWithAnEmitterOrRibbon()
    {
        Reject(new EffectNodeSpec
        {
            Name = "vertical", SpacePlane = LumenSpacePlane.Vertical3D, DepthMask = LumenDepthMask.NorthBehindReference,
            Emitter = new EmitterSpec { Name = "vertical" },
        });
        Reject(new EffectNodeSpec
        {
            Name = "wave", DepthMask = LumenDepthMask.NorthBehindReference,
            Distortion = new DistortionSpec { Kind = LumenDistortionKind.RingWave, Radius = 4, Strength = 1, Thickness = 1, Speed = 4, Duration = 0.1f },
        });
        Reject(new EffectNodeSpec { Name = "unknown", DepthMask = (LumenDepthMask)7, Emitter = new EmitterSpec { Name = "unknown" } });
        LumenAssetRules.Validate(Asset("mask-ribbon", new EffectNodeSpec
        {
            Name = "ribbon", DepthMask = LumenDepthMask.NorthBehindReference,
            Ribbon = new RibbonSpec { Mode = LumenRibbonMode.Beam, EndLocal = Vector3.UnitZ, Segments = 1 },
        }), "programmatic");
    }

    [LumenFact]
    public static void PlayWithoutAReferenceThrows()
    {
        EffectAsset asset = Asset("mask-no-reference", MaskedNode("slash"));
        LumenVfxService service = VfxTestData.Service(asset);
        try
        {
            service.Play(new LumenPlayRequest { MapIndex = 3, EffectId = asset.EffectId });
        }
        catch (ArgumentException error) when (error.Message.Contains("DepthReferenceZ", StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException("Play must reject a depthMask node without LumenPlayRequest.DepthReferenceZ.");
    }

    [LumenFact]
    public static void L1VerticesCarryTheReferenceAndFollowMovesIt()
    {
        EffectAsset asset = Asset("mask-l1", MaskedNode("slash"), new EffectNodeSpec { Name = "plain", Emitter = Burst("plain") });
        LumenVfxService service = VfxTestData.Service(asset);
        Vector3 position = new(10f, 0f, 20f);
        bool Follow(out Vector3 world, out float degrees) { world = position; degrees = 0f; return true; }
        using ILumenEffectHandle handle = service.Play(new LumenPlayRequest
        {
            MapIndex = 3, EffectId = asset.EffectId, OriginWorld = position, DepthReferenceZ = 20.5f, Follow = Follow,
        });
        Step(service, 0.01f, TopDown);
        // Acquisition order: the masked node draws vertices 0..3, the plain node 4..7.
        Vector(new Vector2(Lift, 20.5f), service.Simulator.Vertices[0].DepthMask);
        Vector(Vector2.Zero, service.Simulator.Vertices[4].DepthMask);

        position = new Vector3(10f, 0f, 25f);
        Step(service, 0.01f, TopDown);
        Console.WriteLine($"[MASK] L1 reference after Follow +5 Z: {service.Simulator.Vertices[0].DepthMask.Y}, lift {Lift:F4}");
        Vector(new Vector2(Lift, 25.5f), service.Simulator.Vertices[0].DepthMask);

        // A tilted camera ignores the mask.
        Step(service, 0.01f, Matrix4x4.CreateRotationX(-MathF.PI / 4f));
        Vector(Vector2.Zero, service.Simulator.Vertices[0].DepthMask);
    }

    [LumenFact]
    public static void RibbonVerticesCarryTheReference()
    {
        var node = new EffectNodeSpec
        {
            Name = "rail", DepthMask = LumenDepthMask.NorthBehindReference,
            Ribbon = new RibbonSpec { Mode = LumenRibbonMode.Beam, EndLocal = new Vector3(0f, 0f, 4f), Segments = 1 },
        };
        EffectAsset asset = Asset("mask-ribbon-play", node);
        LumenVfxService service = VfxTestData.Service(asset);
        var backend = new LumenRibbonBackend();
        LumenHostRegistry.RegisterNodeFeatureBackend(backend);
        try
        {
            using ILumenEffectHandle handle = service.Play(new LumenPlayRequest
            {
                MapIndex = 3, EffectId = asset.EffectId, OriginWorld = new(3f, 0f, 7f), DepthReferenceZ = 7.5f,
            });
            Step(service, 0.01f, TopDown);
            backend.Build(Context(0f, TopDown));
            Check(backend.Vertices.Length == 4, $"One beam segment draws 4 vertices, got {backend.Vertices.Length}");
            foreach (var vertex in backend.Vertices) Vector(new Vector2(Lift, 7.5f), vertex.DepthMask);
        }
        finally { LumenHostRegistry.UnregisterNodeFeatureBackend(backend); }
    }

    [LumenFact]
    public static void L2UploadsTheMaskAndReferenceInTheFreedShapeLanes()
    {
        EffectAsset asset = Asset("mask-l2", MaskedNode("slash", LumenParticleTier.L2_GpuCompute),
            new EffectNodeSpec { Name = "plain", Emitter = Burst("plain", LumenParticleTier.L2_GpuCompute) });
        LumenVfxService service = VfxTestData.Service(asset);
        using var gpu = new LumenGpuParticleEngine(8);
        service.GpuEngineProvider = () => gpu;
        using ILumenEffectHandle handle = service.Play(new LumenPlayRequest
        {
            MapIndex = 3, EffectId = asset.EffectId, OriginWorld = new(10f, 0f, 20f), DepthReferenceZ = 19f,
        });
        service.CaptureMainThread(Context(0.01f, TopDown));
        gpu.CaptureMainThread(Context(0.01f, TopDown));
        GpuEmitterParams masked = gpu.CapturedFrame!.Emitters[0].Parameters, plain = gpu.CapturedFrame!.Emitters[1].Parameters;
        Check(masked.ShapePosition.W == 1f && masked.ShapeScale.W == 19f,
            $"Masked L2 emitter must upload (1, 19) in ShapePosition.w/ShapeScale.w, got ({masked.ShapePosition.W}, {masked.ShapeScale.W})");
        Check(plain.ShapePosition.W == 0f && plain.ShapeScale.W == 0f, "An unmasked L2 emitter uploads zero in both lanes");
    }

    private static EffectNodeSpec MaskedNode(string name, LumenParticleTier tier = LumenParticleTier.L1_NativeCpu) => new()
    {
        Name = name, DepthMask = LumenDepthMask.NorthBehindReference, Emitter = Burst(name, tier),
    };

    private static EmitterSpec Burst(string name, LumenParticleTier tier = LumenParticleTier.L1_NativeCpu) => new()
    {
        Name = name, MaxParticles = 1, Duration = 1f, Bursts = new[] { new ParticleBurstSpec(0f, 1) },
        StartSpeedMin = 0f, StartSpeedMax = 0f, StartLifetimeMin = 10f, StartLifetimeMax = 10f, PreferredTier = tier,
    };

    private static EffectAsset Asset(string id, params EffectNodeSpec[] nodes) => new() { EffectId = id, Nodes = nodes };

    private static void Reject(EffectNodeSpec node)
    {
        try
        {
            LumenAssetRules.Validate(Asset("mask-reject", node), "programmatic");
        }
        catch (InvalidDataException error) when (error.Message.Contains("depthMask", StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException($"Node '{node.Name}' must be rejected at the asset boundary.");
    }

    private static LumenCaptureContext Context(float dt, Matrix4x4 view) => new()
    {
        CurrentMapIndex = -1, FrameIndex = 1, DeltaTime = dt, ViewportWidth = 100, ViewportHeight = 100,
        ViewMatrix = view, ProjectionMatrix = Matrix4x4.Identity, LumenRenderPassId = 0,
    };

    private static void Step(LumenVfxService service, float dt, Matrix4x4 view)
    {
        service.CaptureMainThread(Context(dt, view));
        service.Simulator.CaptureMainThread(Context(dt, view));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Vector(Vector2 expected, Vector2 actual)
        => Check(Vector2.Distance(expected, actual) < 1e-4f, $"Expected {expected}, got {actual}.");
}
