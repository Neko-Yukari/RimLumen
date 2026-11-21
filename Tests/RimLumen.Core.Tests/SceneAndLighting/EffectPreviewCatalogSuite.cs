using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Debug;
using RimLumen.Core.Scene;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class EffectPreviewCatalogSuite
{
    [LumenFact]
    public static void BuildEntriesClassifiesJsonXmlCodeAndExternalSourcesAndRefreshesOnReload()
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

            var codeAsset = new EffectAsset
            {
                EffectId = "Code.Flash",
                Nodes = new[] { new EffectNodeSpec { Name = "code_node" } },
            };
            catalog.Register(codeAsset);

            var externalDuplicate = new EffectAsset
            {
                EffectId = "Spark",
                Nodes = new[] { new EffectNodeSpec { Name = "shadowed" } },
            };
            var externalOnly = new EffectAsset
            {
                EffectId = "Author.RimUnity:Effects/Beam",
                Nodes = new[] { new EffectNodeSpec { Name = "ru_node" } },
            };

            IReadOnlyList<LumenEffectPreviewEntry> entries =
                LumenEffectPreviewCatalog.BuildEntries(catalog, new[] { externalDuplicate, externalOnly });

            LumenEffectPreviewEntry jsonBare = Find(entries, "Spark");
            NativeTestData.Check(jsonBare.SourceKind == LumenEffectPreviewSourceKind.Json, "JSON bare source kind");
            NativeTestData.Check(jsonBare.PackageId == "Mod.Json", $"JSON packageId, got '{jsonBare.PackageId}'");
            NativeTestData.Check(jsonBare.SourceDetail == Path.GetFullPath(jsonPath),
                $"JSON detail preserves Windows drive colon path, got '{jsonBare.SourceDetail}'");
            NativeTestData.Check(LumenEffectPreviewCatalog.GetShortSourceDetail(jsonBare) == "Spark.lumen.json",
                "JSON short detail extracts filename");

            LumenEffectPreviewEntry xmlEntry = Find(entries, "XmlBurst");
            NativeTestData.Check(xmlEntry.SourceKind == LumenEffectPreviewSourceKind.XmlDef, "XML Def source kind");
            NativeTestData.Check(xmlEntry.SourceDetail == "LumenEffectDef/XmlBurst", "XML Def detail");

            LumenEffectPreviewEntry codeEntry = Find(entries, "Code.Flash");
            NativeTestData.Check(codeEntry.SourceKind == LumenEffectPreviewSourceKind.Code, "Code source kind");

            LumenEffectPreviewEntry extEntry = Find(entries, "Author.RimUnity:Effects/Beam");
            NativeTestData.Check(extEntry.SourceKind == LumenEffectPreviewSourceKind.ExternalProvider, "External source kind");
            NativeTestData.Check(extEntry.PackageId == "Author.RimUnity", "External packageId");

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
    public static void FilterEntriesMatchesIdPackageSourceAndMultiTokenCaseInsensitively()
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
            new LumenEffectPreviewEntry(
                "RuMod:guid123",
                new EffectAsset { EffectId = "RuMod:guid123", Nodes = new[] { new EffectNodeSpec { Name = "n" } } },
                LumenEffectPreviewSourceKind.ExternalProvider,
                "RuMod",
                "RuMod:guid123"),
        };

        IReadOnlyList<LumenEffectPreviewEntry> emptyQuery = LumenEffectPreviewCatalog.FilterEntries(entries, "   ");
        NativeTestData.Check(emptyQuery.Count == 3, "Whitespace query returns all entries");

        IReadOnlyList<LumenEffectPreviewEntry> multiToken =
            LumenEffectPreviewCatalog.FilterEntries(entries, "json fireball moda");
        NativeTestData.Check(multiToken.Count == 1 && multiToken[0].EffectId == "ModA:Spells/Fireball",
            "Multi-token AND search matches across kind, ID, and package case-insensitively");

        IReadOnlyList<LumenEffectPreviewEntry> xmlSearch =
            LumenEffectPreviewCatalog.FilterEntries(entries, "XML modb");
        NativeTestData.Check(xmlSearch.Count == 1 && xmlSearch[0].EffectId == "ModB:XmlIce",
            "XML keyword matches XmlDef entries");

        IReadOnlyList<LumenEffectPreviewEntry> extraSearch = LumenEffectPreviewCatalog.FilterEntries(
            entries,
            "Effects/Lightning",
            entry => entry.EffectId == "RuMod:guid123" ? "Effects/Lightning" : string.Empty);
        NativeTestData.Check(extraSearch.Count == 1 && extraSearch[0].EffectId == "RuMod:guid123",
            "Extra search text matches external query aliases");
    }

    [LumenFact]
    public static void BuildPlayRequestEnforcesGroundYZeroAndRequiredFeatureInputs()
    {
        var depthAndBeamAsset = new EffectAsset
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
            depthAndBeamAsset,
            mapIndex: 42,
            groundX: 12.5f,
            groundZ: 34.5f,
            initialState: "burst",
            realTime: true);

        NativeTestData.Check(request.OriginWorld == new Vector3(12.5f, 0f, 34.5f), "OriginWorld adheres to Y=0 ground");
        NativeTestData.Check(request.DepthReferenceZ == 34.5f, "DepthReferenceZ auto-populates from ground Z");
        NativeTestData.Check(
            request.TargetWorld == new Vector3(12.5f + LumenEffectPreviewCatalog.DefaultBeamTargetOffsetCells, 0f, 34.5f),
            "TargetWorld auto-populates for EndAtTarget beam ribbons");
        NativeTestData.Check(request.InitialState == "burst" && request.RealTime, "InitialState and RealTime propagate");

        var explicitTarget = new Vector3(20f, 1.5f, 40f);
        LumenPlayRequest withExplicitTarget = LumenEffectPreviewCatalog.BuildPlayRequest(
            depthAndBeamAsset,
            mapIndex: 42,
            groundX: 1f,
            groundZ: 2f,
            targetWorld: explicitTarget);
        NativeTestData.Check(withExplicitTarget.TargetWorld == explicitTarget, "Explicit TargetWorld is preserved");

        var noTargetOrDepthAsset = new EffectAsset
        {
            EffectId = "Preview.Simple",
            Nodes = new[] { new EffectNodeSpec { Name = "n" } },
        };
        LumenPlayRequest simpleReq = LumenEffectPreviewCatalog.BuildPlayRequest(noTargetOrDepthAsset, 42, 3f, 4f);
        NativeTestData.Check(simpleReq.DepthReferenceZ is null && simpleReq.TargetWorld is null,
            "Optional DepthReferenceZ/TargetWorld stay null when asset does not require them");

        var service = NativeTestData.Service(depthAndBeamAsset);
        LumenHostRegistry.RegisterNodeFeatureBackend(new StubRibbonBackend());
        try
        {
            using ILumenEffectHandle handle = service.Play(request);
            NativeTestData.Check(handle.IsAlive, "Play succeeds for DepthMask + EndAtTarget asset");
            var typed = (EffectInstanceHandle)handle;
            NativeTestData.Check(typed.RealTime, "RealTime propagates to EffectInstanceHandle");
        }
        finally
        {
            LumenHostRegistry.UnregisterNodeFeatureBackend(
                LumenHostRegistry.GetNodeFeatureBackend(LumenNodeFeature.Ribbon)!);
        }

        NativeTestData.Throws<ArgumentOutOfRangeException>(
            () => LumenEffectPreviewCatalog.BuildPlayRequest(noTargetOrDepthAsset, 1, float.NaN, 0f),
            "NaN groundX rejected");
        NativeTestData.Throws<ArgumentOutOfRangeException>(
            () => LumenEffectPreviewCatalog.BuildPlayRequest(noTargetOrDepthAsset, 1, 0f, float.PositiveInfinity),
            "Infinite groundZ rejected");
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
