using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Distortion;
using RimLumen.Core.Scene;

namespace RimLumen.WebSim;

public readonly record struct WebDistortionSnapshot(
    Vector3 WorldPosition,
    Vector3 WorldScale,
    LumenDistortionKind Kind,
    float Radius,
    float Strength,
    float Thickness,
    float Speed,
    float Frequency,
    float Elapsed,
    DistortionFrame Frame);

internal sealed class WebDistortionBackend : ILumenNodeFeatureBackend
{
    private sealed class Instance(DistortionSpec spec, LumenNodeFeatureFrame frame)
    {
        internal readonly DistortionSpec Spec = spec;
        internal LumenNodeFeatureFrame Frame = frame;
        internal NodeFeatureClock Clock;
    }

    private readonly Dictionary<int, Instance> instances = new();
    private readonly List<WebDistortionSnapshot> activeSnapshots = new();
    private int nextId;

    public LumenNodeFeature Feature => LumenNodeFeature.Distortion;

    public int CreateInstance(EffectNodeSpec node, in LumenNodeFeatureFrame initial, bool realTime, uint seed)
    {
        int id = nextId++;
        instances.Add(id, new Instance(node.Distortion!, initial));
        return id;
    }

    public void RestartInstance(int instanceId) => instances[instanceId].Clock.Restart();

    public void UpdateInstance(int instanceId, in LumenNodeFeatureFrame frame)
    {
        Instance instance = instances[instanceId];
        instance.Frame = frame;
        instance.Clock.Update(frame);
        if (instance.Clock.IsDrained(instance.Spec.Duration)) instances.Remove(instanceId);
    }

    public void DestroyInstance(int instanceId, bool immediate)
    {
        if (immediate)
        {
            instances.Remove(instanceId);
            return;
        }
        Instance instance = instances[instanceId];
        instance.Clock.Drain();
        if (instance.Clock.IsDrained(instance.Spec.Duration)) instances.Remove(instanceId);
    }

    public bool IsInstanceAlive(int instanceId) => instances.ContainsKey(instanceId);

    public IReadOnlyList<WebDistortionSnapshot> EvaluateActiveSnapshots(int mapIndex)
    {
        activeSnapshots.Clear();
        foreach (Instance instance in instances.Values)
        {
            if (instance.Frame.MapIndex != mapIndex || !instance.Clock.IsVisible(instance.Spec.Duration)) continue;
            DistortionSpec spec = instance.Spec;
            float t = instance.Clock.Time;
            DistortionFrame evaluated = DistortionTimeline.Evaluate(spec, t, instance.Frame.Intensity);
            Matrix4x4 world = instance.Frame.Transform.World;
            float sx = new Vector3(world.M11, world.M12, world.M13).Length();
            float sy = new Vector3(world.M21, world.M22, world.M23).Length();
            float sz = new Vector3(world.M31, world.M32, world.M33).Length();
            activeSnapshots.Add(new WebDistortionSnapshot(
                world.Translation,
                new Vector3(sx, sy, sz),
                spec.Kind,
                spec.Radius,
                spec.Strength,
                spec.Thickness,
                spec.Speed,
                spec.Frequency,
                t,
                evaluated));
        }
        return activeSnapshots;
    }

    public int EvaluateActive(int mapIndex) => EvaluateActiveSnapshots(mapIndex).Count;
}

internal sealed class WebScreenDimBackend : ILumenNodeFeatureBackend
{
    private sealed class Instance(ScreenDimSpec spec, LumenNodeFeatureFrame frame)
    {
        internal readonly ScreenDimSpec Spec = spec;
        internal LumenNodeFeatureFrame Frame = frame;
        internal NodeFeatureClock Clock;
    }

    private readonly Dictionary<int, Instance> instances = new();
    private int nextId;

    public LumenNodeFeature Feature => LumenNodeFeature.ScreenDim;

    public int CreateInstance(EffectNodeSpec node, in LumenNodeFeatureFrame initial, bool realTime, uint seed)
    {
        int id = nextId++;
        instances.Add(id, new Instance(node.ScreenDim!, initial));
        return id;
    }

    public void RestartInstance(int instanceId) => instances[instanceId].Clock.Restart();

    public void UpdateInstance(int instanceId, in LumenNodeFeatureFrame frame)
    {
        Instance instance = instances[instanceId];
        instance.Frame = frame;
        instance.Clock.Update(frame);
        if (instance.Clock.IsDrained(instance.Spec.Duration)) instances.Remove(instanceId);
    }

    public void DestroyInstance(int instanceId, bool immediate)
    {
        if (immediate)
        {
            instances.Remove(instanceId);
            return;
        }
        Instance instance = instances[instanceId];
        instance.Clock.Drain();
        if (instance.Clock.IsDrained(instance.Spec.Duration)) instances.Remove(instanceId);
    }

    public bool IsInstanceAlive(int instanceId) => instances.ContainsKey(instanceId);

    public float EvaluateCoverage(int mapIndex)
    {
        float uncovered = 1f;
        foreach (Instance instance in instances.Values)
        {
            ScreenDimSpec spec = instance.Spec;
            if (instance.Frame.MapIndex != mapIndex || !instance.Clock.IsVisible(spec.Duration)) continue;
            float curve = spec.StrengthOverTime is { } strength ? CurveEvaluator.Evaluate(strength, instance.Clock.Time) : 1f;
            uncovered *= 1f - MathF.Min(1f, spec.Strength * instance.Frame.Intensity * curve);
        }
        return 1f - uncovered;
    }
}
