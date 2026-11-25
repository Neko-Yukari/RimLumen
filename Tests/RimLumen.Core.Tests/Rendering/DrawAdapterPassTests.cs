using System;
using RimLumen.Core.Contracts;
using Waylith.Rendering;

namespace RimLumen.Core.Tests.Rendering;

[LumenTestSuite]
public static class DrawAdapterPassTests
{
    [LumenFact]
    public static void PrivatePassesKeepTheirRegisteredOwner()
    {
        var first = new Adapter();
        var second = new Adapter();
        byte a = LumenHostRegistry.RegisterDrawAdapter("Tests.First", first);
        byte b = LumenHostRegistry.RegisterDrawAdapter("Tests.Second", second);
        if (a == b || a == 0 || b == 0 || LumenHostRegistry.RegisterDrawAdapter("Tests.First", first) != a)
            throw new InvalidOperationException("Adapter passes must be distinct, non-zero and stable.");
        try { LumenHostRegistry.RegisterDrawAdapter("Tests.First", second); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Another adapter displaced the owner of a private pass.");
    }

    private sealed class Adapter : ILumenDrawAdapter
    {
        public int ExcludedGlowLayerMask => 0;
        public MaterialBinding? AdaptBinding(FinalDrawItem source, LumenColorBlendSpace blendSpace) => null;
    }
}
