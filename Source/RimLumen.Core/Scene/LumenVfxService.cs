using System;
using System.Collections.Generic;
using RimLumen.Core.Contracts;
using RimLumen.Core.Lighting;
using RimLumen.Core.ParticlesGpu;
using RimLumen.Core.Rendering;

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
    private readonly HashSet<string> warnedUnknownEffects = new(StringComparer.Ordinal);
    // Per-frame scratch: how many queued events each handle raises this frame (fixed before any handler runs).
    private int[] eventBatches = Array.Empty<int>();
    private bool hasWarnedPoolExhausted;
    private long lastPoolExhaustedWarningMs;
    private uint playSequence;

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
    //          LumenMapLightingRegistry.TryResolve with the exact map uniqueID normalized at Play.
    public Func<int, MapGlowerDriver?>? GlowerDriverProvider { get; set; }

    internal Func<long> ClockMsProvider { get; set; } = static () => Environment.TickCount64;
    internal Action<string> PoolExhaustedWarningSink { get; set; } = static message => Verse.Log.Warning(message);

    // Ensures: always "LumenVfxService".
    public string Name => "LumenVfxService";

    // Ensures: HDR/depth are the render host's last recorded frame facts, not renderer installation state.
    //          IsGpuComputeAvailable reports a resolved L2 backend (not a dispatch this frame);
    //          MaxGpuParticlesPerFrame is the shared slot limit, not the active particle count.
    public LumenCapabilities Capabilities
    {
        get
        {
            LumenCapabilities frame = RimLumenRenderHost.Capabilities;
            ILumenGpuParticleEngine? engine = GpuEngineProvider?.Invoke();
            return new LumenCapabilities(frame.IsHdrActive, frame.IsSceneDepthAvailable, engine is not null,
                engine?.Capacity ?? 0);
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
    // Ensures: resolves request.EffectId through LumenHostRegistry; a miss returns an
    //          inert handle (IsAlive false, every member a safe no-op) that is not tracked as live.
    //          Unknown ids warn once per id for this service; rejected provider assets keep their registry error only.
    //          A hit creates an EffectInstanceHandle, binds each node emitter to the L2 engine when the
    //          emitter prefers L2 and an engine resolves, otherwise to CpuParticleSimulator, and returns it.
    //          The handle enters request.InitialState, or the asset's initial state when it is null.
    //          Provider assets rejected by registry validation (including invalid state graphs) return an inert handle.
    //          Throws ArgumentException for a request.InitialState the validated asset does not declare. MapIndex -1 is
    //          resolved once to Find.CurrentMap.uniqueID; with no current map it throws InvalidOperationException.
    //          Other negative ids throw ArgumentOutOfRangeException. Explicit ids never read CurrentMap.
    public ILumenEffectHandle Play(in LumenPlayRequest request)
    {
        LumenAssetResolution resolution = LumenHostRegistry.ResolveAsset(request.EffectId, out var asset);
        if (resolution != LumenAssetResolution.Found)
        {
            if (ShouldWarnUnknownEffect(request.EffectId, resolution))
                Verse.Log.Warning($"[RimLumen] Unknown effect '{request.EffectId}'; skipping Play (warned once per id).");
            return InertHandle.Instance;
        }
        int mapIndex = LumenPlayMap.ResolveMapIndex(request.MapIndex,
            request.MapIndex == -1 ? Verse.Find.CurrentMap?.uniqueID : null);
        LumenPlayRequest normalized = request with { MapIndex = mapIndex };
        try
        {
            uint playSeed = unchecked(++playSequence * 0x9E3779B9u);
            var handle = new EffectInstanceHandle(this, asset!, normalized, playSeed);
            handles.Add(handle);
            return handle;
        }
        catch (ParticlePoolExhaustedException)
        {
            WarnPoolExhausted(request.EffectId);
            return InertHandle.Instance;
        }
    }

    internal bool ShouldWarnUnknownEffect(string effectId, LumenAssetResolution resolution)
        => resolution == LumenAssetResolution.NotFound && warnedUnknownEffects.Add(effectId);

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

    // Ensures: no-op. The pipeline appends L1 batches and the L2 engine appends its own instances;
    //          MeshBatchRenderer records both from the captured draw list.
    public void RecordHdrDraws(in LumenHdrDrawContext context) { }
}
