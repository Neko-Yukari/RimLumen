using System;
using RimLumen.Core.Assets;
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

    [LumenFact]
    public static void ProviderBoundaryRejectsAndCachesPerAssetInstance()
    {
        var invalid = new EffectAsset
        {
            EffectId = "provider.invalid.once",
            Nodes = new[] { new EffectNodeSpec { Name = "root", Emitter = new EmitterSpec { Name = "n", Duration = 0f } } },
        };
        var fallback = new InMemoryAssetCatalog();
        fallback.Register(new EffectAsset { EffectId = invalid.EffectId, Nodes = new[] { new EffectNodeSpec { Name = "fallback" } } });
        var provider = new InMemoryAssetCatalog();
        provider.Register(invalid);
        LumenHostRegistry.RegisterAssetProvider(fallback);
        LumenHostRegistry.RegisterAssetProvider(provider);
        try
        {
            Require(LumenHostRegistry.ResolveAsset(invalid.EffectId, out var first) == LumenAssetResolution.Rejected && first is null, "Invalid provider asset must report rejection without falling through.");
            Require(LumenHostRegistry.ResolveAsset(invalid.EffectId, out var second) == LumenAssetResolution.Rejected && second is null, "Cached invalid asset must remain rejected, not become unknown.");
            Require(!LumenHostRegistry.TryResolveAsset(invalid.EffectId, out var rejected) && rejected is null, "Public bool resolve must preserve rejection behavior.");
            provider.Register(new EffectAsset { EffectId = invalid.EffectId, Nodes = new[] { new EffectNodeSpec { Name = "replacement" } } });
            Require(LumenHostRegistry.TryResolveAsset(invalid.EffectId, out var replacement) && replacement!.Nodes[0].Name == "replacement", "New asset instance must validate independently of cached rejection.");
        }
        finally
        {
            provider.Clear();
            fallback.Clear();
        }
    }

    [LumenFact]
    public static void ProviderBoundaryRejectionMessageNamesProviderAssetAndRule()
    {
        var invalid = new EffectAsset
        {
            EffectId = "provider.invalid.message",
            Nodes = new[] { new EffectNodeSpec { Name = "root", Emitter = new EmitterSpec { Name = "n", Duration = 0f } } },
        };
        var provider = new InMemoryAssetCatalog();
        var cache = new LumenAssetValidationCache();
        Require(!cache.TryValidate(invalid, provider, out string? rejection), "Invalid instance must fail validation.");
        string message = rejection ?? throw new InvalidOperationException("Expected a rejection message.");
        Require(message.Contains(typeof(InMemoryAssetCatalog).FullName!, StringComparison.Ordinal) &&
            message.Contains(invalid.EffectId, StringComparison.Ordinal) && message.Contains("duration", StringComparison.Ordinal), message);
        Require(!cache.TryValidate(invalid, provider, out var repeated) && repeated is null, "Repeated rejected instance must not produce another log message.");
        Require(!cache.TryValidate(new EffectAsset { EffectId = invalid.EffectId, Nodes = invalid.Nodes }, provider, out var alias) && alias is not null, "Alias/new asset instance must be independently rejected.");
    }

    [LumenFact]
    public static void ProviderBoundaryAcceptsValidAsset()
    {
        var asset = new EffectAsset { EffectId = "provider.valid", Nodes = new[] { new EffectNodeSpec { Name = "root" } } };
        var provider = new InMemoryAssetCatalog();
        provider.Register(asset);
        LumenHostRegistry.RegisterAssetProvider(provider);
        try
        {
            Require(LumenHostRegistry.ResolveAsset(asset.EffectId, out var first) == LumenAssetResolution.Found && ReferenceEquals(first, asset), "Valid provider asset must report found.");
            Require(LumenHostRegistry.TryResolveAsset(asset.EffectId, out var second) && ReferenceEquals(second, asset), "Valid cached asset must resolve.");
        }
        finally { provider.Clear(); }
    }

    [LumenFact]
    public static void ProviderBoundaryReportsUnknownIdWithoutAnAsset()
    {
        Require(LumenHostRegistry.ResolveAsset("provider.f10.missing", out var asset) == LumenAssetResolution.NotFound && asset is null,
            "An unknown id must be distinguished from a rejected provider asset.");
        Require(!LumenHostRegistry.TryResolveAsset("provider.f10.missing", out var missing) && missing is null,
            "Public bool resolve must preserve unknown-id behavior.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
