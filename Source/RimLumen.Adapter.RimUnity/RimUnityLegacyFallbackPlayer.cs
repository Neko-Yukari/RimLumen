using System;
using System.Collections.Generic;
using System.Linq;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;
using RimUnity.Assets;
using RimUnity.Runtime;
using RimUnity.Serialization;
using Waylith;
using RU = global::RimUnity;

namespace RimLumen.Adapter.RimUnity;

public sealed class RimUnityLegacyFallbackPlayer : IRimUnityExternalPlayer
{
    public ILumenEffectHandle Play(in LumenPlayRequest request, RimUnityTranslationReport report)
    {
        if (EngineApplication.Instance?.GraphicsDevice is null)
            throw new InvalidOperationException("L0 playback requires the RimUnity game runtime; inject IRimUnityExternalPlayer for offline playback.");
        RU.GameObject? anchor = null;
        using (NativeRuntime.DeferLifecycle())
        {
            try
            {
                anchor = new RU.GameObject("RimLumen external: " + request.EffectId);
                anchor.SetActive(false);
                RU.GameObject instance = RU.Object.Instantiate(PrefabLoader.LoadDirectory(report.DataDirectory, report.LogicalPath), anchor.transform, false);
                var externalIds = report.Nodes.Where(node => node.Tier == LumenParticleTier.L0_ExternalCpu)
                    .Select(node => node.NodeId).ToHashSet(StringComparer.Ordinal);
                var objects = MapNodes(report.Prefab, instance);
                var selected = new List<RU.ParticleSystem>();
                foreach (NodeData node in report.Prefab.nodes)
                {
                    RU.GameObject obj = objects[node.id];
                    RU.ParticleSystem? particles = obj.GetComponent<RU.ParticleSystem>();
                    if (particles is null) continue;
                    bool enabled = particles.enabled && externalIds.Contains(node.id);
                    particles.main.playOnAwake = false;
                    particles.Stop(false, RU.ParticleSystemStopBehavior.StopEmittingAndClear);
                    particles.enabled = enabled;
                    RU.ParticleSystemRenderer? renderer = obj.GetComponent<RU.ParticleSystemRenderer>();
                    if (!enabled && renderer is not null) renderer.enabled = false;
                    if (enabled) selected.Add(particles);
                }
                var handle = new ExternalHandle(anchor, selected.ToArray(), request);
                anchor.SetActive(true);
                handle.Start();
                return handle;
            }
            catch
            {
                if (anchor is not null) RU.Object.DestroyImmediate(anchor);
                throw;
            }
        }
    }

    // PrefabBuilder creates child transforms in serialized order, not name order. Duplicate names are legal.
    private static Dictionary<string, RU.GameObject> MapNodes(PrefabData prefab, RU.GameObject root)
    {
        var result = new Dictionary<string, RU.GameObject>(StringComparer.Ordinal);
        NodeData rootNode = prefab.nodes.Single(node => node.parent is null);
        root.name = rootNode.name;
        result.Add(rootNode.id, root);
        var pending = new Queue<NodeData>();
        pending.Enqueue(rootNode);
        while (pending.Count > 0)
        {
            NodeData parent = pending.Dequeue();
            int child = 0;
            foreach (NodeData node in prefab.nodes.Where(node => node.parent == parent.id))
            {
                result.Add(node.id, result[parent.id].transform.GetChild(child++).gameObject);
                pending.Enqueue(node);
            }
        }
        return result;
    }

    private sealed class ExternalHandle : ILumenEffectHandle, ILumenFrameContributor
    {
        private enum Playback { Playing, Draining, Dead }
        private Playback state;
        private bool released;
        private bool finishPending;
        private readonly RU.GameObject anchor;
        private readonly RU.ParticleSystem[] systems;
        private readonly RU.ParticleSystem.MinMaxCurve[] rates;
        private readonly RU.ParticleSystem.MinMaxGradient[] colors;
        private readonly LumenPlayRequest request;
        private System.Numerics.Vector3 origin;
        private float rotation;
        private float scale = 1;

        public ExternalHandle(RU.GameObject anchor, RU.ParticleSystem[] systems, in LumenPlayRequest request)
        {
            this.anchor = anchor;
            this.systems = systems;
            this.request = request;
            rates = systems.Select(system => system.emission.rateOverTime).ToArray();
            colors = systems.Select(system => system.main.startColor).ToArray();
            origin = request.OriginWorld;
            rotation = request.TargetWorld is { } target ? SpacePlaneTransform.ComputeFacingDegrees(origin, target) : request.RotationDegrees;
            RefreshTransform();
        }

        public void Start()
        {
            var subTargets = systems.Where(system => system.subEmitters.enabled)
                .SelectMany(system => system.subEmitters.entries).Select(entry => entry.system).ToHashSet();
            // request.InitialState names a native asset state; translated RimUnity assets declare none, so the native
            // handle has already rejected any non-null value and the L0 part starts every system.
            foreach (var system in systems)
                if (system.gameObject.activeInHierarchy && !subTargets.Contains(system)) system.Play(false);
            LumenHostRegistry.RegisterContributor(this);
        }

        public bool IsAlive => state != Playback.Dead && anchor != null;
        public string Name => "RimUnity L0: " + request.EffectId;
        // A released handle still needs one capture to unregister this contributor and raise Finished.
        public bool HasActiveWork => state != Playback.Dead || finishPending;

        // L0 systems carry no timeline events and no states: Trigger restarts the systems named like the node,
        // as the native handle restarts its nodes, and GoTo has nothing to switch (the native handle owns states).
        public void Trigger(string name)
        {
            if (state != Playback.Playing || !IsAlive) return;
            foreach (var system in systems)
                if (system.gameObject.name == name) system.Play(false);
        }

        public void GoTo(string stateName) { }

        public event Action<string>? Notify { add { } remove { } }
        public event Action<string>? StateChanged { add { } remove { } }
        public event Action? Finished;

        public void SetParam(string name, float value)
        {
            if (!IsAlive) return;
            if (name == "scale") { scale = value; RefreshTransform(); }
            for (int i = 0; i < systems.Length; i++)
            {
                if (name == "rate")
                {
                    var rate = rates[i];
                    rate.constantMin *= value;
                    rate.constantMax *= value;
                    rate.curveMultiplier *= value;
                    systems[i].emission.rateOverTime = rate;
                }
                if (name == "intensity")
                {
                    var color = colors[i];
                    color.colorMin = ScaleRgb(color.colorMin, value);
                    color.colorMax = ScaleRgb(color.colorMax, value);
                    color.gradientMin = ScaleGradient(color.gradientMin, value);
                    color.gradientMax = ScaleGradient(color.gradientMax, value);
                    systems[i].main.startColor = color;
                }
            }
        }

        public void Stop(bool allowFadeOut = true)
        {
            if (!IsAlive) { Release(); return; }
            if (allowFadeOut && state == Playback.Draining) return;
            state = allowFadeOut ? Playback.Draining : Playback.Dead;
            foreach (var system in systems)
                system.Stop(false, allowFadeOut ? RU.ParticleSystemStopBehavior.StopEmitting : RU.ParticleSystemStopBehavior.StopEmittingAndClear);
            if (!allowFadeOut || systems.All(system => !system.IsAlive(false))) Release();
        }

        public void Dispose() => Stop(false);
        public void CaptureMainThread(in LumenCaptureContext context)
        {
            if (finishPending)
            {
                finishPending = false;
                LumenHostRegistry.UnregisterContributor(this);
                Finished?.Invoke();
                return;
            }
            if (!IsAlive) { Release(); return; }
            RefreshTransform();
            // Play sets emitting, so a started system stays alive until it has played out (also without a Stop).
            if (systems.All(system => !system.IsAlive(false))) Release();
        }
        public void EnqueuePreCompute(in LumenComputeContext context) { }
        public void RecordBufferBarriers(in LumenBarrierContext context) { }
        public void RecordHdrDraws(in LumenHdrDrawContext context) { }

        private void RefreshTransform()
        {
            if (request.Follow is { } follow && follow(out var position, out float facing)) { origin = position; rotation = facing; }
            anchor.transform.localPosition = new RU.Vector3(origin.X, origin.Y, origin.Z);
            float half = rotation * MathF.PI / 360;
            anchor.transform.localRotation = new RU.Quaternion(0, MathF.Sin(half), 0, MathF.Cos(half));
            float size = request.Scale * scale;
            anchor.transform.localScale = new RU.Vector3(size, size, size);
        }

        // Ensures: idempotent; the next capture unregisters this contributor and raises Finished once.
        private void Release()
        {
            state = Playback.Dead;
            if (anchor != null) RU.Object.DestroyImmediate(anchor);
            if (released) return;
            released = true;
            finishPending = true;
        }

        private static RU.Color ScaleRgb(RU.Color color, float value) => new(color.r * value, color.g * value, color.b * value, color.a);
        private static RU.Gradient ScaleGradient(RU.Gradient gradient, float value)
        {
            if (gradient is null) return null!;
            var keys = gradient.colorKeys;
            for (int i = 0; i < keys.Length; i++) keys[i].color = ScaleRgb(keys[i].color, value);
            return new RU.Gradient { mode = gradient.mode, colorSpace = gradient.colorSpace, colorKeys = keys, alphaKeys = gradient.alphaKeys };
        }
    }
}
