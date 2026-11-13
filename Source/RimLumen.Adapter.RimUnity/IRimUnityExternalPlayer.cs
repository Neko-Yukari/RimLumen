using RimLumen.Core.Contracts;

namespace RimLumen.Adapter.RimUnity;

public interface IRimUnityExternalPlayer
{
    // Requires: report identifies the selected prefab and its L0 nodes; main thread.
    // Ensures: plays only the reported L0 nodes, with a handle owning their lifetime.
    ILumenEffectHandle Play(in LumenPlayRequest request, RimUnityTranslationReport report);
}
