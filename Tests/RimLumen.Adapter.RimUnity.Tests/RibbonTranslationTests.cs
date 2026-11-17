using System.Linq;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimUnity.Serialization;
using static RimLumen.Adapter.RimUnity.Tests.Export;

namespace RimLumen.Adapter.RimUnity.Tests;

internal static class RibbonTranslationTests
{
    internal static void NativeTrailAndBeam()
    {
        var trail = Ribbon("RimUnity.TrailRenderer",
            Member("time", Number(.5f)), Member("minVertexDistance", Number(.2f)), Member("textureMode", Number(1)));
        var asset = Translate(trail, out var report);
        var entry = report.Nodes.Single();
        Check.Equal(RimUnityPrefabTranslator.TrailComponent, entry.Component, "trail component");
        Check.Equal(LumenParticleTier.L1_NativeCpu, entry.Tier, "trail native");
        Check.Reasons([], entry.Reasons, "trail reasons");
        RibbonSpec spec = asset.Nodes[0].Ribbon!;
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
        Check.Equal("tex", spec.TextureKey, "texture");
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

    private static ShaderData? Shader(string key) => new() { blend = true, srcColor = "SrcAlpha", dstColor = "One" };

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
