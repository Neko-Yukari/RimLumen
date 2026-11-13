using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Waylith.Rendering;

[assembly: InternalsVisibleTo("RimLumen.Core.Tests")]

namespace RimLumen.Core.ParticlesGpu;

internal sealed record ParticleQueuedFrame(ParticleFrame Snapshot, Texture[] Textures, bool[] TexturePresent);

// Producer: capture thread. Consumer and retirement: render thread. Keys are
// host Timing.FrameNumber, never the rotating SubmissionFrameIndex.
internal sealed class ParticleFrameQueue
{
    private readonly ConcurrentDictionary<ulong, ParticleQueuedFrame> frames = new();

    internal bool IsEmpty => frames.IsEmpty;

    internal void Enqueue(ParticleQueuedFrame frame)
    {
        if (!frames.TryAdd(frame.Snapshot.Context.FrameIndex, frame))
            throw new System.InvalidOperationException("Particle frame was enqueued twice.");
    }

    internal bool TryTake(ulong frameNumber, out ParticleQueuedFrame? frame) =>
        frames.TryRemove(frameNumber, out frame);

    internal void Retire(ulong frameNumber) => frames.TryRemove(frameNumber, out _);

    internal void Clear() => frames.Clear();
}
