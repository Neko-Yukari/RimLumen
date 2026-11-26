using RimLumen.Core.Contracts;
using Waylith.Jobs;

namespace RimLumen.Core.Scene;

// Game half of CpuParticleSimulator. WebSim links CpuParticleSimulator.cs alone and simulates through CaptureMainThread.
public sealed partial class CpuParticleSimulator
{
    private int simulationJob;

    private struct SimulationJob : IWaylithJob
    {
        public CpuParticleSimulator Owner;
        public FrameInput Input;
        public FrameOutput Output;

        public void Execute() => Owner.Simulate(in Input, Output);
    }

    // Requires: main thread, the render pipeline's per-frame capture; no CaptureMainThread on this instance.
    // Ensures: completes the simulation scheduled by the previous call, rethrowing its exception, and publishes it as
    //          this frame's batch; applies the queued commands in call order and drops the quads and trail strips of the
    //          emitters they release immediately from that batch. If work remains it then gathers this frame's host
    //          state and schedules Simulate on WaylithJobRuntime, to be staged by the next call: L1 shows one frame late.
    internal void CaptureOffThread(in LumenCaptureContext context)
    {
        CompleteOffThread();
        ApplyCommands();
        DrainReclaimed();
        DropReleased();
        active = HasCoreWork;
        if (!active) return;
        FrameInput input = Gather(in context);
        simulationJob = WaylithJobRuntime.Schedule(new SimulationJob { Owner = this, Input = input, Output = BeginSimulation() });
    }

    // Requires: main thread. Frames that skip the capture call this so a simulation is never left in flight long
    //           enough for WaylithJobRuntime to discard its handle and with it the job's exception.
    // Ensures: waits for the in-flight simulation, rethrowing its exception, and publishes its result; no-op otherwise.
    internal void CompleteOffThread()
    {
        if (!simulationInFlight) return;
        // Cleared first: a faulted job is rethrown once, never completed again.
        simulationInFlight = false;
        WaylithJobRuntime.Complete(simulationJob);
        Publish();
    }
}
