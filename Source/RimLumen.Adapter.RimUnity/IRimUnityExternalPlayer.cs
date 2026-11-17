using RimLumen.Core.Contracts;

namespace RimLumen.Adapter.RimUnity;

public interface IRimUnityExternalPlayer
{
    // Requires: report identifies the selected prefab and its External nodes; main thread.
    // Ensures: plays only the reported External nodes, with a handle owning their lifetime.
    ILumenEffectHandle Play(in LumenPlayRequest request, RimUnityTranslationReport report);
}
