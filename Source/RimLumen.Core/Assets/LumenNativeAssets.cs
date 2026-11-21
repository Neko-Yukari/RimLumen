using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;
using Verse;
using CurveKey = RimLumen.Core.Contracts.CurveKey;

namespace RimLumen.Core.Assets;

/// <summary>
/// RimWorld XML Def representation of a native RimLumen effect (<c>Defs/**/*.xml</c>).
/// </summary>
public sealed partial class LumenEffectDef : Def
{
    public float defaultDuration = 2f;
    public List<LumenNodeXml>? nodes;
    public List<LumenStateXml>? states;
    public string? initialState;
    public string? stopState;
    public List<LumenTimelineEventXml>? timelineEvents;
    public List<LumenInteractionEventXml>? interactionEvents;

    // Ensures: converts this XML Def into a validated immutable EffectAsset (EffectId defaults to defName).
    public EffectAsset ToEffectAsset(string? packageId = null)
    {
        string source = string.IsNullOrEmpty(packageId) ? $"LumenEffectDef '{defName}'" : $"LumenEffectDef '{defName}' ({packageId})";
        if (string.IsNullOrEmpty(defName))
        {
            throw new InvalidDataException("A LumenEffectDef must declare a defName.");
        }

        if (nodes is null || nodes.Count == 0)
        {
            throw new InvalidDataException($"{source} has no nodes.");
        }

        var specs = new EffectNodeSpec[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            LumenNodeXml node = nodes[i] ?? throw new InvalidDataException($"{source}: node {i} is null.");
            string nodePath = $"{source}: node {i}";
            string nodeName = string.IsNullOrEmpty(node.name) ? $"node{i}" : node.name!;
            if (!float.IsFinite(node.localYawDegrees))
                throw new InvalidDataException($"{nodePath}.localYawDegrees: must be finite.");
            specs[i] = new EffectNodeSpec
            {
                Name = nodeName,
                ParentIndex = node.parentIndex,
                SpacePlane = node.spacePlane,
                DepthMask = node.depthMask,
                LocalPosition = new Vector3(
                    node.localPosition.x,
                    node.localPosition.y + node.heightOffset,
                    node.localPosition.z),
                LocalRotation = node.localYawDegrees == 0f
                    ? Quaternion.Identity
                    : Quaternion.CreateFromAxisAngle(Vector3.UnitY, node.localYawDegrees * (MathF.PI / 180f)),
                LocalScale = new Vector3(node.localScale, node.localScale, node.localScale),
                Emitter = node.emitter is null ? null : ToEmitterSpec(node.emitter, nodeName, nodePath),
                PointLight = node.pointLight is null ? null : ToPointLightSpec(node.pointLight, nodePath),
                Distortion = node.distortion is null ? null : ToDistortionSpec(node.distortion, nodePath),
                Ribbon = node.ribbon is null ? null : ToRibbonSpec(node.ribbon, nodePath),
                ScreenDim = node.screenDim is null ? null : ToScreenDimSpec(node.screenDim, nodePath),
            };
        }

        var asset = new EffectAsset
        {
            EffectId = defName,
            DefaultDuration = defaultDuration,
            Nodes = specs,
            TimelineEvents = ToTimelineEvents(timelineEvents, source),
            InteractionEvents = ToInteractionEvents(interactionEvents, source),
            States = ToStates(states, source),
            InitialState = initialState,
            StopState = stopState,
        };
        LumenAssetRules.Validate(asset, source);
        return asset;
    }

    private static EffectStateSpec[] ToStates(List<LumenStateXml>? states, string source)
    {
        if (states is null || states.Count == 0)
        {
            return Array.Empty<EffectStateSpec>();
        }

        var result = new EffectStateSpec[states.Count];
        for (int i = 0; i < states.Count; i++)
        {
            LumenStateXml state = states[i] ?? throw new InvalidDataException($"{source}: states[{i}] is null.");
            result[i] = new EffectStateSpec
            {
                Name = state.name ?? throw new InvalidDataException($"{source}: states[{i}]: name is required."),
                Nodes = state.nodes?.ToArray() ?? Array.Empty<string>(),
                Duration = state.duration,
                Loop = state.loop,
                Next = state.next,
            };
        }

        return result;
    }

    private static TimelineEventSpec[] ToTimelineEvents(List<LumenTimelineEventXml>? events, string source)
    {
        if (events is null || events.Count == 0)
        {
            return Array.Empty<TimelineEventSpec>();
        }

        var result = new TimelineEventSpec[events.Count];
        for (int i = 0; i < events.Count; i++)
        {
            LumenTimelineEventXml timelineEvent = events[i] ?? throw new InvalidDataException($"{source}: timelineEvents[{i}] is null.");
            if (string.IsNullOrEmpty(timelineEvent.eventName))
            {
                throw new InvalidDataException($"{source}: timelineEvents[{i}]: eventName is required.");
            }

            result[i] = new TimelineEventSpec(
                timelineEvent.time,
                timelineEvent.eventName!,
                timelineEvent.targetNodeIndex,
                timelineEvent.state,
                timelineEvent.notify);
        }

        return result;
    }

    private static EmitterSpec ToEmitterSpec(LumenEmitterXml xml, string nodeName, string path)
    {
        xml.RejectLegacyFields(path + ".emitter");
        var emitter = new EmitterSpec
        {
            Name = string.IsNullOrEmpty(xml.name) ? nodeName : xml.name!,
            MaxParticles = xml.maxParticles,
            Duration = xml.duration,
            Looping = xml.looping,
            WorldSpaceSimulation = xml.worldSpaceSimulation,
            RateOverTime = xml.rateOverTime,
            Bursts = ToBursts(xml.bursts, path),
            Shape = xml.shape,
            ShapeExtents = new Vector3(xml.shapeExtents.x, xml.shapeExtents.y, xml.shapeExtents.z),
            ShapePosition = new Vector3(xml.shapePosition.x, xml.shapePosition.y, xml.shapePosition.z),
            ShapeRotation = LumenAssetRules.NormalizeRotation(new Quaternion(xml.shapeRotation.x, xml.shapeRotation.y, xml.shapeRotation.z, xml.shapeRotation.w), path + ".emitter.shapeRotation"),
            ShapeScale = new Vector3(xml.shapeScale.x, xml.shapeScale.y, xml.shapeScale.z),
            ConeAngleDegrees = xml.coneAngleDegrees,
            ConeLength = xml.coneLength,
            DonutRadius = xml.donutRadius,
            RadiusThickness = xml.radiusThickness,
            BoxThickness = new Vector3(xml.boxThickness.x, xml.boxThickness.y, xml.boxThickness.z),
            ArcDegrees = xml.arcDegrees,
            ArcSpread = xml.arcSpread,
            RadiusSpread = xml.radiusSpread,
            RandomDirectionAmount = xml.randomDirectionAmount,
            SphericalDirectionAmount = xml.sphericalDirectionAmount,
            RandomPositionAmount = xml.randomPositionAmount,
            StartLifetimeMin = xml.startLifetimeMin,
            StartLifetimeMax = xml.startLifetimeMax,
            StartSpeedMin = xml.startSpeedMin,
            StartSpeedMax = xml.startSpeedMax,
            StartSizeMin = xml.startSizeMin,
            StartSizeMax = xml.startSizeMax,
            StartColor = ParseColor(xml.startColor, path + ".emitter.startColor"),
            ColorSpace = xml.colorSpace,
            MaterialTint = ParseColor(xml.materialTint, path + ".emitter.materialTint"),
            SizeOverLifetime = xml.sizeOverLifetime is null
                ? ScalarCurve.Constant(1f)
                : new ScalarCurve(ToCurveKeys(xml.sizeOverLifetime, path + ".sizeOverLifetime")),
            ColorOverLifetime = xml.colorOverLifetime is null
                ? ColorGradient.Constant(Vector4.One)
                : new ColorGradient(ToColorStops(xml.colorOverLifetime, path + ".colorOverLifetime")),
            ForceFields = ToForceFields(xml.forceFields, path),
            EnvironmentChannels = xml.environmentChannels,
            EnvironmentForceScale = xml.environmentForceScale,
            Collision = ToCollision(xml.collision),
            SubEmitters = ToSubEmitters(xml.subEmitters, path),
            Trails = xml.trails is null ? null : ToParticleTrailSpec(xml.trails, path + ".emitter"),
            TextureKey = string.IsNullOrEmpty(xml.textureKey) ? null : xml.textureKey,
            FlipbookTilesX = xml.flipbookTilesX,
            FlipbookTilesY = xml.flipbookTilesY,
            FlipbookCycles = xml.flipbookCycles,
            BlendMode = xml.blendMode,
            BillboardMode = xml.billboardMode,
            VelocityStretchFactor = xml.velocityStretchFactor,
            LengthScale = xml.lengthScale,
            SoftParticleFadeDistance = xml.softParticleFadeDistance,
            HdrIntensity = xml.hdrIntensity,
            PreferredTier = xml.preferredTier,
            KillBelowFloor = xml.killBelowFloor,
        };
        return emitter;
    }

    private static PointLightSpec ToPointLightSpec(LumenPointLightXml xml, string path)
    {
        xml.RejectLegacyFields(path + ".pointLight");
        // PointLightSpec carries no intensity column: the light pool multiplies the colour by
        // Evaluate(IntensityOverTime) * runtime multiplier, so the authored intensity rides on the curve's multiplier.
        ScalarCurve? intensity;
        if (xml.intensityOverTime is not null)
        {
            intensity = new ScalarCurve(ToCurveKeys(xml.intensityOverTime, path + ".pointLight.intensityOverTime"), xml.intensity);
        }
        else
        {
            intensity = xml.intensity == 1f ? null : ScalarCurve.Constant(xml.intensity);
        }

        Vector4 color = ParseColor(xml.color, path + ".pointLight.color");
        return new PointLightSpec(
            xml.radiusCells,
            new Vector3(color.X, color.Y, color.Z),
            intensity);
    }

    private static ParticleBurstSpec[] ToBursts(List<LumenBurstXml>? bursts, string path)
    {
        if (bursts is null || bursts.Count == 0)
        {
            return Array.Empty<ParticleBurstSpec>();
        }

        var result = new ParticleBurstSpec[bursts.Count];
        for (int i = 0; i < bursts.Count; i++)
        {
            LumenBurstXml burst = bursts[i] ?? throw new InvalidDataException($"{path}.bursts[{i}] is null.");
            result[i] = new ParticleBurstSpec(burst.time, burst.count, burst.probability);
        }

        return result;
    }

    private static CurveKey[] ToCurveKeys(List<LumenCurveKeyXml> keys, string path)
    {
        var result = new CurveKey[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            LumenCurveKeyXml key = keys[i] ?? throw new InvalidDataException($"{path}[{i}] is null.");
            result[i] = new CurveKey(key.time, key.value, key.inTangent, key.outTangent);
        }

        return result;
    }

    private static ColorStop[] ToColorStops(List<LumenGradientStopXml> stops, string path)
    {
        var result = new ColorStop[stops.Count];
        for (int i = 0; i < stops.Count; i++)
        {
            LumenGradientStopXml stop = stops[i] ?? throw new InvalidDataException($"{path}[{i}] is null.");
            stop.RejectLegacyFields($"{path}[{i}]");
            result[i] = new ColorStop(stop.time, ParseColor(stop.color, $"{path}[{i}].color"));
        }

        return result;
    }

    private static ForceFieldSpec[] ToForceFields(List<LumenForceFieldXml>? fields, string path)
    {
        if (fields is null || fields.Count == 0)
        {
            return Array.Empty<ForceFieldSpec>();
        }

        var result = new ForceFieldSpec[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            LumenForceFieldXml field = fields[i] ?? throw new InvalidDataException($"{path}.forceFields[{i}] is null.");
            result[i] = new ForceFieldSpec(
                field.kind,
                new Vector3(field.centerOrAxis.x, field.centerOrAxis.y, field.centerOrAxis.z),
                new Vector3(field.parameters.x, field.parameters.y, field.parameters.z),
                field.radius,
                field.strength ?? throw new InvalidDataException($"{path}.forceFields[{i}].strength: required."),
                field.frequency);
        }

        return result;
    }

    // Keep raw floats until shared domain validation; Verse.ParseColor interprets RGB > 1 as byte values.
    private static Vector4 ParseColor(string text, string path)
    {
        if (text is null)
            throw new InvalidDataException($"{path}: expected (r,g,b) or (r,g,b,a) raw float colour, got null.");
        string value = text.Trim();
        if (value.Length < 2 || value[0] != '(' || value[^1] != ')')
            throw new InvalidDataException($"{path}: expected (r,g,b) or (r,g,b,a) raw float colour, got '{text}'.");
        string[] components = value[1..^1].Split(',');
        if (components.Length != 3 && components.Length != 4)
            throw new InvalidDataException($"{path}: expected 3 or 4 colour components, got {components.Length}.");
        Span<float> rgba = stackalloc float[4];
        rgba[3] = 1f;
        for (int i = 0; i < components.Length; i++)
        {
            if (!float.TryParse(components[i], NumberStyles.Float, CultureInfo.InvariantCulture, out rgba[i]))
                throw new InvalidDataException($"{path}: colour component {i} must be a float, got '{components[i]}'.");
        }
        return new Vector4(rgba[0], rgba[1], rgba[2], rgba[3]);
    }
}

public sealed class LumenNodeXml
{
    public string? name;
    public int parentIndex = -1;
    public LumenSpacePlane spacePlane = LumenSpacePlane.GroundXZ;
    public LumenDepthMask depthMask = LumenDepthMask.None;
    public UnityEngine.Vector3 localPosition;
    public float localYawDegrees;
    public float localScale = 1f;
    public float heightOffset;
    public LumenEmitterXml? emitter;
    public LumenPointLightXml? pointLight;
    public LumenDistortionXml? distortion;
    public LumenRibbonXml? ribbon;
    public LumenScreenDimXml? screenDim;
}

// XML mirror of <see cref="EmitterSpec"/>. Field initialisers are the SAME defaults the .lumen.json loader
// uses (see LumenJsonAssetLoader.ParseEmitter), so the same authored content behaves identically in both formats.
public sealed class LumenEmitterXml
{
    public string? name;
    public LumenParticleTier preferredTier = LumenParticleTier.L2_GpuCompute;
    public int maxParticles = 1024;
    public float duration = 1f;
    public bool looping = false;
    public float rateOverTime = 0f;
    public List<LumenBurstXml>? bursts;
    public float startLifetimeMin = 1f;
    public float startLifetimeMax = 1f;
    public float startSpeedMin = 0f;
    public float startSpeedMax = 0f;
    public float startSizeMin = 1f;
    public float startSizeMax = 1f;
    public string startColor = "(1,1,1,1)";
    // Material calculation: Stored multiplies raw factors; Linear decodes tint and sRGB texture RGB and
    // encodes the product back to stored values. Alpha is never gamma-converted.
    public LumenColorSpace colorSpace = LumenColorSpace.Stored;
    // RGBA material factor: RGB finite >= 0 without an upper bound, alpha finite in [0,1].
    public string materialTint = "(1,1,1,1)";
    private string? startColorLinear = null;
    private string? bloomWeight = null;
    private string? hdrMultiplier = null;

    internal void RejectLegacyFields(string path)
    {
        if (bloomWeight is not null)
            throw new InvalidDataException($"{path}: 'bloomWeight' was removed.");
        if (hdrMultiplier is not null)
            throw new InvalidDataException($"{path}: 'hdrMultiplier' was removed; use hdrIntensity.");
        if (startColorLinear is not null)
            throw new InvalidDataException($"{path}: 'startColorLinear' renamed to startColor.");
    }
    public float hdrIntensity = 1f;
    public LumenShapeKind shape = LumenShapeKind.Point;
    public UnityEngine.Vector3 shapeExtents = UnityEngine.Vector3.one;
    public UnityEngine.Vector3 shapePosition;
    // DirectXmlToObject supports ParseHelper's (x,y,z,w) Unity quaternion format (Assembly-CSharp:20720,21002,142245).
    public UnityEngine.Quaternion shapeRotation = UnityEngine.Quaternion.identity;
    public UnityEngine.Vector3 shapeScale = UnityEngine.Vector3.one;
    public float coneAngleDegrees = 25f;
    public float coneLength = 5f;
    public float donutRadius = 0.2f;
    public float radiusThickness = 1f;
    public UnityEngine.Vector3 boxThickness;
    public float arcDegrees = 360f;
    public float arcSpread;
    public float radiusSpread;
    public float randomDirectionAmount;
    public float sphericalDirectionAmount;
    public float randomPositionAmount;
    public LumenBillboardMode billboardMode = LumenBillboardMode.Camera;
    public float velocityStretchFactor;
    public float lengthScale = 1f;
    public LumenBlendMode blendMode = LumenBlendMode.Additive;
    public float softParticleFadeDistance = 0.5f;
    public string? textureKey;
    public int flipbookTilesX = 1;
    public int flipbookTilesY = 1;
    public float flipbookCycles = 1f;
    public bool worldSpaceSimulation = true;
    public bool killBelowFloor = false;
    public List<LumenCurveKeyXml>? sizeOverLifetime;
    public List<LumenGradientStopXml>? colorOverLifetime;
    public List<LumenForceFieldXml>? forceFields;
    // Flags: comma-separated names, e.g. "Explosion, Cursor" or "None".
    public LumenEnvironmentChannel environmentChannels = LumenEnvironmentChannel.Explosion | LumenEnvironmentChannel.Cursor;
    public float environmentForceScale = 1f;
    public LumenCollisionXml? collision;
    public List<LumenSubEmitterXml>? subEmitters;
    public LumenParticleTrailXml? trails;
}

// XML mirror of <see cref="EffectStateSpec"/>; an omitted duration inherits the def's defaultDuration, as in .lumen.json.
public sealed class LumenStateXml
{
    public string? name;
    public List<string>? nodes;
    public float? duration;
    public bool loop = false;
    public string? next;
}

public sealed class LumenTimelineEventXml
{
    public float time;
    public string? eventName;
    public int targetNodeIndex = -1;
    public string? state;
    public bool notify = false;
}

public sealed class LumenBurstXml
{
    public float time;
    public int count = 1;
    public float probability = 1f;
}

public sealed class LumenCurveKeyXml
{
    public float time;
    public float value = 1f;
    public float inTangent;
    public float outTangent;
}

public sealed class LumenGradientStopXml
{
    public float time;
    public string color = "(1,1,1,1)";
    private string? rgbaLinear = null;

    internal void RejectLegacyFields(string path)
    {
        if (rgbaLinear is not null)
            throw new InvalidDataException($"{path}: 'rgbaLinear' renamed to color.");
    }
}

public sealed class LumenForceFieldXml
{
    public ForceFieldKind kind;
    public UnityEngine.Vector3 centerOrAxis;
    public UnityEngine.Vector3 parameters;
    public float? strength;
    public float frequency = 1f;
    public float radius = 5f;
}

public sealed class LumenPointLightXml
{
    public string color = "(1,1,1,1)";
    private string? colorLinear = null;
    private string? colorRgb = null;
    private string? overlightRadius = null;

    internal void RejectLegacyFields(string path)
    {
        if (colorLinear is not null)
            throw new InvalidDataException($"{path}: 'colorLinear' renamed to color.");
        if (colorRgb is not null)
            throw new InvalidDataException($"{path}: 'colorRgb' renamed to color.");
        if (overlightRadius is not null)
            throw new InvalidDataException($"{path}: 'overlightRadius' removed; point lights are visual-only.");
    }
    public float radiusCells = 5f;
    public float intensity = 1f;
    public List<LumenCurveKeyXml>? intensityOverTime;
}

/// <summary>
/// Shared structural validation for every authored effect asset (XML Def and <c>.lumen.json</c>).
/// </summary>
internal static partial class LumenAssetRules
{
    // Requires: asset is non-null.
    // Ensures: throws InvalidDataException naming <paramref name="source"/> when the asset cannot be played:
    //          empty EffectId, no nodes, an empty node name, a parent index that is neither -1 nor an
    //          earlier node (the only cycle-free ordering the runtime accepts), a non-positive particle
    //          budget/duration/lifetime, an invalid flipbook grid or enum, empty/nonfinite/unordered curves,
    //          particle curve times outside [0,1], light curve times below zero seconds, a light glowRadius outside [0,40],
    //          light colour outside [0,1], a negative light intensity multiplier or curve key value, start/tint/gradient RGB non-finite or negative, colour alpha outside [0,1],
    //          a non-finite or negative hdrIntensity, or state references that EffectStateGraph.For rejects.
    public static void Validate(EffectAsset asset, string source)
    {
        if (string.IsNullOrEmpty(asset.EffectId))
        {
            throw new InvalidDataException($"{source}: effectId must not be null or empty.");
        }

        if (!float.IsFinite(asset.DefaultDuration) || asset.DefaultDuration < 0f)
        {
            throw new InvalidDataException($"{source}: defaultDuration must be finite and >= 0, got {asset.DefaultDuration}.");
        }

        EffectNodeSpec[] nodes = asset.Nodes ?? throw new InvalidDataException($"{source}: nodes must not be null.");
        if (nodes.Length == 0)
        {
            throw new InvalidDataException($"{source}: at least one node is required.");
        }

        for (int i = 0; i < nodes.Length; i++)
        {
            EffectNodeSpec node = nodes[i] ?? throw new InvalidDataException($"{source}: node {i} is null.");
            string path = $"{source}: node {i}";
            if (string.IsNullOrEmpty(node.Name))
            {
                throw new InvalidDataException($"{path}: name must not be null or empty.");
            }

            if (node.ParentIndex < -1 || node.ParentIndex >= i)
            {
                throw new InvalidDataException($"{path} ('{node.Name}'): parentIndex {node.ParentIndex} must be -1 or an earlier node index (< {i}).");
            }

            if (!float.IsFinite(node.LocalScale.X) || !float.IsFinite(node.LocalScale.Y) || !float.IsFinite(node.LocalScale.Z))
            {
                throw new InvalidDataException($"{path} ('{node.Name}'): localScale must be finite.");
            }

            ValidateRotation(node.LocalRotation, $"{path} ('{node.Name}').localRotation");
            ValidateEnum(node.SpacePlane, $"{path} ('{node.Name}').spacePlane");
            ValidateEnum(node.DepthMask, $"{path} ('{node.Name}').depthMask");
            if (node.DepthMask != LumenDepthMask.None
                && (node.SpacePlane != LumenSpacePlane.GroundXZ || (node.Emitter is null && node.Ribbon is null)))
                throw new InvalidDataException($"{path} ('{node.Name}').depthMask: only a GroundXZ node with an emitter or ribbon takes a depth mask.");

            if (node.Emitter is { } emitter)
            {
                ValidateEmitter(emitter, $"{path} ('{node.Name}').emitter");
            }

            if (node.PointLight is { } light)
            {
                string lightPath = $"{path} ('{node.Name}').pointLight";
                if (!float.IsFinite(light.GlowRadius) || light.GlowRadius < 0f || light.GlowRadius > 40f)
                    throw new InvalidDataException($"{lightPath}: glowRadius must be in [0,40], got {light.GlowRadius}.");
                Vector3 color = light.ColorRgb;
                if (!float.IsFinite(color.X) || !float.IsFinite(color.Y) || !float.IsFinite(color.Z) ||
                    color.X < 0f || color.X > 1f || color.Y < 0f || color.Y > 1f || color.Z < 0f || color.Z > 1f)
                    throw new InvalidDataException($"{lightPath}: color components must be in [0,1].");
                if (light.IntensityOverTime is { } intensity)
                {
                    ValidateCurve(intensity, lightPath + ".intensityOverTime", normalized: false);
                    if (intensity.Multiplier < 0f)
                        throw new InvalidDataException($"{lightPath}: intensity must be >= 0, got {intensity.Multiplier}.");
                    for (int k = 0; k < intensity.Keys.Length; k++)
                        if (intensity.Keys[k].Value < 0f)
                            throw new InvalidDataException($"{lightPath}.intensityOverTime[{k}]: value must be >= 0, got {intensity.Keys[k].Value}.");
                }
            }

            if (node.Distortion is { } distortion)
                ValidateDistortion(distortion, $"{path} ('{node.Name}').distortion");
            if (node.Ribbon is { } ribbon)
                ValidateRibbon(ribbon, $"{path} ('{node.Name}').ribbon");
            if (node.ScreenDim is { } screenDim)
                ValidateScreenDim(screenDim, $"{path} ('{node.Name}').screenDim");
        }

        ValidateSubEmitterGraph(nodes, source);

        TimelineEventSpec[] events = asset.TimelineEvents ?? Array.Empty<TimelineEventSpec>();
        for (int i = 0; i < events.Length; i++)
        {
            TimelineEventSpec timelineEvent = events[i];
            if (!float.IsFinite(timelineEvent.TimeSeconds) || timelineEvent.TimeSeconds < 0f)
            {
                throw new InvalidDataException($"{source}: timelineEvents[{i}]: time must be finite and >= 0, got {timelineEvent.TimeSeconds}.");
            }

            if (string.IsNullOrEmpty(timelineEvent.EventName))
            {
                throw new InvalidDataException($"{source}: timelineEvents[{i}]: eventName must not be null or empty.");
            }

            if (timelineEvent.TargetNodeIndex < -1 || timelineEvent.TargetNodeIndex >= nodes.Length)
            {
                throw new InvalidDataException($"{source}: timelineEvents[{i}]: targetNodeIndex {timelineEvent.TargetNodeIndex} must be -1 or in [0, {nodes.Length}).");
            }
        }

        InteractionEventSpec[] interactions = asset.InteractionEvents ?? Array.Empty<InteractionEventSpec>();
        for (int i = 0; i < interactions.Length; i++)
        {
            InteractionEventSpec interaction = interactions[i] ?? throw new InvalidDataException($"{source}: interactionEvents[{i}] is null.");
            ValidateInteractionEvent(interaction, nodes.Length, $"{source}: interactionEvents[{i}]");
        }

        EffectStateGraph.For(asset, source);
    }

    private static void ValidateEnum<TEnum>(TEnum value, string path) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new InvalidDataException($"{path}: unknown {typeof(TEnum).Name} value {value} (known: {string.Join(", ", Enum.GetNames<TEnum>())}).");
    }

    private static void ValidateCurve(ScalarCurve curve, string path, bool normalized)
    {
        if (curve.Keys.Length == 0)
            throw new InvalidDataException($"{path}: at least one key is required.");
        if (!float.IsFinite(curve.Multiplier))
            throw new InvalidDataException($"{path}: multiplier must be finite.");
        for (int i = 0; i < curve.Keys.Length; i++)
        {
            CurveKey key = curve.Keys[i];
            ValidateTime(key.Time, i > 0 ? curve.Keys[i - 1].Time : -1f, $"{path}[{i}]", normalized, allowEqual: true);
            if (!float.IsFinite(key.Value) || !float.IsFinite(key.InTangent) || !float.IsFinite(key.OutTangent))
                throw new InvalidDataException($"{path}[{i}]: value and tangents must be finite.");
        }
    }

    // Stored scene encoding: RGB finite >= 0 without an upper bound (HDR), alpha finite in [0,1] as coverage.
    private static void ValidateStoredColor(Vector4 color, string path)
    {
        if (!float.IsFinite(color.X) || !float.IsFinite(color.Y) || !float.IsFinite(color.Z) ||
            color.X < 0f || color.Y < 0f || color.Z < 0f)
            throw new InvalidDataException($"{path}: RGB components must be finite and >= 0, got ({color.X}, {color.Y}, {color.Z}).");
        if (!float.IsFinite(color.W) || color.W < 0f || color.W > 1f)
            throw new InvalidDataException($"{path}: alpha must be finite and in [0,1], got {color.W}.");
    }

    // Scalar CurveKeys allow equal times (D22); ColorGradient stops remain strict.
    private static void ValidateTime(float time, float previous, string path, bool normalized, bool allowEqual)
    {
        if (!float.IsFinite(time) || time < 0f || (normalized && time > 1f))
            throw new InvalidDataException($"{path}.time: must be finite and {(normalized ? "in [0,1] (normalized lifetime)" : ">= 0 (seconds)")}.");
        if (allowEqual ? time < previous : time <= previous)
            throw new InvalidDataException($"{path}.time: {(allowEqual ? "keys must have non-decreasing time" : "stops must have strictly increasing time")}.");
    }

    // Parser-only normalization; preserve the existing localRotation degeneracy threshold.
    internal static Quaternion NormalizeRotation(Quaternion rotation, string path)
    {
        float lengthSquared = rotation.LengthSquared();
        if (!(lengthSquared > 1e-8f) || !float.IsFinite(lengthSquared))
            throw new InvalidDataException($"{path}: quaternion {rotation} must be finite and nondegenerate.");
        return Quaternion.Normalize(rotation);
    }

    private static void ValidateRotation(Quaternion rotation, string path)
    {
        float lengthSquared = rotation.LengthSquared();
        if (!float.IsFinite(lengthSquared) || MathF.Abs(lengthSquared - 1f) > 1e-5f)
            throw new InvalidDataException($"{path}: quaternion must be finite, nondegenerate and unit (|lengthSquared - 1| <= 1e-5).");
    }

    private static void ValidateRange(float value, float max, string path)
    {
        if (!float.IsFinite(value) || value < 0f || value > max)
            throw new InvalidDataException($"{path}: must be finite and in [0,{max}], got {value}.");
    }

    private static void ValidateNonnegative(float value, string path)
    {
        if (!float.IsFinite(value) || value < 0f)
            throw new InvalidDataException($"{path}: must be finite and >= 0, got {value}.");
    }

    private static void ValidateEmitter(EmitterSpec emitter, string path)
    {
        if (emitter.MaxParticles <= 0)
        {
            throw new InvalidDataException($"{path}: maxParticles must be > 0, got {emitter.MaxParticles}.");
        }

        if (!(emitter.StartLifetimeMin > 0f) || !float.IsFinite(emitter.StartLifetimeMin))
        {
            throw new InvalidDataException($"{path}: startLifetimeMin must be finite and > 0, got {emitter.StartLifetimeMin}.");
        }

        if (!float.IsFinite(emitter.StartLifetimeMax) || emitter.StartLifetimeMax < emitter.StartLifetimeMin)
        {
            throw new InvalidDataException($"{path}: startLifetimeMax ({emitter.StartLifetimeMax}) must be >= startLifetimeMin ({emitter.StartLifetimeMin}).");
        }

        if (emitter.FlipbookTilesX < 1 || emitter.FlipbookTilesY < 1)
        {
            throw new InvalidDataException($"{path}: flipbookTilesX/Y must be >= 1, got {emitter.FlipbookTilesX}x{emitter.FlipbookTilesY}.");
        }

        if (!float.IsFinite(emitter.Duration) || emitter.Duration <= 0f)
        {
            throw new InvalidDataException($"{path}: duration must be finite and > 0, got {emitter.Duration}.");
        }

        Vector3 position = emitter.ShapePosition;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            throw new InvalidDataException($"{path}.shapePosition: components must be finite.");
        Vector3 scale = emitter.ShapeScale;
        if (!float.IsFinite(scale.X) || !float.IsFinite(scale.Y) || !float.IsFinite(scale.Z))
            throw new InvalidDataException($"{path}.shapeScale: components must be finite (zero and negative scales are allowed).");
        ValidateRotation(emitter.ShapeRotation, path + ".shapeRotation");
        ValidateNonnegative(emitter.ConeLength, path + ".coneLength");
        ValidateNonnegative(emitter.DonutRadius, path + ".donutRadius");
        ValidateNonnegative(emitter.RandomPositionAmount, path + ".randomPositionAmount");
        ValidateRange(emitter.RadiusThickness, 1f, path + ".radiusThickness");
        ValidateRange(emitter.BoxThickness.X, 1f, path + ".boxThickness.x");
        ValidateRange(emitter.BoxThickness.Y, 1f, path + ".boxThickness.y");
        ValidateRange(emitter.BoxThickness.Z, 1f, path + ".boxThickness.z");
        ValidateRange(emitter.ArcDegrees, 360f, path + ".arcDegrees");
        ValidateRange(emitter.ArcSpread, 1f, path + ".arcSpread");
        ValidateRange(emitter.RadiusSpread, 1f, path + ".radiusSpread");
        ValidateRange(emitter.RandomDirectionAmount, 1f, path + ".randomDirectionAmount");
        ValidateRange(emitter.SphericalDirectionAmount, 1f, path + ".sphericalDirectionAmount");

        ValidateEnum(emitter.PreferredTier, path + ".preferredTier");
        ValidateEnum(emitter.Shape, path + ".shape");
        ValidateEnum(emitter.BlendMode, path + ".blendMode");
        ValidateEnum(emitter.BillboardMode, path + ".billboardMode");

        ValidateCurve(emitter.SizeOverLifetime, path + ".sizeOverLifetime", normalized: true);
        ValidateGradient(emitter.ColorOverLifetime, path + ".colorOverLifetime");
        ValidateEnvironmentAndCollision(emitter, path);

        if (!float.IsFinite(emitter.RateOverTime) || emitter.RateOverTime < 0f)
        {
            throw new InvalidDataException($"{path}: rateOverTime must be finite and >= 0, got {emitter.RateOverTime}.");
        }

        ValidateStoredColor(emitter.StartColor, path + ".startColor");
        ValidateStoredColor(emitter.MaterialTint, path + ".materialTint");
        ValidateEnum(emitter.ColorSpace, path + ".colorSpace");

        if (!float.IsFinite(emitter.HdrIntensity) || emitter.HdrIntensity < 0f)
        {
            throw new InvalidDataException($"{path}: hdrIntensity must be finite and >= 0, got {emitter.HdrIntensity}.");
        }

        if (!float.IsFinite(emitter.ConeAngleDegrees) || emitter.ConeAngleDegrees < 0f || emitter.ConeAngleDegrees > 180f)
        {
            throw new InvalidDataException($"{path}: coneAngleDegrees must be in [0, 180], got {emitter.ConeAngleDegrees}.");
        }

        for (int i = 0; i < emitter.Bursts.Length; i++)
        {
            ParticleBurstSpec burst = emitter.Bursts[i];
            string burstPath = $"{path}.bursts[{i}]";
            ValidateNonnegative(burst.Time, burstPath + ".time");
            if (burst.Time > emitter.Duration)
                throw new InvalidDataException($"{burstPath}.time: must be <= duration ({emitter.Duration}), got {burst.Time}.");
            if (burst.Count < 0)
                throw new InvalidDataException($"{burstPath}.count: must be >= 0, got {burst.Count}.");
            ValidateRange(burst.Probability, 1f, burstPath + ".probability");
        }

        for (int i = 0; i < emitter.ForceFields.Length; i++)
        {
            ForceFieldSpec field = emitter.ForceFields[i];
            ValidateEnum(field.Kind, $"{path}.forceFields[{i}].kind");

            if (!float.IsFinite(field.Radius) || field.Radius < 0f)
            {
                throw new InvalidDataException($"{path}.forceFields[{i}].radius: must be finite and >= 0, got {field.Radius}.");
            }

            if (!float.IsFinite(field.Strength) || !float.IsFinite(field.Frequency))
            {
                throw new InvalidDataException($"{path}.forceFields[{i}].strength/frequency: must be finite.");
            }

            if (field.Kind == ForceFieldKind.RingWave)
                ValidateRingWave(field, $"{path}.forceFields[{i}]");
        }

        if (emitter.Trails is { } trails)
            ValidateParticleTrail(trails, path + ".trails");
    }

    // Normalized [0,1] stops in strictly increasing time with stored colours.
    private static void ValidateGradient(ColorGradient gradient, string path)
    {
        ColorStop[] stops = gradient.Stops;
        if (stops.Length == 0)
            throw new InvalidDataException($"{path}: at least one stop is required.");
        for (int i = 0; i < stops.Length; i++)
        {
            ColorStop stop = stops[i];
            ValidateTime(stop.Time, i > 0 ? stops[i - 1].Time : -1f, $"{path}[{i}]", normalized: true, allowEqual: false);
            ValidateStoredColor(stop.Rgba, $"{path}[{i}].color");
        }
    }
}

/// <summary>
/// Parses native <c>.lumen.json</c> effect descriptors into <see cref="EffectAsset"/>.
/// </summary>
public static partial class LumenJsonAssetLoader
{
    // Requires: json is non-empty UTF-8 JSON text.
    // Ensures: returns validated EffectAsset; throws InvalidDataException on malformed or invalid fields.
    public static EffectAsset Parse(string json, string? fallbackEffectId = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("Effect asset JSON is empty.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Effect asset JSON is malformed: {error.Message}", error);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Effect asset JSON must be an object, got {root.ValueKind}.");
            }

            var scope = new JsonScope(root, "asset");
            scope.Check(RootProperties);
            string? effectId = scope.String("effectId", null) ?? fallbackEffectId;
            if (string.IsNullOrEmpty(effectId))
            {
                throw new InvalidDataException("Effect asset JSON needs an 'effectId' (or a file name to fall back to).");
            }

            if (!scope.TryGet("nodes", out JsonElement nodesElement) || nodesElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("asset: 'nodes' must be a non-empty array.");
            }

            int nodeCount = nodesElement.GetArrayLength();
            if (nodeCount == 0)
            {
                throw new InvalidDataException("asset: 'nodes' must be a non-empty array.");
            }

            var nodes = new EffectNodeSpec[nodeCount];
            int index = 0;
            foreach (JsonElement nodeElement in nodesElement.EnumerateArray())
            {
                nodes[index] = ParseNode(nodeElement, $"effect '{effectId}'.nodes[{index}]");
                index++;
            }

            var asset = new EffectAsset
            {
                EffectId = effectId,
                DefaultDuration = scope.Float("defaultDuration", 2f),
                Nodes = nodes,
                TimelineEvents = ParseTimelineEvents(scope),
                InteractionEvents = ParseInteractionEvents(scope),
                States = ParseStates(scope),
                InitialState = scope.String("initialState", null),
                StopState = scope.String("stopState", null),
            };
            LumenAssetRules.Validate(asset, $"effect '{effectId}'");
            return asset;
        }
    }

    // Requires: filePath points to an existing .lumen.json file.
    // Ensures: parses file and defaults EffectId from file stem when omitted in JSON.
    public static EffectAsset LoadFile(string filePath, string? packageId = null)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            throw new ArgumentException("filePath must not be null or empty.", nameof(filePath));
        }

        string json;
        try
        {
            json = File.ReadAllText(filePath, Encoding.UTF8);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Could not read effect asset '{filePath}': {error.Message}", error);
        }

        try
        {
            return Parse(json, DeriveEffectId(filePath));
        }
        catch (InvalidDataException error)
        {
            string context = string.IsNullOrEmpty(packageId) ? filePath : $"{filePath} (from {packageId})";
            throw new InvalidDataException($"{context}: {error.Message}", error);
        }
    }

    // Ensures: "…/Data/RimLumen/Spells/Fireball.lumen.json" -> "Spells/Fireball"; a path outside a
    //          Data/RimLumen tree falls back to the bare file stem.
    internal static string DeriveEffectId(string filePath)
    {
        string normalized = filePath.Replace('\\', '/');
        const string marker = "/Data/RimLumen/";
        int markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        string relative = markerIndex >= 0
            ? normalized[(markerIndex + marker.Length)..]
            : normalized[(normalized.LastIndexOf('/') + 1)..];
        return TrimEffectSuffix(relative);
    }

    private static readonly string[] RootProperties =
    {
        "effectId", "defaultDuration", "nodes", "timelineEvents", "interactionEvents", "states", "initialState", "stopState",
    };

    private static readonly string[] StateProperties = { "name", "nodes", "duration", "loop", "next" };

    private static readonly string[] NodeProperties =
    {
        "name", "parentIndex", "spacePlane", "depthMask", "localPosition", "localRotation", "localYawDegrees",
        "localScale", "emitter", "pointLight", "distortion", "ribbon", "screenDim",
    };

    private static readonly string[] EmitterProperties =
    {
        "name", "maxParticles", "duration", "looping", "worldSpaceSimulation", "rateOverTime", "bursts",
        "shape", "shapeExtents", "shapePosition", "shapeRotation", "shapeScale", "coneAngleDegrees",
        "coneLength", "donutRadius", "radiusThickness", "boxThickness", "arcDegrees", "arcSpread", "radiusSpread",
        "randomDirectionAmount", "sphericalDirectionAmount", "randomPositionAmount", "startLifetime", "startLifetimeMin", "startLifetimeMax",
        "startSpeed", "startSpeedMin", "startSpeedMax", "startSize", "startSizeMin", "startSizeMax",
        "startColor", "colorSpace", "materialTint", "sizeOverLifetime", "colorOverLifetime", "forceFields", "textureKey",
        "flipbookTilesX", "flipbookTilesY", "flipbookCycles", "blendMode", "billboardMode",
        "velocityStretchFactor", "lengthScale", "softParticleFadeDistance", "hdrIntensity",
        "preferredTier", "killBelowFloor", "environmentChannels", "environmentForceScale", "collision", "subEmitters", "trails",
    };

    private static readonly string[] PointLightProperties =
    {
        "glowRadius", "color", "intensity", "intensityOverTime",
    };

    private static readonly string[] BurstProperties = { "time", "count", "probability" };

    private static readonly string[] ForceFieldProperties =
    {
        "kind", "centerOrAxis", "parameters", "radius", "strength", "frequency",
    };

    private static readonly string[] TimelineEventProperties =
    {
        "time", "timeSeconds", "eventName", "name", "targetNodeIndex", "state", "notify",
    };

    private static readonly string[] CurveProperties = { "keys", "multiplier" };

    private static readonly string[] CurveKeyProperties = { "time", "value", "inTangent", "outTangent" };

    private static readonly string[] GradientProperties = { "stops" };

    private static readonly string[] GradientStopProperties = { "time", "color" };

    private static EffectNodeSpec ParseNode(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{path}: must be an object, got {element.ValueKind}.");
        }

        var scope = new JsonScope(element, path);
        scope.Check(NodeProperties);
        string name = scope.String("name", null) ?? throw new InvalidDataException($"{path}: 'name' is required.");
        float yaw = scope.Float("localYawDegrees", 0f);
        Quaternion rotation = scope.Has("localRotation")
            ? scope.Rotation("localRotation", Quaternion.Identity)
            : yaw == 0f
                ? Quaternion.Identity
                : Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw * (MathF.PI / 180f));
        return new EffectNodeSpec
        {
            Name = name,
            ParentIndex = scope.Int("parentIndex", -1),
            SpacePlane = scope.Enum("spacePlane", LumenSpacePlane.GroundXZ),
            DepthMask = scope.Enum("depthMask", LumenDepthMask.None),
            LocalPosition = scope.Vector3("localPosition", Vector3.Zero),
            LocalRotation = rotation,
            LocalScale = scope.Vector3("localScale", Vector3.One),
            Emitter = scope.TryGet("emitter", out JsonElement emitterElement) ? ParseEmitter(emitterElement, path + ".emitter", name) : null,
            PointLight = scope.TryGet("pointLight", out JsonElement lightElement) ? ParsePointLight(lightElement, path + ".pointLight") : null,
            Distortion = scope.TryGet("distortion", out JsonElement distortionElement) ? ParseDistortion(distortionElement, path + ".distortion") : null,
            Ribbon = scope.TryGet("ribbon", out JsonElement ribbonElement) ? ParseRibbon(ribbonElement, path + ".ribbon") : null,
            ScreenDim = scope.TryGet("screenDim", out JsonElement dimElement) ? ParseScreenDim(dimElement, path + ".screenDim") : null,
        };
    }

    private static EmitterSpec ParseEmitter(JsonElement element, string path, string nodeName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{path}: must be an object, got {element.ValueKind}.");
        }

        var scope = new JsonScope(element, path);
        scope.Check(EmitterProperties);
        float? lifetime = scope.OptionalFloat("startLifetime");
        float? speed = scope.OptionalFloat("startSpeed");
        float? size = scope.OptionalFloat("startSize");
        string emitterName = scope.String("name", nodeName) ?? nodeName;
        return new EmitterSpec
        {
            Name = emitterName,
            MaxParticles = scope.Int("maxParticles", 1024),
            Duration = scope.Float("duration", 1f),
            Looping = scope.Bool("looping", false),
            WorldSpaceSimulation = scope.Bool("worldSpaceSimulation", true),
            RateOverTime = scope.Float("rateOverTime", 0f),
            Bursts = ParseBursts(scope, path),
            Shape = scope.Enum("shape", LumenShapeKind.Point),
            ShapeExtents = scope.Vector3("shapeExtents", Vector3.One),
            ShapePosition = scope.Vector3("shapePosition", Vector3.Zero),
            ShapeRotation = scope.Rotation("shapeRotation", Quaternion.Identity),
            ShapeScale = scope.Vector3("shapeScale", Vector3.One),
            ConeAngleDegrees = scope.Float("coneAngleDegrees", 25f),
            ConeLength = scope.Float("coneLength", 5f),
            DonutRadius = scope.Float("donutRadius", 0.2f),
            RadiusThickness = scope.Float("radiusThickness", 1f),
            BoxThickness = scope.Vector3("boxThickness", Vector3.Zero),
            ArcDegrees = scope.Float("arcDegrees", 360f),
            ArcSpread = scope.Float("arcSpread", 0f),
            RadiusSpread = scope.Float("radiusSpread", 0f),
            RandomDirectionAmount = scope.Float("randomDirectionAmount", 0f),
            SphericalDirectionAmount = scope.Float("sphericalDirectionAmount", 0f),
            RandomPositionAmount = scope.Float("randomPositionAmount", 0f),
            StartLifetimeMin = lifetime ?? scope.Float("startLifetimeMin", 1f),
            StartLifetimeMax = lifetime ?? scope.Float("startLifetimeMax", 1f),
            StartSpeedMin = speed ?? scope.Float("startSpeedMin", 0f),
            StartSpeedMax = speed ?? scope.Float("startSpeedMax", 0f),
            StartSizeMin = size ?? scope.Float("startSizeMin", 1f),
            StartSizeMax = size ?? scope.Float("startSizeMax", 1f),
            StartColor = scope.ColorRgba("startColor", Vector4.One),
            ColorSpace = scope.Enum("colorSpace", LumenColorSpace.Stored),
            MaterialTint = scope.ColorRgba("materialTint", Vector4.One),
            SizeOverLifetime = scope.TryGet("sizeOverLifetime", out JsonElement sizeElement) ? ParseCurve(sizeElement, path + ".sizeOverLifetime") : ScalarCurve.Constant(1f),
            ColorOverLifetime = scope.TryGet("colorOverLifetime", out JsonElement colorElement) ? ParseGradient(colorElement, path + ".colorOverLifetime") : ColorGradient.Constant(Vector4.One),
            ForceFields = ParseForceFields(scope, path),
            EnvironmentChannels = scope.Enum("environmentChannels", LumenEnvironmentChannel.Explosion | LumenEnvironmentChannel.Cursor),
            EnvironmentForceScale = scope.Float("environmentForceScale", 1f),
            Collision = ParseCollision(scope, path),
            SubEmitters = ParseSubEmitters(scope, path),
            Trails = scope.TryGet("trails", out JsonElement trailsElement) && trailsElement.ValueKind != JsonValueKind.Null
                ? ParseParticleTrail(trailsElement, path + ".trails")
                : null,
            TextureKey = scope.String("textureKey", null),
            FlipbookTilesX = scope.Int("flipbookTilesX", 1),
            FlipbookTilesY = scope.Int("flipbookTilesY", 1),
            FlipbookCycles = scope.Float("flipbookCycles", 1f),
            BlendMode = scope.Enum("blendMode", LumenBlendMode.Additive),
            BillboardMode = scope.Enum("billboardMode", LumenBillboardMode.Camera),
            VelocityStretchFactor = scope.Float("velocityStretchFactor", 0f),
            LengthScale = scope.Float("lengthScale", 1f),
            SoftParticleFadeDistance = scope.Float("softParticleFadeDistance", 0.5f),
            HdrIntensity = scope.Float("hdrIntensity", 1f),
            PreferredTier = scope.Enum("preferredTier", LumenParticleTier.L2_GpuCompute),
            KillBelowFloor = scope.Bool("killBelowFloor", false),
        };
    }

    private static PointLightSpec ParsePointLight(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{path}: must be an object, got {element.ValueKind}.");
        }

        var scope = new JsonScope(element, path);
        scope.Check(PointLightProperties);
        Vector4 color = scope.ColorRgba("color", Vector4.One);
        Vector3 rgb = new(color.X, color.Y, color.Z);

        float intensity = scope.Float("intensity", 1f);
        ScalarCurve? overTime = null;
        if (scope.TryGet("intensityOverTime", out JsonElement curveElement))
        {
            overTime = Multiply(ParseCurve(curveElement, path + ".intensityOverTime"), intensity);
        }
        else if (intensity != 1f)
        {
            overTime = ScalarCurve.Constant(intensity);
        }

        return new PointLightSpec(scope.Float("glowRadius", 5f), rgb, overTime);
    }

    private static ScalarCurve Multiply(ScalarCurve curve, float factor) =>
        factor == 1f ? curve : new ScalarCurve(curve.Keys, curve.Multiplier * factor);

    private static ParticleBurstSpec[] ParseBursts(JsonScope scope, string path)
    {
        if (!scope.TryGet("bursts", out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<ParticleBurstSpec>();
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{path}.bursts: must be an array, got {element.ValueKind}.");
        }

        var bursts = new List<ParticleBurstSpec>(element.GetArrayLength());
        int index = 0;
        foreach (JsonElement burstElement in element.EnumerateArray())
        {
            string burstPath = $"{path}.bursts[{index++}]";
            if (burstElement.ValueKind == JsonValueKind.Array)
            {
                float time = NumberAt(burstElement, 0, burstPath);
                int count = (int)NumberAt(burstElement, 1, burstPath);
                float probability = burstElement.GetArrayLength() > 2 ? NumberAt(burstElement, 2, burstPath) : 1f;
                bursts.Add(new ParticleBurstSpec(time, count, probability));
                continue;
            }

            if (burstElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{burstPath}: expected an object or a [time, count] pair.");
            }

            var burst = new JsonScope(burstElement, burstPath);
            burst.Check(BurstProperties);
            float burstTime = burst.Float("time", 0f);
            bursts.Add(new ParticleBurstSpec(burstTime, burst.Int("count", 1), burst.Float("probability", 1f)));
        }

        return bursts.ToArray();
    }

    private static ForceFieldSpec[] ParseForceFields(JsonScope scope, string path)
    {
        if (!scope.TryGet("forceFields", out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<ForceFieldSpec>();
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{path}.forceFields: must be an array, got {element.ValueKind}.");
        }

        var fields = new ForceFieldSpec[element.GetArrayLength()];
        int index = 0;
        foreach (JsonElement fieldElement in element.EnumerateArray())
        {
            string fieldPath = $"{path}.forceFields[{index}]";
            if (fieldElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{fieldPath}: must be an object, got {fieldElement.ValueKind}.");
            }

            var field = new JsonScope(fieldElement, fieldPath);
            field.Check(ForceFieldProperties);
            if (!field.Has("kind"))
            {
                throw new InvalidDataException($"{fieldPath}: 'kind' is required.");
            }

            fields[index] = new ForceFieldSpec(
                field.Enum("kind", ForceFieldKind.GravityDrag),
                field.Vector3("centerOrAxis", Vector3.Zero),
                field.Vector3("parameters", Vector3.Zero),
                field.Float("radius", 5f),
                field.OptionalFloat("strength") ?? throw new InvalidDataException($"{fieldPath}.strength: required."),
                field.Float("frequency", 1f));
            index++;
        }

        return fields;
    }

    private static TimelineEventSpec[] ParseTimelineEvents(JsonScope scope)
    {
        if (!scope.TryGet("timelineEvents", out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<TimelineEventSpec>();
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"asset.timelineEvents: must be an array, got {element.ValueKind}.");
        }

        var events = new TimelineEventSpec[element.GetArrayLength()];
        int index = 0;
        foreach (JsonElement eventElement in element.EnumerateArray())
        {
            string eventPath = $"asset.timelineEvents[{index}]";
            if (eventElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{eventPath}: must be an object, got {eventElement.ValueKind}.");
            }

            var timelineEvent = new JsonScope(eventElement, eventPath);
            timelineEvent.Check(TimelineEventProperties);
            string? name = timelineEvent.String("eventName", null) ?? timelineEvent.String("name", null);
            if (string.IsNullOrEmpty(name))
            {
                throw new InvalidDataException($"{eventPath}: 'eventName' is required.");
            }

            float time = timelineEvent.Has("time") ? timelineEvent.Float("time", 0f) : timelineEvent.Float("timeSeconds", 0f);
            // Target indices and state names are checked by LumenAssetRules once the whole asset exists.
            events[index++] = new TimelineEventSpec(
                time,
                name,
                timelineEvent.Int("targetNodeIndex", -1),
                timelineEvent.String("state", null),
                timelineEvent.Bool("notify", false));
        }

        return events;
    }

    private static EffectStateSpec[] ParseStates(JsonScope scope)
    {
        if (!scope.TryGet("states", out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<EffectStateSpec>();
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"asset.states: must be an array, got {element.ValueKind}.");
        }

        var states = new EffectStateSpec[element.GetArrayLength()];
        int index = 0;
        foreach (JsonElement stateElement in element.EnumerateArray())
        {
            string statePath = $"asset.states[{index}]";
            if (stateElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{statePath}: must be an object, got {stateElement.ValueKind}.");
            }

            var state = new JsonScope(stateElement, statePath);
            state.Check(StateProperties);
            states[index++] = new EffectStateSpec
            {
                Name = state.String("name", null) ?? throw new InvalidDataException($"{statePath}: 'name' is required."),
                Nodes = ParseNames(state, "nodes", statePath),
                Duration = state.OptionalFloat("duration"),
                Loop = state.Bool("loop", false),
                Next = state.String("next", null),
            };
        }

        return states;
    }

    private static string[] ParseNames(JsonScope scope, string name, string path)
    {
        if (!scope.TryGet(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<string>();
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{path}.{name}: must be an array of strings, got {element.ValueKind}.");
        }

        var names = new string[element.GetArrayLength()];
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            names[index] = item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw new InvalidDataException($"{path}.{name}[{index}]: expected a string, got {item.ValueKind}.");
            index++;
        }

        return names;
    }

    private static ScalarCurve ParseCurve(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return ScalarCurve.Constant(NumberValue(element, path));
            case JsonValueKind.Array:
            {
                CurveKey[] keys = ParseCurveKeys(element, path);
                return new ScalarCurve(keys);
            }

            case JsonValueKind.Object:
            {
                var scope = new JsonScope(element, path);
                scope.Check(CurveProperties);
                if (!scope.TryGet("keys", out JsonElement keysElement) || keysElement.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException($"{path}: a curve object needs a 'keys' array.");
                }

                return new ScalarCurve(ParseCurveKeys(keysElement, path + ".keys"), scope.Float("multiplier", 1f));
            }

            default:
                throw new InvalidDataException($"{path}: expected a number, a key array, or a {{ keys, multiplier }} object.");
        }
    }

    private static CurveKey[] ParseCurveKeys(JsonElement element, string path)
    {
        int length = element.GetArrayLength();
        if (length == 0)
        {
            throw new InvalidDataException($"{path}: a curve needs at least one key.");
        }

        var keys = new CurveKey[length];
        int index = 0;
        foreach (JsonElement keyElement in element.EnumerateArray())
        {
            string keyPath = $"{path}[{index}]";
            CurveKey key;
            if (keyElement.ValueKind == JsonValueKind.Array)
            {
                key = new CurveKey(
                    NumberAt(keyElement, 0, keyPath),
                    NumberAt(keyElement, 1, keyPath),
                    OptionalNumberAt(keyElement, 2),
                    OptionalNumberAt(keyElement, 3));
            }
            else if (keyElement.ValueKind == JsonValueKind.Object)
            {
                var scope = new JsonScope(keyElement, keyPath);
                scope.Check(CurveKeyProperties);
                key = new CurveKey(
                    scope.Float("time", 0f),
                    scope.Float("value", 1f),
                    scope.Float("inTangent", 0f),
                    scope.Float("outTangent", 0f));
            }
            else
            {
                throw new InvalidDataException($"{keyPath}: expected [time, value] or {{ time, value }}.");
            }

            keys[index++] = key;
        }

        return keys;
    }

    private static ColorGradient ParseGradient(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                return new ColorGradient(ParseGradientStops(element, path));
            case JsonValueKind.Object:
            {
                var scope = new JsonScope(element, path);
                scope.Check(GradientProperties);
                if (!scope.TryGet("stops", out JsonElement stopsElement) || stopsElement.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException($"{path}: a gradient object needs a 'stops' array.");
                }

                return new ColorGradient(ParseGradientStops(stopsElement, path + ".stops"));
            }

            default:
                throw new InvalidDataException($"{path}: expected a stop array or a {{ stops }} object.");
        }
    }

    private static ColorStop[] ParseGradientStops(JsonElement element, string path)
    {
        int length = element.GetArrayLength();
        if (length == 0)
        {
            throw new InvalidDataException($"{path}: a gradient needs at least one stop.");
        }

        var stops = new ColorStop[length];
        int index = 0;
        foreach (JsonElement stopElement in element.EnumerateArray())
        {
            string stopPath = $"{path}[{index}]";
            ColorStop stop;
            if (stopElement.ValueKind == JsonValueKind.Array)
            {
                float time = NumberAt(stopElement, 0, stopPath);
                if (stopElement.GetArrayLength() < 2)
                {
                    throw new InvalidDataException($"{stopPath}: expected [time, [r, g, b, a]].");
                }

                stop = new ColorStop(time, JsonScope.ReadColor(stopElement[1], stopPath));
            }
            else if (stopElement.ValueKind == JsonValueKind.Object)
            {
                var scope = new JsonScope(stopElement, stopPath);
                scope.Check(GradientStopProperties);
                Vector4 rgba = scope.ColorRgba("color", Vector4.One);
                stop = new ColorStop(scope.Float("time", 0f), rgba);
            }
            else
            {
                throw new InvalidDataException($"{stopPath}: expected [time, color] or {{ time, color }}.");
            }

            stops[index++] = stop;
        }

        return stops;
    }

    private static string TrimEffectSuffix(string name)
    {
        const string lumenSuffix = ".lumen.json";
        if (name.EndsWith(lumenSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return name[..^lumenSuffix.Length];
        }

        return name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name[..^".json".Length] : name;
    }

    private static float NumberValue(JsonElement element, string path) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out float value) && float.IsFinite(value)
            ? value
            : throw new InvalidDataException($"{path}: expected a finite number, got {element.ValueKind} '{element}'.");

    private static float NumberAt(JsonElement array, int index, string path)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() <= index)
        {
            throw new InvalidDataException($"{path}: expected at least {index + 1} numbers.");
        }

        return NumberValue(array[index], $"{path}[{index}]");
    }

    private static float OptionalNumberAt(JsonElement array, int index)
        => array.GetArrayLength() > index ? NumberValue(array[index], $"[{index}]") : 0f;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>Typed reader over one JSON object; every failure names the JSON path it came from.</summary>
    private readonly struct JsonScope
    {
        private readonly JsonElement element;
        private readonly string path;

        public JsonScope(JsonElement element, string path)
        {
            this.element = element;
            this.path = path;
        }

        public bool Has(string name) => TryGetProperty(element, name, out _);

        public bool TryGet(string name, out JsonElement value) => TryGetProperty(element, name, out value);

        // Ensures: throws when the object carries a property outside <paramref name="allowed"/> (typo guard),
        //          ignoring "$"/"_"-prefixed documentation keys.
        public void Check(string[] allowed)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string name = property.Name;
                if (name.Length > 0 && (name[0] == '$' || name[0] == '_'))
                {
                    continue;
                }

                bool known = false;
                for (int i = 0; i < allowed.Length; i++)
                {
                    if (string.Equals(allowed[i], name, StringComparison.OrdinalIgnoreCase))
                    {
                        known = true;
                        break;
                    }
                }

                if (!known)
                {
                    if (name.Equals("bloomWeight", StringComparison.OrdinalIgnoreCase) || name.Equals("hdrMultiplier", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"{path}: '{name}' was removed.");
                    string? renamed = name.ToLowerInvariant() switch
                    {
                        "startcolorlinear" => "startColor",
                        "rgbalinear" or "colorlinear" or "colorrgb" => "color",
                        _ => null,
                    };
                    if (renamed is not null)
                        throw new InvalidDataException($"{path}: '{name}' renamed to {renamed}.");
                    throw new InvalidDataException($"{path}: unknown property '{name}' (known: {string.Join(", ", allowed)}).");
                }
            }
        }

        public float Float(string name, float fallback)
        {
            if (!TryGetProperty(element, name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                return fallback;
            }

            return NumberValue(value, $"{path}.{name}");
        }

        public float? OptionalFloat(string name)
            => TryGetProperty(element, name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
                ? NumberValue(value, $"{path}.{name}")
                : null;

        public int Int(string name, int fallback)
        {
            if (!TryGetProperty(element, name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                return fallback;
            }

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number))
            {
                throw new InvalidDataException($"{path}.{name}: expected an integer, got {value.ValueKind} '{value}'.");
            }

            return number;
        }

        public bool Bool(string name, bool fallback)
        {
            if (!TryGetProperty(element, name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                return fallback;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new InvalidDataException($"{path}.{name}: expected true/false, got {value.ValueKind} '{value}'."),
            };
        }

        public string? String(string name, string? fallback)
        {
            if (!TryGetProperty(element, name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                return fallback;
            }

            return value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : throw new InvalidDataException($"{path}.{name}: expected a string, got {value.ValueKind} '{value}'.");
        }

        // Parses a member name or representable integer; LumenAssetRules checks defined values once.
        public TEnum Enum<TEnum>(string name, TEnum fallback) where TEnum : struct, System.Enum
        {
            if (!TryGetProperty(element, name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                return fallback;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _) &&
                System.Enum.TryParse(value.GetRawText(), out TEnum fromNumber))
            {
                return fromNumber;
            }

            if (value.ValueKind == JsonValueKind.String && System.Enum.TryParse(value.GetString(), ignoreCase: true, out TEnum parsed))
            {
                return parsed;
            }

            throw new InvalidDataException($"{path}.{name}: expected a {typeof(TEnum).Name} name or integer, got '{value}' (known: {string.Join(", ", System.Enum.GetNames(typeof(TEnum)))}).");
        }

        // Ensures: [x, y, z], { x, y, z }, or a single uniform number.
        public Vector3 Vector3(string name, Vector3 fallback)
        {
            if (!TryGetProperty(element, name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                return fallback;
            }

            string valuePath = $"{path}.{name}";
            if (value.ValueKind == JsonValueKind.Number)
            {
                float uniform = NumberValue(value, valuePath);
                return new Vector3(uniform, uniform, uniform);
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                return new Vector3(
                    NumberAt(value, 0, valuePath),
                    NumberAt(value, 1, valuePath),
                    NumberAt(value, 2, valuePath));
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                var scope = new JsonScope(value, valuePath);
                scope.Check(VectorComponents);
                return new Vector3(scope.Float("x", 0f), scope.Float("y", 0f), scope.Float("z", 0f));
            }

            throw new InvalidDataException($"{valuePath}: expected [x, y, z], {{ x, y, z }}, or a number, got {value.ValueKind}.");
        }

        public Vector4 ColorRgba(string name, Vector4 fallback)
            => TryGetProperty(element, name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
                ? ReadColor(value, $"{path}.{name}")
                : fallback;

        public Quaternion Rotation(string name, Quaternion fallback)
        {
            if (!TryGetProperty(element, name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                return fallback;
            }

            string valuePath = $"{path}.{name}";
            Quaternion result = value.ValueKind switch
            {
                JsonValueKind.Array => new Quaternion(
                    NumberAt(value, 0, valuePath),
                    NumberAt(value, 1, valuePath),
                    NumberAt(value, 2, valuePath),
                    NumberAt(value, 3, valuePath)),
                JsonValueKind.Object => ObjectQuaternion(new JsonScope(value, valuePath)),
                _ => throw new InvalidDataException($"{valuePath}: expected [x, y, z, w] or {{ x, y, z, w }}, got {value.ValueKind}."),
            };
            return LumenAssetRules.NormalizeRotation(result, valuePath);
        }

        private static Quaternion ObjectQuaternion(JsonScope scope)
        {
            scope.Check(QuaternionComponents);
            return new Quaternion(scope.Float("x", 0f), scope.Float("y", 0f), scope.Float("z", 0f), scope.Float("w", 1f));
        }

        // Ensures: [r, g, b] (alpha 1) or [r, g, b, a], or { r, g, b, a }.
        public static Vector4 ReadColor(JsonElement value, string path)
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                int length = value.GetArrayLength();
                if (length < 3)
                {
                    throw new InvalidDataException($"{path}: expected [r, g, b] or [r, g, b, a].");
                }

                return new Vector4(
                    NumberAt(value, 0, path),
                    NumberAt(value, 1, path),
                    NumberAt(value, 2, path),
                    length > 3 ? NumberAt(value, 3, path) : 1f);
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                var scope = new JsonScope(value, path);
                scope.Check(ColorComponents);
                return new Vector4(
                    scope.Float("r", 1f),
                    scope.Float("g", 1f),
                    scope.Float("b", 1f),
                    scope.Float("a", 1f));
            }

            throw new InvalidDataException($"{path}: expected [r, g, b, a] or {{ r, g, b, a }}, got {value.ValueKind}.");
        }
    }

    private static readonly string[] VectorComponents = { "x", "y", "z" };
    private static readonly string[] QuaternionComponents = { "x", "y", "z", "w" };
    private static readonly string[] ColorComponents = { "r", "g", "b", "a" };
}

internal readonly record struct LumenScanRoot(string PackageId, string DataDirectory);

internal readonly record struct LumenAliasChange(string EffectId, string? OldSource, string? NewSource);

internal readonly record struct LumenAssetReloadResult(int Loaded, int Failed, IReadOnlyList<LumenAliasChange> AliasChanges);

/// <summary>
/// Discovers native RimLumen assets across loaded mods (<c>DefDatabase&lt;LumenEffectDef&gt;</c> and <c>1.7/Data/RimLumen/**/*.lumen.json</c>).
/// </summary>
public static class LumenModAssetScanner
{
    // Requires: catalog is non-null.
    // Ensures: registers both "<packageId>:<effectId>" and bare "<effectId>" (first-writer or overwrite documented) into catalog; returns registered asset count.
    public static int ScanDirectory(InMemoryAssetCatalog catalog, string packageId, string dataDirectory)
        => ScanDirectory(catalog, packageId, dataDirectory, onError: null);

    // Ensures: same as the public overload, but a malformed file is reported to onError and skipped instead of
    //          aborting the whole mod; onError == null lets InvalidDataException propagate (unit tests, fail-loud).
    internal static int ScanDirectory(InMemoryAssetCatalog catalog, string packageId, string dataDirectory, Action<string, Exception>? onError)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrEmpty(dataDirectory) || !Directory.Exists(dataDirectory))
        {
            return 0;
        }

        string[] files = Directory.GetFiles(dataDirectory, "*.json", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        int registered = 0;
        foreach (string file in files)
        {
            string relativeId = RelativeEffectId(dataDirectory, file);
            if (relativeId.Length == 0)
            {
                continue;
            }

            EffectAsset asset;
            try
            {
                asset = LumenJsonAssetLoader.LoadFile(file, packageId);
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                if (onError is null)
                {
                    throw;
                }

                onError(file, error);
                continue;
            }

            RegisterAliases(catalog, packageId, relativeId, asset, $"{packageId}:{Path.GetFullPath(file)}");
            registered++;
        }

        return registered;
    }

    // Ensures: scans all running mods' 1.7/Data/RimLumen directories plus DefDatabase<LumenEffectDef> into catalog.
    public static int ScanRunningMods(InMemoryAssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var counts = ScanSources(catalog, RunningModRoots(), LoadedEffectDefs());
        return counts.JsonLoaded + counts.DefsLoaded;
    }

    // Ensures: scans roots in order, then already-loaded Defs; JsonLoaded counts JSON files only.
    internal static (int JsonLoaded, int DefsLoaded) ScanSources(InMemoryAssetCatalog catalog,
        IReadOnlyList<LumenScanRoot> roots, IReadOnlyList<LumenEffectDef> loadedDefs,
        Action<string, string, Exception>? onJsonError = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        int jsonLoaded = 0;
        foreach (LumenScanRoot root in roots)
        {
            string packageId = root.PackageId;
            jsonLoaded += ScanDirectory(catalog, packageId, root.DataDirectory,
                (file, error) => (onJsonError ?? DefaultJsonError)(packageId, file, error));
        }

        int defsLoaded = 0;
        foreach (LumenEffectDef def in loadedDefs)
        {
            if (def is null)
            {
                continue;
            }

            string packageId = def.modContentPack?.PackageIdPlayerFacing ?? "unknown";
            try
            {
                EffectAsset asset = def.ToEffectAsset(packageId);
                RegisterAliases(catalog, packageId, asset.EffectId, asset, $"{packageId}:LumenEffectDef/{def.defName}");
                defsLoaded++;
            }
            catch (InvalidDataException error)
            {
                DefaultDefError(packageId, def.defName, error);
            }
        }

        return (jsonLoaded, defsLoaded);
    }

    // Ensures: explicit reload of the current JSON sources and already-loaded Defs; Loaded/Failed count JSON files.
    internal static LumenAssetReloadResult ReloadRunningModAssets(InMemoryAssetCatalog catalog)
    {
        LumenAssetReloadResult result = ReloadEffectAssets(catalog, RunningModRoots(), LoadedEffectDefs());
        foreach (LumenAliasChange change in result.AliasChanges)
        {
            Log.Warning($"[RimLumen] Bare effect id '{change.EffectId}' reload winner changed from " +
                $"{change.OldSource ?? "none"} to {change.NewSource ?? "none"}.");
        }

        return result;
    }

    internal static LumenAssetReloadResult ReloadEffectAssets(InMemoryAssetCatalog catalog,
        IReadOnlyList<LumenScanRoot> roots, IReadOnlyList<LumenEffectDef> loadedDefs)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var rebuilt = new InMemoryAssetCatalog();
        int failed = 0;
        var counts = ScanSources(rebuilt, roots, loadedDefs,
            onJsonError: (packageId, file, error) =>
            {
                failed++;
                DefaultJsonError(packageId, file, error);
            });
        LumenScannedAsset[] fresh = rebuilt.GetScannedSnapshot();
        var freshByKey = fresh.ToDictionary(entry => entry.EffectId, StringComparer.Ordinal);
        LumenScannedAsset[] previous = catalog.GetScannedSnapshot();
        var changes = new List<LumenAliasChange>();
        foreach (LumenScannedAsset old in previous.OrderBy(entry => entry.EffectId, StringComparer.Ordinal))
        {
            if (old.EffectId.Contains(':')) continue;
            string? newSource = freshByKey.TryGetValue(old.EffectId, out LumenScannedAsset next) ? next.Source : null;
            if (!string.Equals(old.Source, newSource, StringComparison.Ordinal))
            {
                changes.Add(new LumenAliasChange(old.EffectId, old.Source, newSource));
            }
        }

        foreach (LumenScannedAsset entry in freshByKey.Values.OrderBy(entry => entry.EffectId, StringComparer.Ordinal))
        {
            catalog.RegisterScanned(entry.Asset, entry.Source);
        }

        foreach (LumenScannedAsset old in previous)
        {
            if (!freshByKey.ContainsKey(old.EffectId)) catalog.Unregister(old.EffectId);
        }

        return new LumenAssetReloadResult(counts.JsonLoaded, failed, changes);
    }

    private static List<LumenScanRoot> RunningModRoots()
    {
        var roots = new List<LumenScanRoot>();
        foreach (ModContentPack mod in LoadedModManager.RunningMods)
        {
            string packageId = string.IsNullOrEmpty(mod.PackageIdPlayerFacing) ? mod.Name : mod.PackageIdPlayerFacing;
            roots.Add(new LumenScanRoot(packageId, Path.Combine(mod.RootDir, "1.7", "Data", "RimLumen")));
            roots.Add(new LumenScanRoot(packageId, Path.Combine(mod.RootDir, "Data", "RimLumen")));
        }

        return roots;
    }

    private static IReadOnlyList<LumenEffectDef> LoadedEffectDefs() => DefDatabase<LumenEffectDef>.AllDefsListForReading;

    private static void DefaultJsonError(string packageId, string file, Exception error) =>
        Log.Error($"[RimLumen] Failed to load effect asset '{file}' from {packageId}: {error.Message}");

    private static void DefaultDefError(string packageId, string defName, Exception error) =>
        Log.Error($"[RimLumen] Failed to load effect def '{defName}' from {packageId}: {error.Message}");

    // Ensures: registers "<packageId>:<id>" plus the bare "<id>" short name for the asset's own id and for the
    //          file-relative id. Canonical (package-qualified) keys overwrite, so a mod rescan refreshes its own
    //          assets; bare short names (the asset's own id and the file-relative id) are first-writer-wins so a mod cannot
    //          silently shadow another mod's id. Every key records its source.
    private static void RegisterAliases(InMemoryAssetCatalog catalog, string? packageId, string relativeId, EffectAsset asset, string source)
    {
        if (!catalog.TryResolveEffect(asset.EffectId, out _))
        {
            catalog.RegisterScanned(asset, source);
        }

        if (!string.IsNullOrEmpty(packageId))
        {
            catalog.RegisterScanned(Alias(asset, $"{packageId}:{asset.EffectId}"), source);
        }

        if (!string.Equals(asset.EffectId, relativeId, StringComparison.Ordinal))
        {
            if (!catalog.TryResolveEffect(relativeId, out _))
            {
                catalog.RegisterScanned(Alias(asset, relativeId), source);
            }

            if (!string.IsNullOrEmpty(packageId))
            {
                catalog.RegisterScanned(Alias(asset, $"{packageId}:{relativeId}"), source);
            }
        }
    }

    private static readonly string[] EffectSuffixes = { ".lumen.json", ".json" };

    // Ensures: a copy registered under effectId that reuses the asset's compiled state graph.
    private static EffectAsset Alias(EffectAsset asset, string effectId)
    {
        var alias = new EffectAsset
        {
            EffectId = effectId,
            DefaultDuration = asset.DefaultDuration,
            Nodes = asset.Nodes,
            TimelineEvents = asset.TimelineEvents,
            InteractionEvents = asset.InteractionEvents,
            States = asset.States,
            InitialState = asset.InitialState,
            StopState = asset.StopState,
        };
        EffectStateGraph.ShareWithAlias(asset, alias);
        return alias;
    }

    private static string RelativeEffectId(string dataDirectory, string file)
    {
        string relative = Path.GetRelativePath(dataDirectory, file).Replace('\\', '/');
        foreach (string suffix in EffectSuffixes)
        {
            if (relative.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return relative[..^suffix.Length];
            }
        }

        return relative;
    }
}
