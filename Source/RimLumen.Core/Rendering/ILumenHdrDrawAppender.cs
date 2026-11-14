using System.Collections.Generic;
using Waylith.Rendering;

namespace RimLumen.Core.Rendering;

// Main-thread seam for VFX that submit their own meshes into the RimLumenHdr pass. MeshDrawSubmission
// drains mesh uploads immediately before the frame's draw snapshot is captured, and MeshBatchRenderer
// records the sorted pass-0 list, so a contributor with mesh draws must append here (during Capture)
// instead of recording command-buffer work inside RecordHdrDraws.
internal interface ILumenHdrDrawAppender
{
    // Requires: main thread, after the engine flushed mesh uploads for this frame.
    // Ensures: appends zero or more pass-0 draws to `draws`; `sceneDepth` mirrors this frame's depth proxy.
    void AppendHdrDraws(ulong frameIndex, List<FinalDrawItem> draws, DrawItemFactory maker, DrawPropertyArena arena,
        int queueOrder, Texture sceneDepth);
}
