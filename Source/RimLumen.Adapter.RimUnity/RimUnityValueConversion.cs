using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimUnity.Serialization;

namespace RimLumen.Adapter.RimUnity;

public static class RimUnityValueConversion
{
    internal static ValueData? Member(IEnumerable<MemberData>? members, string name) =>
        members?.LastOrDefault(member => member.name == name)?.value;
    internal static ValueData? Field(ValueData? value, string name) => Member(value?.fields, name);
    internal static bool Enabled(ValueData? module, bool defaultValue = false) => Boolean(Field(module, "enabled"), defaultValue);

    internal static float Number(ValueData? value, float fallback = 0f)
    {
        if (value is null || value.kind == "null") return fallback;
        float result = value.kind is "integer" or "specialNumber"
            ? float.Parse(value.text, CultureInfo.InvariantCulture) : (float)value.number;
        if (!float.IsFinite(result)) throw new InvalidDataException("Non-finite numeric export value.");
        return result;
    }
    internal static int Integer(ValueData? value, int fallback = 0) => checked((int)Number(value, fallback));
    internal static bool Boolean(ValueData? value, bool fallback = false) => value is null ? fallback : Number(value) != 0;
    internal static Vector4 Vector(ValueData? value, Vector4 fallback)
    {
        if (value is null || value.kind == "null") return fallback;
        float[] v = value.vector;
        if (v is null || v.Length < 3 || v.Length > 4 || v.Any(n => !float.IsFinite(n)))
            throw new InvalidDataException("Invalid exported vector.");
        return new Vector4(v[0], v[1], v[2], v.Length == 4 ? v[3] : fallback.W);
    }

    /// <summary>RGB * intensity reconstructs the input exactly; alpha is not scaled.</summary>
    public static Vector4 ExtractHdrIntensity(Vector4 color, out float intensity)
    {
        intensity = MathF.Max(1f, MathF.Max(color.X, MathF.Max(color.Y, color.Z)));
        return new Vector4(color.X / intensity, color.Y / intensity, color.Z / intensity, color.W);
    }

    /// <summary>Two-constant/two-curve values select the upper branch, matching Evaluate(time, 1).</summary>
    public static ScalarCurve ToScalarCurve(ValueData? value, float fallback = 1f)
    {
        if (value is null) return ScalarCurve.Constant(fallback);
        if (value.kind is "number" or "integer") return ScalarCurve.Constant(Number(value));
        int mode = Integer(Field(value, "mode"));
        if (mode is 0 or 3)
            return ScalarCurve.Constant(Number(Field(value, "constant") ?? Field(value, "constantMax")));
        if (mode is not (1 or 2)) throw new InvalidDataException("Unknown MinMaxCurve mode: " + mode);
        ValueData? curve = Field(value, "curve") ?? Field(value, "curveMax");
        CurveKey[] keys = ReadKeys(curve);
        return new ScalarCurve(keys, Number(Field(value, "curveMultiplier")));
    }

    private static CurveKey[] ReadKeys(ValueData? curve)
    {
        List<ValueData>? items = Field(curve, "keys")?.items;
        if (items is null) return Array.Empty<CurveKey>();
        var keys = new CurveKey[items.Count];
        for (int i = 0; i < keys.Length; i++)
        {
            ValueData key = items[i];
            if (Integer(Field(key, "weightedMode")) != 0)
                throw new NotSupportedException("Weighted animation curve keys require external playback.");
            keys[i] = new CurveKey(Number(Field(key, "time")), Number(Field(key, "value")),
                Number(Field(key, "inTangent")), Number(Field(key, "outTangent")));
        }
        Array.Sort(keys, (a, b) => a.Time.CompareTo(b.Time));
        // RimUnity 45ca90987f7d3721715b45c021cab986ee14c468, Source/RimUnity/Animation/AnimationCurve.cs:13,23:
        // an all-equal-time curve is constant at the last value after the same sort, regardless of tangents.
        if (keys.Length > 1 && keys[0].Time == keys[^1].Time)
            return new[] { new CurveKey(keys[^1].Time, keys[^1].Value) };
        return keys;
    }

    public static (float Min, float Max) CurveRange(ValueData? value, float fallback = 1f)
    {
        if (value is null) return (fallback, fallback);
        int mode = Integer(Field(value, "mode"));
        if (mode == 3)
        {
            float min = Number(Field(value, "constantMin"));
            float max = Number(Field(value, "constantMax"));
            return (MathF.Min(min, max), MathF.Max(min, max));
        }
        var range = Extrema(ToScalarCurve(value, fallback));
        if (mode == 2)
        {
            var lower = Extrema(new ScalarCurve(ReadKeys(Field(value, "curveMin")), Number(Field(value, "curveMultiplier"))));
            range = (MathF.Min(range.Min, lower.Min), MathF.Max(range.Max, lower.Max));
        }
        return range;
    }

    private static (float Min, float Max) Extrema(ScalarCurve curve)
    {
        if (curve.Keys.Length == 0) return (0, 0);
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        void Include(float value) { value *= curve.Multiplier; min = MathF.Min(min, value); max = MathF.Max(max, value); }
        for (int i = 0; i < curve.Keys.Length; i++)
        {
            CurveKey left = curve.Keys[i];
            Include(left.Value);
            if (i + 1 == curve.Keys.Length) continue;
            CurveKey right = curve.Keys[i + 1];
            float dt = right.Time - left.Time;
            if (dt <= 0) continue;
            float a = 2 * left.Value - 2 * right.Value + dt * (left.OutTangent + right.InTangent);
            float b = -3 * left.Value + 3 * right.Value - dt * (2 * left.OutTangent + right.InTangent);
            float c = dt * left.OutTangent;
            void Root(float t) { if (t > 0 && t < 1) Include(((a * t + b) * t + c) * t + left.Value); }
            if (MathF.Abs(a) < 1e-7f) { if (MathF.Abs(b) > 1e-7f) Root(-c / (2 * b)); }
            else
            {
                float discriminant = 4 * b * b - 12 * a * c;
                if (discriminant >= 0)
                {
                    float sqrt = MathF.Sqrt(discriminant);
                    Root((-2 * b + sqrt) / (6 * a));
                    Root((-2 * b - sqrt) / (6 * a));
                }
            }
        }
        return (min, max);
    }

    /// <summary>Combines independently keyed RGB/alpha in stored encoding; two gradients use the upper branch.</summary>
    public static ColorGradient ToColorGradient(ValueData? value)
    {
        if (value is null) return ColorGradient.Constant(Vector4.One);
        if (value.kind == "vector") return ColorGradient.Constant(Vector(value, Vector4.One));
        int mode = Integer(Field(value, "mode"));
        if (mode is 0 or 2)
            return ColorGradient.Constant(Vector(Field(value, "color") ?? Field(value, "colorMax"), Vector4.Zero));
        if (mode is not (1 or 3 or 4)) throw new InvalidDataException("Unknown MinMaxGradient mode: " + mode);
        ValueData? gradient = Field(value, "gradient") ?? Field(value, "gradientMax");
        var colors = (Field(gradient, "colorKeys")?.items ?? new List<ValueData>())
            .Select(key => new ColorStop(Number(Field(key, "time")), Vector(Field(key, "color"), Vector4.One)))
            .OrderBy(key => key.Time).ToArray();
        var alphas = (Field(gradient, "alphaKeys")?.items ?? new List<ValueData>())
            .Select(key => (Time: Number(Field(key, "time")), Alpha: Number(Field(key, "alpha"), 1f)))
            .OrderBy(key => key.Time).ToArray();
        float[] times = colors.Select(key => key.Time).Concat(alphas.Select(key => key.Time)).Append(0).Append(1).Distinct().Order().ToArray();
        bool fixedMode = Integer(Field(gradient, "mode")) == 1;
        var stops = new List<ColorStop>();
        foreach (float time in times)
        {
            float before = MathF.BitDecrement(time);
            if (fixedMode && stops.Count > 0 && before > stops[^1].Time)
                stops.Add(new ColorStop(before, EvaluateGradient(colors, alphas, before, true)));
            stops.Add(new ColorStop(time, EvaluateGradient(colors, alphas, time, fixedMode)));
        }
        return new ColorGradient(stops.ToArray());
    }

    private static Vector4 EvaluateGradient(ColorStop[] colors, (float Time, float Alpha)[] alphas, float time, bool fixedMode)
    {
        Vector4 color = colors.Length == 0 ? Vector4.One : colors[0].Rgba;
        for (int i = 1; i < colors.Length; i++)
        {
            if (time >= colors[i].Time) { color = colors[i].Rgba; continue; }
            if (!fixedMode) color = Vector4.Lerp(colors[i - 1].Rgba, colors[i].Rgba,
                Fraction(time, colors[i - 1].Time, colors[i].Time));
            break;
        }
        float alpha = alphas.Length == 0 ? 1f : alphas[0].Alpha;
        for (int i = 1; i < alphas.Length; i++)
        {
            if (time >= alphas[i].Time) { alpha = alphas[i].Alpha; continue; }
            if (!fixedMode) alpha = float.Lerp(alphas[i - 1].Alpha, alphas[i].Alpha, Fraction(time, alphas[i - 1].Time, alphas[i].Time));
            break;
        }
        color.W = alpha;
        return color;
    }

    private static float Fraction(float time, float start, float end) => end <= start ? 1 : Math.Clamp((time - start) / (end - start), 0, 1);
}
