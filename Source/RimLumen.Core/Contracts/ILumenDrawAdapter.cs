using Waylith.Rendering;

namespace RimLumen.Core.Contracts;

/// <summary>Adapts world draws tagged with this adapter's private pass during main-thread map capture.</summary>
public interface ILumenDrawAdapter
{
    /// <summary>Layers to omit from vanilla glow copies. The host combines all masks per capture.</summary>
    int ExcludedGlowLayerMask { get; }

    /// <summary>Borrow source for this call. Return null to restore its map route, or a binding that stays
    /// immutable for all in-flight frames, does not write depth, and accepts the source mesh, uniforms and
    /// resources unchanged. The host owns the pooled copy. A thrown exception trips the host render fail-safe.</summary>
    MaterialBinding? AdaptBinding(FinalDrawItem source, LumenColorBlendSpace blendSpace);
}
