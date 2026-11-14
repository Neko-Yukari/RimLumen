using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using Verse;

namespace RimLumen.Core.Lighting;

/// <summary>
/// Drives <c>GlowGrid</c> custom glowers for spell-attached point lights at frame rate, with
/// dirty-state deduplication so a light that sits still at steady intensity produces exactly one
/// Register call and zero Deregister calls.
/// </summary>
public sealed class MapGlowerDriver : IDisposable
{
    private readonly IGlowGridSink sink;
    private readonly Dictionary<int, Light> lights = new();
    private int nextId;
    private bool isDisposed;

    private sealed class Light
    {
        public readonly PointLightSpec Spec;
        public readonly float BaseMultiplier;
        public int? Handle;
        public Snapshot Registered;

        public Light(in PointLightSpec spec, float baseMultiplier)
        {
            Spec = spec;
            BaseMultiplier = baseMultiplier;
        }
    }

    private readonly record struct Snapshot(IntVec3 Cell, float Radius, float OverlightRadius, ColorInt Color);

    // Requires: sink is non-null.
    public MapGlowerDriver(IGlowGridSink sink) => this.sink = sink;

    // Ensures: number of lights tracked by this driver, registered or not.
    public int TrackedLightCount => lights.Count;

    // Ensures: true after Dispose.
    public bool IsDisposed => isDisposed;

    // Requires: spec.GlowRadius and spec.OverlightRadius are finite; intensityMultiplier is finite.
    // Ensures: tracks the light and returns a non-negative id; nothing is registered until the first
    //          Update supplies a cell. intensityMultiplier is this light's constant brightness scale and
    //          composes with the per-frame Update multiplier. Throws ObjectDisposedException after Dispose.
    public int Add(in PointLightSpec spec, float intensityMultiplier = 1f)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        int id = nextId++;
        lights.Add(id, new Light(spec, intensityMultiplier));
        return id;
    }

    // Requires: elapsedSeconds is the light's age in seconds (drives IntensityOverTime).
    // Ensures: effective intensity = Add's base multiplier * intensityMultiplier *
    //          (IntensityOverTime is null ? 1 : CurveEvaluator.Evaluate(IntensityOverTime, elapsedSeconds)).
    // Ensures: effective intensity <= 0 deregisters the light and leaves it unregistered.
    // Ensures: radii are clamped to [0, 40] and quantized to 0.05 cells; colour is
    //          spec.ColorRgb * effective intensity clamped to [0,1], mapped to 0..255 and quantized to
    //          a step of 4 per channel (ColorInt alpha 255).
    // Ensures: a registered light is re-registered only when its IntVec3 cell changed or its quantized
    //          colour / glowRadius / overlightRadius changed: a stationary light at steady intensity
    //          yields zero further sink calls.
    // Ensures: unknown ids and calls after Dispose are no-ops.
    public void Update(int lightId, IntVec3 cell, float elapsedSeconds, float intensityMultiplier)
    {
        if (isDisposed || !lights.TryGetValue(lightId, out var light))
        {
            return;
        }

        var spec = light.Spec;
        float intensity = light.BaseMultiplier * intensityMultiplier * (spec.IntensityOverTime is null
            ? 1f : CurveEvaluator.Evaluate(spec.IntensityOverTime, elapsedSeconds));
        if (intensity <= 0f)
        {
            Deregister(light);
            return;
        }

        var color = spec.ColorRgb * intensity;
        var snapshot = new Snapshot(cell, QuantizeRadius(spec.GlowRadius), QuantizeRadius(spec.OverlightRadius),
            new ColorInt(QuantizeChannel(color.X), QuantizeChannel(color.Y), QuantizeChannel(color.Z), 255));
        if (light.Handle.HasValue && light.Registered == snapshot)
        {
            return;
        }

        Deregister(light);
        light.Handle = sink.Register(snapshot.Cell, snapshot.Radius, snapshot.Color, snapshot.OverlightRadius);
        light.Registered = snapshot;
    }

    // Ensures: deregisters the light when registered, then stops tracking it. Unknown ids are ignored.
    public void Remove(int lightId)
    {
        if (lights.TryGetValue(lightId, out var light))
        {
            Deregister(light);
            lights.Remove(lightId);
        }
    }

    // Ensures: deregisters every tracked light (map unload / handle teardown) and makes the driver inert.
    public void Dispose()
    {
        foreach (var light in lights.Values)
        {
            Deregister(light);
        }
        lights.Clear();
        isDisposed = true;
    }

    // GlowGrid is a ground-cell grid: floor X/Z, ignoring visual altitude.
    public static IntVec3 CellFromWorld(Vector3 worldPosition) => new(
        (int)MathF.Floor(worldPosition.X), 0, (int)MathF.Floor(worldPosition.Z));

    private void Deregister(Light light)
    {
        if (light.Handle is not int handle)
        {
            return;
        }
        sink.Deregister(handle);
        light.Handle = null;
    }

    private static float QuantizeRadius(float radius) =>
        MathF.Round(Math.Clamp(radius, 0f, 40f) * 20f, MidpointRounding.AwayFromZero) / 20f;

    private static int QuantizeChannel(float channel)
    {
        float value = MathF.Round(Math.Clamp(channel, 0f, 1f) * 255f, MidpointRounding.AwayFromZero);
        return Math.Clamp((int)MathF.Round(value / 4f, MidpointRounding.AwayFromZero) * 4, 0, 255);
    }
}
