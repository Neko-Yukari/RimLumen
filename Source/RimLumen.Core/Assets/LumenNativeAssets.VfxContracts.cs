using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Assets;

// Authoring boundary for the VFX feature contracts (Contracts/VfxFeatureContracts.cs): environment forces, collision,
// sub-emitters, distortion, ribbon and screen-dim nodes. Every field has the same name in C# (PascalCase), .lumen.json and XML.

public sealed partial class LumenEffectDef
{
    private static CollisionSpec ToCollision(LumenCollisionXml? xml) => xml is null
        ? new CollisionSpec()
        : new CollisionSpec
        {
            Mode = xml.mode,
            Bounce = xml.bounce,
            Dampen = xml.dampen,
            LifetimeLoss = xml.lifetimeLoss,
            KillUnderRoof = xml.killUnderRoof,
        };

    private static SubEmitterSpec[] ToSubEmitters(List<LumenSubEmitterXml>? subEmitters, string path)
    {
        if (subEmitters is null || subEmitters.Count == 0)
            return Array.Empty<SubEmitterSpec>();
        var result = new SubEmitterSpec[subEmitters.Count];
        for (int i = 0; i < subEmitters.Count; i++)
        {
            string subPath = $"{path}.emitter.subEmitters[{i}]";
            LumenSubEmitterXml sub = subEmitters[i] ?? throw new InvalidDataException($"{subPath} is null.");
            result[i] = new SubEmitterSpec(
                sub.trigger ?? throw new InvalidDataException($"{subPath}.trigger: required."),
                sub.targetNodeIndex ?? throw new InvalidDataException($"{subPath}.targetNodeIndex: required."),
                sub.count ?? throw new InvalidDataException($"{subPath}.count: required."),
                sub.probability,
                sub.inherit);
        }

        return result;
    }

    private static DistortionSpec ToDistortionSpec(LumenDistortionXml xml, string path)
    {
        string distortionPath = path + ".distortion";
        return new DistortionSpec
        {
            Kind = xml.kind ?? throw new InvalidDataException($"{distortionPath}.kind: required."),
            Radius = xml.radius ?? throw new InvalidDataException($"{distortionPath}.radius: required."),
            Strength = xml.strength ?? throw new InvalidDataException($"{distortionPath}.strength: required."),
            Thickness = xml.thickness,
            Speed = xml.speed,
            Frequency = xml.frequency,
            Duration = xml.duration,
            StrengthOverTime = xml.strengthOverTime is null
                ? null
                : new ScalarCurve(ToCurveKeys(xml.strengthOverTime, distortionPath + ".strengthOverTime")),
        };
    }

    private static ScreenDimSpec ToScreenDimSpec(LumenScreenDimXml xml, string path)
    {
        string dimPath = path + ".screenDim";
        return new ScreenDimSpec
        {
            Strength = xml.strength ?? throw new InvalidDataException($"{dimPath}.strength: required."),
            Duration = xml.duration,
            StrengthOverTime = xml.strengthOverTime is null
                ? null
                : new ScalarCurve(ToCurveKeys(xml.strengthOverTime, dimPath + ".strengthOverTime")),
        };
    }

    private static RibbonSpec ToRibbonSpec(LumenRibbonXml xml, string path)
    {
        string ribbonPath = path + ".ribbon";
        return new RibbonSpec
        {
            Mode = xml.mode ?? throw new InvalidDataException($"{ribbonPath}.mode: required."),
            Lifetime = xml.lifetime,
            MinVertexDistance = xml.minVertexDistance,
            MaxPoints = xml.maxPoints,
            EndLocal = xml.endLocal is { } end ? new Vector3(end.x, end.y, end.z) : null,
            EndAtTarget = xml.endAtTarget,
            Segments = xml.segments,
            NoiseAmplitude = xml.noiseAmplitude,
            NoiseFrequency = xml.noiseFrequency,
            Width = xml.width,
            WidthOverLength = xml.widthOverLength is null
                ? ScalarCurve.Constant(1f)
                : new ScalarCurve(ToCurveKeys(xml.widthOverLength, ribbonPath + ".widthOverLength")),
            ColorOverLength = xml.colorOverLength is null
                ? ColorGradient.Constant(Vector4.One)
                : new ColorGradient(ToColorStops(xml.colorOverLength, ribbonPath + ".colorOverLength")),
            HdrIntensity = xml.hdrIntensity,
            TextureKey = string.IsNullOrEmpty(xml.textureKey) ? null : xml.textureKey,
            BlendMode = xml.blendMode,
            TextureMode = xml.textureMode,
            Alignment = xml.alignment,
            SoftParticleFadeDistance = xml.softParticleFadeDistance,
        };
    }
}

// XML mirror of CollisionSpec; defaults equal the C# and JSON defaults.
public sealed class LumenCollisionXml
{
    public LumenCollisionMode mode = LumenCollisionMode.None;
    public float bounce = 1f;
    public float dampen;
    public float lifetimeLoss;
    public bool killUnderRoof;
}

// XML mirror of SubEmitterSpec; trigger, targetNodeIndex and count are required.
public sealed class LumenSubEmitterXml
{
    public LumenSubEmitterTrigger? trigger;
    public int? targetNodeIndex;
    public int? count;
    public float probability = 1f;
    public LumenSubEmitterInherit inherit = LumenSubEmitterInherit.None;
}

// XML mirror of DistortionSpec; kind, radius and strength are required.
public sealed class LumenDistortionXml
{
    public LumenDistortionKind? kind;
    public float? radius;
    public float? strength;
    public float thickness;
    public float speed;
    public float frequency;
    public float duration;
    public List<LumenCurveKeyXml>? strengthOverTime;
}

// XML mirror of ScreenDimSpec; strength is required.
public sealed class LumenScreenDimXml
{
    public float? strength;
    public float duration;
    public List<LumenCurveKeyXml>? strengthOverTime;
}

// XML mirror of RibbonSpec; mode is required.
public sealed class LumenRibbonXml
{
    public LumenRibbonMode? mode;
    public float lifetime;
    public float minVertexDistance;
    public int maxPoints;
    public UnityEngine.Vector3? endLocal;
    public bool endAtTarget;
    public int segments;
    public float noiseAmplitude;
    public float noiseFrequency;
    public float width = 1f;
    public List<LumenCurveKeyXml>? widthOverLength;
    public List<LumenGradientStopXml>? colorOverLength;
    public float hdrIntensity = 1f;
    public string? textureKey;
    public LumenBlendMode blendMode = LumenBlendMode.Additive;
    public LumenRibbonTextureMode textureMode = LumenRibbonTextureMode.Stretch;
    public LumenRibbonAlignment alignment = LumenRibbonAlignment.View;
    public float softParticleFadeDistance = 0.5f;
}

internal static partial class LumenAssetRules
{
    private static void ValidateEnvironmentAndCollision(EmitterSpec emitter, string path)
    {
        ValidateFlags(emitter.EnvironmentChannels, path + ".environmentChannels");
        ValidateNonnegative(emitter.EnvironmentForceScale, path + ".environmentForceScale");
        CollisionSpec collision = emitter.Collision;
        ValidateEnum(collision.Mode, path + ".collision.mode");
        ValidateRange(collision.Bounce, 1f, path + ".collision.bounce");
        ValidateRange(collision.Dampen, 1f, path + ".collision.dampen");
        ValidateRange(collision.LifetimeLoss, 1f, path + ".collision.lifetimeLoss");
        for (int i = 0; i < emitter.SubEmitters.Length; i++)
        {
            SubEmitterSpec sub = emitter.SubEmitters[i];
            string subPath = $"{path}.subEmitters[{i}]";
            ValidateEnum(sub.Trigger, subPath + ".trigger");
            ValidateFlags(sub.Inherit, subPath + ".inherit");
            if (sub.Count < 0)
                throw new InvalidDataException($"{subPath}.count: must be >= 0, got {sub.Count}.");
            ValidateRange(sub.Probability, 1f, subPath + ".probability");
        }
    }

    private static void ValidateRingWave(ForceFieldSpec field, string path)
    {
        if (!(field.Parameters.X > 0f) || !float.IsFinite(field.Parameters.X))
            throw new InvalidDataException($"{path}.parameters.x: RingWave speed must be finite and > 0 cells/s, got {field.Parameters.X}.");
        if (!(field.Parameters.Y > 0f) || !float.IsFinite(field.Parameters.Y))
            throw new InvalidDataException($"{path}.parameters.y: RingWave shell half-thickness must be finite and > 0 cells, got {field.Parameters.Y}.");
        if (!(field.Radius > 0f))
            throw new InvalidDataException($"{path}.radius: RingWave end radius must be > 0 cells, got {field.Radius}.");
    }

    // Ensures: every sub-emitter targets another node whose emitter only emits through events and shares the
    //          source's tier, and the sub-emitter graph is acyclic.
    private static void ValidateSubEmitterGraph(EffectNodeSpec[] nodes, string source)
    {
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].Emitter is not { } emitter) continue;
            for (int k = 0; k < emitter.SubEmitters.Length; k++)
            {
                int target = emitter.SubEmitters[k].TargetNodeIndex;
                string path = $"{source}: node {i} ('{nodes[i].Name}').emitter.subEmitters[{k}].targetNodeIndex";
                if (target < 0 || target >= nodes.Length)
                    throw new InvalidDataException($"{path}: must be in [0, {nodes.Length}), got {target}.");
                if (target == i)
                    throw new InvalidDataException($"{path}: must differ from the source node {i}.");
                if (nodes[target].Emitter is not { } targetEmitter)
                    throw new InvalidDataException($"{path}: node {target} ('{nodes[target].Name}') has no emitter.");
                if (targetEmitter.RateOverTime != 0f || targetEmitter.Bursts.Length != 0)
                    throw new InvalidDataException($"{path}: node {target} ('{nodes[target].Name}') must have rateOverTime 0 and no bursts; it emits only through sub-emitter events.");
                if (targetEmitter.PreferredTier != emitter.PreferredTier)
                    throw new InvalidDataException($"{path}: node {target} ('{nodes[target].Name}') has preferredTier {targetEmitter.PreferredTier}, the source has {emitter.PreferredTier}.");
            }
        }

        var visit = new byte[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
            VisitSubEmitters(nodes, i, visit, source);
    }

    // visit: 0 = unvisited, 1 = on the current path, 2 = finished.
    private static void VisitSubEmitters(EffectNodeSpec[] nodes, int index, byte[] visit, string source)
    {
        if (visit[index] == 2) return;
        if (visit[index] == 1)
            throw new InvalidDataException($"{source}: node {index} ('{nodes[index].Name}').emitter.subEmitters: the sub-emitter graph has a cycle through this node.");
        visit[index] = 1;
        if (nodes[index].Emitter is { } emitter)
            foreach (SubEmitterSpec sub in emitter.SubEmitters)
                VisitSubEmitters(nodes, sub.TargetNodeIndex, visit, source);
        visit[index] = 2;
    }

    private static void ValidateDistortion(DistortionSpec distortion, string path)
    {
        LumenDistortionKind kind = distortion.Kind;
        ValidateEnum(kind, path + ".kind");
        ValidatePositive(distortion.Radius, path + ".radius");
        ValidatePositive(distortion.Strength, path + ".strength");
        bool ring = kind is LumenDistortionKind.RingWave or LumenDistortionKind.Ripple;
        ValidateModeField(distortion.Thickness, ring, path + ".thickness", kind.ToString());
        ValidateModeField(distortion.Speed, ring, path + ".speed", kind.ToString());
        ValidateModeField(distortion.Frequency, kind != LumenDistortionKind.RingWave, path + ".frequency", kind.ToString());
        if (kind == LumenDistortionKind.RingWave)
            ValidatePositive(distortion.Duration, path + ".duration");
        else
            ValidateNonnegative(distortion.Duration, path + ".duration");
        if (distortion.StrengthOverTime is { } curve)
            ValidateNonnegativeCurve(curve, path + ".strengthOverTime", normalized: false);
    }

    private static void ValidateScreenDim(ScreenDimSpec screenDim, string path)
    {
        ValidateRange(screenDim.Strength, 1f, path + ".strength");
        ValidateNonnegative(screenDim.Duration, path + ".duration");
        if (screenDim.StrengthOverTime is { } curve)
            ValidateNonnegativeCurve(curve, path + ".strengthOverTime", normalized: false);
    }

    private static void ValidateRibbon(RibbonSpec ribbon, string path)
    {
        ValidateEnum(ribbon.Mode, path + ".mode");
        ValidateEnum(ribbon.BlendMode, path + ".blendMode");
        ValidateEnum(ribbon.TextureMode, path + ".textureMode");
        ValidateEnum(ribbon.Alignment, path + ".alignment");
        ValidatePositive(ribbon.Width, path + ".width");
        ValidateNonnegative(ribbon.HdrIntensity, path + ".hdrIntensity");
        ValidateNonnegative(ribbon.SoftParticleFadeDistance, path + ".softParticleFadeDistance");
        ValidateNonnegativeCurve(ribbon.WidthOverLength, path + ".widthOverLength", normalized: true);
        ValidateGradient(ribbon.ColorOverLength, path + ".colorOverLength");

        bool trail = ribbon.Mode == LumenRibbonMode.Trail;
        string mode = ribbon.Mode.ToString();
        ValidateModeField(ribbon.Lifetime, trail, path + ".lifetime", mode);
        ValidateModeField(ribbon.MinVertexDistance, trail, path + ".minVertexDistance", mode);
        if (trail ? ribbon.MaxPoints <= 1 : ribbon.MaxPoints != 0)
            throw new InvalidDataException(trail
                ? $"{path}.maxPoints: required > 1 for Trail, got {ribbon.MaxPoints}."
                : $"{path}.maxPoints: not used by {mode}; must be 0, got {ribbon.MaxPoints}.");
        if (trail ? ribbon.Segments != 0 : ribbon.Segments < 1)
            throw new InvalidDataException(trail
                ? $"{path}.segments: not used by Trail; must be 0, got {ribbon.Segments}."
                : $"{path}.segments: required >= 1 for Beam, got {ribbon.Segments}.");
        if (trail)
        {
            if (ribbon.EndLocal is not null || ribbon.EndAtTarget)
                throw new InvalidDataException($"{path}.endLocal/endAtTarget: not used by Trail; leave both unset.");
            ValidateModeField(ribbon.NoiseAmplitude, false, path + ".noiseAmplitude", mode);
            ValidateModeField(ribbon.NoiseFrequency, false, path + ".noiseFrequency", mode);
            return;
        }

        if ((ribbon.EndLocal is not null) == ribbon.EndAtTarget)
            throw new InvalidDataException($"{path}.endLocal/endAtTarget: Beam needs exactly one of endLocal or endAtTarget.");
        if (ribbon.EndLocal is { } end && (!float.IsFinite(end.X) || !float.IsFinite(end.Y) || !float.IsFinite(end.Z)))
            throw new InvalidDataException($"{path}.endLocal: components must be finite.");
        ValidateNonnegative(ribbon.NoiseAmplitude, path + ".noiseAmplitude");
        ValidateNonnegative(ribbon.NoiseFrequency, path + ".noiseFrequency");
    }

    // Ensures: a field of the current kind/mode is finite and > 0; a field it does not use is exactly 0.
    private static void ValidateModeField(float value, bool used, string path, string kind)
    {
        if (used ? !(value > 0f) || !float.IsFinite(value) : value != 0f)
            throw new InvalidDataException(used
                ? $"{path}: required finite > 0 for {kind}, got {value}."
                : $"{path}: not used by {kind}; must be 0, got {value}.");
    }

    private static void ValidatePositive(float value, string path)
    {
        if (!(value > 0f) || !float.IsFinite(value))
            throw new InvalidDataException($"{path}: must be finite and > 0, got {value}.");
    }

    private static void ValidateNonnegativeCurve(ScalarCurve curve, string path, bool normalized)
    {
        ValidateCurve(curve, path, normalized);
        if (curve.Multiplier < 0f)
            throw new InvalidDataException($"{path}: multiplier must be >= 0, got {curve.Multiplier}.");
        for (int i = 0; i < curve.Keys.Length; i++)
            if (curve.Keys[i].Value < 0f)
                throw new InvalidDataException($"{path}[{i}]: value must be >= 0, got {curve.Keys[i].Value}.");
    }

    private static void ValidateFlags<TEnum>(TEnum value, string path) where TEnum : struct, Enum
    {
        ulong known = 0;
        foreach (TEnum flag in Enum.GetValues<TEnum>())
            known |= Convert.ToUInt64(flag);
        if ((Convert.ToUInt64(value) & ~known) != 0)
            throw new InvalidDataException($"{path}: unknown {typeof(TEnum).Name} bits in {Convert.ToUInt64(value)} (known: {string.Join(", ", Enum.GetNames<TEnum>())}).");
    }
}

public static partial class LumenJsonAssetLoader
{
    private static readonly string[] CollisionProperties = { "mode", "bounce", "dampen", "lifetimeLoss", "killUnderRoof" };

    private static readonly string[] SubEmitterProperties = { "trigger", "targetNodeIndex", "count", "probability", "inherit" };

    private static readonly string[] DistortionProperties =
    {
        "kind", "radius", "strength", "thickness", "speed", "frequency", "duration", "strengthOverTime",
    };

    private static readonly string[] ScreenDimProperties = { "strength", "duration", "strengthOverTime" };

    private static readonly string[] RibbonProperties =
    {
        "mode", "lifetime", "minVertexDistance", "maxPoints", "endLocal", "endAtTarget", "segments", "noiseAmplitude",
        "noiseFrequency", "width", "widthOverLength", "colorOverLength", "hdrIntensity", "textureKey", "blendMode",
        "textureMode", "alignment", "softParticleFadeDistance",
    };

    private static CollisionSpec ParseCollision(JsonScope scope, string path)
    {
        if (!scope.TryGet("collision", out JsonElement element) || element.ValueKind == JsonValueKind.Null)
            return new CollisionSpec();
        JsonScope collision = ObjectScope(element, path + ".collision", CollisionProperties);
        return new CollisionSpec
        {
            Mode = collision.Enum("mode", LumenCollisionMode.None),
            Bounce = collision.Float("bounce", 1f),
            Dampen = collision.Float("dampen", 0f),
            LifetimeLoss = collision.Float("lifetimeLoss", 0f),
            KillUnderRoof = collision.Bool("killUnderRoof", false),
        };
    }

    private static SubEmitterSpec[] ParseSubEmitters(JsonScope scope, string path)
    {
        if (!scope.TryGet("subEmitters", out JsonElement element) || element.ValueKind == JsonValueKind.Null)
            return Array.Empty<SubEmitterSpec>();
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{path}.subEmitters: must be an array, got {element.ValueKind}.");
        var subEmitters = new SubEmitterSpec[element.GetArrayLength()];
        int index = 0;
        foreach (JsonElement subElement in element.EnumerateArray())
        {
            string subPath = $"{path}.subEmitters[{index}]";
            JsonScope sub = ObjectScope(subElement, subPath, SubEmitterProperties);
            subEmitters[index++] = new SubEmitterSpec(
                RequiredEnum<LumenSubEmitterTrigger>(sub, "trigger", subPath),
                RequiredInt(sub, "targetNodeIndex", subPath),
                RequiredInt(sub, "count", subPath),
                sub.Float("probability", 1f),
                sub.Enum("inherit", LumenSubEmitterInherit.None));
        }

        return subEmitters;
    }

    private static DistortionSpec ParseDistortion(JsonElement element, string path)
    {
        JsonScope scope = ObjectScope(element, path, DistortionProperties);
        return new DistortionSpec
        {
            Kind = RequiredEnum<LumenDistortionKind>(scope, "kind", path),
            Radius = scope.OptionalFloat("radius") ?? throw new InvalidDataException($"{path}.radius: required."),
            Strength = scope.OptionalFloat("strength") ?? throw new InvalidDataException($"{path}.strength: required."),
            Thickness = scope.Float("thickness", 0f),
            Speed = scope.Float("speed", 0f),
            Frequency = scope.Float("frequency", 0f),
            Duration = scope.Float("duration", 0f),
            StrengthOverTime = scope.TryGet("strengthOverTime", out JsonElement curve) && curve.ValueKind != JsonValueKind.Null
                ? ParseCurve(curve, path + ".strengthOverTime")
                : null,
        };
    }

    private static ScreenDimSpec ParseScreenDim(JsonElement element, string path)
    {
        JsonScope scope = ObjectScope(element, path, ScreenDimProperties);
        return new ScreenDimSpec
        {
            Strength = scope.OptionalFloat("strength") ?? throw new InvalidDataException($"{path}.strength: required."),
            Duration = scope.Float("duration", 0f),
            StrengthOverTime = scope.TryGet("strengthOverTime", out JsonElement curve) && curve.ValueKind != JsonValueKind.Null
                ? ParseCurve(curve, path + ".strengthOverTime")
                : null,
        };
    }

    private static RibbonSpec ParseRibbon(JsonElement element, string path)
    {
        JsonScope scope = ObjectScope(element, path, RibbonProperties);
        return new RibbonSpec
        {
            Mode = RequiredEnum<LumenRibbonMode>(scope, "mode", path),
            Lifetime = scope.Float("lifetime", 0f),
            MinVertexDistance = scope.Float("minVertexDistance", 0f),
            MaxPoints = scope.Int("maxPoints", 0),
            EndLocal = scope.TryGet("endLocal", out JsonElement end) && end.ValueKind != JsonValueKind.Null
                ? scope.Vector3("endLocal", Vector3.Zero)
                : null,
            EndAtTarget = scope.Bool("endAtTarget", false),
            Segments = scope.Int("segments", 0),
            NoiseAmplitude = scope.Float("noiseAmplitude", 0f),
            NoiseFrequency = scope.Float("noiseFrequency", 0f),
            Width = scope.Float("width", 1f),
            WidthOverLength = scope.TryGet("widthOverLength", out JsonElement width) && width.ValueKind != JsonValueKind.Null
                ? ParseCurve(width, path + ".widthOverLength")
                : ScalarCurve.Constant(1f),
            ColorOverLength = scope.TryGet("colorOverLength", out JsonElement color) && color.ValueKind != JsonValueKind.Null
                ? ParseGradient(color, path + ".colorOverLength")
                : ColorGradient.Constant(Vector4.One),
            HdrIntensity = scope.Float("hdrIntensity", 1f),
            TextureKey = scope.String("textureKey", null),
            BlendMode = scope.Enum("blendMode", LumenBlendMode.Additive),
            TextureMode = scope.Enum("textureMode", LumenRibbonTextureMode.Stretch),
            Alignment = scope.Enum("alignment", LumenRibbonAlignment.View),
            SoftParticleFadeDistance = scope.Float("softParticleFadeDistance", 0.5f),
        };
    }

    private static JsonScope ObjectScope(JsonElement element, string path, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path}: must be an object, got {element.ValueKind}.");
        var scope = new JsonScope(element, path);
        scope.Check(allowed);
        return scope;
    }

    private static TEnum RequiredEnum<TEnum>(JsonScope scope, string name, string path) where TEnum : struct, Enum
        => scope.TryGet(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? scope.Enum(name, default(TEnum))
            : throw new InvalidDataException($"{path}.{name}: required.");

    private static int RequiredInt(JsonScope scope, string name, string path)
        => scope.TryGet(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? scope.Int(name, 0)
            : throw new InvalidDataException($"{path}.{name}: required.");
}
