using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using RimLumen.Core.Api;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;
using RimUnity.Serialization;
using static RimLumen.Adapter.RimUnity.Tests.Export;

namespace RimLumen.Adapter.RimUnity.Tests;

internal static class RibbonTranslationTests
{
    internal static void NativeTrailAndBeam()
    {
        var trail = Ribbon("RimUnity.TrailRenderer",
            Member("time", Number(.5f)), Member("minVertexDistance", Number(.2f)), Member("textureMode", Number(1)));
        using var temp = new TestDirectory();
        var json = new JsonSerializerOptions { IncludeFields = true };
        void Write(string file, object data) => File.WriteAllText(Path.Combine(temp.Root, file), JsonSerializer.Serialize(data, json));
        Write("catalog.json", new Manifest { assets = [
            new AssetRecord { id = "trail", path = "Effects/Trail", file = "trail.json", type = "Prefab" },
            new AssetRecord { id = "material", path = "Materials/Trail", file = "material.json", type = "Material" },
            new AssetRecord { id = "additive", path = "Shaders/Additive", file = "shader.json", type = "Shader" },
            new AssetRecord { id = "tex", path = "Textures/Trail", file = "trail.rutex", type = "Texture2D" },
        ] });
        Write("trail.json", Prefab(trail));
        Write("material.json", Material("material")!);
        Write("shader.json", Shader("additive")!);
        var provider = new RimUnityAssetProvider();
        provider.RegisterPackageRoot("Ribbon", temp.Root);
        Check.True(provider.TryResolveEffect("Ribbon:trail", out var asset), "resolve trail from catalog");
        RibbonSpec spec = asset!.Nodes[0].Ribbon!;
        Check.True(spec.TextureKey == "tex" && provider.Textures.ResolvedTextureKeys.Contains(spec.TextureKey),
            $"expected native ribbon texture 'tex' in provider bridge; ribbon='{spec.TextureKey}', resolved=[{string.Join(",", provider.Textures.ResolvedTextureKeys)}]");
        Check.True(provider.TryInspectTranslation("Ribbon:trail", out var report), "inspect trail translation");
        var entry = report!.Nodes.Single();
        Check.Equal(RimUnityPrefabTranslator.TrailComponent, entry.Component, "trail component");
        Check.Equal(LumenParticleTier.L1_NativeCpu, entry.Tier, "trail native");
        Check.Reasons([], entry.Reasons, "trail reasons");
        Check.Equal(LumenRibbonMode.Trail, spec.Mode, "trail mode");
        Check.Near(.5f, spec.Lifetime, "time");
        Check.Near(.2f, spec.MinVertexDistance, "minVertexDistance");
        Check.Equal(120, spec.MaxPoints, "240 points per second of lifetime");
        Check.Equal(LumenRibbonTextureMode.Tile, spec.TextureMode, "Tile");
        Check.Near(.3f, spec.Width, "widthMultiplier");
        Check.Near(.5f, CurveEvaluator.Evaluate(spec.WidthOverLength, .5f), "widthCurve midpoint");
        // Colour key (2, 1, .5) times _Color (1, .5, 1) stays one stored product; Linear blending decodes it before any gain.
        Check.Near(1f, spec.HdrIntensity, "no gain split");
        Vector4 head = GradientEvaluator.Evaluate(spec.ColorOverLength, 0f);
        Check.True(Vector4.Distance(new Vector4(2f, .5f, .5f, 1f), head) < 1e-5f, "stored head colour: " + head);
        Check.Near(0f, GradientEvaluator.Evaluate(spec.ColorOverLength, 1f).W, "alpha fades to the tail");
        Check.Equal(LumenBlendMode.Additive, spec.BlendMode, "additive shader");
        Check.Equal(LumenRibbonAlignment.View, spec.Alignment, "View alignment");
        RimLumen.Core.Api.RimLumenApi.RegisterEffect(asset);

        var line = Ribbon("RimUnity.LineRenderer", Member("useWorldSpace", Number(0)), Member("textureMode", Number(3)),
            Member("positions", Array(Vector(0, 0, 0), Vector(0, 2, 0))));
        asset = Translate(line, out report);
        Check.Equal(LumenParticleTier.L1_NativeCpu, report.Nodes.Single().Tier, "two-point local line native");
        spec = asset.Nodes[0].Ribbon!;
        Check.Equal(LumenRibbonMode.Beam, spec.Mode, "beam");
        Check.Equal(new Vector3(0, 2, 0), spec.EndLocal!.Value, "end point");
        Check.Equal(1, spec.Segments, "one straight segment");
        Check.Equal(LumenRibbonTextureMode.Stretch, spec.TextureMode, "two-point RepeatPerSegment is Stretch");
        RimLumen.Core.Api.RimLumenApi.RegisterEffect(asset);
    }

    internal static void ResidentRibbon(bool withParticles)
    {
        var prefab = Prefab(Ribbon("RimUnity.TrailRenderer"));
        if (withParticles)
        {
            var particles = Particles();
            particles.members.Single(m => m.name == "main").value = Data(("duration", Number(1)), ("loop", Number(0)),
                ("maxParticles", Number(32)), ("startLifetime", Constant(1)));
            prefab.nodes.Add(Node("particles", particles, "root"));
        }
        var asset = new RimUnityPrefabTranslator().Translate("ribbon:residency", prefab, Material, Shader, out _);
        Check.Equal(0, asset.States.Length, "imported ribbon retains the implicit state");
        Check.True(asset.DefaultDuration > 2.01f, "native ribbon extends adapter playback beyond the old fallback duration");
        RimLumenApi.RegisterEffect(asset);
        var ribbon = new RecordingRibbon();
        var service = new LumenVfxService(new CpuParticleSimulator());
        LumenHostRegistry.RegisterNodeFeatureBackend(ribbon);
        try
        {
            using var handle = service.Play(new LumenPlayRequest { EffectId = asset.EffectId, MapIndex = 1 });
            Step(0, .25f);
            Check.Equal(withParticles, service.Simulator.LiveParticleCount > 0, "finite emitter plays on its own schedule");
            Step(1, .75f);
            Step(2, 1.01f);
            Check.Equal(0, service.Simulator.LiveParticleCount, "no particles after duration + lifetime");
            Check.Equal(true, ribbon.Active, "native trail emitting at 2.01 s without Stop");
            handle.Stop();
            Check.Equal(false, ribbon.Active, "Stop ends ribbon emission");
        }
        finally
        {
            LumenHostRegistry.UnregisterNodeFeatureBackend(ribbon);
            LumenSceneBootstrap.Catalog.Unregister(asset.EffectId);
        }

        void Step(ulong index, float dt)
        {
            var frame = new LumenCaptureContext { FrameIndex = index, DeltaTime = dt, CurrentMapIndex = 1,
                ViewportWidth = 100, ViewportHeight = 100, ViewMatrix = Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity, LumenRenderPassId = 1 };
            service.CaptureMainThread(in frame);
            service.Simulator.CaptureMainThread(in frame);
        }
    }

    // Observes the node activity sent by the real playback service; no game renderer is installed offline.
    private sealed class RecordingRibbon : ILumenNodeFeatureBackend
    {
        public LumenNodeFeature Feature => LumenNodeFeature.Ribbon;
        public bool Active { get; private set; }
        public int CreateInstance(EffectNodeSpec node, in LumenNodeFeatureFrame initial, bool realTime, uint seed) => 0;
        public void RestartInstance(int instanceId) => Active = true;
        public void UpdateInstance(int instanceId, in LumenNodeFeatureFrame frame) => Active = frame.Active;
        public void DestroyInstance(int instanceId, bool immediate) => Active = false;
        public bool IsInstanceAlive(int instanceId) => Active;
    }

    internal static void RibbonFallbackReasons()
    {
        AssertExternal(Ribbon("RimUnity.LineRenderer", Member("useWorldSpace", Number(0)),
            Member("positions", Array(Vector(0, 0, 0), Vector(1, 0, 0), Vector(2, 1, 0)))), "line.positions.count");
        AssertExternal(Ribbon("RimUnity.LineRenderer", Member("positions", Array(Vector(0, 0, 0), Vector(1, 0, 0)))), "line.useWorldSpace");
        AssertExternal(Ribbon("RimUnity.LineRenderer", Member("useWorldSpace", Number(0)),
            Member("positions", Array(Vector(1, 0, 0), Vector(2, 0, 0)))), "line.positions.start");
        AssertExternal(Ribbon("RimUnity.TrailRenderer", Member("numCornerVertices", Number(2))), "trail.numCornerVertices");
        AssertExternal(Ribbon("RimUnity.TrailRenderer", Member("textureMode", Number(2))), "trail.textureMode.2");
        AssertExternal(Ribbon("RimUnity.TrailRenderer", Member("alignment", Number(1))), "trail.alignment.TransformZ");
        AssertExternal(Ribbon("RimUnity.TrailRenderer", Member("emitting", Number(0))), "trail.emitting");
        AssertExternal(Ribbon("RimUnity.TrailRenderer", Member("widthCurve", Data(("keys", Array(
            Data(("time", Number(0)), ("value", Number(1))), Data(("time", Number(2)), ("value", Number(0)))))))), "trail.widthCurve.domain");

        AssertExternal(Ribbon("RimUnity.TrailRenderer", Member("sharedMaterials", Array(new ValueData { kind = "null" }))),
            "trail.sharedMaterials.null");

        // The first trail alone would be External (caps), the second native: both stay External together.
        var second = new PrefabData { nodes = [Node("root", Ribbon("RimUnity.TrailRenderer", Member("numCapVertices", Number(2))))] };
        var extra = Ribbon("RimUnity.LineRenderer", Member("useWorldSpace", Number(0)), Member("positions", Array(Vector(0, 0, 0), Vector(1, 0, 0))));
        extra.id = "second";
        second.nodes[0].components.Add(extra);
        var asset = new RimUnityPrefabTranslator().Translate("second", second, Material, Shader, out var report);
        Check.Reasons(["ribbon.multipleOnNode", "trail.numCapVertices"], report.Nodes[0].Reasons, "first of two ribbons");
        Check.Reasons(["ribbon.multipleOnNode"], report.Nodes[1].Reasons, "second of two ribbons");
        Check.True(report.Nodes.All(n => n.Tier == LumenParticleTier.External) && asset.Nodes[0].Ribbon is null, "no native ribbon");

        var disabled = Ribbon("RimUnity.TrailRenderer");
        disabled.enabled = false;
        asset = Translate(disabled, out _);
        Check.True(asset.Nodes[0].Ribbon is null, "disabled renderer draws nothing natively");
    }

    private static void AssertExternal(ComponentData component, string reason)
    {
        var asset = Translate(component, out var report);
        Check.Equal(LumenParticleTier.External, report.Nodes.Single().Tier, reason + " tier");
        Check.Reasons([reason], report.Nodes.Single().Reasons, reason);
        Check.True(asset.Nodes[0].Ribbon is null, reason + " has no native ribbon");
    }

    private static EffectAsset Translate(ComponentData component, out RimUnityTranslationReport report)
        => new RimUnityPrefabTranslator().Translate("ribbon." + component.type, new PrefabData { nodes = [Node("root", component)] },
            Material, Shader, out report);

    private static ResourceData? Material(string key) => new()
    {
        type = "Material", members = [Member("_Color", Vector(1, .5f, 1, 1)), Member("_MainTex", new ValueData { kind = "asset", text = "tex" }),
            Member("shader", new ValueData { kind = "asset", text = "additive" })]
    };

    private static ShaderData? Shader(string key) => new() { key = key, profile = "world", blend = true, srcColor = "SrcAlpha", dstColor = "One" };

    // A ribbon with RimUnity's serialized defaults plus the given members.
    private static ComponentData Ribbon(string type, params MemberData[] members)
    {
        var component = new ComponentData
        {
            id = type, type = type, members =
            [
                Member("widthMultiplier", Number(.3f)),
                Member("widthCurve", Data(("keys", Array(
                    Data(("time", Number(0)), ("value", Number(1)), ("inTangent", Number(-1)), ("outTangent", Number(-1))),
                    Data(("time", Number(1)), ("value", Number(0)), ("inTangent", Number(-1)), ("outTangent", Number(-1))))))),
                Member("colorGradient", Data(("mode", Number(0)),
                    ("colorKeys", Array(Data(("time", Number(0)), ("color", Vector(2, 1, .5f, 1))), Data(("time", Number(1)), ("color", Vector(1, 1, 1, 1))))),
                    ("alphaKeys", Array(Data(("time", Number(0)), ("alpha", Number(1))), Data(("time", Number(1)), ("alpha", Number(0))))))),
                Member("sharedMaterials", Array(new ValueData { kind = "asset", text = "material" })),
            ]
        };
        foreach (MemberData member in members)
        {
            component.members.RemoveAll(m => m.name == member.name);
            component.members.Add(member);
        }
        return component;
    }
}
