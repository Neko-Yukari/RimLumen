using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimUnity.Serialization;
using static RimLumen.Adapter.RimUnity.RimUnityValueConversion;

namespace RimLumen.Adapter.RimUnity;

public sealed class RimUnityPrefabTranslator
{
    public RimUnityTierClassifier Classifier { get; } = new();

    public EffectAsset Translate(string effectId, RimUnityPackageCatalog catalog, AssetRecord record, out RimUnityTranslationReport report)
    {
        EffectAsset result = Translate(effectId, catalog.ReadPrefab(record), catalog.ReadResource, catalog.ReadShader, out var memoryReport);
        report = new RimUnityTranslationReport
        {
            EffectId = effectId, PackageId = catalog.PackageId, DataDirectory = catalog.DataDirectory,
            LogicalPath = record.path, Prefab = memoryReport.Prefab, Nodes = memoryReport.Nodes,
        };
        return result;
    }

    public EffectAsset Translate(string effectId, PrefabData prefab, Func<string, ResourceData?> resourceResolver,
        Func<string, ShaderData?> shaderResolver, out RimUnityTranslationReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(effectId);
        NodeData[] ordered = OrderNodes(prefab);
        HashSet<string> subEmitterTargets = SubEmitterTargets(ordered);
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var nodes = new EffectNodeSpec[ordered.Length];
        var active = new bool[ordered.Length];
        var classifications = new List<RimUnityNodeTranslation>();
        float duration = 0;
        for (int i = 0; i < ordered.Length; i++)
        {
            NodeData source = ordered[i];
            int parent = source.parent is null ? -1 : indices[source.parent];
            indices.Add(source.id, i);
            active[i] = source.active && (parent < 0 || active[parent]);
            ComponentData? particles = source.components?.SingleOrDefault(component => component.type == "RimUnity.ParticleSystem");
            ComponentData? renderer = source.components?.SingleOrDefault(component => component.type == "RimUnity.ParticleSystemRenderer");
            EmitterSpec? emitter = null;
            if (particles is not null)
            {
                MaterialLook material = ReadMaterial(Member(renderer?.members, "sharedMaterial"), resourceResolver, shaderResolver);
                ValueData? trailMatRef = Member(renderer?.members, "trailMaterial");
                MaterialLook? trailMaterial = Enabled(Member(particles.members, "trails")) && trailMatRef?.kind == "asset"
                    ? ReadMaterial(trailMatRef, resourceResolver, shaderResolver)
                    : null;
                var classification = Classifier.Classify(particles, renderer, out ParticleBurstSpec[] bursts);
                var reasons = new List<string>(classification.Reasons);
                if (material.TintDomainReason is { } tintReason)
                {
                    classification.Tier = LumenParticleTier.External;
                    reasons.RemoveAll(reason => reason.EndsWith("L2ParticleThreshold", StringComparison.Ordinal));
                    reasons.Add(tintReason);
                }
                if (trailMaterial?.TintDomainReason is { } trailTintReason)
                {
                    classification.Tier = LumenParticleTier.External;
                    reasons.RemoveAll(reason => reason.EndsWith("L2ParticleThreshold", StringComparison.Ordinal));
                    reasons.Add(trailTintReason);
                }
                if (subEmitterTargets.Contains(particles.id))
                {
                    classification.Tier = LumenParticleTier.External;
                    reasons.RemoveAll(reason => reason.EndsWith("L2ParticleThreshold", StringComparison.Ordinal));
                    reasons.Add("subEmitters.dependency");
                }
                if (active[i] && particles.enabled && renderer?.enabled != false && classification.Tier != LumenParticleTier.External)
                    emitter = ConvertEmitter(source.name, particles, renderer, classification.Tier, material, trailMaterial, bursts);
                classifications.Add(new RimUnityNodeTranslation(i, source.id, ParticleComponent, classification.Tier, reasons));
                ValueData? main = Member(particles.members, "main");
                // External playback owns deferred lifetime curves; do not evaluate them as native values.
                float maxParticleLife = Integer(Field(Field(main, "startLifetime"), "mode")) == 2 ? 0 : CurveRange(Field(main, "startLifetime"), 5f).Max;
                float extraTrailLife = 0f;
                if (emitter?.Trails is { DieWithParticles: false } trails)
                {
                    float maxSize = CurveRange(Field(main, "startSize"), 1f).Max *
                        (Enabled(Member(particles.members, "sizeOverLifetime")) ? MathF.Max(0f, CurveRange(Field(Member(particles.members, "sizeOverLifetime"), "size"), 1f).Max) : 1f);
                    extraTrailLife = maxParticleLife * trails.Lifetime * (trails.SizeAffectsLifetime ? maxSize : 1f);
                }
                duration = MathF.Max(duration, Number(Field(main, "duration"), 5f) + maxParticleLife + extraTrailLife);
            }
            RibbonSpec? ribbon = null;
            List<ComponentData> components = source.components ?? new List<ComponentData>();
            // A node holds one native ribbon, and external playback finds a renderer by node and type.
            bool multipleRibbons = components.Count(c => c.type is "RimUnity.TrailRenderer" or "RimUnity.LineRenderer") > 1;
            foreach (ComponentData component in components)
            {
                string? kind = component.type switch
                {
                    "RimUnity.TrailRenderer" => TrailComponent, "RimUnity.LineRenderer" => LineComponent, _ => null,
                };
                if (kind is null) continue;
                var reasons = new List<string>();
                if (multipleRibbons) reasons.Add("ribbon.multipleOnNode");
                RibbonSpec? spec = ConvertRibbon(kind, component, resourceResolver, shaderResolver, reasons);
                if (spec is not null && active[i] && component.enabled) ribbon = spec;
                classifications.Add(new RimUnityNodeTranslation(i, source.id, kind,
                    spec is null ? LumenParticleTier.External : LumenParticleTier.L1_NativeCpu, reasons));
            }
            nodes[i] = new EffectNodeSpec
            {
                Name = source.name ?? source.id, ParentIndex = parent,
                // Unity's local quaternion already contains the quad pitch; GroundXZ adds no second rotation.
                SpacePlane = LumenSpacePlane.GroundXZ,
                LocalPosition = new Vector3(source.position[0], source.position[1], source.position[2]),
                LocalRotation = new Quaternion(source.rotation[0], source.rotation[1], source.rotation[2], source.rotation[3]),
                LocalScale = new Vector3(source.scale[0], source.scale[1], source.scale[2]),
                Emitter = emitter, Ribbon = ribbon,
            };
        }
        report = new RimUnityTranslationReport
        {
            EffectId = effectId, PackageId = string.Empty, DataDirectory = string.Empty,
            LogicalPath = effectId, Prefab = prefab, Nodes = classifications,
        };
        // An enabled RimUnity trail/line lives until Stop. Keep the implicit state and extend only the adapter asset's
        // lifetime; emitter schedules still stop finite particle emission on their own durations.
        float defaultDuration = nodes.Any(node => node.Ribbon is not null) ? float.MaxValue : duration > 0 ? duration : 2f;
        return new EffectAsset { EffectId = effectId, DefaultDuration = defaultDuration, Nodes = nodes };
    }

    public const string ParticleComponent = "ParticleSystem", TrailComponent = "TrailRenderer", LineComponent = "LineRenderer";

    // TintDomainReason: set when _Color is outside finite RGB >= 0, alpha in [0,1].
    private readonly record struct MaterialLook(Vector4 Tint, string? TintDomainReason, string? TextureKey,
        LumenBlendMode Blend, float SoftFade, LumenColorSpace ColorSpace);

    private static MaterialLook ReadMaterial(ValueData? materialRef, Func<string, ResourceData?> resourceResolver,
        Func<string, ShaderData?> shaderResolver)
    {
        ResourceData? material = null;
        if (materialRef?.kind == "asset")
        {
            material = resourceResolver(materialRef.text) ?? throw new InvalidDataException("Missing material: " + materialRef.text);
            if (material.type != "Material") throw new InvalidDataException("Renderer asset is not a Material: " + materialRef.text);
        }
        Vector4 tint = Vector4.One;
        ValueData? tintValue = Member(material?.members, "_Color");
        if (tintValue is not null && tintValue.kind != "null")
        {
            float[] values = tintValue.vector;
            if (values is null || values.Length < 3 || values.Length > 4)
                throw new InvalidDataException("Invalid exported material[" + materialRef!.text + "]._Color vector.");
            tint = new Vector4(values[0], values[1], values[2], values.Length == 4 ? values[3] : 1f);
        }
        string? domainReason = !float.IsFinite(tint.X) || tint.X < 0 || !float.IsFinite(tint.Y) || tint.Y < 0 ||
            !float.IsFinite(tint.Z) || tint.Z < 0 || !float.IsFinite(tint.W) || tint.W < 0 || tint.W > 1
            ? "material[" + materialRef!.text + "]._Color.domain" : null;
        ShaderData? shader = null;
        if (material is not null)
        {
            ValueData? shaderRef = Member(material.members, "shader");
            if (shaderRef?.kind == "asset")
                shader = shaderResolver(shaderRef.text) ?? throw new InvalidDataException("Missing shader: " + shaderRef.text);
        }
        LumenBlendMode blend = LumenBlendMode.AlphaBlend;
        if (shader?.blend == true && string.Equals(shader.dstColor, "One", StringComparison.OrdinalIgnoreCase)) blend = LumenBlendMode.Additive;
        else if (shader?.blend == true && string.Equals(shader.srcColor, "One", StringComparison.OrdinalIgnoreCase)) blend = LumenBlendMode.Premultiplied;
        ValueData? texture = Member(material?.members, "_MainTex");
        float softFade = 0.35f;
        ValueData? nearFade = Member(material?.members, "_SoftParticlesNearFadeDistance");
        ValueData? inverseFade = Member(material?.members, "_InvFade");
        if (nearFade is not null) softFade = Number(nearFade);
        else if (inverseFade is not null)
        {
            float inverse = Number(inverseFade);
            softFade = inverse > 0 ? 1f / inverse : 0;
        }
        // RimUnity 45ca90987f7d3721715b45c021cab986ee14c468, Source/Shared/ShaderColorSpace.cs:69-103,122-134:
        // material tint/texture are decoded only by Linear material shading; vertex factors stay stored.
        return new MaterialLook(tint, domainReason, texture?.kind is "asset" or "string" ? texture.text : null, blend, softFade,
            string.Equals(material?.colorSpace, "Linear", StringComparison.OrdinalIgnoreCase) ? LumenColorSpace.Linear : LumenColorSpace.Stored);
    }

    // RimUnity 45ca90987f7d3721715b45c021cab986ee14c468: TrailRenderer.cs:11-18 and LineRenderer.cs:10-19 (fields),
    // TrailRenderer.Geometry.cs:41-86 and TrailRenderer.LineGeometry.cs:12-19 (what the native strip reproduces).
    // Ensures: null with one named reason per option the native ribbon cannot reproduce; otherwise the RibbonSpec.
    private static RibbonSpec? ConvertRibbon(string kind, ComponentData component, Func<string, ResourceData?> resourceResolver,
        Func<string, ShaderData?> shaderResolver, List<string> reasons)
    {
        List<MemberData> members = component.members;
        bool trail = kind == TrailComponent;
        string prefix = trail ? "trail." : "line.";
        if (Integer(Member(members, "numCornerVertices")) != 0) reasons.Add(prefix + "numCornerVertices");
        if (Integer(Member(members, "numCapVertices")) != 0) reasons.Add(prefix + "numCapVertices");
        if (Integer(Member(members, "alignment")) != 0) reasons.Add(prefix + "alignment.TransformZ");
        if (Boolean(Member(members, "generateLightingData"))) reasons.Add(prefix + "generateLightingData");
        int textureModeValue = Integer(Member(members, "textureMode"));
        // With exactly two points RepeatPerSegment's U (the point index) equals Stretch's (normalized length).
        LumenRibbonTextureMode? textureMode = textureModeValue switch
        {
            0 => LumenRibbonTextureMode.Stretch, 1 => LumenRibbonTextureMode.Tile, 3 when !trail => LumenRibbonTextureMode.Stretch, _ => null,
        };
        if (textureMode is null) reasons.Add(prefix + "textureMode." + textureModeValue);
        if (Member(members, "textureScale")?.vector is { } textureScale && (textureScale[0] != 1 || textureScale[1] != 1))
            reasons.Add(prefix + "textureScale");
        float width = Number(Member(members, "widthMultiplier"), 1f);
        if (width <= 0) reasons.Add(prefix + "widthMultiplier.domain");
        ValueData? widthCurve = Member(members, "widthCurve");
        List<ValueData> widthKeys = Field(widthCurve, "keys")?.items ?? new List<ValueData>();
        if (widthCurve is not null && widthKeys.Count == 0) reasons.Add(prefix + "widthCurve.empty");
        if (widthKeys.Any(key => Integer(Field(key, "weightedMode")) != 0)) reasons.Add(prefix + "widthCurve.weighted");
        else if (widthKeys.Any(key => Number(Field(key, "value")) < 0 || Number(Field(key, "time")) is < 0 or > 1))
            reasons.Add(prefix + "widthCurve.domain");
        // AnimationCurve.cs:24-29: Loop (2) and PingPong (4) repeat the keys outside their range; the rest clamp.
        if (Integer(Field(widthCurve, "preWrapMode")) is 2 or 4 || Integer(Field(widthCurve, "postWrapMode")) is 2 or 4)
            reasons.Add(prefix + "widthCurve.wrapMode");
        ValueData? gradient = Member(members, "colorGradient");
        if (Integer(Field(gradient, "mode")) == 2) reasons.Add(prefix + "colorGradient.perceptualBlend");
        List<ValueData> materials = Member(members, "sharedMaterials")?.items ?? new List<ValueData>();
        if (materials.Count != 1) reasons.Add(prefix + "sharedMaterials.count");
        // TrailRenderer.Geometry.cs:26-28 / LineGeometry.cs:25-27: a null material slot draws nothing.
        else if (materials[0].kind != "asset") reasons.Add(prefix + "sharedMaterials." + (materials[0].kind ?? "null"));
        MaterialLook material = materials.Count == 1 ? ReadMaterial(materials[0], resourceResolver, shaderResolver) : default;
        if (material.TintDomainReason is { } tintReason) reasons.Add(tintReason);
        if (material.ColorSpace == LumenColorSpace.Linear) reasons.Add(prefix + "material.colorSpace.Linear");

        float lifetime = 0, minVertexDistance = 0;
        int maxPoints = 0;
        Vector3? end = null;
        if (trail)
        {
            lifetime = Number(Member(members, "time"), 5f);
            minVertexDistance = Number(Member(members, "minVertexDistance"), .1f);
            if (lifetime <= 0) reasons.Add("trail.time.domain");
            if (minVertexDistance < 0) reasons.Add("trail.minVertexDistance.domain");
            if (!Boolean(Member(members, "emitting"), true)) reasons.Add("trail.emitting");
            if (Boolean(Member(members, "autodestruct"))) reasons.Add("trail.autodestruct");
            // Unity keeps every point younger than `time` (at most one per frame); capacity covers 240 frames/s.
            maxPoints = Math.Max(2, (int)MathF.Ceiling(lifetime * 240f));
        }
        else
        {
            List<ValueData> positions = Member(members, "positions")?.items ?? new List<ValueData>();
            if (Boolean(Member(members, "useWorldSpace"), true)) reasons.Add("line.useWorldSpace");
            if (Boolean(Member(members, "loop"))) reasons.Add("line.loop");
            if (positions.Count != 2) reasons.Add("line.positions.count");
            else
            {
                Vector4 start = Vector(positions[0], Vector4.Zero), stop = Vector(positions[1], Vector4.Zero);
                // The beam starts at the node; a line whose first point is elsewhere would need a second node.
                if (start.X != 0 || start.Y != 0 || start.Z != 0) reasons.Add("line.positions.start");
                end = new Vector3(stop.X, stop.Y, stop.Z);
            }
        }
        if (reasons.Count != 0) return null;

        // Stored vertex colour times material tint is the L1 shader's Stored product (ParticleColor.glsl), which Linear
        // blending decodes before the gain; an HDR tint therefore stays in the colour, not in HdrIntensity.
        Vector4 tint = material.Tint;
        return new RibbonSpec
        {
            Mode = trail ? LumenRibbonMode.Trail : LumenRibbonMode.Beam,
            // TrailRenderer.cs:55 samples at no less than 1e-5 cells.
            Lifetime = lifetime, MinVertexDistance = trail ? MathF.Max(1e-5f, minVertexDistance) : 0, MaxPoints = maxPoints,
            EndLocal = end, Segments = trail ? 0 : 1,
            // TrailRenderer.cs:17 / LineRenderer.cs:14: an unserialized widthCurve is AnimationCurve.Constant(0, 1, 1).
            Width = width, WidthOverLength = widthCurve is null ? ScalarCurve.Constant(1) : ToAnimationCurve(widthCurve),
            ColorOverLength = new ColorGradient(ToGradient(gradient).Stops.Select(stop => new ColorStop(stop.Time, stop.Rgba * tint)).ToArray()),
            HdrIntensity = 1f, TextureKey = material.TextureKey, BlendMode = material.Blend, TextureMode = textureMode!.Value,
            Alignment = LumenRibbonAlignment.View, SoftParticleFadeDistance = material.SoftFade,
        };
    }

    private static EmitterSpec ConvertEmitter(string name, ComponentData particles, ComponentData? renderer,
        LumenParticleTier tier, MaterialLook material, MaterialLook? trailMaterial, ParticleBurstSpec[] bursts)
    {
        ValueData? main = Member(particles.members, "main");
        ValueData? emission = Member(particles.members, "emission");
        ValueData? shape = Member(particles.members, "shape");
        ValueData? sheet = Member(particles.members, "textureSheetAnimation");
        ValueData? sizeModule = Member(particles.members, "sizeOverLifetime");
        ValueData? colorModule = Member(particles.members, "colorOverLifetime");
        float duration = Number(Field(main, "duration"), 5f);
        var lifetime = CurveRange(Field(main, "startLifetime"), 5f);
        var speed = CurveRange(Field(main, "startSpeed"), 5f);
        var size = CurveRange(Field(main, "startSize"), 1f);
        Vector4 color = ToColorGradient(Field(main, "startColor")).Stops[0].Rgba;
        color = ExtractHdrIntensity(color, out float hdr);
        ColorGradient lifetimeColors = Enabled(colorModule) ? ToColorGradient(Field(colorModule, "color")) : ColorGradient.Constant(Vector4.One);
        float gradientHdr = lifetimeColors.Stops.Aggregate(1f, (peak, stop) =>
            MathF.Max(peak, MathF.Max(stop.Rgba.X, MathF.Max(stop.Rgba.Y, stop.Rgba.Z))));
        if (gradientHdr > 1f)
        {
            lifetimeColors = new ColorGradient(lifetimeColors.Stops.Select(stop => new ColorStop(stop.Time,
                new Vector4(stop.Rgba.X / gradientHdr, stop.Rgba.Y / gradientHdr, stop.Rgba.Z / gradientHdr, stop.Rgba.W))).ToArray());
            hdr *= gradientHdr;
        }
        LumenShapeKind shapeKind = LumenShapeKind.Point;
        Vector3 extents = Vector3.One;
        Vector3 shapePosition = Vector3.Zero, shapeScale = Vector3.One;
        Quaternion shapeRotation = Quaternion.Identity;
        if (Enabled(shape, true))
        {
            int kind = Integer(Field(shape, "shapeType"), 4);
            shapeKind = kind switch
            {
                0 => LumenShapeKind.Sphere, 1 => LumenShapeKind.SphereShell,
                2 => LumenShapeKind.Hemisphere, 3 => LumenShapeKind.HemisphereShell,
                4 => LumenShapeKind.Cone, 5 => LumenShapeKind.Box,
                7 => LumenShapeKind.ConeShell, 8 => LumenShapeKind.ConeVolume, 9 => LumenShapeKind.ConeVolumeShell,
                10 => LumenShapeKind.CircleXZ, 11 => LumenShapeKind.CircleEdgeXZ, 12 => LumenShapeKind.SingleSidedEdge,
                15 => LumenShapeKind.BoxShell, 16 => LumenShapeKind.BoxEdge, 17 => LumenShapeKind.Donut,
                18 => LumenShapeKind.Rectangle,
                _ => throw new NotSupportedException("Unsupported particle shape: " + kind),
            };
            Vector4 position = Vector(Field(shape, "position"), Vector4.Zero);
            Vector4 rotation = Vector(Field(shape, "rotation"), Vector4.Zero);
            Vector4 scale = Vector(Field(shape, "scale"), Vector4.One);
            shapePosition = new Vector3(position.X, position.Y, position.Z);
            shapeScale = new Vector3(scale.X, scale.Y, scale.Z);
            shapeRotation = global::RimUnity.Quaternion.ToNumerics(global::RimUnity.Quaternion.Euler(rotation.X, rotation.Y, rotation.Z));
            float radius = Number(Field(shape, "radius"), 1f);
            extents = kind is 5 or 15 or 16 ? new Vector3(.5f) : kind == 18 ? new Vector3(.5f, 0, .5f) : new Vector3(radius);
        }
        else shape = null;
        // RimUnity 45ca90987f7d3721715b45c021cab986ee14c468, Source/RimUnity/Particles/ParticleSystem.Shapes.cs:12,84-86.
        // Core +Y -> Unity +Z before the authored shape rotation; B * diag(sx,sz,sy) = diag(sx,sy,sz) * B.
        shapeRotation *= Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);
        shapeScale = new Vector3(shapeScale.X, shapeScale.Z, shapeScale.Y);
        var forces = ConvertForces(particles);
        return new EmitterSpec
        {
            Name = name, PreferredTier = tier, Duration = duration,
            Looping = Boolean(Field(main, "loop"), true), MaxParticles = Integer(Field(main, "maxParticles"), 1000),
            WorldSpaceSimulation = Integer(Field(main, "simulationSpace")) == 1,
            RateOverTime = Enabled(emission, true) ? CurveRange(Field(emission, "rateOverTime"), 10f).Max : 0,
            Bursts = bursts,
            Shape = shapeKind, ShapeExtents = extents, ConeAngleDegrees = Number(Field(shape, "angle"), 25f),
            ShapePosition = shapePosition, ShapeRotation = shapeRotation, ShapeScale = shapeScale,
            ConeLength = Number(Field(shape, "length"), 5f), DonutRadius = Number(Field(shape, "donutRadius"), .2f),
            RadiusThickness = Number(Field(shape, "radiusThickness"), 1f),
            BoxThickness = ConvertBoxThickness(Field(shape, "boxThickness")),
            ArcDegrees = Number(Field(shape, "arc"), 360f), ArcSpread = Number(Field(shape, "arcSpread")),
            RadiusSpread = Number(Field(shape, "radiusSpread")),
            RandomDirectionAmount = Number(Field(shape, "randomDirectionAmount")),
            SphericalDirectionAmount = Number(Field(shape, "sphericalDirectionAmount")),
            RandomPositionAmount = Number(Field(shape, "randomPositionAmount")),
            StartLifetimeMin = lifetime.Min, StartLifetimeMax = lifetime.Max,
            StartSpeedMin = speed.Min, StartSpeedMax = speed.Max, StartSizeMin = size.Min, StartSizeMax = size.Max,
            StartColor = color, HdrIntensity = hdr, MaterialTint = material.Tint, ColorSpace = material.ColorSpace,
            SizeOverLifetime = Enabled(sizeModule) ? ToScalarCurve(Field(sizeModule, "size")) : ScalarCurve.Constant(1),
            ColorOverLifetime = lifetimeColors,
            ForceFields = forces, TextureKey = material.TextureKey, BlendMode = material.Blend,
            FlipbookTilesX = Enabled(sheet) ? Integer(Field(sheet, "numTilesX"), 1) : 1,
            FlipbookTilesY = Enabled(sheet) ? Integer(Field(sheet, "numTilesY"), 1) : 1,
            FlipbookCycles = Enabled(sheet) ? Number(Field(sheet, "cycleCount"), 1) : 1,
            BillboardMode = Integer(Member(renderer?.members, "renderMode")) switch
            {
                0 => Integer(Member(renderer?.members, "alignment")) == 2 ? LumenBillboardMode.Local : LumenBillboardMode.Camera,
                1 => LumenBillboardMode.StretchedVelocity, 2 => LumenBillboardMode.Horizontal,
                3 => LumenBillboardMode.Vertical, 4 => LumenBillboardMode.Mesh,
                var mode => throw new NotSupportedException("Unsupported particle render mode: " + mode),
            },
            VelocityStretchFactor = Number(Member(renderer?.members, "velocityScale")),
            LengthScale = Number(Member(renderer?.members, "lengthScale"), 2f), SoftParticleFadeDistance = material.SoftFade,
            Trails = Enabled(Member(particles.members, "trails")) && trailMaterial is { } tm
                ? ConvertParticleTrails(particles, Member(particles.members, "trails")!, tm, lifetime.Max, size.Max)
                : null,
        };
    }

    private static ParticleTrailSpec ConvertParticleTrails(ComponentData particles, ValueData trails,
        MaterialLook trailMaterial, float maxParticleLifetime, float maxStartSize)
    {
        ValueData? lifeField = Field(trails, "lifetime");
        float trailLifetime = lifeField is null || lifeField.kind == "null" ? 1f
            : lifeField.kind is "number" or "integer" ? Number(lifeField)
            : Number(Field(lifeField, "constant"), 1f);
        ValueData? widthField = Field(trails, "widthOverTrail");
        float baseWidth = 1f;
        ScalarCurve widthOverTrail = ScalarCurve.Constant(1f);
        if (widthField is not null && widthField.kind != "null")
        {
            if (widthField.kind is "number" or "integer")
                baseWidth = Number(widthField);
            else if (Integer(Field(widthField, "mode")) == 0)
                baseWidth = Number(Field(widthField, "constant"), 1f);
            else
                widthOverTrail = ToScalarCurve(widthField, 1f);
        }
        bool sizeAffectsLifetime = Boolean(Field(trails, "sizeAffectsLifetime"), false);
        ValueData? sizeModule = Member(particles.members, "sizeOverLifetime");
        float maxSize = maxStartSize * (Enabled(sizeModule) ? MathF.Max(0f, CurveRange(Field(sizeModule, "size"), 1f).Max) : 1f);
        int maxPoints = (int)Math.Clamp(
            MathF.Ceiling(maxParticleLifetime * trailLifetime * (sizeAffectsLifetime ? maxSize : 1f) * 60f) + 2f,
            16f, 256f);
        return new ParticleTrailSpec
        {
            Ratio = Number(Field(trails, "ratio"), 1f),
            Lifetime = trailLifetime,
            MinVertexDistance = MathF.Max(1e-5f, Number(Field(trails, "minVertexDistance"), 0.2f)),
            MaxPoints = maxPoints,
            DieWithParticles = Boolean(Field(trails, "dieWithParticles"), true),
            WorldSpace = Boolean(Field(trails, "worldSpace"), false),
            SizeAffectsWidth = Boolean(Field(trails, "sizeAffectsWidth"), true),
            SizeAffectsLifetime = sizeAffectsLifetime,
            InheritParticleColor = Boolean(Field(trails, "inheritParticleColor"), true),
            Width = baseWidth,
            WidthOverTrail = widthOverTrail,
            ColorOverTrail = ToColorGradient(Field(trails, "colorOverTrail")),
            ColorOverLifetime = ToColorGradient(Field(trails, "colorOverLifetime")),
            TextureKey = trailMaterial.TextureKey,
            MaterialTint = trailMaterial.Tint,
            ColorSpace = trailMaterial.ColorSpace,
            BlendMode = trailMaterial.Blend,
            TextureMode = Integer(Field(trails, "textureMode")) == 1 ? LumenRibbonTextureMode.Tile : LumenRibbonTextureMode.Stretch,
            Alignment = LumenRibbonAlignment.View,
            HdrIntensity = 1f,
            SoftParticleFadeDistance = trailMaterial.SoftFade,
        };
    }

    private static Vector3 ConvertBoxThickness(ValueData? value)
    {
        Vector4 source = Vector(value, Vector4.Zero);
        return new Vector3(source.X, source.Z, source.Y);
    }

    private static ForceFieldSpec[] ConvertForces(ComponentData particles)
    {
        var forces = new List<ForceFieldSpec>();
        ValueData? main = Member(particles.members, "main");
        ValueData? limit = Member(particles.members, "limitVelocityOverLifetime");
        float gravity = CurveRange(Field(main, "gravityModifier"), 0).Max * 9.81f;
        float drag = Enabled(limit) ? Number(Field(limit, "dampen")) : 0;
        ValueData? force = Member(particles.members, "forceOverLifetime");
        Vector3 acceleration = new(0, -gravity, 0);
        if (Enabled(force))
        {
            acceleration += new Vector3(CurveRange(Field(force, "x"), 0).Max,
                CurveRange(Field(force, "y"), 0).Max, CurveRange(Field(force, "z"), 0).Max);
        }
        if (acceleration != Vector3.Zero || drag != 0)
        {
            forces.Add(new ForceFieldSpec(ForceFieldKind.GravityDrag, acceleration, new Vector3(drag, 0, 0), 0, 1));
        }
        ValueData? velocity = Member(particles.members, "velocityOverLifetime");
        if (Enabled(velocity))
        {
            if (Nonzero(Field(velocity, "x")) || Nonzero(Field(velocity, "y")) || Nonzero(Field(velocity, "z")))
                throw new NotSupportedException("Linear velocity over lifetime cannot be represented by EmitterSpec.");
            var axis = new Vector3(CurveRange(Field(velocity, "orbitalX"), 0).Max,
                CurveRange(Field(velocity, "orbitalY"), 0).Max, CurveRange(Field(velocity, "orbitalZ"), 0).Max);
            float radial = CurveRange(Field(velocity, "radial"), 0).Max;
            float strength = axis.Length();
            if (strength != 0 || radial != 0)
                forces.Add(new ForceFieldSpec(ForceFieldKind.Vortex, strength > 0 ? axis / strength : Vector3.UnitY,
                    new Vector3(-radial, 0, 0),
                    0, strength > 0 ? strength : 1));
        }
        ValueData? noise = Member(particles.members, "noise");
        if (Enabled(noise)) forces.Add(new ForceFieldSpec(ForceFieldKind.CurlTurbulence, Vector3.Zero, Vector3.Zero,
            0, CurveRange(Field(noise, "strength"), 1).Max, Number(Field(noise, "frequency"), 0.5f)));
        return forces.ToArray();
        static bool Nonzero(ValueData? value)
        {
            var range = CurveRange(value, 0);
            return range.Min != 0 || range.Max != 0;
        }
    }

    private static HashSet<string> SubEmitterTargets(NodeData[] nodes)
    {
        var systems = nodes.SelectMany(node => node.components ?? new List<ComponentData>())
            .Where(component => component.type == "RimUnity.ParticleSystem")
            .ToDictionary(component => component.id, StringComparer.Ordinal);
        var queue = new Queue<ComponentData>(systems.Values.Where(system => Enabled(Member(system.members, "subEmitters"))));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        while (queue.TryDequeue(out ComponentData? system))
        {
            if (!visited.Add(system.id)) continue;
            ValueData? module = Member(system.members, "subEmitters");
            if (!Enabled(module) || Field(module, "entries")?.items is not { } entries) continue;
            foreach (ValueData entry in entries)
            {
                ValueData? reference = Field(entry, "system");
                if (reference?.kind != "object" || reference.text is null || !systems.TryGetValue(reference.text, out ComponentData? target))
                    throw new InvalidDataException("Invalid subEmitter ParticleSystem reference on " + system.id + ": " + reference?.text);
                targets.Add(target.id);
                queue.Enqueue(target);
            }
        }
        return targets;
    }

    private static NodeData[] OrderNodes(PrefabData prefab)
    {
        if (prefab.formatVersion != 1 || prefab.nodes is null || prefab.nodes.Count == 0)
            throw new InvalidDataException("Invalid Prefab version or empty hierarchy.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var byId = new Dictionary<string, NodeData>(StringComparer.Ordinal);
        var children = new Dictionary<string, List<NodeData>>(StringComparer.Ordinal);
        NodeData? root = null;
        foreach (NodeData node in prefab.nodes)
        {
            if (string.IsNullOrWhiteSpace(node.id) || !byId.TryAdd(node.id, node)) throw new InvalidDataException("Duplicate or empty node id: " + node.id);
            AddId(node.id); AddId(node.transformId);
            if (node.components is not null) foreach (ComponentData component in node.components) AddId(component.id);
            CheckVector(node.position, 3); CheckVector(node.rotation, 4); CheckVector(node.scale, 3);
            if (node.parent is null)
            {
                if (root is not null) throw new InvalidDataException("Prefab must have exactly one root.");
                root = node;
            }
            else
            {
                if (!children.TryGetValue(node.parent, out var list)) children.Add(node.parent, list = new List<NodeData>());
                list.Add(node);
            }
        }
        if (root is null) throw new InvalidDataException("Prefab has no root (cyclic hierarchy).");
        foreach (string parent in children.Keys) if (!byId.ContainsKey(parent)) throw new InvalidDataException("Missing parent node: " + parent);
        var result = new List<NodeData>();
        var queue = new Queue<NodeData>();
        queue.Enqueue(root);
        while (queue.TryDequeue(out NodeData? node))
        {
            result.Add(node);
            if (children.TryGetValue(node.id, out var list)) foreach (NodeData child in list) queue.Enqueue(child);
        }
        if (result.Count != prefab.nodes.Count) throw new InvalidDataException("Prefab hierarchy has a cycle or disconnected subtree.");
        return result.ToArray();
        void AddId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id)) throw new InvalidDataException("Duplicate or empty object id: " + id);
        }
        static void CheckVector(float[] vector, int count)
        {
            if (vector is null || vector.Length != count || vector.Any(value => !float.IsFinite(value)))
                throw new InvalidDataException("Invalid prefab local transform.");
        }
    }
}
