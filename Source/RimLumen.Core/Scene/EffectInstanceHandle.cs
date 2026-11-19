using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Lighting;
using RimLumen.Core.ParticlesGpu;

namespace RimLumen.Core.Scene;

/// <summary>
/// Runtime instance of one played <see cref="EffectAsset"/>: owns the per-node world transforms, the
/// emitter bindings (L1 or L2), the visual light pools and the state-machine playback.
/// Created by <see cref="LumenVfxService.Play"/>; advanced once per frame by the service.
/// Time starts at 0 on creation (Play does not simulate); node matrices, emitter bindings and the initial
/// state are established in the constructor, so a caller can inspect them before the first frame.
/// </summary>
public sealed class EffectInstanceHandle : ILumenEffectHandle
{
    private enum PlaybackState { Playing, Draining, Dead }
    private enum EventKind : byte { Notify, StateChanged, Finished }
    private readonly LumenVfxService owner;
    private readonly EffectAsset asset;
    private readonly EffectStateGraph graph;
    private readonly LumenPlayRequest request;
    private readonly Matrix4x4[] nodeWorldMatrices;
    private readonly Quaternion[] nodeWorldRotations;
    private readonly int[] cpuIds;
    private readonly int[] gpuIds;
    // A node's light is on from the frame its node emits until its LightFadeSeconds fade has ended.
    private readonly bool[] lightOn;
    // This frame's ColorRgb * effective intensity for every node whose light is on.
    private readonly Vector3[] lightRgb;
    private readonly bool[] nodeActive;
    private readonly float[] nodeFrameDelta;
    // PlaybackTime of the node's last (re)start: the origin of its light's IntensityOverTime curve.
    private readonly float[] nodeActivatedAt;
    // PlaybackTime the node stopped emitting: the origin of its light's LightFadeSeconds fade.
    private readonly float[] nodeDeactivatedAt;
    private readonly ILumenGpuParticleEngine? engine;
    // Indexed by LumenNodeFeature: the backend resolved at Play (null when no node carries the feature) and, per node,
    // its backend instance id (-1 = unbound).
    private readonly ILumenNodeFeatureBackend?[] featureBackends;
    private readonly int[][] featureIds;
    private readonly Queue<(EventKind Kind, string? Name)> pendingEvents = new();
    private readonly Dictionary<string, float> parameters = new(StringComparer.Ordinal)
    {
        ["intensity"] = 1f, ["rate"] = 1f, ["scale"] = 1f,
    };
    private PlaybackState state;
    private int currentState = -1;
    private double stateTime;
    private bool stateEntryPending;
    private bool stopRequested;
    private Vector3 origin;
    // LumenPlayRequest.DepthReferenceZ - OriginWorld.Z: the depth-mask reference rides the origin (and Follow).
    private readonly float? depthReferenceOffsetZ;
    private float rotation;
    private float playbackTime;
    private Matrix4x4 rootWorld;

    // Requires: request.InitialState is null or names a state the asset declares.
    internal EffectInstanceHandle(LumenVfxService owner, EffectAsset asset, in LumenPlayRequest request, uint playSeed)
    {
        this.owner = owner;
        this.asset = asset;
        this.request = request;
        graph = EffectStateGraph.For(asset, $"effect '{asset.EffectId}'");
        int initial = graph.InitialIndex;
        if (request.InitialState is { } initialState && (initial = graph.IndexOf(initialState)) < 0)
            throw new ArgumentException($"Effect '{asset.EffectId}' declares no state '{initialState}'.", nameof(request));
        nodeWorldMatrices = new Matrix4x4[asset.Nodes.Length];
        nodeWorldRotations = new Quaternion[asset.Nodes.Length];
        cpuIds = new int[asset.Nodes.Length];
        gpuIds = new int[asset.Nodes.Length];
        lightOn = new bool[asset.Nodes.Length];
        lightRgb = new Vector3[asset.Nodes.Length];
        nodeActive = new bool[asset.Nodes.Length];
        nodeFrameDelta = new float[asset.Nodes.Length];
        nodeActivatedAt = new float[asset.Nodes.Length];
        nodeDeactivatedAt = new float[asset.Nodes.Length];
        Array.Fill(cpuIds, -1);
        Array.Fill(gpuIds, -1);
        featureBackends = new ILumenNodeFeatureBackend?[FeatureCount];
        featureIds = new int[FeatureCount][];
        for (int f = 0; f < FeatureCount; f++)
        {
            featureIds[f] = new int[asset.Nodes.Length];
            Array.Fill(featureIds[f], -1);
        }
        for (int i = 0; i < asset.Nodes.Length; i++)
        {
            EffectNodeSpec node = asset.Nodes[i];
            if (node.Ribbon is { EndAtTarget: true } && request.TargetWorld is null)
                throw new ArgumentException($"Effect '{asset.EffectId}' node {i} '{node.Name}': ribbon endAtTarget needs LumenPlayRequest.TargetWorld.", nameof(request));
            if (node.DepthMask != LumenDepthMask.None && request.DepthReferenceZ is null)
                throw new ArgumentException($"Effect '{asset.EffectId}' node {i} '{node.Name}': depthMask needs LumenPlayRequest.DepthReferenceZ.", nameof(request));
            for (int f = 0; f < FeatureCount; f++)
            {
                var feature = (LumenNodeFeature)f;
                if (!HasFeature(node, feature)) continue;
                featureBackends[f] ??= LumenHostRegistry.GetNodeFeatureBackend(feature)
                    ?? throw new InvalidOperationException($"Effect '{asset.EffectId}' node {i} '{node.Name}' needs a {feature} node backend, but none is registered.");
            }
        }
        origin = request.OriginWorld;
        depthReferenceOffsetZ = request.DepthReferenceZ - request.OriginWorld.Z;
        rotation = request.TargetWorld is Vector3 target
            ? SpacePlaneTransform.ComputeFacingDegrees(origin, target) : request.RotationDegrees;
        // A throwing backend call (e.g. an L2 slot that no longer fits) must not leak the emitters
        // already acquired by earlier nodes: Dispose below releases every id assigned so far.
        try
        {
            RefreshTransforms();
            engine = owner.GpuEngineProvider?.Invoke();
            bool[] initialNodes = graph.States[initial].NodeMask;
            for (int i = 0; i < asset.Nodes.Length; i++)
            {
                uint nodeSeed = unchecked(playSeed + (uint)i * 0x9E3779B9u);
                if (nodeSeed == 0) nodeSeed = 1;
                for (int f = 0; f < FeatureCount; f++)
                    if (HasFeature(asset.Nodes[i], (LumenNodeFeature)f))
                        featureIds[f][i] = featureBackends[f]!.CreateInstance(asset.Nodes[i], FeatureFrame(i, false, 0f), request.RealTime, nodeSeed);
                if (asset.Nodes[i].Emitter is not { } spec) continue;
                if (spec.PreferredTier == LumenParticleTier.L2_GpuCompute && engine is not null)
                {
                    gpuIds[i] = engine.CreateInstance(spec, NodeTransform(i), request.MapIndex, request.RealTime, nodeSeed);
                    if (!initialNodes[i]) engine.UpdateInstance(gpuIds[i], NodeTransform(i), false, 1f, 1f);
                }
                else
                {
                    cpuIds[i] = owner.Simulator.AcquireEmitter(spec, NodeTransform(i), nodeSeed, emitting: initialNodes[i], rateMultiplier: 1f, realTime: request.RealTime, mapIndex: request.MapIndex);
                }
                PushDisplayState(i);
            }
            owner.Simulator.BindNodeInstances(cpuIds, engine, gpuIds);
            EnterState(initial);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string EffectId => asset.EffectId;

    // Ensures: true when the play request asked for real-time (unscaled) simulation: the handle and its L1
    //          emitters keep advancing while the game is paused or running at another speed.
    public bool RealTime => request.RealTime;

    // Ensures: true until the handle is stopped immediately/disposed, or until the effect has ended (its last
    //          state finished, or Stop(true) ran) and every emitter, every light and every node feature backend
    //          instance (ILumenNodeFeatureBackend.IsInstanceAlive) has been released.
    public bool IsAlive => state != PlaybackState.Dead;

    // Ensures: true while a state plays (including the stop state after Stop(true)); false once the effect
    //          drains its last particles and lights, and after any immediate stop.
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

    // Ensures: node lights currently on (lit, or still fading out).
    public int LightCount
    {
        get
        {
            int count = 0;
            foreach (bool on in lightOn)
                if (on) count++;
            return count;
        }
    }

    public event Action<string>? Notify;
    public event Action<string>? StateChanged;
    public event Action? Finished;

    // Ensures: true while lifecycle events wait to be raised (e.g. Finished after Stop(false) or Dispose).
    internal bool HasPendingEvents => pendingEvents.Count != 0;
    internal int PendingEventCount => pendingEvents.Count;

    // Requires: name is non-empty.
    // Ensures: while a state plays: every TimelineEventSpec whose EventName equals name fires now, whatever its
    //          state and time (FireEvent), and every node whose Name equals name restarts its emission as on state
    //          entry; it keeps emitting until the next state entry decides again. The current state and its clock
    //          are unchanged. Unmatched names are a no-op (WotF may trigger animator states RimLumen does not own).
    //          No-op once the effect drains or is dead.
    public void Trigger(string name)
    {
        if (state != PlaybackState.Playing) return;
        foreach (var timelineEvent in asset.TimelineEvents)
            if (timelineEvent.EventName == name) FireEvent(timelineEvent);
        for (int i = 0; i < asset.Nodes.Length; i++)
            if (asset.Nodes[i].Name == name) RestartNode(i);
    }

    // Requires: stateName names a state the asset declares (throws ArgumentException otherwise, even when dead).
    // Ensures: while a state plays and Stop(true) has not been called: enters stateName now (EnterState), also when it
    //          is the current state, which restarts it. No-op once Stop(true) was called, while draining, or dead.
    public void GoTo(string stateName)
    {
        int index = graph.IndexOf(stateName);
        if (index < 0)
            throw new ArgumentException($"Effect '{asset.EffectId}' declares no state '{stateName}'.", nameof(stateName));
        if (state == PlaybackState.Playing && !stopRequested) EnterState(index);
    }

    // Requires: name is non-empty; values other than rate and intensity are finite.
    // Ensures: rate and intensity accept only finite nonnegative values; otherwise throws
    //          ArgumentOutOfRangeException without changing the stored value.
    // Ensures: "intensity" scales light colour and particle vertex colour, "rate" scales emission rate,
    //          "scale" multiplies the root scale. Unknown names are recorded and retrievable through
    //          TryGetParam without affecting simulation. Safe no-op once the handle is dead.
    public void SetParam(string name, float value)
    {
        if ((name == "rate" || name == "intensity") && (!float.IsFinite(value) || value < 0f))
            throw new ArgumentOutOfRangeException(nameof(value), value, $"{name} must be finite and nonnegative.");
        if (IsAlive) parameters[name] = value;
    }

    // Ensures: allowFadeOut == true: when the asset declares a StopState, enters it (unless it is already the
    //          current state) and lets it play to its end, ignoring later GoTo calls; the effect then drains.
    //          Without a StopState the effect drains at once. Draining stops new emission, fades the point lights
    //          out over LightFadeSeconds and lets live particles finish; the handle becomes dead once nothing is
    //          left: an L1 emitter once its accepted pulses and live particles finish, an L2 emitter once
    //          ILumenGpuParticleEngine.IsInstanceAlive turns false after DestroyInstance(id, false).
    //          allowFadeOut == false tears down emitters and lights now, also while draining. Repeated calls are no-ops.
    public void Stop(bool allowFadeOut = true)
    {
        if (state == PlaybackState.Dead) return;
        if (!allowFadeOut)
        {
            Teardown();
            return;
        }
        if (state == PlaybackState.Draining || stopRequested) return;
        stopRequested = true;
        if (graph.StopIndex < 0) BeginDrain();
        else if (currentState != graph.StopIndex) EnterState(graph.StopIndex);
    }

    // Ensures: equivalent to Stop(false); idempotent.
    public void Dispose() => Stop(false);

    // Ensures: exposes a value stored by SetParam, including the three built-in names.
    public bool TryGetParam(string name, out float value) => parameters.TryGetValue(name, out value);

    // Ensures: seconds over which a light fades to zero after its node stops emitting (state exit or drain).
    public const float LightFadeSeconds = 0.25f;

    // Requires: called once per frame by LumenVfxService.CaptureMainThread on the main thread.
    // Ensures, in this order:
    //   1. playbackTime += deltaTime; when a Follow provider exists it refreshes origin/rotation, and a
    //      provider that yields false keeps the last known transform (detach = freeze, never snap back).
    //   2. node world matrices are recomposed (SpacePlaneTransform.BuildWorldMatrices).
    //   3. the state clock advances by deltaTime (AdvanceStates): timeline events fire, and a state that
    //      reaches its Duration enters its Next or finishes the effect, possibly several times in one frame.
    //   4. every emitter binding is pushed to its backend (SetEmitterTransform/SetEmitterParams or
    //      UpdateInstance), every node feature instance receives ILumenNodeFeatureBackend.UpdateInstance, and every
    //      node light computes this frame's colour (UpdateLights).
    //   5. a drain that has finished (all emitters released, lights faded out) marks the handle dead.
    //   Notify, StateChanged and Finished are only queued here; LumenVfxService raises them (RaiseEvents) after it
    //   has advanced every handle. A dead handle does nothing.
    internal void Advance(float deltaTime)
    {
        if (state != PlaybackState.Dead)
        {
            playbackTime += deltaTime;
            RefreshTransforms();
            Array.Fill(nodeFrameDelta, deltaTime);
            if (state == PlaybackState.Playing) AdvanceStates(deltaTime);
            float intensity = parameters["intensity"];
            for (int i = 0; i < asset.Nodes.Length; i++)
            {
                bool emitting = state == PlaybackState.Playing && nodeActive[i];
                if (cpuIds[i] >= 0)
                {
                    owner.Simulator.SetEmitterTransform(cpuIds[i], NodeTransform(i));
                    owner.Simulator.SetEmitterParams(cpuIds[i], emitting, parameters["rate"], intensity);
                    if (asset.Nodes[i].DepthMask != LumenDepthMask.None) PushDisplayState(i);
                    if (nodeFrameDelta[i] != deltaTime) owner.Simulator.SetEmitterFrameDelta(cpuIds[i], nodeFrameDelta[i]);
                }
                if (gpuIds[i] >= 0)
                {
                    if (state == PlaybackState.Draining && !engine!.IsInstanceAlive(gpuIds[i])) gpuIds[i] = -1;
                    else
                    {
                        engine!.UpdateInstance(gpuIds[i], NodeTransform(i), emitting, intensity, parameters["rate"]);
                        if (asset.Nodes[i].DepthMask != LumenDepthMask.None) PushDisplayState(i);
                        if (nodeFrameDelta[i] != deltaTime) engine.SetInstanceFrameDelta(gpuIds[i], nodeFrameDelta[i]);
                    }
                }
                for (int f = 0; f < FeatureCount; f++)
                {
                    int id = featureIds[f][i];
                    if (id < 0) continue;
                    if (state == PlaybackState.Draining && !featureBackends[f]!.IsInstanceAlive(id)) featureIds[f][i] = -1;
                    else featureBackends[f]!.UpdateInstance(id, FeatureFrame(i, emitting, nodeFrameDelta[i]));
                }
            }
            UpdateLights(intensity);
            FinishDrain();
        }
    }

    // Requires: main thread; count <= PendingEventCount.
    // Ensures: raises the oldest count queued Notify / StateChanged / Finished events in the order they happened.
    //          Events their handlers queue stay queued for a later call.
    internal void RaiseEvents(int count)
    {
        for (; count > 0; count--)
        {
            var pending = pendingEvents.Dequeue();
            switch (pending.Kind)
            {
                case EventKind.Notify: Notify?.Invoke(pending.Name!); break;
                case EventKind.StateChanged: StateChanged?.Invoke(pending.Name!); break;
                default: Finished?.Invoke(); break;
            }
        }
    }

    // Ensures: the clock of the current state advances by deltaTime. Events whose time lies in (start, end] of
    //          the state-local clock fire (FireEvent) in time order; the first advance after entry also fires the
    //          events at time 0. A Loop state with Duration > 0 repeats its events every Duration (events with
    //          TimeSeconds >= Duration never fire on the timer); a Loop state with Duration 0 fires them once.
    //          A non-Loop state ends when its clock reaches Duration (events at exactly Duration still fire) and
    //          the remaining time carries into Next; with no Next, or after Stop(true), the effect drains.
    private void AdvanceStates(float deltaTime)
    {
        double remaining = deltaTime;
        while (state == PlaybackState.Playing)
        {
            var current = graph.States[currentState];
            double start = stateTime;
            double end = start + remaining;
            double stop = current.Loop ? end : Math.Min(end, current.Duration);
            FireStateEvents(current, start, stop, stateEntryPending);
            stateEntryPending = false;
            stateTime = stop;
            if (current.Loop || end < current.Duration) return;
            remaining = end - current.Duration;
            if (stopRequested || current.Next < 0) BeginDrain((float)remaining);
            else EnterState(current.Next, (float)remaining);
        }
    }

    private void FireStateEvents(EffectStateGraph.State current, double start, double stop, bool includeStart)
    {
        if (current.Events.Length == 0) return;
        if (current.Loop && current.Duration > 0d)
        {
            for (long cycle = (long)Math.Floor(start / current.Duration); cycle * current.Duration <= stop; cycle++)
            {
                foreach (var timelineEvent in current.Events)
                {
                    if (timelineEvent.TimeSeconds >= current.Duration) break;
                    double time = cycle * current.Duration + timelineEvent.TimeSeconds;
                    if ((time > start || (includeStart && time == start)) && time <= stop) FireEvent(timelineEvent);
                }
            }
            return;
        }
        foreach (var timelineEvent in current.Events)
        {
            double time = timelineEvent.TimeSeconds;
            if ((time > start || (includeStart && time == start)) && time <= stop) FireEvent(timelineEvent);
        }
    }

    // Ensures: the state clock restarts at 0; every node of the state restarts its emission (RestartNode), every
    //          other node stops emitting while its live particles finish and its light fades; StateChanged(name) is
    //          queued for a declared state.
    private void EnterState(int index, float frameDelta = 0f)
    {
        currentState = index;
        stateTime = 0d;
        stateEntryPending = true;
        var entered = graph.States[index];
        for (int i = 0; i < asset.Nodes.Length; i++)
        {
            if (entered.NodeMask[i]) RestartNode(i, frameDelta);
            else DeactivateNode(i, frameDelta);
        }
        if (entered.Name is { } name) pendingEvents.Enqueue((EventKind.StateChanged, name));
    }

    // Ensures: the node emits again from the start of its own schedule (burst times, Duration and rate measured
    //          from now) on L1 and L2 alike, and its light curve restarts.
    private void RestartNode(int index, float frameDelta = 0f)
    {
        nodeActive[index] = true;
        nodeFrameDelta[index] = frameDelta;
        nodeActivatedAt[index] = playbackTime - frameDelta;
        if (cpuIds[index] >= 0) owner.Simulator.RestartEmitter(cpuIds[index]);
        if (gpuIds[index] >= 0) engine!.RestartInstance(gpuIds[index]);
        for (int f = 0; f < FeatureCount; f++)
            if (featureIds[f][index] >= 0) featureBackends[f]!.RestartInstance(featureIds[f][index]);
    }

    // Ensures: the node's backend stops emitting now, so a switch made from an event handler (after this frame's
    //          push) does not leave the old state's emitters running through this frame's capture.
    private void DeactivateNode(int index, float frameDelta)
    {
        if (!nodeActive[index]) return;
        nodeActive[index] = false;
        nodeDeactivatedAt[index] = playbackTime - frameDelta;
        // A light can start and enter its fade between two captures without ever being drawn fully lit.
        if (asset.Nodes[index].PointLight.HasValue && nodeDeactivatedAt[index] > nodeActivatedAt[index]) lightOn[index] = true;
        if (cpuIds[index] >= 0) owner.Simulator.SetEmitterParams(cpuIds[index], false, parameters["rate"], parameters["intensity"]);
        if (gpuIds[index] >= 0) engine!.UpdateInstance(gpuIds[index], NodeTransform(index), false, parameters["intensity"], parameters["rate"]);
        for (int f = 0; f < FeatureCount; f++)
            if (featureIds[f][index] >= 0) featureBackends[f]!.UpdateInstance(featureIds[f][index], FeatureFrame(index, false, 0f));
    }

    private void BeginDrain(float frameDelta = 0f)
    {
        state = PlaybackState.Draining;
        currentState = -1;
        for (int i = 0; i < asset.Nodes.Length; i++)
        {
            DeactivateNode(i, frameDelta);
            if (cpuIds[i] >= 0) owner.Simulator.ReleaseEmitter(cpuIds[i], immediate: false);
            if (gpuIds[i] >= 0) engine!.DestroyInstance(gpuIds[i], immediate: false);
            for (int f = 0; f < FeatureCount; f++)
            {
                int id = featureIds[f][i];
                if (id < 0) continue;
                featureBackends[f]!.DestroyInstance(id, immediate: false);
                if (!featureBackends[f]!.IsInstanceAlive(id)) featureIds[f][i] = -1;
            }
        }
        FinishDrain();
    }

    private void Teardown()
    {
        state = PlaybackState.Dead;
        currentState = -1;
        for (int i = 0; i < asset.Nodes.Length; i++)
        {
            if (cpuIds[i] >= 0) owner.Simulator.ReleaseEmitter(cpuIds[i], immediate: true);
            if (gpuIds[i] >= 0) engine?.DestroyInstance(gpuIds[i], immediate: true);
            cpuIds[i] = -1;
            gpuIds[i] = -1;
            for (int f = 0; f < FeatureCount; f++)
            {
                if (featureIds[f][i] >= 0) featureBackends[f]!.DestroyInstance(featureIds[f][i], immediate: true);
                featureIds[f][i] = -1;
            }
        }
        RemoveLights();
        pendingEvents.Enqueue((EventKind.Finished, null));
    }

    // Pushed at creation, and every frame for a depth-masked node so its reference follows the origin. Each backend
    // derives the 2D ground anchor from the node's world transform.
    private void PushDisplayState(int index)
    {
        bool isVertical3D = asset.Nodes[index].SpacePlane == LumenSpacePlane.Vertical3D;
        float? depthReferenceZ = DepthReferenceZ(index);
        if (cpuIds[index] >= 0) owner.Simulator.SetEmitterDisplayState(cpuIds[index], isVertical3D, depthReferenceZ);
        if (gpuIds[index] >= 0) engine!.SetInstanceDisplayState(gpuIds[index], isVertical3D, depthReferenceZ);
    }

    private float? DepthReferenceZ(int index) => asset.Nodes[index].DepthMask == LumenDepthMask.None
        ? null : origin.Z + depthReferenceOffsetZ!.Value;

    private void RefreshTransforms()
    {
        if (request.Follow is { } follow && follow(out var position, out float facing))
        {
            origin = position;
            rotation = facing;
        }
        rootWorld = SpacePlaneTransform.BuildRootMatrix(origin, rotation, request.Scale * parameters["scale"]);
        SpacePlaneTransform.BuildWorldMatrices(asset, rootWorld, nodeWorldMatrices);
        Quaternion rootRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, rotation * (MathF.PI / 180f));
        SpacePlaneTransform.BuildWorldRotations(asset, rootRotation, nodeWorldRotations);
    }

    private LumenEmitterTransform NodeTransform(int index) => new(nodeWorldMatrices[index], nodeWorldRotations[index]);

    private LumenNodeFeatureFrame FeatureFrame(int index, bool active, float deltaTime) =>
        new(NodeTransform(index), request.TargetWorld, request.MapIndex, parameters["intensity"], active, deltaTime, DepthReferenceZ(index));

    private static readonly int FeatureCount = Enum.GetValues<LumenNodeFeature>().Length;

    private static bool HasFeature(EffectNodeSpec node, LumenNodeFeature feature) => feature switch
    {
        LumenNodeFeature.Distortion => node.Distortion is not null,
        LumenNodeFeature.Ribbon => node.Ribbon is not null,
        LumenNodeFeature.ScreenDim => node.ScreenDim is not null,
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, null),
    };

    // Ensures: emits one pulse on TargetNodeIndex's emitter when that node has one (Bursts[0].Count if the
    //          emitter declares bursts, else round(RateOverTime), clamped to [0, MaxParticles]) through
    //          CpuParticleSimulator.EmitBurst or ILumenGpuParticleEngine.EmitBurst, without changing whether the
    //          node emits; queues Notify(EventName) when the event has Notify.
    private void FireEvent(TimelineEventSpec timelineEvent)
    {
        int node = timelineEvent.TargetNodeIndex;
        if (node >= 0 && node < asset.Nodes.Length && asset.Nodes[node].Emitter is { } spec)
        {
            int count = spec.Bursts.Length > 0 ? Math.Clamp(spec.Bursts[0].Count, 0, spec.MaxParticles)
                : (int)Math.Clamp(MathF.Round(spec.RateOverTime), 0f, spec.MaxParticles);
            if (cpuIds[node] >= 0)
            {
                owner.Simulator.SetEmitterTransform(cpuIds[node], NodeTransform(node));
                owner.Simulator.EmitBurst(cpuIds[node], count);
            }
            if (gpuIds[node] >= 0) engine!.EmitBurst(gpuIds[node], count);
        }
        if (timelineEvent.Notify) pendingEvents.Enqueue((EventKind.Notify, timelineEvent.EventName));
    }

    // Ensures: a node's light colour this frame is ColorRgb * intensity * fade * (IntensityOverTime is null ? 1 :
    //          Evaluate(IntensityOverTime, seconds since the node's last restart)); fade is 1 while the node emits and
    //          falls linearly to 0 over LightFadeSeconds after it stops, when the light turns off.
    private void UpdateLights(float intensity)
    {
        for (int i = 0; i < asset.Nodes.Length; i++)
        {
            if (asset.Nodes[i].PointLight is not { } spec) continue;
            bool lit = IsEmitting && nodeActive[i];
            float fade = lit ? 1f : Math.Clamp(1f - (playbackTime - nodeDeactivatedAt[i]) / LightFadeSeconds, 0f, 1f);
            lightOn[i] = lit || (lightOn[i] && fade > 0f);
            if (!lightOn[i]) continue;
            float curve = spec.IntensityOverTime is { } overTime
                ? CurveEvaluator.Evaluate(overTime, playbackTime - nodeActivatedAt[i]) : 1f;
            lightRgb[i] = spec.ColorRgb * (intensity * fade * curve);
        }
    }

    // Requires: main thread, after this frame's Advance.
    // Ensures: when the handle plays on currentMapIndex, appends one light pool per node light that is on with a
    //          positive radius and a positive colour, at the node's world position (Y = height above the ground; a light
    //          below the ground sits on the ground, Y = 0).
    internal void CollectLights(int currentMapIndex, List<VisualPointLight> into)
    {
        if (request.MapIndex != currentMapIndex) return;
        for (int i = 0; i < asset.Nodes.Length; i++)
        {
            if (!lightOn[i]) continue;
            float radius = asset.Nodes[i].PointLight!.Value.GlowRadius;
            Vector3 rgb = lightRgb[i];
            if (radius <= 0f || MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z)) <= 0f) continue;
            Vector3 world = nodeWorldMatrices[i].Translation;
            into.Add(new VisualPointLight(world.X, MathF.Max(0f, world.Y), world.Z, radius, rgb, VisualPointLightKind.Effect));
        }
    }

    private void RemoveLights() => Array.Clear(lightOn);

    private void FinishDrain()
    {
        if (state != PlaybackState.Draining) return;
        for (int i = 0; i < cpuIds.Length; i++)
            if (cpuIds[i] >= 0 && !owner.Simulator.IsEmitterAlive(cpuIds[i])) cpuIds[i] = -1;
        if (CpuEmitterCount == 0 && GpuEmitterCount == 0 && LightCount == 0 && FeatureInstanceCount() == 0)
        {
            state = PlaybackState.Dead;
            pendingEvents.Enqueue((EventKind.Finished, null));
        }
    }

    private int FeatureInstanceCount()
    {
        int count = 0;
        foreach (int[] ids in featureIds)
            count += CountBindings(ids);
        return count;
    }

    private static int CountBindings(int[] ids)
    {
        int count = 0;
        foreach (int id in ids)
            if (id >= 0) count++;
        return count;
    }
}
