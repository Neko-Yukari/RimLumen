using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Debug;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class EffectPreviewCatalogSuite
{
    [LumenFact]
    public static void BuildEntriesClassifiesSourcesAndSelectsAcrossReloadFailureAndRecovery()
    {
        string root = NativeTestData.NewTempDirectory();
        try
        {
            string dataDir = Path.Combine(root, "1.7", "Data", "RimLumen");
            Directory.CreateDirectory(dataDir);
            string jsonPath = Path.Combine(dataDir, "Spark.lumen.json");
            File.WriteAllText(jsonPath, """{ "nodes": [ { "name": "v1" } ] }""");

            var catalog = new InMemoryAssetCatalog();
            var roots = new[] { new LumenScanRoot("Mod.Json", dataDir) };
            var xmlDef = new LumenEffectDef
            {
                defName = "XmlBurst",
                nodes = new List<LumenNodeXml> { new() { name = "xml_node" } },
            };

            LumenModAssetScanner.ScanSources(catalog, roots, new[] { xmlDef });
            catalog.Register(new EffectAsset
            {
                EffectId = "Code.Flash",
                Nodes = new[] { new EffectNodeSpec { Name = "code_node" } },
            });

            IReadOnlyList<LumenEffectPreviewEntry> entries = LumenEffectPreviewCatalog.BuildEntries(catalog);

            LumenEffectPreviewEntry jsonBare = Find(entries, "Spark");
            NativeTestData.Check(jsonBare.SourceKind == LumenEffectPreviewSourceKind.Json, "JSON source kind");
            NativeTestData.Check(jsonBare.PackageId == "Mod.Json", $"JSON packageId, got '{jsonBare.PackageId}'");
            NativeTestData.Check(jsonBare.SourceDetail == Path.GetFullPath(jsonPath),
                $"JSON detail preserves Windows drive colon path, got '{jsonBare.SourceDetail}'");

            LumenEffectPreviewEntry xmlEntry = Find(entries, "XmlBurst");
            NativeTestData.Check(xmlEntry.SourceKind == LumenEffectPreviewSourceKind.XmlDef, "XML Def source kind");
            NativeTestData.Check(xmlEntry.SourceDetail == "LumenEffectDef/XmlBurst", "XML Def detail");

            LumenEffectPreviewEntry codeEntry = Find(entries, "Code.Flash");
            NativeTestData.Check(codeEntry.SourceKind == LumenEffectPreviewSourceKind.Code, "Code source kind");

            for (int i = 1; i < entries.Count; i++)
            {
                NativeTestData.Check(string.CompareOrdinal(entries[i - 1].EffectId, entries[i].EffectId) < 0,
                    "Entries must be ordered ordinally by EffectId");
            }

            File.WriteAllText(jsonPath, """{ "nodes": [ ] }""");
            LumenAssetReloadResult broken = LumenModAssetScanner.ReloadEffectAssets(catalog, roots, new[] { xmlDef });
            NativeTestData.Check(broken.Failed == 1, "Broken JSON reports Failed=1");

            IReadOnlyList<LumenEffectPreviewEntry> afterBroken = LumenEffectPreviewCatalog.BuildEntries(catalog);
            NativeTestData.Check(
                LumenEffectPreviewCatalog.SelectEntryAfterRefresh(afterBroken, "Mod.Json:Spark", selectFirstIfEmpty: true) is null,
                "Missing selected ID after failed reload stays unselected instead of jumping to another item");
            NativeTestData.Check(
                LumenEffectPreviewCatalog.SelectEntryAfterRefresh(afterBroken, null, selectFirstIfEmpty: true)?.EffectId == afterBroken[0].EffectId,
                "Empty selection with selectFirstIfEmpty picks first entry");

            File.WriteAllText(jsonPath, """{ "nodes": [ { "name": "v2" } ] }""");
            LumenAssetReloadResult fixedReload = LumenModAssetScanner.ReloadEffectAssets(catalog, roots, new[] { xmlDef });
            NativeTestData.Check(fixedReload.Failed == 0 && fixedReload.Loaded == 1, "Fixed JSON reloads cleanly");

            IReadOnlyList<LumenEffectPreviewEntry> afterFix = LumenEffectPreviewCatalog.BuildEntries(catalog);
            LumenEffectPreviewEntry? reselected =
                LumenEffectPreviewCatalog.SelectEntryAfterRefresh(afterFix, "Mod.Json:Spark", selectFirstIfEmpty: false);
            NativeTestData.Check(reselected is not null && reselected.Asset.Nodes[0].Name == "v2",
                "Previously selected ID automatically re-selects updated asset after fix");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LumenFact]
    public static void FilterEntriesMatchesMultiTokenAcrossIdPackageAndSource()
    {
        var entries = new[]
        {
            new LumenEffectPreviewEntry(
                "ModA:Spells/Fireball",
                new EffectAsset { EffectId = "ModA:Spells/Fireball", Nodes = new[] { new EffectNodeSpec { Name = "n" } } },
                LumenEffectPreviewSourceKind.Json,
                "ModA",
                @"C:\Mods\ModA\1.7\Data\RimLumen\Spells\Fireball.lumen.json"),
            new LumenEffectPreviewEntry(
                "ModB:XmlIce",
                new EffectAsset { EffectId = "ModB:XmlIce", Nodes = new[] { new EffectNodeSpec { Name = "n" } } },
                LumenEffectPreviewSourceKind.XmlDef,
                "ModB",
                "LumenEffectDef/XmlIce"),
        };

        IReadOnlyList<LumenEffectPreviewEntry> emptyQuery = LumenEffectPreviewCatalog.FilterEntries(entries, "   ");
        NativeTestData.Check(emptyQuery.Count == 2, "Whitespace query returns all entries");

        IReadOnlyList<LumenEffectPreviewEntry> multiToken =
            LumenEffectPreviewCatalog.FilterEntries(entries, "json fireball moda");
        NativeTestData.Check(multiToken.Count == 1 && multiToken[0].EffectId == "ModA:Spells/Fireball",
            "Multi-token AND search matches across kind, ID, and package case-insensitively");

        IReadOnlyList<LumenEffectPreviewEntry> xmlSearch =
            LumenEffectPreviewCatalog.FilterEntries(entries, "XML modb");
        NativeTestData.Check(xmlSearch.Count == 1 && xmlSearch[0].EffectId == "ModB:XmlIce",
            "XML keyword matches XmlDef entries");
    }

    [LumenFact]
    public static void BuildPlayRequestSatisfiesGroundOriginAndRequiredFeatureContracts()
    {
        var asset = new EffectAsset
        {
            EffectId = "Preview.Complex",
            Nodes = new[]
            {
                new EffectNodeSpec
                {
                    Name = "beam",
                    SpacePlane = LumenSpacePlane.GroundXZ,
                    DepthMask = LumenDepthMask.NorthBehindReference,
                    Ribbon = new RibbonSpec
                    {
                        Mode = LumenRibbonMode.Beam,
                        EndAtTarget = true,
                        Segments = 4,
                        Width = 0.25f,
                    },
                },
            },
            States = new[]
            {
                new EffectStateSpec { Name = "idle", Nodes = new[] { "beam" }, Loop = true },
                new EffectStateSpec { Name = "burst", Nodes = new[] { "beam" }, Duration = 1f },
            },
            InitialState = "idle",
        };

        LumenPlayRequest request = LumenEffectPreviewCatalog.BuildPlayRequest(
            asset,
            mapIndex: 42,
            groundX: 12.5f,
            groundZ: 34.5f,
            initialState: "burst");

        NativeTestData.Check(request.OriginWorld.Y == 0f, "OriginWorld adheres to Y=0 ground");

        var service = NativeTestData.Service(asset);
        LumenHostRegistry.RegisterNodeFeatureBackend(new StubRibbonBackend());
        try
        {
            using ILumenEffectHandle handle = service.Play(request);
            NativeTestData.Check(handle.IsAlive, "Play succeeds for DepthMask + EndAtTarget asset");
        }
        finally
        {
            LumenHostRegistry.UnregisterNodeFeatureBackend(
                LumenHostRegistry.GetNodeFeatureBackend(LumenNodeFeature.Ribbon)!);
        }
    }

    private static LumenEffectPreviewEntry Find(IReadOnlyList<LumenEffectPreviewEntry> entries, string effectId)
        => entries.Single(entry => string.Equals(entry.EffectId, effectId, StringComparison.Ordinal));

    private sealed class StubRibbonBackend : ILumenNodeFeatureBackend
    {
        private int nextId = 1;
        public LumenNodeFeature Feature => LumenNodeFeature.Ribbon;
        public int CreateInstance(EffectNodeSpec node, in LumenNodeFeatureFrame initialFrame, bool realTime, uint seed) => nextId++;
        public void UpdateInstance(int instanceId, in LumenNodeFeatureFrame frame) { }
        public void RestartInstance(int instanceId) { }
        public void DestroyInstance(int instanceId, bool immediate) { }
        public bool IsInstanceAlive(int instanceId) => false;
    }
}
