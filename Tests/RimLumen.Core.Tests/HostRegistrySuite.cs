using System;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;

namespace RimLumen.Core.Tests;

[LumenTestSuite]
public static class HostRegistrySuite
{
    [LumenFact]
    public static void ContributorSnapshotsPreserveMembershipOrderAndCallerIsolation()
    {
        ILumenFrameContributor[] before = LumenHostRegistry.GetContributorsSnapshot();
        var first = new CpuParticleSimulator();
        var second = new CpuParticleSimulator();
        try
        {
            LumenHostRegistry.RegisterContributor(first);
            LumenHostRegistry.RegisterContributor(first);
            LumenHostRegistry.RegisterContributor(second);
            ILumenFrameContributor[] registered = LumenHostRegistry.GetContributorsSnapshot();
            Require(registered.Length == before.Length + 2, "Duplicate registration must be idempotent.");
            Require(ReferenceEquals(registered[^2], first) && ReferenceEquals(registered[^1], second),
                "Contributors must retain registration order.");
            Require(Array.IndexOf(before, first) < 0, "Registration must not mutate earlier snapshots.");

            ILumenFrameContributor[] callerCopy = LumenHostRegistry.GetContributorsSnapshot();
            callerCopy[^1] = first;
            Require(ReferenceEquals(LumenHostRegistry.GetContributorsSnapshot()[^1], second),
                "A caller must not mutate published registry contents through its snapshot.");

            LumenHostRegistry.UnregisterContributor(first);
            LumenHostRegistry.UnregisterContributor(first);
            ILumenFrameContributor[] removed = LumenHostRegistry.GetContributorsSnapshot();
            Require(removed.Length == before.Length + 1 && Array.IndexOf(removed, first) < 0
                && ReferenceEquals(removed[^1], second), "Unregistration must remove only the requested contributor.");
            Require(ReferenceEquals(registered[^2], first) && ReferenceEquals(registered[^1], second),
                "Unregistration must not mutate captured snapshots.");
        }
        finally
        {
            LumenHostRegistry.UnregisterContributor(first);
            LumenHostRegistry.UnregisterContributor(second);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
