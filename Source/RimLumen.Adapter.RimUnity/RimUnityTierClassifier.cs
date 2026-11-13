using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimUnity.Serialization;
using static RimLumen.Adapter.RimUnity.RimUnityValueConversion;

namespace RimLumen.Adapter.RimUnity;

public sealed class RimUnityTierClassifier
{
    private int l2ParticleThreshold = 128;
    public int L2ParticleThreshold
    {
        get => l2ParticleThreshold;
        set => l2ParticleThreshold = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public (LumenParticleTier Tier, IReadOnlyList<string> Reasons) Classify(ComponentData particleSystem, ComponentData? renderer)
    {
        var reasons = new List<string>();
        foreach (string module in new[] { "trails", "subEmitters", "collision", "trigger" })
            if (Enabled(Member(particleSystem.members, module))) reasons.Add(module);
        if (Integer(Member(renderer?.members, "renderMode")) == 4) reasons.Add("renderMode.Mesh");
        ValueData? velocity = Member(particleSystem.members, "velocityOverLifetime");
        if (Enabled(velocity) && (Nonzero(Field(velocity, "x")) || Nonzero(Field(velocity, "y")) || Nonzero(Field(velocity, "z"))))
            reasons.Add("velocityOverLifetime.linear");
        if (Enabled(Member(particleSystem.members, "rotationOverLifetime"))) reasons.Add("rotationOverLifetime");
        ValueData? force = Member(particleSystem.members, "forceOverLifetime");
        if (Enabled(force) && (Varying(Field(force, "x")) || Varying(Field(force, "y")) || Varying(Field(force, "z"))))
            reasons.Add("forceOverLifetime.curve");
        ValueData? lights = Member(particleSystem.members, "lights");
        if (Enabled(lights) && Number(Field(lights, "ratio"), 1f) != 1f) reasons.Add("lights.ratio");

        ValueData? main = Member(particleSystem.members, "main");
        ValueData? emission = Member(particleSystem.members, "emission");
        int maxParticles = Integer(Field(main, "maxParticles"), 1000);
        float maxLifetime = CurveRange(Field(main, "startLifetime"), 5f).Max;
        float rate = Enabled(emission, true) ? CurveRange(Field(emission, "rateOverTime"), 10f).Max : 0;
        float maxBurst = 0;
        if (Enabled(emission, true) && Field(emission, "bursts")?.items is { } bursts)
            foreach (ValueData burst in bursts)
            {
                float probability = Number(Field(burst, "probability"), 1 - Number(Field(burst, "inverseProbability")));
                if (probability > 0 && probability < 1 && !reasons.Contains("emission.bursts.probability"))
                    reasons.Add("emission.bursts.probability");
                if (probability > 0)
                    maxBurst = MathF.Max(maxBurst, CurveRange(Field(burst, "count"), Number(Field(burst, "maxCount"))).Max);
            }
        bool gpu = maxParticles >= L2ParticleThreshold || rate * maxLifetime + maxBurst >= L2ParticleThreshold;
        AddShapeReasons(Member(particleSystem.members, "shape"), main, gpu, reasons);
        if (!gpu && Enabled(force) && (Nonzero(Field(force, "x")) || Nonzero(Field(force, "z"))))
            reasons.Add("forceOverLifetime.lateral");
        if (reasons.Count != 0) return (LumenParticleTier.L0_ExternalCpu, reasons);
        if (maxParticles >= L2ParticleThreshold) reasons.Add("maxParticles >= L2ParticleThreshold");
        if (rate * maxLifetime + maxBurst >= L2ParticleThreshold) reasons.Add("peakConcurrent >= L2ParticleThreshold");
        return (gpu ? LumenParticleTier.L2_GpuCompute : LumenParticleTier.L1_NativeCpu, reasons);
    }

    private static void AddShapeReasons(ValueData? shape, ValueData? main, bool gpu, List<string> reasons)
    {
        bool enabled = Enabled(shape, true);
        int kind = enabled ? Integer(Field(shape, "shapeType"), 4) : -1;
        if (enabled)
        {
            Vector4 position = Vector(Field(shape, "position"), Vector4.Zero);
            Vector4 rotation = Vector(Field(shape, "rotation"), Vector4.Zero);
            Vector4 scale = Vector(Field(shape, "scale"), Vector4.One);
            // Box extents preserve per-axis scale. Other native shapes have no independent shape-local transform.
            if (position != Vector4.Zero || rotation != Vector4.Zero || (kind != 5 && scale != Vector4.One))
                reasons.Add("shape.localTransform");
            if (kind is 4 or 7 or 8 or 9 && Number(Field(shape, "radius"), 1f) != 0)
                reasons.Add("shape.coneRadius");
            if (kind is 10 or 11) reasons.Add("shape.circlePlane");
            string? unsupported = kind switch
            {
                1 => "shape.sphereShell", 2 or 3 => "shape.hemisphere", 6 or 13 or 14 or 19 or 20 => "shape.geometry",
                7 => "shape.coneShell", 8 or 9 => "shape.coneVolume", 12 => "shape.edge",
                15 => "shape.boxShell", 16 => "shape.boxEdge", 17 => "shape.donut", 18 => "shape.rectangle",
                0 or 4 or 5 or 10 or 11 => null, _ => "shape.unsupported",
            };
            if (unsupported is not null) reasons.Add(unsupported);
            if (kind == 0 && Number(Field(shape, "radiusThickness"), 1f) != 1f)
                reasons.Add("shape.radiusThickness");
            // Unity normalizes a sampled base disk; Core samples solid angle uniformly, even at zero base radius.
            if (kind == 4 && Number(Field(shape, "angle"), 25f) != 0)
                reasons.Add("shape.coneDistribution");
            if (kind is 0 or 4 or 10 or 11 && (Number(Field(shape, "arc"), 360f) != 360f ||
                Integer(Field(shape, "arcMode")) != 0 || Number(Field(shape, "arcSpread")) != 0))
                reasons.Add("shape.arc");
            if (Number(Field(shape, "randomDirectionAmount")) != 0 || Number(Field(shape, "sphericalDirectionAmount")) != 0)
                reasons.Add("shape.direction");
            if (Number(Field(shape, "randomPositionAmount")) != 0) reasons.Add("shape.randomPosition");
            if (Boolean(Field(shape, "alignToDirection"))) reasons.Add("shape.alignToDirection");
            if (Field(shape, "texture") is { kind: not "null" }) reasons.Add("shape.texture");
        }
        // Core GPU defaults to +Y for points/boxes/cones; Unity and the CPU path use +Z.
        if (gpu && kind is -1 or 4 or 5 && NonzeroWithDefault(Field(main, "startSpeed"), 5f))
            reasons.Add("shape.directionAxis");
    }

    private static bool NonzeroWithDefault(ValueData? value, float fallback)
    {
        var range = CurveRange(value, fallback);
        return range.Min != 0 || range.Max != 0;
    }

    private static bool Nonzero(ValueData? value)
    {
        var range = CurveRange(value, 0);
        return range.Min != 0 || range.Max != 0;
    }

    private static bool Varying(ValueData? value)
    {
        var range = CurveRange(value, 0);
        return range.Min != range.Max;
    }
}
