using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class AssetReloadSuite
{
    [LumenFact]
    public static void ReloadReplacesScanInstancesAndKeepsFirstWriterOrder()
    {
        string root = NativeTestData.NewTempDirectory();
        try
        {
            string dataA = Path.Combine(root, "A", "Data", "RimLumen");
            string dataB = Path.Combine(root, "B", "Data", "RimLumen");
            Directory.CreateDirectory(dataA);
            Directory.CreateDirectory(dataB);
            string fileA = Path.Combine(dataA, "Fireball.lumen.json");
            File.WriteAllText(fileA, """{ "nodes": [ { "name": "from_a1" } ] }""");
            File.WriteAllText(Path.Combine(dataB, "Fireball.lumen.json"), """{ "nodes": [ { "name": "from_b1" } ] }""");
            var roots = new[] { new LumenScanRoot("mod.a", dataA), new LumenScanRoot("mod.b", dataB) };
            var catalog = new InMemoryAssetCatalog();

            var first = LumenModAssetScanner.ScanSources(catalog, roots, Array.Empty<LumenEffectDef>());
            NativeTestData.Check(first.JsonLoaded == 2 && first.DefsLoaded == 0, $"initial scan counts, got {first.JsonLoaded}/{first.DefsLoaded}");
            NativeTestData.Check(Node(catalog, "Fireball") == "from_a1", "bare id keeps the first mod");
            NativeTestData.Check(Node(catalog, "mod.a:Fireball") == "from_a1", "first mod qualified id");
            NativeTestData.Check(Node(catalog, "mod.b:Fireball") == "from_b1", "second mod qualified id");
            NativeTestData.Check(catalog.TryResolveEffect("Fireball", out EffectAsset? oldBare), "capture old bare instance");

            File.WriteAllText(fileA, """{ "nodes": [ { "name": "from_a2" } ] }""");
            LumenAssetReloadResult second = LumenModAssetScanner.ReloadEffectAssets(catalog, roots, Array.Empty<LumenEffectDef>());
            NativeTestData.Check(second.Loaded == 2 && second.Failed == 0 && second.AliasChanges.Count == 0,
                "same-source instance refresh is not an alias change");
            NativeTestData.Check(Node(catalog, "Fireball") == "from_a2", "bare id keeps the first mod with new content");
            NativeTestData.Check(Node(catalog, "mod.a:Fireball") == "from_a2", "qualified id refreshes");
            NativeTestData.Check(catalog.TryResolveEffect("Fireball", out EffectAsset? newBare) && !ReferenceEquals(oldBare, newBare),
                "reload installs a new immutable instance");
            NativeTestData.Check(oldBare!.Nodes[0].Name == "from_a1", "already-acquired instance keeps its content");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LumenFact]
    public static void ReloadReleasesDeletedOrBrokenWinnersAndReportsBareChanges()
    {
        string root = NativeTestData.NewTempDirectory();
        try
        {
            string dataA = Path.Combine(root, "A", "Data", "RimLumen");
            string dataB = Path.Combine(root, "B", "Data", "RimLumen");
            Directory.CreateDirectory(dataA);
            Directory.CreateDirectory(dataB);
            string fileA = Path.Combine(dataA, "Fireball.lumen.json");
            string fileB = Path.Combine(dataB, "Fireball.lumen.json");
            File.WriteAllText(fileA, """{ "nodes": [ { "name": "from_a1" } ] }""");
            File.WriteAllText(fileB, """{ "nodes": [ { "name": "from_b1" } ] }""");
            var roots = new[] { new LumenScanRoot("mod.a", dataA), new LumenScanRoot("mod.b", dataB) };
            var catalog = new InMemoryAssetCatalog();
            LumenModAssetScanner.ReloadEffectAssets(catalog, roots, Array.Empty<LumenEffectDef>());

            File.Delete(fileA);
            LumenAssetReloadResult moved = LumenModAssetScanner.ReloadEffectAssets(catalog, roots, Array.Empty<LumenEffectDef>());
            NativeTestData.Check(!catalog.TryResolveEffect("mod.a:Fireball", out _), "deleted qualified id is released");
            NativeTestData.Check(Node(catalog, "Fireball") == "from_b1", "bare id moves to the next live candidate");
            LumenAliasChange movedChange = SingleChange(moved, "Fireball");
            NativeTestData.Check(movedChange.OldSource == "mod.a:" + fileA && movedChange.NewSource == "mod.b:" + fileB,
                $"bare winner report, got {movedChange.OldSource} -> {movedChange.NewSource}");

            File.WriteAllText(fileB, """{ "nodes": [ ] }""");
            LumenAssetReloadResult broken = LumenModAssetScanner.ReloadEffectAssets(catalog, roots, Array.Empty<LumenEffectDef>());
            NativeTestData.Check(broken.Failed == 1, $"broken file is counted once, got {broken.Failed}");
            NativeTestData.Check(!catalog.TryResolveEffect("Fireball", out _) && !catalog.TryResolveEffect("mod.b:Fireball", out _),
                "failed winner releases its ids");
            LumenAliasChange brokenChange = SingleChange(broken, "Fireball");
            NativeTestData.Check(brokenChange.OldSource == "mod.b:" + fileB && brokenChange.NewSource is null,
                $"failed winner reports none, got {brokenChange.OldSource} -> {brokenChange.NewSource}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LumenFact]
    public static void ReloadReportsWinnerChangeBetweenDirectoriesOfSameMod()
    {
        string root = NativeTestData.NewTempDirectory();
        try
        {
            string versioned = Path.Combine(root, "1.7", "Data", "RimLumen");
            string unversioned = Path.Combine(root, "Data", "RimLumen");
            Directory.CreateDirectory(versioned);
            Directory.CreateDirectory(unversioned);
            string first = Path.Combine(versioned, "Fireball.lumen.json");
            string second = Path.Combine(unversioned, "Fireball.lumen.json");
            File.WriteAllText(first, """{ "nodes": [ { "name": "versioned" } ] }""");
            File.WriteAllText(second, """{ "nodes": [ { "name": "unversioned" } ] }""");
            var roots = new[] { new LumenScanRoot("mod.a", versioned), new LumenScanRoot("mod.a", unversioned) };
            var catalog = new InMemoryAssetCatalog();
            LumenModAssetScanner.ScanSources(catalog, roots, Array.Empty<LumenEffectDef>());
            NativeTestData.Check(Node(catalog, "Fireball") == "versioned", "versioned directory wins the bare id");
            File.Delete(first);
            LumenAssetReloadResult result = LumenModAssetScanner.ReloadEffectAssets(catalog, roots, Array.Empty<LumenEffectDef>());
            NativeTestData.Check(Node(catalog, "Fireball") == "unversioned", "unversioned directory becomes the winner");
            LumenAliasChange change = SingleChange(result, "Fireball");
            NativeTestData.Check(change.OldSource == "mod.a:" + first && change.NewSource == "mod.a:" + second,
                "winner report distinguishes the two files in one package");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LumenFact]
    public static void ReloadPublishesFreshIdsAndPreservesUnrelatedCodeAndStaleOverrides()
    {
        string root = NativeTestData.NewTempDirectory();
        try
        {
            string dataA = Path.Combine(root, "A", "Data", "RimLumen");
            Directory.CreateDirectory(dataA);
            string fileA = Path.Combine(dataA, "Fireball.lumen.json");
            File.WriteAllText(fileA, """{ "nodes": [ { "name": "from_a1" } ] }""");
            var roots = new[] { new LumenScanRoot("mod.a", dataA) };
            var catalog = new InMemoryAssetCatalog();
            var codeAsset = new EffectAsset
            {
                EffectId = "Fireball",
                Nodes = new[] { new EffectNodeSpec { Name = "code1" } },
            };
            var codeOnly = new EffectAsset
            {
                EffectId = "CodeOnly",
                Nodes = new[] { new EffectNodeSpec { Name = "unrelated" } },
            };
            catalog.Register(codeAsset);
            catalog.Register(codeOnly);

            LumenModAssetScanner.ReloadEffectAssets(catalog, roots, Array.Empty<LumenEffectDef>());
            NativeTestData.Check(catalog.TryResolveEffect("CodeOnly", out EffectAsset? kept) && ReferenceEquals(kept, codeOnly),
                "unrelated code-only id keeps its instance");
            string freshNode = Node(catalog, "Fireball");
            NativeTestData.Check(freshNode == "from_a1", $"fresh JSON replaces same-name code asset: expected from_a1, actual {freshNode}");
            NativeTestData.Check(catalog.TryResolveEffect("Fireball", out EffectAsset? fresh) && !ReferenceEquals(fresh, codeAsset),
                "fresh id publishes a new scanned instance");
            NativeTestData.Check(codeAsset.Nodes[0].Name == "code1", "previously acquired code instance stays unchanged");
            NativeTestData.Check(Node(catalog, "mod.a:Fireball") == "from_a1", "scan-owned qualified id still loads");

            catalog.Register(new EffectAsset
            {
                EffectId = "mod.a:Fireball",
                Nodes = new[] { new EffectNodeSpec { Name = "code2" } },
            });
            File.Delete(fileA);
            LumenAssetReloadResult reloaded = LumenModAssetScanner.ReloadEffectAssets(catalog, roots, Array.Empty<LumenEffectDef>());
            NativeTestData.Check(!catalog.TryResolveEffect("Fireball", out _), "deleted source releases its scan-owned bare id");
            NativeTestData.Check(catalog.TryResolveEffect("CodeOnly", out kept) && ReferenceEquals(kept, codeOnly),
                "unrelated code-only id survives source deletion");
            NativeTestData.Check(Node(catalog, "mod.a:Fireball") == "code2", "later code override of a stale scanned key is preserved");
            LumenAliasChange change = SingleChange(reloaded, "Fireball");
            NativeTestData.Check(change.OldSource == "mod.a:" + fileA && change.NewSource is null,
                "deleted scan-owned bare id reports its old source to none");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LumenFact]
    public static void ReloadRefreshesLoadedDefsAndKeepsJsonCountsSeparate()
    {
        var catalog = new InMemoryAssetCatalog();
        LumenEffectDef first = Def("v1");
        LumenAssetReloadResult initial = LumenModAssetScanner.ReloadEffectAssets(catalog, Array.Empty<LumenScanRoot>(), new[] { first });
        NativeTestData.Check(initial.Loaded == 0 && initial.Failed == 0, "Def entries are not JSON file counts");
        NativeTestData.Check(Node(catalog, "RimLumen.ReloadDef") == "v1", "loaded Def registers its bare id");
        NativeTestData.Check(Node(catalog, "unknown:RimLumen.ReloadDef") == "v1", "loaded Def registers its qualified id");
        NativeTestData.Check(catalog.TryResolveEffect("RimLumen.ReloadDef", out EffectAsset? oldDef), "capture old Def instance");

        LumenAssetReloadResult refreshed = LumenModAssetScanner.ReloadEffectAssets(catalog, Array.Empty<LumenScanRoot>(), new[] { Def("v2") });
        NativeTestData.Check(refreshed.Loaded == 0 && refreshed.Failed == 0, "Def refresh stays outside JSON counts");
        NativeTestData.Check(Node(catalog, "RimLumen.ReloadDef") == "v2", "loaded Def is refreshed from current Def state");
        NativeTestData.Check(catalog.TryResolveEffect("RimLumen.ReloadDef", out EffectAsset? newDef) && !ReferenceEquals(oldDef, newDef),
            "Def refresh installs a new instance");

        var broken = new LumenEffectDef { defName = "RimLumen.ReloadDef" };
        LumenAssetReloadResult failed = LumenModAssetScanner.ReloadEffectAssets(catalog, Array.Empty<LumenScanRoot>(), new[] { broken });
        NativeTestData.Check(failed.Loaded == 0 && failed.Failed == 0, "Def failure is not a JSON file failure");
        NativeTestData.Check(!catalog.TryResolveEffect("RimLumen.ReloadDef", out _), "failed Def releases its previous ids");
    }

    private static LumenEffectDef Def(string nodeName) => new()
    {
        defName = "RimLumen.ReloadDef",
        nodes = new List<LumenNodeXml> { new() { name = nodeName } },
    };

    private static string Node(InMemoryAssetCatalog catalog, string effectId)
    {
        NativeTestData.CheckResolves(catalog, effectId);
        catalog.TryResolveEffect(effectId, out EffectAsset? asset);
        return asset!.Nodes[0].Name;
    }

    private static LumenAliasChange SingleChange(LumenAssetReloadResult result, string effectId)
    {
        LumenAliasChange[] matches = result.AliasChanges.Where(change => change.EffectId == effectId).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException($"Expected one alias change for '{effectId}', got {matches.Length}.");
        }

        return matches[0];
    }
}
