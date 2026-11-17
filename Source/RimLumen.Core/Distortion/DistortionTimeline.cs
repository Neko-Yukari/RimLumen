using System;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Distortion;

// This frame's shader inputs for one distortion node. Distances are node-local cells, amplitudes signed node-local
// cells of apparent outward displacement of the scene behind the node (the backend scales both by the node's world
// scale). RingRadii/RingAmplitudes: up to RingCount rings, amplitude 0 = absent. HazeAmplitude 0 for ring kinds.
public readonly record struct DistortionFrame(Vector4 RingRadii, Vector4 RingAmplitudes, float HazeAmplitude, float HazePhase);

// Time behaviour of DistortionSpec. t is seconds since the node's last (re)start.
public static class DistortionTimeline
{
    public const int RingCount = 4;
    // Ripple ring k trails the front by k / Frequency cells with RippleRingFactor^k of the front's amplitude.
    public const float RippleRingFactor = -0.5f;
    // HeatHaze noise lattice steps per second.
    public const float HazeEvolveRate = 1f;

    // Ensures: nothing shows before the first restart; afterwards a node with Duration > 0 shows while t < Duration
    //          whether or not it is still active (a wave already launched finishes), and a node with Duration 0 shows
    //          exactly while it is active.
    public static bool IsVisible(DistortionSpec spec, bool started, float time, bool active)
        => started && (spec.Duration > 0f ? time < spec.Duration : active);

    // Requires: spec validated by LumenAssetRules; time >= 0; intensity finite >= 0.
    // Ensures: A = Strength * intensity * StrengthOverTime(t) (1 without a curve).
    //   RingWave: one ring at r0 = Speed * t with amplitude A * min(1, r0 / Thickness).
    //   Ripple:   front r0 = Speed * t; ring k (0..RingCount-1) at rk = r0 - k / Frequency with amplitude
    //             A * RippleRingFactor^k * min(1, rk / Thickness) once rk > 0, else absent (radius 0, amplitude 0).
    //   HeatHaze: no rings; HazeAmplitude = A, HazePhase = t * HazeEvolveRate.
    public static DistortionFrame Evaluate(DistortionSpec spec, float time, float intensity)
    {
        float amplitude = spec.Strength * intensity
            * (spec.StrengthOverTime is { } curve ? CurveEvaluator.Evaluate(curve, time) : 1f);
        if (spec.Kind == LumenDistortionKind.HeatHaze)
            return new DistortionFrame(Vector4.Zero, Vector4.Zero, amplitude, time * HazeEvolveRate);
        float front = spec.Speed * time;
        if (spec.Kind == LumenDistortionKind.RingWave)
            return new DistortionFrame(new Vector4(front, 0f, 0f, 0f),
                new Vector4(amplitude * MathF.Min(1f, front / spec.Thickness), 0f, 0f, 0f), 0f, 0f);
        Vector4 radii = Vector4.Zero;
        Vector4 amplitudes = Vector4.Zero;
        float factor = 1f;
        for (int k = 0; k < RingCount; k++, factor *= RippleRingFactor)
        {
            float radius = front - k / spec.Frequency;
            if (radius <= 0f) break;
            radii[k] = radius;
            amplitudes[k] = amplitude * factor * MathF.Min(1f, radius / spec.Thickness);
        }
        return new DistortionFrame(radii, amplitudes, 0f, 0f);
    }
}
