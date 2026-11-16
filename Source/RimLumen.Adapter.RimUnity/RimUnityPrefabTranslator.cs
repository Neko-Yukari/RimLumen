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
                ResourceData? material = null;
                ValueData? materialRef = Member(renderer?.members, "sharedMaterial");
                if (materialRef?.kind == "asset")
                {
                    material = resourceResolver(materialRef.text) ?? throw new InvalidDataException("Missing material: " + materialRef.text);
                    if (material.type != "Material") throw new InvalidDataException("Renderer asset is not a Material: " + materialRef.text);
                }
                var classification = Classifier.Classify(particles, renderer);
                var reasons = new List<string>(classification.Reasons);
                if (string.Equals(material?.colorSpace, "Linear", StringComparison.OrdinalIgnoreCase))
                {
                    classification.Tier = LumenParticleTier.L0_ExternalCpu;
                    reasons.RemoveAll(reason => reason.EndsWith("L2ParticleThreshold", StringComparison.Ordinal));
                    reasons.Add("material.linearColorSpace");
                }
                if (subEmitterTargets.Contains(particles.id))
                {
                    classification.Tier = LumenParticleTier.L0_ExternalCpu;
                    reasons.RemoveAll(reason => reason.EndsWith("L2ParticleThreshold", StringComparison.Ordinal));
                    reasons.Add("subEmitters.dependency");
                }
                if (active[i] && particles.enabled && renderer?.enabled != false && classification.Tier != LumenParticleTier.L0_ExternalCpu)
                    emitter = ConvertEmitter(source.name, particles, renderer, classification.Tier, material, shaderResolver);
                classifications.Add(new RimUnityNodeTranslation(i, source.id, classification.Tier, reasons));
                ValueData? main = Member(particles.members, "main");
                duration = MathF.Max(duration, Number(Field(main, "duration"), 5f) + CurveRange(Field(main, "startLifetime"), 5f).Max);
            }
            nodes[i] = new EffectNodeSpec
            {
                Name = source.name ?? source.id, ParentIndex = parent,
                // Unity's local quaternion already contains the quad pitch; GroundXZ adds no second rotation.
                SpacePlane = LumenSpacePlane.GroundXZ,
                LocalPosition = new Vector3(source.position[0], source.position[1], source.position[2]),
                LocalRotation = new Quaternion(source.rotation[0], source.rotation[1], source.rotation[2], source.rotation[3]),
                LocalScale = new Vector3(source.scale[0], source.scale[1], source.scale[2]),
                Emitter = emitter,
            };
        }
        report = new RimUnityTranslationReport
        {
            EffectId = effectId, PackageId = string.Empty, DataDirectory = string.Empty,
            LogicalPath = effectId, Prefab = prefab, Nodes = classifications,
        };
        return new EffectAsset { EffectId = effectId, DefaultDuration = duration > 0 ? duration : 2f, Nodes = nodes };
    }

    private static EmitterSpec ConvertEmitter(string name, ComponentData particles, ComponentData? renderer,
        LumenParticleTier tier, ResourceData? material, Func<string, ShaderData?> shaderResolver)
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
        ShaderData? shader = null;
        if (material is not null)
        {
            ValueData? shaderRef = Member(material.members, "shader");
            if (shaderRef?.kind == "asset")
                shader = shaderResolver(shaderRef.text) ?? throw new InvalidDataException("Missing shader: " + shaderRef.text);
            Vector4 tint = Vector(Member(material.members, "_Color"), Vector4.One);
            // RimUnity 45ca90987f7d3721715b45c021cab986ee14c468, ParticleSystem.Simulation.cs:110-116:
            // vertex factors retain stored values. Native HDR deliberately does not reproduce Color32 clipping.
            color *= tint;
        }
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
        LumenBlendMode blend = LumenBlendMode.AlphaBlend;
        if (shader?.blend == true && string.Equals(shader.dstColor, "One", StringComparison.OrdinalIgnoreCase)) blend = LumenBlendMode.Additive;
        else if (shader?.blend == true && string.Equals(shader.srcColor, "One", StringComparison.OrdinalIgnoreCase)) blend = LumenBlendMode.Premultiplied;
        ValueData? texture = Member(material?.members, "_MainTex");
        string? textureKey = texture?.kind is "asset" or "string" ? texture.text : null;
        float softFade = 0.35f;
        ValueData? nearFade = Member(material?.members, "_SoftParticlesNearFadeDistance");
        ValueData? inverseFade = Member(material?.members, "_InvFade");
        if (nearFade is not null) softFade = Number(nearFade);
        else if (inverseFade is not null)
        {
            float inverse = Number(inverseFade);
            softFade = inverse > 0 ? 1f / inverse : 0;
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
                0 => LumenShapeKind.Sphere,
                4 => LumenShapeKind.Cone,
                5 => LumenShapeKind.Box,
                _ => throw new NotSupportedException("Unsupported particle shape: " + kind),
            };
            Vector4 position = Vector(Field(shape, "position"), Vector4.Zero);
            Vector4 rotation = Vector(Field(shape, "rotation"), Vector4.Zero);
            Vector4 scale = Vector(Field(shape, "scale"), Vector4.One);
            shapePosition = new Vector3(position.X, position.Y, position.Z);
            shapeScale = new Vector3(scale.X, scale.Y, scale.Z);
            shapeRotation = global::RimUnity.Quaternion.ToNumerics(global::RimUnity.Quaternion.Euler(rotation.X, rotation.Y, rotation.Z));
            extents = shapeKind == LumenShapeKind.Box ? new Vector3(0.5f) : new Vector3(Number(Field(shape, "radius"), 1f));
        }
        // RimUnity 45ca90987f7d3721715b45c021cab986ee14c468, Source/RimUnity/Particles/ParticleSystem.Shapes.cs:12,84-86.
        // Core +Y -> Unity +Z before the authored shape rotation; B * diag(sx,sz,sy) = diag(sx,sy,sz) * B.
        // Sphere already samples XYZ isotropically; only point/box/cone need the emission-axis basis change.
        if (shapeKind != LumenShapeKind.Sphere)
        {
            shapeRotation *= Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);
            shapeScale = new Vector3(shapeScale.X, shapeScale.Z, shapeScale.Y);
        }
        var forces = ConvertForces(particles);
        return new EmitterSpec
        {
            Name = name, PreferredTier = tier, Duration = duration,
            Looping = Boolean(Field(main, "loop"), true), MaxParticles = Integer(Field(main, "maxParticles"), 1000),
            WorldSpaceSimulation = Integer(Field(main, "simulationSpace")) == 1,
            RateOverTime = Enabled(emission, true) ? CurveRange(Field(emission, "rateOverTime"), 10f).Max : 0,
            Bursts = Enabled(emission, true) ? ConvertBursts(Field(emission, "bursts"), duration) : Array.Empty<(float, int)>(),
            Shape = shapeKind, ShapeExtents = extents, ConeAngleDegrees = Number(Field(shape, "angle"), 25f),
            ShapePosition = shapePosition, ShapeRotation = shapeRotation, ShapeScale = shapeScale,
            StartLifetimeMin = lifetime.Min, StartLifetimeMax = lifetime.Max,
            StartSpeedMin = speed.Min, StartSpeedMax = speed.Max, StartSizeMin = size.Min, StartSizeMax = size.Max,
            StartColor = color, HdrIntensity = hdr,
            SizeOverLifetime = Enabled(sizeModule) ? ToScalarCurve(Field(sizeModule, "size")) : ScalarCurve.Constant(1),
            ColorOverLifetime = lifetimeColors,
            ForceFields = forces, TextureKey = textureKey, BlendMode = blend,
            FlipbookTilesX = Enabled(sheet) ? Integer(Field(sheet, "numTilesX"), 1) : 1,
            FlipbookTilesY = Enabled(sheet) ? Integer(Field(sheet, "numTilesY"), 1) : 1,
            FlipbookCycles = Enabled(sheet) ? Number(Field(sheet, "cycleCount"), 1) : 1,
            BillboardMode = Integer(Member(renderer?.members, "renderMode")) switch
            {
                0 => LumenBillboardMode.Camera, 1 => LumenBillboardMode.StretchedVelocity, 2 => LumenBillboardMode.Horizontal,
                4 => LumenBillboardMode.Mesh,
                var mode => throw new NotSupportedException("Unsupported particle render mode: " + mode),
            },
            VelocityStretchFactor = Number(Member(renderer?.members, "velocityScale")),
            LengthScale = Number(Member(renderer?.members, "lengthScale"), 2f), SoftParticleFadeDistance = softFade,
        };
    }

    private static (float Time, int Count)[] ConvertBursts(ValueData? value, float duration)
    {
        var result = new List<(float, int)>();
        if (value?.items is null) return result.ToArray();
        foreach (ValueData burst in value.items)
        {
            float time = Number(Field(burst, "time"));
            float interval = Number(Field(burst, "repeatInterval"));
            int cycles = Integer(Field(burst, "cycleCount") ?? Field(burst, "cycles"), Integer(Field(burst, "repeats")) + 1);
            float probability = Number(Field(burst, "probability"), 1 - Number(Field(burst, "inverseProbability")));
            if (probability > 0 && probability < 1)
                throw new NotSupportedException("Probabilistic bursts require external playback.");
            if (probability <= 0) continue;
            int count = checked((int)MathF.Round(CurveRange(Field(burst, "count"), Number(Field(burst, "maxCount"))).Max));
            if (cycles <= 0) throw new NotSupportedException("Unbounded burst cycles require external playback.");
            for (int i = 0; i < cycles && time + i * interval <= duration; i++) result.Add((time + i * interval, count));
        }
        return result.OrderBy(burst => burst.Item1).ToArray();
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
