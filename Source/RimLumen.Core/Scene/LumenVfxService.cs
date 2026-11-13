using System;
using RimLumen.Core.Contracts;
using RimLumen.Core.Lighting;

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
    // Requires: simulator is non-null and is the instance registered as a frame contributor.
    public LumenVfxService(CpuParticleSimulator simulator) => throw new NotImplementedException();

    public CpuParticleSimulator Simulator => throw new NotImplementedException();

    // Ensures: L2 backend resolver. Default resolves LumenHostRegistry.GpuParticleEngine on every Play,
    //          so an engine registered after bootstrap is still picked up.
    public Func<ILumenGpuParticleEngine?>? GpuEngineProvider { get; set; }

    // Ensures: per-map glower resolver. Default resolves through LumenMapLightingRegistry, treating a
    //          negative map index as "the most recently registered map".
    public Func<int, MapGlowerDriver?>? GlowerDriverProvider { get; set; }

    // Ensures: always "LumenVfxService".
    public string Name => throw new NotImplementedException();

    // Ensures: capability snapshot derived from the resolvers above.
    public LumenCapabilities Capabilities => throw new NotImplementedException();

    // Ensures: number of handles that have not been released yet.
    public int LiveHandleCount => throw new NotImplementedException();

    // Requires: called on the main thread; request.EffectId is non-empty.
    // Ensures: resolves request.EffectId through LumenHostRegistry.TryResolveAsset; a miss returns an
    //          inert handle whose IsAlive is false and whose members are safe no-ops.
    public ILumenEffectHandle Play(in LumenPlayRequest request) => throw new NotImplementedException();

    // Ensures: true while any live handle is alive; false once every handle is dead (zero draws).
    public bool HasActiveWork => throw new NotImplementedException();

    // Requires: main thread, called before CpuParticleSimulator.CaptureMainThread in the same frame.
    // Ensures: advances every live handle by context.DeltaTime and drops dead handles.
    public void CaptureMainThread(in LumenCaptureContext context) => throw new NotImplementedException();

    // Ensures: no-op. Compute dispatches belong to the registered contributors that own them
    //          (CpuParticleSimulator / the L2 engine); the service must not duplicate them.
    public void EnqueuePreCompute(in LumenComputeContext context) => throw new NotImplementedException();

    // Ensures: no-op, for the same reason as EnqueuePreCompute.
    public void RecordBufferBarriers(in LumenBarrierContext context) => throw new NotImplementedException();

    // Ensures: no-op, for the same reason as EnqueuePreCompute: CpuParticleSimulator.RecordHdrDraws
    //          submits the L1 batch once, and the L2 engine submits its own instances.
    public void RecordHdrDraws(in LumenHdrDrawContext context) => throw new NotImplementedException();
}
