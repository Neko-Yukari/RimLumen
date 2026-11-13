using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Lighting;

namespace RimLumen.Core.Scene;

/// <summary>
/// Runtime instance of one played <see cref="EffectAsset"/>: owns the per-node world transforms, the
/// emitter bindings (L1 or L2), the point-light bindings and the timeline playback.
/// Created by <see cref="LumenVfxService.Play"/>; advanced once per frame by the service.
/// Time starts at 0 on creation (Play does not simulate); node matrices and emitter bindings are
/// established in the constructor, so a caller can inspect them before the first frame.
/// </summary>
public sealed class EffectInstanceHandle : ILumenEffectHandle
{
    private enum PlaybackState { Playing, Draining, Dead }
    private readonly LumenVfxService owner;
    private readonly EffectAsset asset;
    private readonly LumenPlayRequest request;
    private readonly Matrix4x4[] nodeWorldMatrices;
    private readonly int[] cpuIds;
    private readonly int[] gpuIds;
    private readonly int[] lightIds;
    private readonly bool[] nodeEmitting;
    private readonly ILumenGpuParticleEngine? engine;
    private readonly bool looping;
    private readonly Dictionary<string, float> parameters = new(StringComparer.Ordinal)
    {
        ["intensity"] = 1f, ["rate"] = 1f, ["scale"] = 1f,
    };
    private MapGlowerDriver? driver;
    private PlaybackState state;
    private Vector3 origin;
    private float rotation;
    private float playbackTime;
    private float fadeTime;
    private Matrix4x4 rootWorld;
    private int eventFireCount;

    internal EffectInstanceHandle(LumenVfxService owner, EffectAsset asset, in LumenPlayRequest request)
    {
        this.owner = owner;
        this.asset = asset;
        this.request = request;
        nodeWorldMatrices = new Matrix4x4[asset.Nodes.Length];
        cpuIds = new int[asset.Nodes.Length];
        gpuIds = new int[asset.Nodes.Length];
        lightIds = new int[asset.Nodes.Length];
        nodeEmitting = new bool[asset.Nodes.Length];
        Array.Fill(cpuIds, -1);
        Array.Fill(gpuIds, -1);
        Array.Fill(lightIds, -1);
        origin = request.OriginWorld;
        rotation = request.TargetWorld is Vector3 target
            ? SpacePlaneTransform.ComputeFacingDegrees(origin, target) : request.RotationDegrees;
        RefreshTransforms();
        engine = owner.GpuEngineProvider?.Invoke();
        driver = owner.GlowerDriverProvider?.Invoke(request.MapIndex);
        for (int i = 0; i < asset.Nodes.Length; i++)
        {
            var node = asset.Nodes[i];
            if (node.Emitter is not { } spec) continue;
            looping |= spec.Looping;
            nodeEmitting[i] = request.InitialState is null || node.Name == request.InitialState;
            if (spec.PreferredTier == LumenParticleTier.L2_GpuCompute && engine is not null)
            {
                gpuIds[i] = engine.CreateInstance(spec, nodeWorldMatrices[i]);
                if (!nodeEmitting[i]) engine.UpdateInstance(gpuIds[i], nodeWorldMatrices[i], false);
            }
            else
            {
                cpuIds[i] = owner.Simulator.AcquireEmitter(spec, nodeWorldMatrices[i], emitting: true, rateMultiplier: 1f);
                if (!nodeEmitting[i]) owner.Simulator.SetEmitterParams(cpuIds[i], false, 1f, 1f);
            }
        }
    }

    public string EffectId => asset.EffectId;

    // Ensures: true until the handle is stopped immediately/disposed, or until a fade-out stop has
    //          released every emitter and every light.
    public bool IsAlive => state != PlaybackState.Dead;

    // Ensures: false after Stop(true) (no new particles/lights) and after any immediate stop.
    public bool IsEmitting => state == PlaybackState.Playing;

    // Ensures: seconds since creation, advanced by the per-frame delta; never wrapped.
    public float PlaybackTime => playbackTime;

    // Ensures: the root world matrix for the current frame:
    //          S(request.Scale * scaleParam) * R_y(facing) * T(origin), with
    //          facing = Follow's degrees when the provider yields, else ComputeFacingDegrees(origin, TargetWorld)
    //          when TargetWorld is set, else request.RotationDegrees.
    public Matrix4x4 RootWorld => rootWorld;

    // Ensures: one entry per EffectAsset.Nodes entry, parent composed, as of the current frame.
    public ReadOnlySpan<Matrix4x4> NodeWorldMatrices => nodeWorldMatrices;

    // Ensures: emitters currently routed to CpuParticleSimulator / to LumenHostRegistry.GpuParticleEngine.
    public int CpuEmitterCount => CountBindings(cpuIds);
    public int GpuEmitterCount => CountBindings(gpuIds);

    // Ensures: point lights currently registered on a MapGlowerDriver.
    public int LightCount => driver is { IsDisposed: false } ? CountBindings(lightIds) : 0;

    // Ensures: number of timeline events fired so far (automatic crossings plus manual Trigger hits).
    public int TimelineEventFireCount => eventFireCount;

    // Requires: eventOrStateName is non-empty.
    // Ensures: for every TimelineEventSpec whose EventName matches: fires it immediately (counts it and
    //          emits one burst on TargetNodeIndex's emitter when that node has one: Burst[0].Count if
    //          the emitter declares bursts, else round(RateOverTime), clamped to [0, MaxParticles]).
    //          For every node whose Name matches: activates that node's emitter and emits the same burst.
    //          Unmatched names are a no-op (WotF may trigger animator states RimLumen does not own).
    //          L2 emitters are only activated: ILumenGpuParticleEngine exposes no burst entry point.
    //          Safe no-op once the handle is dead.
    public void Trigger(string eventOrStateName)
    {
        if (!IsEmitting) return;
        foreach (var timelineEvent in asset.TimelineEvents)
            if (timelineEvent.EventName == eventOrStateName) FireEvent(timelineEvent);
        for (int i = 0; i < asset.Nodes.Length; i++)
            if (asset.Nodes[i].Name == eventOrStateName) BurstNode(i, activate: true);
    }

    // Requires: name is non-empty; value is finite.
    // Ensures: "intensity" scales light colour and particle vertex colour, "rate" scales emission rate,
    //          "scale" multiplies the root scale. Unknown names are recorded and retrievable through
    //          TryGetParam without affecting simulation. Safe no-op once the handle is dead.
    public void SetParam(string name, float value)
    {
        if (IsAlive) parameters[name] = value;
    }

    // Ensures: allowFadeOut == true stops new emission immediately, fades the point lights out over
    //          EffectInstanceHandle.LightFadeSeconds, and lets live particles finish; the handle becomes
    //          dead once nothing is left. allowFadeOut == false tears down emitters and lights now.
    //          L1 emitters gate handle death on their own drained particle count. ILumenGpuParticleEngine
    //          exposes no per-instance liveness, so an L2 emitter is handed to DestroyInstance(id, false)
    //          and stops being tracked at once: the engine owns the GPU-side drain.
    public void Stop(bool allowFadeOut = true)
    {
        if (!IsAlive || (allowFadeOut && state == PlaybackState.Draining)) return;
        state = allowFadeOut ? PlaybackState.Draining : PlaybackState.Dead;
        for (int i = 0; i < asset.Nodes.Length; i++)
        {
            if (cpuIds[i] >= 0)
            {
                owner.Simulator.ReleaseEmitter(cpuIds[i], immediate: !allowFadeOut);
                if (!allowFadeOut || owner.Simulator.GetLiveParticleCount(cpuIds[i]) == 0) cpuIds[i] = -1;
            }
            if (gpuIds[i] >= 0)
            {
                engine!.DestroyInstance(gpuIds[i], immediate: !allowFadeOut);
                gpuIds[i] = -1;
            }
        }
        if (!allowFadeOut) RemoveLights();
        FinishDrain();
    }

    // Ensures: equivalent to Stop(false); idempotent.
    public void Dispose() => Stop(false);

    // Ensures: exposes a value stored by SetParam, including the three built-in names.
    public bool TryGetParam(string name, out float value) => parameters.TryGetValue(name, out value);

    // Ensures: seconds over which Stop(true) fades point lights to zero before removing them.
    public const float LightFadeSeconds = 0.25f;

    // Requires: called once per frame by LumenVfxService.CaptureMainThread on the main thread.
    // Ensures, in this order:
    //   1. playbackTime += deltaTime; when a Follow provider exists it refreshes origin/rotation, and a
    //      provider that yields false keeps the last known transform (detach = freeze, never snap back).
    //   2. node world matrices are recomposed (SpacePlaneTransform.BuildWorldMatrices).
    //   3. timeline events whose TimeSeconds lies in (previousPlaybackTime, playbackTime] fire once each;
    //      a looping asset (any emitter with Looping) rewinds them so the next cycle fires them again.
    //   4. every emitter binding is pushed to its backend (SetEmitterTransform/SetEmitterParams or
    //      UpdateInstance), every light binding calls MapGlowerDriver.Update with this frame's cell.
    //   5. a fade-out stop that has finished (all emitters released, lights removed) marks the handle dead.
    internal void Advance(float deltaTime)
    {
        if (!IsAlive) return;
        float previousTime = playbackTime;
        playbackTime += deltaTime;
        RefreshTransforms();
        if (IsEmitting)
        {
            foreach (var timelineEvent in asset.TimelineEvents)
            {
                if (looping && asset.DefaultDuration > 0f)
                {
                    if (timelineEvent.TimeSeconds < 0f || timelineEvent.TimeSeconds > asset.DefaultDuration) continue;
                    double cycle = Math.Max(0d, Math.Floor(((double)previousTime - timelineEvent.TimeSeconds) / asset.DefaultDuration) + 1d);
                    for (double time = timelineEvent.TimeSeconds + cycle * asset.DefaultDuration;
                         time <= playbackTime; time += asset.DefaultDuration)
                        FireEvent(timelineEvent);
                }
                else if (previousTime < timelineEvent.TimeSeconds && timelineEvent.TimeSeconds <= playbackTime)
                {
                    FireEvent(timelineEvent);
                }
            }
        }
        else
        {
            fadeTime += deltaTime;
        }
        float intensity = parameters["intensity"];
        for (int i = 0; i < asset.Nodes.Length; i++)
        {
            if (cpuIds[i] >= 0)
            {
                owner.Simulator.SetEmitterTransform(cpuIds[i], nodeWorldMatrices[i]);
                owner.Simulator.SetEmitterParams(cpuIds[i], IsEmitting && nodeEmitting[i], parameters["rate"], intensity);
                if (!IsEmitting && owner.Simulator.GetLiveParticleCount(cpuIds[i]) == 0) cpuIds[i] = -1;
            }
            if (gpuIds[i] >= 0)
                engine!.UpdateInstance(gpuIds[i], nodeWorldMatrices[i], IsEmitting && nodeEmitting[i], intensity);
        }
        UpdateLights(intensity);
        FinishDrain();
    }

    private void RefreshTransforms()
    {
        if (request.Follow is { } follow && follow(out var position, out float facing))
        {
            origin = position;
            rotation = facing;
        }
        rootWorld = SpacePlaneTransform.BuildRootMatrix(origin, rotation, request.Scale * parameters["scale"]);
        SpacePlaneTransform.BuildWorldMatrices(asset, rootWorld, nodeWorldMatrices);
    }

    private void FireEvent(TimelineEventSpec timelineEvent)
    {
        eventFireCount++;
        int node = timelineEvent.TargetNodeIndex;
        if (node >= 0 && node < asset.Nodes.Length) BurstNode(node, activate: false);
    }

    private void BurstNode(int index, bool activate)
    {
        if (asset.Nodes[index].Emitter is not { } spec) return;
        if (activate || gpuIds[index] >= 0) nodeEmitting[index] = true;
        if (cpuIds[index] >= 0)
        {
            owner.Simulator.SetEmitterTransform(cpuIds[index], nodeWorldMatrices[index]);
            owner.Simulator.SetEmitterParams(cpuIds[index], nodeEmitting[index], parameters["rate"], parameters["intensity"]);
            int count = spec.Bursts.Length > 0 ? Math.Clamp(spec.Bursts[0].Count, 0, spec.MaxParticles)
                : (int)Math.Clamp(MathF.Round(spec.RateOverTime), 0f, spec.MaxParticles);
            owner.Simulator.EmitBurst(cpuIds[index], count);
        }
        if (gpuIds[index] >= 0)
            engine!.UpdateInstance(gpuIds[index], nodeWorldMatrices[index], true, parameters["intensity"]);
    }

    private void UpdateLights(float intensity)
    {
        var resolved = owner.GlowerDriverProvider?.Invoke(request.MapIndex);
        if (driver is { IsDisposed: true })
        {
            Array.Fill(lightIds, -1);
            driver = null;
        }
        if (IsEmitting && resolved is { IsDisposed: false } && !ReferenceEquals(driver, resolved))
        {
            RemoveLights();
            driver = resolved;
        }
        if (driver is null) return;
        float fadeMultiplier = IsEmitting ? 1f : Math.Clamp(1f - fadeTime / LightFadeSeconds, 0f, 1f);
        for (int i = 0; i < asset.Nodes.Length; i++)
        {
            if (IsEmitting && lightIds[i] < 0 && asset.Nodes[i].PointLight is { } spec)
                lightIds[i] = driver.Add(spec);
            if (lightIds[i] >= 0)
                driver.Update(lightIds[i], MapGlowerDriver.CellFromWorld(nodeWorldMatrices[i].Translation),
                    playbackTime, intensity * fadeMultiplier);
        }
        if (!IsEmitting && fadeMultiplier == 0f) RemoveLights();
    }

    private void RemoveLights()
    {
        for (int i = 0; i < lightIds.Length; i++)
        {
            if (lightIds[i] >= 0) driver!.Remove(lightIds[i]);
            lightIds[i] = -1;
        }
    }

    private void FinishDrain()
    {
        if (state == PlaybackState.Draining && CpuEmitterCount == 0 && GpuEmitterCount == 0 && LightCount == 0)
            state = PlaybackState.Dead;
    }

    private static int CountBindings(int[] ids)
    {
        int count = 0;
        foreach (int id in ids)
            if (id >= 0) count++;
        return count;
    }
}
