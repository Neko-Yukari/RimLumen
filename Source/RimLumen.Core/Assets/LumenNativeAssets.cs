using System;
using System.Collections.Generic;
using System.IO;
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
public sealed class LumenEffectDef : Def
{
    public float defaultDuration = 2f;
    public List<LumenNodeXml>? nodes;
    public List<LumenStateXml>? states;
    public string? initialState;
    public string? stopState;
    public List<LumenTimelineEventXml>? timelineEvents;

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
            specs[i] = new EffectNodeSpec
            {
                Name = nodeName,
                ParentIndex = node.parentIndex,
                SpacePlane = node.spacePlane,
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
            };
        }

        var asset = new EffectAsset
        {
            EffectId = defName,
            DefaultDuration = defaultDuration,
            Nodes = specs,
            TimelineEvents = ToTimelineEvents(timelineEvents, source),
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
            ConeAngleDegrees = xml.coneAngleDegrees,
            StartLifetimeMin = xml.startLifetimeMin,
            StartLifetimeMax = xml.startLifetimeMax,
            StartSpeedMin = xml.startSpeedMin,
            StartSpeedMax = xml.startSpeedMax,
            StartSizeMin = xml.startSizeMin,
            StartSizeMax = xml.startSizeMax,
            StartColorLinear = ToVector4(xml.startColorLinear),
            SizeOverLifetime = xml.sizeOverLifetime is { Count: > 0 }
                ? new ScalarCurve(ToCurveKeys(xml.sizeOverLifetime, path + ".sizeOverLifetime"))
                : ScalarCurve.Constant(1f),
            ColorOverLifetime = xml.colorOverLifetime is { Count: > 0 }
                ? new ColorGradient(ToColorStops(xml.colorOverLifetime, path + ".colorOverLifetime"))
                : ColorGradient.Constant(Vector4.One),
            ForceFields = ToForceFields(xml.forceFields, path),
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
            BloomWeight = xml.bloomWeight,
            PreferredTier = xml.preferredTier,
            KillBelowFloor = xml.killBelowFloor,
        };
        return emitter;
    }

    private static PointLightSpec ToPointLightSpec(LumenPointLightXml xml, string path)
    {
        // PointLightSpec carries no intensity column: the map glower driver multiplies the colour by
        // Evaluate(IntensityOverTime) * runtime multiplier, so the authored intensity rides on the curve's multiplier.
        ScalarCurve? intensity;
        if (xml.intensityOverTime is { Count: > 0 })
        {
            intensity = new ScalarCurve(ToCurveKeys(xml.intensityOverTime, path + ".pointLight.intensityOverTime"), xml.intensity);
        }
        else
        {
            intensity = xml.intensity == 1f ? null : ScalarCurve.Constant(xml.intensity);
        }

        return new PointLightSpec(
            xml.radiusCells,
            xml.overlightRadius,
            new Vector3(xml.colorLinear.r, xml.colorLinear.g, xml.colorLinear.b),
            intensity);
    }

    private static (float Time, int Count)[] ToBursts(List<LumenBurstXml>? bursts, string path)
    {
        if (bursts is null || bursts.Count == 0)
        {
            return Array.Empty<(float, int)>();
        }

        var result = new (float Time, int Count)[bursts.Count];
        for (int i = 0; i < bursts.Count; i++)
        {
            LumenBurstXml burst = bursts[i] ?? throw new InvalidDataException($"{path}.bursts[{i}] is null.");
            if (!float.IsFinite(burst.time) || burst.time < 0f)
            {
                throw new InvalidDataException($"{path}.bursts[{i}]: time must be finite and >= 0, got {burst.time}.");
            }

            if (burst.count < 0)
            {
                throw new InvalidDataException($"{path}.bursts[{i}]: count must be >= 0, got {burst.count}.");
            }

            result[i] = (burst.time, burst.count);
        }

        return result;
    }

    private static CurveKey[] ToCurveKeys(List<LumenCurveKeyXml> keys, string path)
    {
        var result = new CurveKey[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            LumenCurveKeyXml key = keys[i] ?? throw new InvalidDataException($"{path}[{i}] is null.");
            if (i > 0 && key.time < keys[i - 1].time)
            {
                throw new InvalidDataException($"{path}: keys must be ordered by non-decreasing time; key {i} ({key.time}) follows {keys[i - 1].time}.");
            }

            result[i] = new CurveKey(key.time, key.value);
        }

        return result;
    }

    private static ColorStop[] ToColorStops(List<LumenGradientStopXml> stops, string path)
    {
        var result = new ColorStop[stops.Count];
        for (int i = 0; i < stops.Count; i++)
        {
            LumenGradientStopXml stop = stops[i] ?? throw new InvalidDataException($"{path}[{i}] is null.");
            if (i > 0 && stop.time < stops[i - 1].time)
            {
                throw new InvalidDataException($"{path}: stops must be ordered by non-decreasing time; stop {i} ({stop.time}) follows {stops[i - 1].time}.");
            }

            result[i] = new ColorStop(stop.time, ToVector4(stop.rgbaLinear));
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
                field.strength,
                field.frequency);
        }

        return result;
    }

    private static Vector4 ToVector4(UnityEngine.Color color) => new(color.r, color.g, color.b, color.a);
}

public sealed class LumenNodeXml
{
    public string? name;
    public int parentIndex = -1;
    public LumenSpacePlane spacePlane = LumenSpacePlane.GroundXZ;
    public UnityEngine.Vector3 localPosition;
    public float localYawDegrees;
    public float localScale = 1f;
    public float heightOffset;
    public LumenEmitterXml? emitter;
    public LumenPointLightXml? pointLight;
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
    public UnityEngine.Color startColorLinear = UnityEngine.Color.white;
    public float hdrIntensity = 1f;
    public float bloomWeight = 1f;
    public LumenShapeKind shape = LumenShapeKind.Point;
    public UnityEngine.Vector3 shapeExtents = UnityEngine.Vector3.one;
    public float coneAngleDegrees = 25f;
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
}

public sealed class LumenCurveKeyXml
{
    public float time;
    public float value = 1f;
}

public sealed class LumenGradientStopXml
{
    public float time;
    public UnityEngine.Color rgbaLinear = UnityEngine.Color.white;
}

public sealed class LumenForceFieldXml
{
    public ForceFieldKind kind;
    public UnityEngine.Vector3 centerOrAxis;
    public UnityEngine.Vector3 parameters;
    public float strength = 1f;
    public float frequency = 1f;
    public float radius = 5f;
}

public sealed class LumenPointLightXml
{
    public UnityEngine.Color colorLinear = UnityEngine.Color.white;
    public float radiusCells = 5f;
    public float overlightRadius;
    public float intensity = 1f;
    public List<LumenCurveKeyXml>? intensityOverTime;
}

/// <summary>
/// Shared structural validation for every authored effect asset (XML Def and <c>.lumen.json</c>).
/// </summary>
internal static class LumenAssetRules
{
    // Requires: asset is non-null.
    // Ensures: throws InvalidDataException naming <paramref name="source"/> when the asset cannot be played:
    //          empty EffectId, no nodes, an empty node name, a parent index that is neither -1 nor an
    //          earlier node (the only cycle-free ordering the runtime accepts), a non-positive particle
    //          budget, a non-positive or inverted lifetime range, a flipbook grid below 1 tile, or a states /
    //          initialState / stopState / timeline-event state reference that EffectStateGraph.For rejects.
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

            if (node.Emitter is { } emitter)
            {
                ValidateEmitter(emitter, $"{path} ('{node.Name}').emitter");
            }

            if (node.PointLight is { } light && (!float.IsFinite(light.GlowRadius) || light.GlowRadius < 0f))
            {
                throw new InvalidDataException($"{path} ('{node.Name}').pointLight: glowRadius must be finite and >= 0, got {light.GlowRadius}.");
            }
        }

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

        EffectStateGraph.For(asset, source);
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

        if (!float.IsFinite(emitter.Duration) || emitter.Duration < 0f)
        {
            throw new InvalidDataException($"{path}: duration must be finite and >= 0, got {emitter.Duration}.");
        }

        if (!float.IsFinite(emitter.RateOverTime) || emitter.RateOverTime < 0f)
        {
            throw new InvalidDataException($"{path}: rateOverTime must be finite and >= 0, got {emitter.RateOverTime}.");
        }

        if (!float.IsFinite(emitter.ConeAngleDegrees) || emitter.ConeAngleDegrees < 0f || emitter.ConeAngleDegrees > 180f)
        {
            throw new InvalidDataException($"{path}: coneAngleDegrees must be in [0, 180], got {emitter.ConeAngleDegrees}.");
        }

        foreach ((float time, int count) in emitter.Bursts)
        {
            if (!float.IsFinite(time) || time < 0f)
            {
                throw new InvalidDataException($"{path}: burst time must be finite and >= 0, got {time}.");
            }

            if (count < 0)
            {
                throw new InvalidDataException($"{path}: burst count must be >= 0, got {count}.");
            }
        }

        foreach (ForceFieldSpec field in emitter.ForceFields)
        {
            if (!float.IsFinite(field.Radius) || field.Radius < 0f)
            {
                throw new InvalidDataException($"{path}: force field radius must be finite and >= 0, got {field.Radius}.");
            }

            if (!float.IsFinite(field.Strength) || !float.IsFinite(field.Frequency))
            {
                throw new InvalidDataException($"{path}: force field strength/frequency must be finite.");
            }
        }
    }
}

/// <summary>
/// Parses native <c>.lumen.json</c> effect descriptors into <see cref="EffectAsset"/>.
/// </summary>
public static class LumenJsonAssetLoader
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
                nodes[index] = ParseNode(nodeElement, $"asset.nodes[{index}]");
                index++;
            }

            var asset = new EffectAsset
            {
                EffectId = effectId,
                DefaultDuration = scope.Float("defaultDuration", 2f),
                Nodes = nodes,
                TimelineEvents = ParseTimelineEvents(scope),
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
        "effectId", "defaultDuration", "nodes", "timelineEvents", "states", "initialState", "stopState",
    };

    private static readonly string[] StateProperties = { "name", "nodes", "duration", "loop", "next" };

    private static readonly string[] NodeProperties =
    {
        "name", "parentIndex", "spacePlane", "localPosition", "localRotation", "localYawDegrees",
        "localScale", "emitter", "pointLight",
    };

    private static readonly string[] EmitterProperties =
    {
        "name", "maxParticles", "duration", "looping", "worldSpaceSimulation", "rateOverTime", "bursts",
        "shape", "shapeExtents", "coneAngleDegrees", "startLifetime", "startLifetimeMin", "startLifetimeMax",
        "startSpeed", "startSpeedMin", "startSpeedMax", "startSize", "startSizeMin", "startSizeMax",
        "startColorLinear", "sizeOverLifetime", "colorOverLifetime", "forceFields", "textureKey",
        "flipbookTilesX", "flipbookTilesY", "flipbookCycles", "blendMode", "billboardMode",
        "velocityStretchFactor", "lengthScale", "softParticleFadeDistance", "hdrIntensity", "bloomWeight",
        "preferredTier", "killBelowFloor",
    };

    private static readonly string[] PointLightProperties =
    {
        "glowRadius", "overlightRadius", "colorRgb", "colorLinear", "intensity", "intensityOverTime",
    };

    private static readonly string[] BurstProperties = { "time", "count" };

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

    private static readonly string[] GradientStopProperties = { "time", "rgbaLinear", "color" };

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
            LocalPosition = scope.Vector3("localPosition", Vector3.Zero),
            LocalRotation = rotation,
            LocalScale = scope.Vector3("localScale", Vector3.One),
            Emitter = scope.TryGet("emitter", out JsonElement emitterElement) ? ParseEmitter(emitterElement, path + ".emitter", name) : null,
            PointLight = scope.TryGet("pointLight", out JsonElement lightElement) ? ParsePointLight(lightElement, path + ".pointLight") : null,
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
            ConeAngleDegrees = scope.Float("coneAngleDegrees", 25f),
            StartLifetimeMin = lifetime ?? scope.Float("startLifetimeMin", 1f),
            StartLifetimeMax = lifetime ?? scope.Float("startLifetimeMax", 1f),
            StartSpeedMin = speed ?? scope.Float("startSpeedMin", 0f),
            StartSpeedMax = speed ?? scope.Float("startSpeedMax", 0f),
            StartSizeMin = size ?? scope.Float("startSizeMin", 1f),
            StartSizeMax = size ?? scope.Float("startSizeMax", 1f),
            StartColorLinear = scope.ColorRgba("startColorLinear", Vector4.One),
            SizeOverLifetime = scope.TryGet("sizeOverLifetime", out JsonElement sizeElement) ? ParseCurve(sizeElement, path + ".sizeOverLifetime") : ScalarCurve.Constant(1f),
            ColorOverLifetime = scope.TryGet("colorOverLifetime", out JsonElement colorElement) ? ParseGradient(colorElement, path + ".colorOverLifetime") : ColorGradient.Constant(Vector4.One),
            ForceFields = ParseForceFields(scope, path),
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
            BloomWeight = scope.Float("bloomWeight", 1f),
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
        Vector3 rgb;
        if (scope.Has("colorRgb"))
        {
            rgb = scope.Vector3("colorRgb", Vector3.One);
        }
        else
        {
            Vector4 linear = scope.ColorRgba("colorLinear", Vector4.One);
            rgb = new Vector3(linear.X, linear.Y, linear.Z);
        }

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

        return new PointLightSpec(scope.Float("glowRadius", 5f), scope.Float("overlightRadius", 0f), rgb, overTime);
    }

    private static ScalarCurve Multiply(ScalarCurve curve, float factor) =>
        factor == 1f ? curve : new ScalarCurve(curve.Keys, curve.Multiplier * factor);

    private static (float Time, int Count)[] ParseBursts(JsonScope scope, string path)
    {
        if (!scope.TryGet("bursts", out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<(float, int)>();
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{path}.bursts: must be an array, got {element.ValueKind}.");
        }

        var bursts = new List<(float, int)>(element.GetArrayLength());
        int index = 0;
        foreach (JsonElement burstElement in element.EnumerateArray())
        {
            string burstPath = $"{path}.bursts[{index++}]";
            if (burstElement.ValueKind == JsonValueKind.Array)
            {
                float time = NumberAt(burstElement, 0, burstPath);
                int count = (int)NumberAt(burstElement, 1, burstPath);
                bursts.Add((time, count));
                continue;
            }

            if (burstElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{burstPath}: expected an object or a [time, count] pair.");
            }

            var burst = new JsonScope(burstElement, burstPath);
            burst.Check(BurstProperties);
            float burstTime = burst.Float("time", 0f);
            bursts.Add((burstTime, burst.Int("count", 1)));
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
                field.Float("strength", 0f),
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

            if (index > 0 && key.Time < keys[index - 1].Time)
            {
                throw new InvalidDataException($"{path}: keys must be ordered by non-decreasing time; key {index} ({key.Time}) follows {keys[index - 1].Time}.");
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
                Vector4 rgba = scope.Has("rgbaLinear")
                    ? scope.ColorRgba("rgbaLinear", Vector4.One)
                    : scope.ColorRgba("color", Vector4.One);
                stop = new ColorStop(scope.Float("time", 0f), rgba);
            }
            else
            {
                throw new InvalidDataException($"{stopPath}: expected [time, color] or {{ time, rgbaLinear }}.");
            }

            if (index > 0 && stop.Time < stops[index - 1].Time)
            {
                throw new InvalidDataException($"{path}: stops must be ordered by non-decreasing time; stop {index} ({stop.Time}) follows {stops[index - 1].Time}.");
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

        // Ensures: accepts an enum member name (case-insensitive) or its integer value.
        public TEnum Enum<TEnum>(string name, TEnum fallback) where TEnum : struct, System.Enum
        {
            if (!TryGetProperty(element, name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                return fallback;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
            {
                var fromNumber = (TEnum)System.Enum.ToObject(typeof(TEnum), number);
                return System.Enum.IsDefined(fromNumber) ? fromNumber : throw new InvalidDataException($"{path}.{name}: {number} is not a valid {typeof(TEnum).Name} value (known: {string.Join(", ", System.Enum.GetNames(typeof(TEnum)))}).");
            }

            if (value.ValueKind == JsonValueKind.String && System.Enum.TryParse(value.GetString(), ignoreCase: true, out TEnum parsed) && System.Enum.IsDefined(parsed))
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
            float lengthSquared = result.LengthSquared();
            if (!(lengthSquared > 1e-8f) || !float.IsFinite(lengthSquared))
            {
                throw new InvalidDataException($"{valuePath}: quaternion {result} is degenerate.");
            }

            return Quaternion.Normalize(result);
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

            RegisterAliases(catalog, packageId, relativeId, asset);
            registered++;
        }

        return registered;
    }

    // Ensures: scans all running mods' 1.7/Data/RimLumen directories plus DefDatabase<LumenEffectDef> into catalog.
    public static int ScanRunningMods(InMemoryAssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        int registered = 0;
        foreach (ModContentPack mod in LoadedModManager.RunningMods)
        {
            string packageId = string.IsNullOrEmpty(mod.PackageIdPlayerFacing) ? mod.Name : mod.PackageIdPlayerFacing;
            Action<string, Exception> report = (file, error) =>
                Verse.Log.Error($"[RimLumen] Failed to load effect asset '{file}' from {packageId}: {error.Message}");
            registered += ScanDirectory(catalog, packageId, Path.Combine(mod.RootDir, "1.7", "Data", "RimLumen"), report);
            registered += ScanDirectory(catalog, packageId, Path.Combine(mod.RootDir, "Data", "RimLumen"), report);
        }

        foreach (LumenEffectDef def in DefDatabase<LumenEffectDef>.AllDefsListForReading)
        {
            if (def is null)
            {
                continue;
            }

            string packageId = def.modContentPack?.PackageIdPlayerFacing ?? "unknown";
            try
            {
                EffectAsset asset = def.ToEffectAsset(packageId);
                RegisterAliases(catalog, packageId, asset.EffectId, asset);
                registered++;
            }
            catch (InvalidDataException error)
            {
                Verse.Log.Error($"[RimLumen] Failed to load effect def '{def.defName}' from {packageId}: {error.Message}");
            }
        }

        return registered;
    }

    // Ensures: registers "<packageId>:<id>" plus the bare "<id>" short name for the asset's own id and for the
    //          file-relative id. Canonical (package-qualified) keys overwrite, so a mod rescan refreshes its own
    //          assets; bare short names (the asset's own id and the file-relative id) are first-writer-wins so a mod cannot
    //          silently shadow another mod's id.
    private static void RegisterAliases(InMemoryAssetCatalog catalog, string? packageId, string relativeId, EffectAsset asset)
    {
        if (!catalog.TryResolveEffect(asset.EffectId, out _))
        {
            catalog.Register(asset);
        }

        if (!string.IsNullOrEmpty(packageId))
        {
            catalog.Register(Alias(asset, $"{packageId}:{asset.EffectId}"));
        }

        if (!string.Equals(asset.EffectId, relativeId, StringComparison.Ordinal))
        {
            if (!catalog.TryResolveEffect(relativeId, out _))
            {
                catalog.Register(Alias(asset, relativeId));
            }

            if (!string.IsNullOrEmpty(packageId))
            {
                catalog.Register(Alias(asset, $"{packageId}:{relativeId}"));
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
