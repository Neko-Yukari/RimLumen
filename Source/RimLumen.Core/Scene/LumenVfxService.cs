using System;
using System.Collections.Generic;
using RimLumen.Core.Contracts;
using RimLumen.Core.Lighting;
using RimLumen.Core.ParticlesGpu;

namespace RimLumen.Core.Scene;

/// <summary>
/// Native <see cref="ILumenVfxService"/>: resolves effects through <see cref="LumenHostRegistry"/>,
/// plays them as <see cref="EffectInstanceHandle"/> instances, routes each emitter to L2 or L1, and
/// routes point lights to the per-map <see cref="MapGlowerDriver"/>.
/// It is also the frame contributor that advances live handles; it must be registered BEFORE
/// <see cref="CpuParticleSimulator"/> so handle updates land in the same frame as the simulation.
/// </summary>
public sealed class LumenVfxService : ILumenVfxService, ILumenFrameContributor
{
    public const long PoolExhaustedWarningCooldownMs = 10_000;

    private readonly List<EffectInstanceHandle> handles = new();
    private bool hasWarnedPoolExhausted;
    private long lastPoolExhaustedWarningMs;

    internal sealed class InertHandle : ILumenEffectHandle
    {
        public static readonly InertHandle Instance = new();
        public bool IsAlive => false;
        public void Trigger(string eventOrStateName) { }
        public void SetParam(string name, float value) { }
        public void Stop(bool allowFadeOut = true) { }
        public void Dispose() { }
    }

    // Requires: simulator is non-null and is the instance registered as a frame contributor.
    public LumenVfxService(CpuParticleSimulator simulator)
    {
        Simulator = simulator;
        GpuEngineProvider = static () => LumenHostRegistry.GpuParticleEngine;
        GlowerDriverProvider = static mapIndex =>
            LumenMapLightingRegistry.TryResolve(mapIndex, out var driver) ? driver : null;
    }

    public CpuParticleSimulator Simulator { get; }

    // Ensures: L2 backend resolver, read on every Play so an engine registered after bootstrap is picked
    //          up. Default resolves LumenHostRegistry.GpuParticleEngine.
    public Func<ILumenGpuParticleEngine?>? GpuEngineProvider { get; set; }

    // Ensures: per-map glower resolver, read on every Play and every frame. Default resolves through
    //          LumenMapLightingRegistry.TryResolve, which treats a negative index as "most recent map".
    public Func<int, MapGlowerDriver?>? GlowerDriverProvider { get; set; }

    internal Func<long> ClockMsProvider { get; set; } = static () => Environment.TickCount64;
    internal Action<string> PoolExhaustedWarningSink { get; set; } = static message => Verse.Log.Warning(message);

    // Ensures: always "LumenVfxService".
    public string Name => "LumenVfxService";

    // Ensures: IsHdrActive and IsSceneDepthAvailable report whether an HDR pass owner has installed
    //          Simulator.BatchSink (the sink is only offered inside RimLumenVfxPass, which has RGBA16F
    //          colour plus read-only scene depth); IsGpuComputeAvailable reports whether the L2 resolver
    //          yields an engine; MaxGpuParticlesPerFrame is 4096 with an engine and 0 without;
    //          ValidationClean stays false until the render pipeline reports validation telemetry.
    public LumenCapabilities Capabilities
    {
        get
        {
            bool hdr = Simulator.BatchSink is not null;
            bool gpu = GpuEngineProvider?.Invoke() is not null;
            return new LumenCapabilities(hdr, hdr, gpu, false, gpu ? 4096 : 0);
        }
    }

    // Ensures: number of handles that have not been released yet.
    public int LiveHandleCount
    {
        get
        {
            int count = 0;
            foreach (var handle in handles)
                if (handle.IsAlive) count++;
            return count;
        }
    }

    // Requires: called on the main thread; request.EffectId is non-empty.
    // Ensures: resolves request.EffectId through LumenHostRegistry.TryResolveAsset; a miss returns an
    //          inert handle (IsAlive false, every member a safe no-op) that is not tracked as live.
    //          A hit creates an EffectInstanceHandle, binds each node emitter to the L2 engine when the
    //          emitter prefers L2 and an engine resolves, otherwise to CpuParticleSimulator, and returns it.
    //          InitialState selects the initially active nodes: when it is non-null only emitters on nodes
    //          whose Name equals it start emitting, every other emitter starts paused (a later
    //          Trigger(nodeName) activates it); when it is null every emitter starts emitting.
    public ILumenEffectHandle Play(in LumenPlayRequest request)
    {
        if (!LumenHostRegistry.TryResolveAsset(request.EffectId, out var asset) || asset is null)
            return InertHandle.Instance;
        try
        {
            var handle = new EffectInstanceHandle(this, asset, request);
            handles.Add(handle);
            return handle;
        }
        catch (ParticlePoolExhaustedException)
        {
            WarnPoolExhausted(request.EffectId);
            return InertHandle.Instance;
        }
    }

    private void WarnPoolExhausted(string effectId)
    {
        long now = ClockMsProvider();
        if (hasWarnedPoolExhausted && now - lastPoolExhaustedWarningMs < PoolExhaustedWarningCooldownMs)
            return;
        hasWarnedPoolExhausted = true;
        lastPoolExhaustedWarningMs = now;
        PoolExhaustedWarningSink(
            $"[RimLumen] GPU particle slot pool is full; skipping '{effectId}' until active particles expire (throttled to once per 10s).");
    }

    // Ensures: true while any live handle is alive; false once every handle is dead (zero draws).
    public bool HasActiveWork => LiveHandleCount != 0;

    // Requires: main thread, called before CpuParticleSimulator.CaptureMainThread in the same frame.
    // Ensures: advances every live handle by context.DeltaTime (real-time handles by UnscaledDeltaTime when it is
    //          positive) and drops dead handles.
    public void CaptureMainThread(in LumenCaptureContext context)
    {
        for (int i = handles.Count - 1; i >= 0; i--)
        {
            var handle = handles[i];
            float dt = handle.RealTime && context.UnscaledDeltaTime > 0f ? context.UnscaledDeltaTime : context.DeltaTime;
            handle.Advance(dt);
            if (!handles[i].IsAlive) handles.RemoveAt(i);
        }
    }

    // Ensures: no-op. Compute dispatches belong to the registered contributors that own them
    //          (CpuParticleSimulator / the L2 engine); the service must not duplicate them.
    public void EnqueuePreCompute(in LumenComputeContext context) { }

    // Ensures: no-op, for the same reason as EnqueuePreCompute.
    public void RecordBufferBarriers(in LumenBarrierContext context) { }

    // Ensures: no-op, for the same reason as EnqueuePreCompute: CpuParticleSimulator.RecordHdrDraws
    //          submits the L1 batch once, and the L2 engine submits its own instances.
    public void RecordHdrDraws(in LumenHdrDrawContext context) { }
}
