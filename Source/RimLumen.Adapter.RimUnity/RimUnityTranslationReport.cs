using System;
using System.Collections.Generic;
using RimLumen.Core.Contracts;
using RimUnity.Serialization;

namespace RimLumen.Adapter.RimUnity;

public sealed record RimUnityNodeTranslation(int NodeIndex, string NodeId, LumenParticleTier Tier, IReadOnlyList<string> Reasons);

public sealed class RimUnityTranslationReport
{
    public required string EffectId { get; init; }
    public required string PackageId { get; init; }
    public required string LogicalPath { get; init; }
    public required string DataDirectory { get; init; }
    public required PrefabData Prefab { get; init; }
    public IReadOnlyList<RimUnityNodeTranslation> Nodes { get; init; } = Array.Empty<RimUnityNodeTranslation>();
}
