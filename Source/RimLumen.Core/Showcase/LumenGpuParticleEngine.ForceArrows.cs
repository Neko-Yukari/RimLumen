using System.Collections.Generic;
using RimLumen.Core.Showcase;
using Waylith.Rendering;

namespace RimLumen.Core.ParticlesGpu;

// Lives under Showcase/ so the release build strips it; without these bodies the compiler drops both hook calls in
// LumenGpuParticleEngine.Gpu.cs.
internal sealed partial class LumenGpuParticleEngine
{
    partial void RecordDevCompute(in FrameContext frame, ParticleFrame snapshot, ParticleGpuFrame resources) =>
        ShowcaseForceArrows.Renderer?.RecordCompute(frame, snapshot, particles!, resources);

    partial void AppendDevDraws(ParticleQueuedFrame frame, List<FinalDrawItem> draws, DrawItemFactory maker,
        DrawPropertyArena arena, int queueOrder)
    {
        if (ShowcaseForceArrows.Enabled) ShowcaseForceArrows.Renderer!.AppendDraws(frame.Snapshot, draws, maker, arena, queueOrder);
    }
}
