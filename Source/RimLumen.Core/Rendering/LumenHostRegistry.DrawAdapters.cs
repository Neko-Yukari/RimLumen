using System;
using System.Collections.Generic;
using RimLumen.Core.Rendering;
using Waylith.Rendering;

namespace RimLumen.Core.Contracts;

// Host-render half of LumenHostRegistry: depends on Waylith render passes, so it stays out of the WebEditor link set.
public static partial class LumenHostRegistry
{
    internal static readonly Dictionary<byte, DrawAdapterPass> DrawAdapters = new();

    // Main thread, at startup. Passes and adapters are process-lived; no global layer is assigned.
    public static byte RegisterDrawAdapter(string passName, ILumenDrawAdapter adapter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passName);
        ArgumentNullException.ThrowIfNull(adapter);
        byte id = RenderPassRegistry.Register("RimLumen.Adapter." + passName);
        if (DrawAdapters.TryGetValue(id, out var existing))
        {
            if (!ReferenceEquals(existing.Adapter, adapter))
                throw new InvalidOperationException($"A draw adapter is already registered for '{passName}'.");
        }
        else DrawAdapters.Add(id, new DrawAdapterPass(id, adapter));
        return id;
    }
}
