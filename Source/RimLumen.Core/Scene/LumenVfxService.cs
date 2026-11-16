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
    // Per-frame scratch: how many queued events each handle raises this frame (fixed before any handler runs).
    private int[] eventBatches = Array.Empty<int>();
    private bool hasWarnedPoolExhausted;
    private long lastPoolExhaustedWarningMs;

    internal sealed class InertHandle : ILumenEffectHandle
    {
        public static readonly InertHandle Instance = new();
        public bool IsAlive => false;
        public void Trigger(string name) { }
        public void GoTo(string stateName) { }
        public void SetParam(string name, float value) { }
        public void Stop(bool allowFadeOut = true) { }
        public void Dispose() { }
        // Never raised: the handle is dead before anyone can subscribe.
        public event Action<string>? Notify { add { } remove { } }
        public event Action<string>? StateChanged { add { } remove { } }
        public event Action? Finished { add { } remove { } }
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
    //          yields an engine; MaxGpuParticlesPerFrame is 4096 with an engine and 0 without.
    public LumenCapabilities Capabilities
    {
        get
        {
            bool hdr = Simulator.BatchSink is not null;
            bool gpu = GpuEngineProvider?.Invoke() is not null;
            return new LumenCapabilities(hdr, hdr, gpu, gpu ? 4096 : 0);
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
    //          The handle enters request.InitialState, or the asset's initial state when it is null.
    //          Throws InvalidDataException for an asset whose states do not validate (EffectStateGraph.For) and
    //          ArgumentException for a request.InitialState the asset does not declare.
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

    // Ensures: true while any handle is alive or still owes its lifecycle events (Finished after an immediate
    //          stop is raised on the next frame); false once every handle is dead and has raised them (zero draws).
    public bool HasActiveWork
    {
        get
        {
            foreach (var handle in handles)
                if (handle.IsAlive || handle.HasPendingEvents) return true;
            return false;
        }
    }

    // Requires: main thread, called before CpuParticleSimulator.CaptureMainThread in the same frame.
    // Ensures: advances every handle by context.DeltaTime (real-time handles by UnscaledDeltaTime when it is
    //          positive); then each handle raises the events queued before any handler of this frame ran, so an
    //          event a handler causes on any handle (including Finished after Stop(false)) waits for the next frame
    //          whatever the handle order; finally drops dead handles that have raised all their events.
    //          Handles played from a handler start on the next frame.
    public void CaptureMainThread(in LumenCaptureContext context)
    {
        int count = handles.Count;
        if (eventBatches.Length < count) eventBatches = new int[count];
        for (int i = 0; i < count; i++)
        {
            var handle = handles[i];
            handle.Advance(handle.RealTime && context.UnscaledDeltaTime > 0f ? context.UnscaledDeltaTime : context.DeltaTime);
            eventBatches[i] = handle.PendingEventCount;
        }
        for (int i = 0; i < count; i++) handles[i].RaiseEvents(eventBatches[i]);
        handles.RemoveAll(static handle => !handle.IsAlive && !handle.HasPendingEvents);
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
