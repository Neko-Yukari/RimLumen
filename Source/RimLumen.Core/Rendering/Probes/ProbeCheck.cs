using System.Collections.Generic;

namespace RimLumen.Core.Rendering.Probes;

// One entry of .scratch/probe-results/<run>/probe-result.json; Id is T1a/T1b/T2/T3/T4/P1-idle/P1-active.
public sealed record ProbeCheck(string Id, bool Passed, string Detail, IReadOnlyDictionary<string, double> Metrics);
