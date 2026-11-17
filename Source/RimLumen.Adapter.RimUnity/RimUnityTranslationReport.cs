using System;
using System.Collections.Generic;
using RimLumen.Core.Contracts;
using RimUnity.Serialization;

namespace RimLumen.Adapter.RimUnity;

// One entry per translated component of a node: Component is "ParticleSystem", "TrailRenderer" or "LineRenderer".
// A native ribbon reports L1_NativeCpu (its strip is built on the CPU each frame).
public sealed record RimUnityNodeTranslation(int NodeIndex, string NodeId, string Component, LumenParticleTier Tier, IReadOnlyList<string> Reasons);

public sealed class RimUnityTranslationReport
{
    public required string EffectId { get; init; }
    public required string PackageId { get; init; }
    public required string LogicalPath { get; init; }
    public required string DataDirectory { get; init; }
    public required PrefabData Prefab { get; init; }
    public IReadOnlyList<RimUnityNodeTranslation> Nodes { get; init; } = Array.Empty<RimUnityNodeTranslation>();
}
