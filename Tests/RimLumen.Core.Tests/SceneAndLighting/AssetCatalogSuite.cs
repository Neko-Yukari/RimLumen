using System;
using System.Threading.Tasks;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class AssetCatalogSuite
{
    [LumenFact]
    public static void RegisterReplacesAndLookupUsesOrdinalIds()
    {
        var catalog = new InMemoryAssetCatalog();
        ILumenAssetProvider provider = catalog;
        if (catalog.Count != 0 || provider.TryResolveEffect("missing", out var missing) || missing is not null)
        {
            throw new InvalidOperationException("An empty catalog must resolve a miss to false/null.");
        }

        var first = new EffectAsset
        {
            EffectId = "Spell",
            Nodes = new[]
            {
                new EffectNodeSpec { Name = "root" },
                new EffectNodeSpec { Name = "child", ParentIndex = 0 },
                new EffectNodeSpec { Name = "grandchild", ParentIndex = 1 },
                new EffectNodeSpec { Name = "other root" },
            },
        };
        catalog.Register(first);
        if (catalog.Count != 1 || !provider.TryResolveEffect("Spell", out var resolved) || !ReferenceEquals(first, resolved))
        {
            throw new InvalidOperationException("Register must expose the exact asset through ILumenAssetProvider.");
        }
        if (provider.TryResolveEffect("spell", out missing) || missing is not null)
        {
            throw new InvalidOperationException("Effect IDs must be case-sensitive.");
        }

        var replacement = new EffectAsset { EffectId = "Spell", DefaultDuration = 10f };
        catalog.Register(replacement);
        if (catalog.Count != 1 || !provider.TryResolveEffect("Spell", out resolved) || !ReferenceEquals(replacement, resolved))
        {
            throw new InvalidOperationException("Register must replace an existing ID without adding an entry.");
        }
        catalog.Register(new EffectAsset { EffectId = "spell" });
        if (catalog.Count != 2)
        {
            throw new InvalidOperationException("Distinct ordinal IDs must have distinct entries.");
        }
    }

    [LumenFact]
    public static void RejectsInvalidIdsAndParentIndicesWithoutChangingEntries()
    {
        var catalog = new InMemoryAssetCatalog();
        var original = new EffectAsset { EffectId = "effect" };
        catalog.Register(original);
        AssertArgumentException(() => catalog.Register(new EffectAsset { EffectId = "" }));
        AssertArgumentException(() => catalog.Register(new EffectAsset { EffectId = null! }));
        AssertArgumentException(() => catalog.Register(new EffectAsset
        {
            EffectId = "effect", Nodes = new[] { new EffectNodeSpec { Name = "negative", ParentIndex = -2 } },
        }));
        AssertArgumentException(() => catalog.Register(new EffectAsset
        {
            EffectId = "effect", Nodes = new[] { new EffectNodeSpec { Name = "self", ParentIndex = 0 } },
        }));
        AssertArgumentException(() => catalog.Register(new EffectAsset
        {
            EffectId = "effect",
            Nodes = new[] { new EffectNodeSpec { Name = "forward", ParentIndex = 1 }, new EffectNodeSpec { Name = "root" } },
        }));
        AssertArgumentException(() => catalog.Register(new EffectAsset
        {
            EffectId = "effect",
            Nodes = new[] { new EffectNodeSpec { Name = "root" }, new EffectNodeSpec { Name = "self", ParentIndex = 1 } },
        }));
        AssertArgumentException(() => catalog.Register(new EffectAsset
        {
            EffectId = "effect",
            Nodes = new[] { new EffectNodeSpec { Name = "root" }, new EffectNodeSpec { Name = "negative", ParentIndex = -2 } },
        }));
        if (catalog.Count != 1 || !catalog.TryResolveEffect("effect", out var resolved) || !ReferenceEquals(original, resolved))
        {
            throw new InvalidOperationException("A rejected registration must not replace the original entry.");
        }
    }

    [LumenFact]
    public static void UnregisterAndClearRemoveEntries()
    {
        var catalog = new InMemoryAssetCatalog();
        catalog.Register(new EffectAsset { EffectId = "first" });
        catalog.Register(new EffectAsset { EffectId = "second" });
        if (!catalog.Unregister("first") || catalog.Unregister("first") || catalog.Count != 1)
        {
            throw new InvalidOperationException("Unregister must distinguish removed and absent entries.");
        }
        if (catalog.TryResolveEffect("first", out var removed) || removed is not null)
        {
            throw new InvalidOperationException("A removed ID must resolve to false/null.");
        }
        catalog.Clear();
        catalog.Clear();
        if (catalog.Count != 0 || catalog.TryResolveEffect("second", out removed) || removed is not null)
        {
            throw new InvalidOperationException("Clear must remove all entries and be repeatable.");
        }
    }

    [LumenFact]
    public static void ParallelRegistrationsAndRemovalsRetainEveryIndependentEntry()
    {
        var catalog = new InMemoryAssetCatalog();
        var assets = new EffectAsset[128];
        Parallel.For(0, assets.Length, i =>
        {
            var asset = new EffectAsset { EffectId = $"effect.{i}" };
            assets[i] = asset;
            catalog.Register(asset);
        });
        if (catalog.Count != assets.Length)
        {
            throw new InvalidOperationException("Concurrent registrations lost entries.");
        }
        Parallel.For(0, assets.Length, i =>
        {
            if (!catalog.TryResolveEffect(assets[i].EffectId, out var resolved) || !ReferenceEquals(assets[i], resolved))
            {
                throw new InvalidOperationException("Concurrent lookup returned the wrong entry.");
            }
            if (!catalog.Unregister(assets[i].EffectId))
            {
                throw new InvalidOperationException("Concurrent removal lost an independent entry.");
            }
        });
        if (catalog.Count != 0)
        {
            throw new InvalidOperationException("Concurrent removals left entries behind.");
        }
    }

    [LumenFact]
    public static void ParallelReplacementAndClearOnlyExposeWholeRegisteredAssets()
    {
        var catalog = new InMemoryAssetCatalog();
        var first = new EffectAsset { EffectId = "shared", DefaultDuration = 1f };
        var second = new EffectAsset { EffectId = "shared", DefaultDuration = 2f };
        Parallel.For(0, 1000, i =>
        {
            catalog.Register(i % 2 == 0 ? first : second);
            bool found = catalog.TryResolveEffect("shared", out var resolved);
            if ((found && !ReferenceEquals(first, resolved) && !ReferenceEquals(second, resolved)) ||
                (!found && resolved is not null))
            {
                throw new InvalidOperationException("Concurrent lookup exposed an unregistered asset or an invalid miss.");
            }
            catalog.Clear();
        });
        catalog.Register(second);
        if (catalog.Count != 1 || !catalog.TryResolveEffect("shared", out var final) || !ReferenceEquals(second, final))
        {
            throw new InvalidOperationException("The catalog must remain usable after concurrent replacement/clear.");
        }
    }

    private static void AssertArgumentException(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new InvalidOperationException("Expected ArgumentException.");
    }
}
