using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        => Classify(particleSystem, renderer, out _);

    internal (LumenParticleTier Tier, IReadOnlyList<string> Reasons) Classify(ComponentData particleSystem, ComponentData? renderer,
        out ParticleBurstSpec[] convertedBursts)
    {
        var reasons = new List<string>();
        AddDeferredCurveReasons(particleSystem, reasons);
        foreach (string module in new[] { "subEmitters", "collision", "trigger" })
            if (Enabled(Member(particleSystem.members, module))) reasons.Add(module);
        AddTrailsReasons(particleSystem, renderer, reasons);
        int renderMode = Integer(Member(renderer?.members, "renderMode"));
        if (renderMode == 4) reasons.Add("renderMode.Mesh");
        else if (renderMode is not (0 or 1 or 2 or 3)) reasons.Add("renderMode." + renderMode);
        int alignment = Integer(Member(renderer?.members, "alignment"));
        if (alignment is < 0 or > 4 || (renderMode == 0 && alignment is 1 or 3 or 4))
            reasons.Add("alignment." + alignment);
        ValueData? velocity = Member(particleSystem.members, "velocityOverLifetime");
        if (Enabled(velocity) && (NonzeroSingleCurve(Field(velocity, "x")) || NonzeroSingleCurve(Field(velocity, "y")) || NonzeroSingleCurve(Field(velocity, "z"))))
            reasons.Add("velocityOverLifetime.linear");
        if (Enabled(Member(particleSystem.members, "rotationOverLifetime"))) reasons.Add("rotationOverLifetime");
        ValueData? force = Member(particleSystem.members, "forceOverLifetime");
        if (Enabled(force) && (IsCurve(Field(force, "x")) || IsCurve(Field(force, "y")) || IsCurve(Field(force, "z")) ||
            Varying(Field(force, "x")) || Varying(Field(force, "y")) || Varying(Field(force, "z"))))
            reasons.Add("forceOverLifetime.curve");
        ValueData? lights = Member(particleSystem.members, "lights");
        if (Enabled(lights)) reasons.Add("lights.perParticle");

        ValueData? main = Member(particleSystem.members, "main");
        ValueData? emission = Member(particleSystem.members, "emission");
        int maxParticles = Integer(Field(main, "maxParticles"), 1000);
        if (Enabled(emission, true) && Field(emission, "bursts")?.items is { } bursts)
            foreach (ValueData burst in bursts)
            {
                float probability = RawNumber(Field(burst, "probability"), 1 - RawNumber(Field(burst, "inverseProbability"), 0));
                if ((!float.IsFinite(probability) || probability < 0 || probability > 1) && !reasons.Contains("emission.bursts.probability.domain"))
                    reasons.Add("emission.bursts.probability.domain");
                ValueData? count = Field(burst, "count");
                int countMode = Integer(Field(count, "mode"));
                if (countMode is 1 or 3 && Varying(count) && !reasons.Contains("emission.bursts.count.variable"))
                    reasons.Add("emission.bursts.count.variable");
            }
        AddShapeReasons(Member(particleSystem.members, "shape"), reasons);
        convertedBursts = ConvertBurstSchedule(emission, Number(Field(main, "duration"), 5f), reasons);
        if (reasons.Count != 0) return (LumenParticleTier.External, reasons);
        float maxLifetime = CurveRange(Field(main, "startLifetime"), 5f).Max;
        float rate = Enabled(emission, true) ? CurveRange(Field(emission, "rateOverTime"), 10f).Max : 0;
        float maxBurst = 0;
        if (Enabled(emission, true) && Field(emission, "bursts")?.items is { } nativeBursts)
            foreach (ValueData burst in nativeBursts)
                if (RawNumber(Field(burst, "probability"), 1 - RawNumber(Field(burst, "inverseProbability"), 0)) > 0)
                    maxBurst = MathF.Max(maxBurst, CurveRange(Field(burst, "count"), Number(Field(burst, "maxCount"))).Max);
        bool gpu = maxParticles >= L2ParticleThreshold || rate * maxLifetime + maxBurst >= L2ParticleThreshold;
        if (maxParticles >= L2ParticleThreshold) reasons.Add("maxParticles >= L2ParticleThreshold");
        if (rate * maxLifetime + maxBurst >= L2ParticleThreshold) reasons.Add("peakConcurrent >= L2ParticleThreshold");
        return (gpu ? LumenParticleTier.L2_GpuCompute : LumenParticleTier.L1_NativeCpu, reasons);
    }

    private static void AddShapeReasons(ValueData? shape, List<string> reasons)
    {
        bool enabled = Enabled(shape, true);
        int kind = enabled ? Integer(Field(shape, "shapeType"), 4) : -1;
        if (enabled)
        {
            string? unsupported = kind switch
            {
                6 or 13 or 14 or 19 or 20 => "shape.geometry",
                >= 0 and <= 18 => null, _ => "shape.unsupported",
            };
            if (unsupported is not null) reasons.Add(unsupported);
            if (Integer(Field(shape, "arcMode")) != 0) reasons.Add("shape.arcMode");
            if (kind == 12 && Integer(Field(shape, "radiusMode")) != 0) reasons.Add("shape.radiusMode");
            Domain("radius", 1, 0, float.MaxValue);
            Domain("radiusThickness", 1, 0, 1);
            Domain("angle", 25, 0, 180);
            Domain("length", 5, 0, float.MaxValue);
            Domain("donutRadius", .2f, 0, float.MaxValue);
            Domain("arc", 360, 0, 360);
            Domain("arcSpread", 0, 0, 1);
            Domain("radiusSpread", 0, 0, 1);
            Domain("randomDirectionAmount", 0, 0, 1);
            Domain("sphericalDirectionAmount", 0, 0, 1);
            Domain("randomPositionAmount", 0, 0, float.MaxValue);
            foreach (string field in new[] { "position", "rotation", "scale" })
            {
                ValueData? transform = Field(shape, field);
                if (transform is not null && transform.kind != "null" && transform.vector is { } components &&
                    Array.Exists(components, x => !float.IsFinite(x))) reasons.Add("shape." + field + ".domain");
            }
            ValueData? box = Field(shape, "boxThickness");
            if (box is not null && box.kind != "null" && box.vector is { } thickness &&
                Array.Exists(thickness, x => !float.IsFinite(x) || x < 0 || x > 1)) reasons.Add("shape.boxThickness.domain");
            void Domain(string name, float fallback, float min, float max)
            {
                float value = RawNumber(Field(shape, name), fallback);
                if (!float.IsFinite(value) || value < min || value > max) reasons.Add("shape." + name + ".domain");
            }
            if (Boolean(Field(shape, "alignToDirection"))) reasons.Add("shape.alignToDirection");
            if (Field(shape, "texture") is { kind: not "null" }) reasons.Add("shape.texture");
        }
    }

    private static float RawNumber(ValueData? value, float fallback) => value is null || value.kind == "null" ? fallback :
        value.kind is "integer" or "specialNumber" ? float.Parse(value.text, CultureInfo.InvariantCulture) : (float)value.number;

    private static void AddDeferredCurveReasons(ComponentData particles, List<string> reasons)
    {
        // Export/ParticleWriter.cs:17-25 serializes inactive fields too; Emission.cs:175-176 selects the active axes.
        ValueData? main = Member(particles.members, "main");
        bool size3D = Boolean(Field(main, "startSize3D"));
        bool rotation3D = Boolean(Field(main, "startRotation3D"));
        if (size3D) reasons.Add("main.startSize3D");
        if (rotation3D) reasons.Add("main.startRotation3D");
        else if (NonzeroSingleCurve(Field(main, "startRotation"))) reasons.Add("main.startRotation");
        AddDual("main", main, "startLifetime", "startSpeed", "gravityModifier");
        if (size3D) AddDual("main", main, "startSizeX", "startSizeY", "startSizeZ");
        else AddDual("main", main, "startSize");
        if (rotation3D) AddDual("main", main, "startRotationX", "startRotationY", "startRotationZ");
        else AddDual("main", main, "startRotation");
        foreach (var selection in new (string Module, string[] Fields)[]
        {
            ("emission", new[] { "rateOverTime", "rateOverDistance" }), ("sizeOverLifetime", new[] { "size" }),
            ("velocityOverLifetime", new[] { "x", "y", "z", "orbitalX", "orbitalY", "orbitalZ", "radial" }),
            ("forceOverLifetime", new[] { "x", "y", "z" }), ("noise", new[] { "strength" })
        })
        {
            ValueData? module = Member(particles.members, selection.Module);
            if (Enabled(module, selection.Module == "emission")) AddDual(selection.Module, module, selection.Fields);
        }
        void AddDual(string moduleName, ValueData? module, params string[] fields)
        {
            foreach (string field in fields)
                if (Integer(Field(Field(module, field), "mode")) == 2)
                    reasons.Add(moduleName + "." + field + ".dualCurve");
        }
        ValueData? emission = Member(particles.members, "emission");
        if (Enabled(emission, true) && Field(emission, "bursts")?.items is { } bursts)
            foreach (ValueData burst in bursts)
                if (Integer(Field(Field(burst, "count"), "mode")) == 2)
                    reasons.Add("emission.bursts.count.dualCurve");
        ValueData? velocity = Member(particles.members, "velocityOverLifetime");
        if (Enabled(velocity))
            foreach (string field in new[] { "x", "y", "z", "orbitalX", "orbitalY", "orbitalZ", "radial" })
                if (IsCurve(Field(velocity, field)) || Varying(Field(velocity, field))) { reasons.Add("velocityOverLifetime.curve"); break; }
    }

    private static ParticleBurstSpec[] ConvertBurstSchedule(ValueData? emission, float duration, List<string> reasons)
    {
        if (!Enabled(emission, true) || Field(emission, "bursts")?.items is not { } bursts)
            return Array.Empty<ParticleBurstSpec>();
        var plans = new List<(ValueData Source, float Time, float Interval, int Occurrences)>();
        int total = 0;
        foreach (ValueData burst in bursts)
        {
            float time = RawNumber(Field(burst, "time"), 0);
            float interval = RawNumber(Field(burst, "repeatInterval"), 0);
            int cycles = Integer(Field(burst, "cycleCount") ?? Field(burst, "cycles"), Integer(Field(burst, "repeats")) + 1);
            if (!float.IsFinite(time) || time < 0 || time > duration || !float.IsFinite(interval) || cycles < 0 ||
                (cycles != 1 && interval <= 0))
            {
                if (!reasons.Contains("emission.bursts.schedule.domain")) reasons.Add("emission.bursts.schedule.domain");
                continue;
            }
            // RimUnity 45ca90987f7d3721715b45c021cab986ee14c468, ParticleSystem.Emission.cs:48-52,121-129.
            // Repeat phase restarts each loop. Keep source float arithmetic and its strict endpoint test.
            float available = cycles == 1 ? 1 : MathF.Max(0, MathF.Ceiling((duration - time) / interval));
            int occurrences = 0;
            for (int i = 0; i < available && (cycles == 0 || i < cycles); i++)
            {
                if (time + i * interval >= duration) break;
                if (++total > 4096)
                {
                    reasons.Add("emission.bursts.occurrenceLimit");
                    return Array.Empty<ParticleBurstSpec>();
                }
                occurrences++;
            }
            plans.Add((burst, time, interval, occurrences));
        }
        if (reasons.Count != 0) return Array.Empty<ParticleBurstSpec>();
        var result = new List<ParticleBurstSpec>(total);
        foreach (var plan in plans)
        {
            float probability = RawNumber(Field(plan.Source, "probability"), 1 - RawNumber(Field(plan.Source, "inverseProbability"), 0));
            int count = checked((int)MathF.Round(CurveRange(Field(plan.Source, "count"), Number(Field(plan.Source, "maxCount"))).Max));
            for (int i = 0; i < plan.Occurrences; i++)
                result.Add(new ParticleBurstSpec(plan.Time + i * plan.Interval, count, probability));
        }
        return result.OrderBy(burst => burst.Time).ToArray();
    }

    private static bool NonzeroSingleCurve(ValueData? value) => Integer(Field(value, "mode")) != 2 && Nonzero(value);

    private static bool IsCurve(ValueData? value) => Integer(Field(value, "mode")) is 1 or 2;

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

    private static void AddTrailsReasons(ComponentData particleSystem, ComponentData? renderer, List<string> reasons)
    {
        ValueData? trails = Member(particleSystem.members, "trails");
        if (!Enabled(trails)) return;
        int mode = Integer(Field(trails, "mode"), 0);
        if (mode == 1) reasons.Add("trails.mode.Ribbon");
        else if (mode != 0) reasons.Add("trails.mode." + mode);

        float ratio = RawNumber(Field(trails, "ratio"), 1f);
        if (!float.IsFinite(ratio) || ratio < 0f || ratio > 1f) reasons.Add("trails.ratio.domain");

        float minDist = RawNumber(Field(trails, "minVertexDistance"), 0.2f);
        if (!float.IsFinite(minDist) || minDist < 0f) reasons.Add("trails.minVertexDistance.domain");

        ValueData? lifetime = Field(trails, "lifetime");
        if (lifetime is not null && lifetime.kind != "null")
        {
            if (lifetime.kind is "number" or "integer" or "specialNumber")
            {
                float val = RawNumber(lifetime, 1f);
                if (!float.IsFinite(val) || val <= 0f) reasons.Add("trails.lifetime.domain");
            }
            else
            {
                int lMode = Integer(Field(lifetime, "mode"));
                if (lMode == 2) reasons.Add("trails.lifetime.dualCurve");
                else if (lMode == 3) reasons.Add("trails.lifetime.variable");
                else if (lMode == 1) reasons.Add("trails.lifetime.curve");
                else if (lMode != 0) reasons.Add("trails.lifetime.domain");
                else
                {
                    float val = RawNumber(Field(lifetime, "constant"), 1f);
                    if (!float.IsFinite(val) || val <= 0f) reasons.Add("trails.lifetime.domain");
                }
            }
        }

        if (Boolean(Field(trails, "generateLightingData"))) reasons.Add("trails.generateLightingData");

        int texMode = Integer(Field(trails, "textureMode"), 0);
        if (texMode is not (0 or 1)) reasons.Add("trails.textureMode." + texMode);

        AddTrailWidthReasons(Field(trails, "widthOverTrail"), reasons);
        AddTrailGradientReasons(Field(trails, "colorOverLifetime"), "trails.colorOverLifetime", reasons);
        AddTrailGradientReasons(Field(trails, "colorOverTrail"), "trails.colorOverTrail", reasons);

        ValueData? trailMaterial = Member(renderer?.members, "trailMaterial");
        if (trailMaterial is null || trailMaterial.kind != "asset")
            reasons.Add("trails.trailMaterial.null");
    }

    private static void AddTrailWidthReasons(ValueData? widthCurve, List<string> reasons)
    {
        if (widthCurve is null || widthCurve.kind == "null") return;
        if (widthCurve.kind is "number" or "integer" or "specialNumber")
        {
            float w = RawNumber(widthCurve, 1f);
            if (!float.IsFinite(w) || w <= 0f) reasons.Add("trails.widthOverTrail.domain");
            return;
        }
        int mode = Integer(Field(widthCurve, "mode"));
        if (mode == 2) { reasons.Add("trails.widthOverTrail.dualCurve"); return; }
        if (mode == 3) { reasons.Add("trails.widthOverTrail.variable"); return; }
        if (mode == 0)
        {
            float w = RawNumber(Field(widthCurve, "constant"), 1f);
            if (!float.IsFinite(w) || w <= 0f) reasons.Add("trails.widthOverTrail.domain");
            return;
        }
        if (mode != 1) { reasons.Add("trails.widthOverTrail.domain"); return; }
        float mult = RawNumber(Field(widthCurve, "curveMultiplier"), 1f);
        List<ValueData> keys = Field(Field(widthCurve, "curve"), "keys")?.items ?? new List<ValueData>();
        if (keys.Count == 0 || !float.IsFinite(mult) || mult < 0f)
        {
            reasons.Add("trails.widthOverTrail.domain");
            return;
        }
        if (keys.Any(k => Integer(Field(k, "weightedMode")) != 0))
        {
            reasons.Add("trails.widthOverTrail.weighted");
            return;
        }
        foreach (ValueData key in keys)
        {
            float t = RawNumber(Field(key, "time"), 0f);
            float v = RawNumber(Field(key, "value"), 0f);
            float iTan = RawNumber(Field(key, "inTangent"), 0f);
            float oTan = RawNumber(Field(key, "outTangent"), 0f);
            if (!float.IsFinite(t) || t < 0f || t > 1f || !float.IsFinite(v) || v < 0f ||
                !float.IsFinite(iTan) || !float.IsFinite(oTan))
            {
                reasons.Add("trails.widthOverTrail.domain");
                return;
            }
        }
    }

    private static void AddTrailGradientReasons(ValueData? value, string prefix, List<string> reasons)
    {
        if (value is null || value.kind == "null") return;
        if (value.kind == "vector")
        {
            if (!ValidStoredRgba(value.vector)) reasons.Add(prefix + ".domain");
            return;
        }
        int mode = Integer(Field(value, "mode"));
        if (mode is 2 or 3 or 4) { reasons.Add(prefix + ".random"); return; }
        if (mode == 0)
        {
            ValueData? color = Field(value, "color");
            if (color is not null && color.kind != "null" && !ValidStoredRgba(color.vector))
                reasons.Add(prefix + ".domain");
            return;
        }
        if (mode != 1) { reasons.Add(prefix + ".domain"); return; }
        ValueData? gradient = Field(value, "gradient");
        if (Integer(Field(gradient, "mode")) == 2)
        {
            reasons.Add(prefix + ".perceptualBlend");
            return;
        }
        foreach (ValueData ck in Field(gradient, "colorKeys")?.items ?? new List<ValueData>())
        {
            float t = RawNumber(Field(ck, "time"), 0f);
            ValueData? c = Field(ck, "color");
            if (!float.IsFinite(t) || t < 0f || t > 1f || (c is not null && c.kind != "null" && !ValidStoredRgba(c.vector)))
            {
                reasons.Add(prefix + ".domain");
                return;
            }
        }
        foreach (ValueData ak in Field(gradient, "alphaKeys")?.items ?? new List<ValueData>())
        {
            float t = RawNumber(Field(ak, "time"), 0f);
            float a = RawNumber(Field(ak, "alpha"), 1f);
            if (!float.IsFinite(t) || t < 0f || t > 1f || !float.IsFinite(a) || a < 0f || a > 1f)
            {
                reasons.Add(prefix + ".domain");
                return;
            }
        }
    }

    private static bool ValidStoredRgba(float[]? v) =>
        v is { Length: 3 or 4 } &&
        float.IsFinite(v[0]) && v[0] >= 0f &&
        float.IsFinite(v[1]) && v[1] >= 0f &&
        float.IsFinite(v[2]) && v[2] >= 0f &&
        (v.Length < 4 || (float.IsFinite(v[3]) && v[3] >= 0f && v[3] <= 1f));
}
