using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using RimUnity.Serialization;
using static RimLumen.Adapter.RimUnity.Tests.Export;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;
using RU = global::RimUnity;
using ExternalHandle = RimLumen.Adapter.RimUnity.RimUnityLegacyFallbackPlayer.ExternalHandle;

namespace RimLumen.Adapter.RimUnity.Tests;

internal static class IntegrationTests
{
    private const string Wotf = "NekoYukari.WhisperOfTheForest";
    private const string WotfData = "E:/SteamLibrary/steamapps/common/RimWorld/Mods/Whisper-of-the-Forest/1.7/Data/RimUnity";

    private static readonly (string Path, int Nodes, int Report, int Native, int External, int L1, int L2)[] Expected =
    [
        ("Effects/FireTornado", 1, 1, 1, 0, 1, 0),
        ("Effects/Lightning", 10, 12, 1, 11, 0, 1),
        ("Effects/Linger", 4, 4, 1, 3, 0, 1),
        ("Effects/Orbit", 3, 3, 1, 2, 0, 1),
        ("Effects/Probe", 1, 1, 0, 1, 0, 0),
        ("Effects/Rune", 5, 4, 1, 3, 0, 1),
        ("Effects/Tornado", 35, 34, 1, 33, 0, 1),
    ];

    private static ulong frame;
    private static readonly List<string> Log = new();

    internal static void WotfTierReport()
    {
        var catalog = new RimUnityPackageCatalog(Wotf, WotfData);
        var translator = new RimUnityPrefabTranslator();
        foreach (var entry in Expected)
        {
            var asset = translator.Translate(entry.Path, catalog, catalog.Find(entry.Path)!, out var report);
            foreach (var classification in report.Nodes)
            {
                var node = asset.Nodes[classification.NodeIndex];
                Console.WriteLine("TIER " + JsonSerializer.Serialize(new
                {
                    Effect = entry.Path, Index = classification.NodeIndex, node.Name, classification.NodeId, classification.Component,
                    Tier = classification.Tier.ToString(), classification.Reasons,
                    CreatedNative = classification.Component == RimUnityPrefabTranslator.ParticleComponent ? node.Emitter is not null : node.Ribbon is not null
                }));
            }
        }
    }

    internal static void WotfIntegration()
    {
        LumenSceneBootstrap.EnsureInitialized();
        var catalog = new RimUnityPackageCatalog(Wotf, WotfData);
        Check.Equal(50, catalog.Manifest.assets.Count, "current WotF catalog assets");
        Check.Equal(7, catalog.Manifest.assets.Count(a => a.type == "Prefab"), "current WotF prefabs");
        var provider = new RimUnityAssetProvider();
        provider.RegisterPackageRoot(Wotf, WotfData);
        Check.Equal(1, provider.PackageCount, "provider packages");

        var expectedTextureKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expected in Expected)
        {
            string id = expected.Path;
            Check.True(provider.TryResolveEffect(id, out var asset) && asset is not null, "resolve " + id);
            Check.True(provider.TryInspectTranslation(id, out var report) && report is not null, "report " + id);
            Check.Equal(expected.Nodes, asset!.Nodes.Length, id + " node count");
            Check.Equal(expected.Nodes, report!.Prefab.nodes.Count, id + " prefab nodes");
            Check.Equal(expected.Report, report.Nodes.Count, id + " report count");
            Check.True(report.Nodes.Select(n => n.NodeIndex).SequenceEqual(report.Nodes.Select(n => n.NodeIndex).Order()), id + " report order");
            Check.Equal(report.Nodes.Count, report.Nodes.Select(n => (n.NodeIndex, n.Component)).Distinct().Count(), id + " unique report components");
            Check.Equal(expected.Native, asset.Nodes.Count(n => n.Emitter is not null), id + " native emitters");
            Check.Equal(expected.External, report.Nodes.Count(n => n.Tier == LumenParticleTier.External), id + " External");
            Check.Equal(expected.L1, report.Nodes.Count(n => n.Tier == LumenParticleTier.L1_NativeCpu), id + " L1");
            Check.Equal(expected.L2, report.Nodes.Count(n => n.Tier == LumenParticleTier.L2_GpuCompute), id + " L2");
            Check.Equal(Wotf, report.PackageId, id + " report package");
            Check.Equal(expected.Path, report.LogicalPath, id + " report path");
            Check.True(catalog.Find(expected.Path) is { type: "Prefab" }, id + " catalog record");
            string assetId = catalog.Find(expected.Path)!.id;
            Check.True(asset.EffectId.StartsWith(Wotf + ":", StringComparison.Ordinal), id + " canonical id");
            Check.True(provider.TryResolveEffect(assetId, out var byId) && ReferenceEquals(byId, asset), id + " asset id alias");
            Check.True(provider.TryResolveEffect(Wotf + ":" + expected.Path, out var byCanonical) && ReferenceEquals(byCanonical, asset), id + " canonical alias");
            Check.True(provider.TryResolveEffect(id.ToUpperInvariant(), out var alias) && ReferenceEquals(alias, asset), id + " case alias cached");
            foreach (var node in report.Nodes)
            {
                if (node.Tier != LumenParticleTier.External) continue;
                if (node.Component == RimUnityPrefabTranslator.ParticleComponent)
                    Check.True(asset.Nodes[node.NodeIndex].Emitter is null && asset.Nodes[node.NodeIndex].PointLight is null,
                        id + " External owns both particles and lights: " + node.NodeId);
                else Check.True(asset.Nodes[node.NodeIndex].Ribbon is null, id + " External ribbon has no native ribbon: " + node.NodeId);
            }
            Check.True(asset.Nodes.All(n => n.Ribbon is null), id + " installed WotF lines are polylines, none native");
            foreach (var node in asset.Nodes.Where(n => n.Emitter is not null))
            {
                var e = node.Emitter!;
                Console.WriteLine($"NATIVE_TEXTURE effect={id} node={node.Name} key={JsonSerializer.Serialize(e.TextureKey)}");
                if (e.TextureKey is { Length: > 0 } key) expectedTextureKeys.Add(key);
                Check.True(e.MaxParticles > 0, id + " maxParticles");
                Check.True(e.MaxParticles >= e.StartLifetimeMin, id + " ordering sanity " + e.Name);
                foreach (float v in new[] { e.Duration, e.RateOverTime, e.StartLifetimeMin, e.StartLifetimeMax, e.StartSpeedMin, e.StartSpeedMax,
                    e.StartSizeMin, e.StartSizeMax, e.StartColor.X, e.StartColor.Y, e.StartColor.Z, e.StartColor.W,
                    e.HdrIntensity, e.ConeAngleDegrees, e.SoftParticleFadeDistance })
                    Check.True(float.IsFinite(v), id + " finite emitter " + e.Name);
                Check.True(e.Bursts.All(b => float.IsFinite(b.Time) && b.Count >= 0), id + " finite bursts " + e.Name);
                Check.True(e.ColorOverLifetime.Stops.All(s => float.IsFinite(s.Time) && float.IsFinite(s.Rgba.X) && float.IsFinite(s.Rgba.W)),
                    id + " finite gradient " + e.Name);
            }
            Log.Add($"{id}: nodes={asset.Nodes.Length} report={report.Nodes.Count} native={asset.Nodes.Count(n => n.Emitter is not null)} " +
                $"External={report.Nodes.Count(n => n.Tier == LumenParticleTier.External)} L1={report.Nodes.Count(n => n.Tier == LumenParticleTier.L1_NativeCpu)} L2={report.Nodes.Count(n => n.Tier == LumenParticleTier.L2_GpuCompute)}");
        }

        Check.True(!provider.TryResolveEffect("Effects/DoesNotExist", out var miss) && miss is null, "miss");
        Check.True(provider.TryInspectTranslation("Effects/FireTornado", out var fire), "FireTornado report");
        Check.Reasons([], fire!.Nodes.Single().Reasons, "FireTornado cone radius/distribution now supported");
        Check.True(provider.TryInspectTranslation("Effects/Lightning", out var lightningReport), "Lightning report");
        var lines = lightningReport!.Nodes.Where(n => n.Component == RimUnityPrefabTranslator.LineComponent).ToArray();
        Check.Equal(6, lines.Length, "Lightning LineRenderers reported");
        foreach (var line in lines)
            Check.Reasons(["line.positions.count"], line.Reasons, "Lightning 9/4-point polylines stay External: " + line.NodeId);

        Console.WriteLine("AUTO_TEXTURES expected=" + JsonSerializer.Serialize(expectedTextureKeys.Order()) +
            " actual=" + JsonSerializer.Serialize(provider.Textures.ResolvedTextureKeys.Order()));
        Check.True(expectedTextureKeys.SetEquals(provider.Textures.ResolvedTextureKeys), "automatic texture registrations exactly match native emitter keys");
        Check.True(provider.Textures.ResolvedTextureKeys.All(key => catalog.Find(key) is { type: "Texture2D" }), "texture keys resolve to catalog records");
        string textureKey = catalog.Manifest.assets.First(a => a.type == "Texture2D").id;
        var textureBridge = new RimUnityTextureBridge();
        Check.Equal(textureKey, textureBridge.ResolveTextureKey(catalog, textureKey), "real catalog texture key preserved offline");
        Check.True(textureBridge.ResolvedTextureKeys.SequenceEqual([textureKey]), "explicit texture resolution records exactly the selected key");
        Check.True(textureBridge.ResolvedTextureKeys.All(key => catalog.Find(key) is { type: "Texture2D" }), "explicit texture keys resolve to catalog Texture2D records");

        var supported = new RimUnityPrefabTranslator().Translate("phase-a.native-play", Prefab(Particles(1000, 40, .8f)),
            _ => null, _ => null, out _);
        RimLumen.Core.Api.RimLumenApi.RegisterEffect(supported);

        LumenHostRegistry.RegisterAssetProvider(provider);
        var recording = new RecordingInner(LumenSceneBootstrap.Service);
        var spy = new SpyExternalPlayer();
        var hybrid = new RimUnityHybridVfxService(recording, provider, spy);
        Check.Equal(recording.Capabilities, hybrid.Capabilities, "capabilities delegated");
        LumenHostRegistry.RegisterVfxService(hybrid);

        var request = new LumenPlayRequest { MapIndex = 111, EffectId = supported.EffectId, OriginWorld = new Vector3(3, 0, 4) };
        var handle = LumenHostRegistry.VfxService!.Play(request);
        Check.True(ReferenceEquals(recording.Last, handle), "no-External hybrid returns the direct inner handle");
        Check.Equal(0, spy.Plays, "no-External never calls external");
        var instance = handle as EffectInstanceHandle ?? throw new InvalidOperationException("Supported native handle is not the native instance.");
        Step(1f / 60f, 20);
        int spawned = LumenSceneBootstrap.Simulator.LiveParticleCount;
        Check.True(spawned > 5, "native frames spawn supported particles: " + spawned);
        Vector3 before = LumenSceneBootstrap.Simulator.LiveParticles[0].Position;
        Step(1f / 60f, 5);
        Vector3 after = LumenSceneBootstrap.Simulator.LiveParticles[0].Position;
        Check.True(Vector3.Distance(before, after) > 0.0001f, $"live particle advances: before={before} after={after}");
        Console.WriteLine($"Supported native actual frames=25 liveAt20={spawned} before={before} after={after} displacement={Vector3.Distance(before, after)}");
        Check.Near(25f / 60f, instance.PlaybackTime, "playback advanced", 0.0001f);
        Check.True(instance.IsAlive && instance.CpuEmitterCount == 1, "supported native bound to L1 backend without GPU");
        handle.Stop(false);
        Check.True(!handle.IsAlive, "stop false kills supported native handle");
        Check.Equal(0, LumenSceneBootstrap.Simulator.LiveParticleCount, "stop false releases supported native particles");

        var lightningRequest = new LumenPlayRequest { MapIndex = 111, EffectId = "Effects/Lightning" };
        var lightning = LumenHostRegistry.VfxService!.Play(lightningRequest);
        int lightningFinished = 0;
        lightning.Finished += () => lightningFinished++;
        Check.Equal(1, spy.Plays, "Lightning delegates External to external");
        Check.Equal(11, spy.LastReport!.Nodes.Count(n => n.Tier == LumenParticleTier.External), "external report External count (5 systems, 6 lines)");
        var lightningInstance = recording.Last as EffectInstanceHandle ?? throw new InvalidOperationException("Lightning inner handle is not native.");
        Check.Equal(1, lightningInstance.CpuEmitterCount, "Lightning native root uses offline CPU fallback, external children are not duplicated");
        Check.Equal(0, lightningInstance.GpuEmitterCount, "no L2 engine offline");
        var lightningRoot = Resolve(provider, "Effects/Lightning").Nodes[0].Emitter!;
        Check.Near(0, lightningRoot.RateOverTime, "installed Lightning root emission is disabled");
        Check.Equal(0, lightningRoot.Bursts.Length, "installed Lightning root has no scheduled pulses");
        Step(1f / 60f, 10);
        Check.True(lightning.IsAlive, "hybrid handle alive while external plays");
        lightning.Trigger("state");
        lightning.SetParam("intensity", .5f);
        Check.Equal(1, spy.LastHandle!.Triggers, "Trigger forwarded");
        Check.Equal(1, spy.LastHandle.Params, "SetParam forwarded");
        spy.LastHandle.Stop(true);
        Check.True(lightningInstance.IsAlive && lightning.IsAlive, "composite remains alive when external finishes before native");
        Check.Equal(0, lightningFinished, "external finish alone does not complete composite");
        lightning.Stop(true);
        Check.Equal(2, spy.LastHandle.Stops, "Stop forwarded after external-only stop");
        Check.True(spy.LastHandle.LastAllowFadeOut, "Stop arg forwarded");
        Step(1f / 60f, 1);
        Check.True(!lightningInstance.IsAlive && !lightning.IsAlive, "disabled root emission has no live particles left after stop");
        Check.Equal(1, lightningFinished, "composite Finished once both parts ended");
        lightning.Dispose();
        Check.Equal(1, spy.LastHandle.Disposes, "Dispose forwarded");
        Check.Equal(1, lightningFinished, "composite Finished raised exactly once");

        int beforeLinger = spy.Plays;
        var linger = LumenHostRegistry.VfxService!.Play(new LumenPlayRequest { MapIndex = 111, EffectId = "Effects/Linger" });
        Check.Equal(beforeLinger + 1, spy.Plays, "Linger delegates its three External nodes");
        Check.Equal(3, spy.LastReport!.Nodes.Count(n => n.Tier == LumenParticleTier.External), "Linger External count");
        Check.Equal(1, (recording.Last as EffectInstanceHandle)!.CpuEmitterCount, "Linger native root uses offline CPU fallback");
        linger.Dispose();
        Check.True(!linger.IsAlive && !spy.LastHandle!.IsAlive, "Linger dispose terminates both handles");
        Check.Equal(0, LumenSceneBootstrap.Simulator.LiveParticleCount, "Linger dispose leaves no particles");
        int beforeOrbit = spy.Plays;
        var orbit = LumenHostRegistry.VfxService!.Play(new LumenPlayRequest { MapIndex = 111, EffectId = "Effects/Orbit" });
        Check.Equal(beforeOrbit + 1, spy.Plays, "Orbit delegates external subset");
        Check.Equal(2, spy.LastReport!.Nodes.Count(n => n.Tier == LumenParticleTier.External), "Orbit External count");
        Check.Equal(1, (recording.Last as EffectInstanceHandle)!.CpuEmitterCount, "Orbit native root replaces the now-external Core node");
        Step(1f / 60f, 10);
        orbit.Stop(false);
        Check.Equal(0, LumenSceneBootstrap.Simulator.LiveParticleCount, "mixed Orbit stop releases native particles");
        Check.True(!orbit.IsAlive && !spy.LastHandle!.IsAlive, "mixed Orbit stop terminates both handles");

        int beforeFire = spy.Plays;
        using (var fireHandle = LumenHostRegistry.VfxService!.Play(new LumenPlayRequest { MapIndex = 111, EffectId = "Effects/FireTornado" }))
        {
            Check.Equal(beforeFire, spy.Plays, "FireTornado is all-native and never calls external");
            Check.True(ReferenceEquals(recording.Last, fireHandle), "FireTornado returns direct native handle");
            Check.Equal(1, (fireHandle as EffectInstanceHandle)!.CpuEmitterCount, "FireTornado owns one CPU emitter");
            Step(1f / 60f, 10);
            fireHandle.Stop(false);
            Check.True(!fireHandle.IsAlive, "FireTornado stops native handle");
            Check.Equal(0, LumenSceneBootstrap.Simulator.LiveParticleCount, "FireTornado stop releases particles");
        }
        foreach (var remaining in new[] { ("Effects/Rune", 3), ("Effects/Tornado", 33) })
        {
            int plays = spy.Plays;
            using var mixed = LumenHostRegistry.VfxService!.Play(new LumenPlayRequest { MapIndex = 111, EffectId = remaining.Item1 });
            Check.Equal(plays + 1, spy.Plays, remaining.Item1 + " external delegation");
            Check.Equal(remaining.Item2, spy.LastReport!.Nodes.Count(n => n.Tier == LumenParticleTier.External), remaining.Item1 + " external node count");
            Check.Equal(1, (recording.Last as EffectInstanceHandle)!.CpuEmitterCount, remaining.Item1 + " one native root with offline CPU fallback");
            Check.Equal(0, (recording.Last as EffectInstanceHandle)!.GpuEmitterCount, remaining.Item1 + " no GPU engine offline");
            Step(1f / 60f, 10);
            mixed.Stop(false);
            Check.True(!mixed.IsAlive && !spy.LastHandle!.IsAlive, remaining.Item1 + " stopped");
            Check.Equal(0, LumenSceneBootstrap.Simulator.LiveParticleCount, remaining.Item1 + " stop releases native particles");
        }
        Console.WriteLine("HYBRID six WotF effects played here; Probe external handoff checked separately; Lightning native=1/External=5 systems+6 lines, Orbit native=1/External=2, FireTornado native=1/External=0; supported in-memory 25-frame native simulation retained");

        var inert = LumenHostRegistry.VfxService.Play(new LumenPlayRequest { MapIndex = 111, EffectId = "Effects/DoesNotExist" });
        Check.True(!inert.IsAlive, "missing effect stays inert");

        Console.WriteLine(string.Join("; ", Log));
        Console.WriteLine($"WotF textures={provider.Textures.ResolvedTextureKeys.Count}");
    }

    internal static void WotfProbeMaterial()
    {
        var catalog = new RimUnityPackageCatalog(Wotf, WotfData);
        ResourceData material = catalog.ReadResource("4fc865aeebe035242bc33dd60bc7ef80_2100000")!;
        Check.True(material.members.Single(m => m.name == "_Color").value.vector.SequenceEqual(new[] { 2f, 1.7f, .8f, 2f }),
            "installed ProbeAdditive source _Color is the approved out-of-domain input");
        var provider = new RimUnityAssetProvider();
        provider.RegisterPackageRoot(Wotf, WotfData);
        var probe = Resolve(provider, "Effects/Probe");
        Check.True(provider.TryInspectTranslation("Effects/Probe", out var report), "Probe translation report");
        const string reason = "material[4fc865aeebe035242bc33dd60bc7ef80_2100000]._Color.domain";
        Check.Reasons([reason], report!.Nodes.Single().Reasons, "actual ProbeAdditive alpha2 material-domain reason");
        Check.Equal(LumenParticleTier.External, report.Nodes.Single().Tier, "actual Probe changes from L2 to External");
        Check.True(probe.Nodes.Single().Emitter is null && probe.Nodes.Single().PointLight is null, "Probe native ownership removed");
        LumenHostRegistry.RegisterAssetProvider(provider);
        LumenSceneBootstrap.EnsureInitialized();
        var inner = new RecordingInner(LumenSceneBootstrap.Service);
        var external = new SpyExternalPlayer();
        var hybrid = new RimUnityHybridVfxService(inner, provider, external);
        using var handle = hybrid.Play(new LumenPlayRequest { MapIndex = 111, EffectId = Wotf + ":Effects/Probe" });
        Check.Equal(1, external.Plays, "actual Probe handed to existing external player seam");
        Check.Reasons([reason], external.LastReport!.Nodes.Single().Reasons, "external receives material-domain report");
        var native = inner.Last as EffectInstanceHandle ?? throw new InvalidOperationException("Probe inner is not native handle");
        Check.Equal(0, native.CpuEmitterCount, "external Probe has no duplicate CPU emitter");
        Check.Equal(0, native.GpuEmitterCount, "external Probe has no duplicate GPU emitter");
        Check.True(handle.IsAlive, "external Probe handle owns lifetime");
        handle.Stop(false);
        Check.True(!handle.IsAlive && !external.LastHandle!.IsAlive, "Probe composite stop forwards externally");
        Console.WriteLine($"PROBE L2->External reason={reason} externalPlays={external.Plays} nativeCpu=0 nativeGpu=0");
    }

    internal static void ExternalMapDisposal()
    {
        RU.Runtime.NativeRuntime.InitializeOnCurrentThread();
        var anchorA = new RU.GameObject("lightning A");
        var anchorB = new RU.GameObject("lightning B");
        static ExternalHandle PlayLine(RU.GameObject anchor, int mapIndex)
        {
            var request = new LumenPlayRequest { EffectId = "lightning", MapIndex = mapIndex };
            var handle = new ExternalHandle(anchor, [], [], [anchor.AddComponent<RU.LineRenderer>()], request);
            handle.Start();
            return handle;
        }
        var first = PlayLine(anchorA, 7101);
        var second = PlayLine(anchorB, 7102);
        try
        {
            Check.True(first.IsAlive && second.IsAlive, "Both external lines started");
            ExternalHandle.ReleaseMap(7101);
            Check.True(!first.IsAlive && anchorA == null, "Map A's external line and anchor must be destroyed immediately");
            Check.True(second.IsAlive && anchorB != null, "Map B's external line and anchor must survive");
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            first.CaptureMainThread(default);
            second.CaptureMainThread(default);
        }
    }

    internal static void RejectedHybrid()
    {
        using var temp = new TestDirectory();
        var native = Particles();
        native.id = "native.ps";
        native.members.Add(Member("sizeOverLifetime", Data(("enabled", Number(1)), ("size", Data(("mode", Number(1)),
            ("curveMultiplier", Number(1)), ("curve", Data(("keys", Array(
                Data(("time", Number(-.5f)), ("value", Number(1))), Data(("time", Number(1)), ("value", Number(2))))))))))));
        var external = Particles();
        external.id = "external.ps";
        external.members.Add(Member("collision", Data(("enabled", Number(1)))));
        var prefab = new PrefabData { nodes = [Node("native", native), Node("external", external, "native")] };
        var manifest = new Manifest { assets = [
            new AssetRecord { id = "rejected.hybrid", path = "Effects/Rejected", file = "prefab.json", type = "Prefab" },
            new AssetRecord { id = "valid.external", path = "Effects/External", file = "external.json", type = "Prefab" }
        ] };
        var json = new JsonSerializerOptions { IncludeFields = true };
        File.WriteAllText(Path.Combine(temp.Root, "prefab.json"), JsonSerializer.Serialize(prefab, json));
        File.WriteAllText(Path.Combine(temp.Root, "external.json"), JsonSerializer.Serialize(Prefab(external), json));
        File.WriteAllText(Path.Combine(temp.Root, "catalog.json"), JsonSerializer.Serialize(manifest, json));
        var provider = new RimUnityAssetProvider();
        provider.RegisterPackageRoot("Hybrid.Rejected", temp.Root);
        LumenHostRegistry.RegisterAssetProvider(provider);
        LumenSceneBootstrap.EnsureInitialized();
        var spy = new SpyExternalPlayer();
        var hybrid = new RimUnityHybridVfxService(LumenSceneBootstrap.Service, provider, spy);
        using var handle = hybrid.Play(new LumenPlayRequest { EffectId = "Hybrid.Rejected:Effects/Rejected" });
        Check.Equal(0, spy.Plays, "registry rejection must not start external subset");
        Check.True(!handle.IsAlive, "rejected native remains inert");
        var finishedInner = new FinishedInner();
        var validHybrid = new RimUnityHybridVfxService(finishedInner, provider, spy);
        using var valid = validHybrid.Play(new LumenPlayRequest { EffectId = "Hybrid.Rejected:Effects/External" });
        Check.Equal(1, spy.Plays, "valid all-external asset still starts external when native is already finished");
        Check.True(valid.IsAlive, "external lifetime is independent of finished native");
    }

    private sealed class FinishedInner : ILumenVfxService
    {
        public LumenCapabilities Capabilities => LumenSceneBootstrap.Service.Capabilities;
        public ILumenEffectHandle Play(in LumenPlayRequest request)
        {
            var handle = new SpyHandle();
            handle.Stop(false);
            return handle;
        }
    }

    private static EffectAsset Resolve(RimUnityAssetProvider provider, string id)
    {
        Check.True(provider.TryResolveEffect(id, out var asset) && asset is not null, "resolve " + id);
        return asset!;
    }

    private static void Step(float deltaTime, int frames)
    {
        var contributors = LumenHostRegistry.GetContributorsSnapshot();
        for (int i = 0; i < frames; i++)
        {
            var context = new LumenCaptureContext
            {
                FrameIndex = frame++, DeltaTime = deltaTime, ViewportWidth = 1920, ViewportHeight = 1080,
                ViewMatrix = Matrix4x4.Identity, ProjectionMatrix = Matrix4x4.Identity, LumenRenderPassId = 1,
            };
            foreach (var contributor in contributors) contributor.CaptureMainThread(in context);
        }
    }

    internal static void CoreIndependence()
    {
        var coreRefs = typeof(LumenHostRegistry).Assembly.GetReferencedAssemblies();
        Check.True(!coreRefs.Any(reference => reference.Name!.Contains("RimUnity", StringComparison.OrdinalIgnoreCase)),
            "Core references RimUnity: " + string.Join(",", coreRefs.Select(r => r.Name)));
        var adapterRefs = typeof(RimUnityAssetProvider).Assembly.GetReferencedAssemblies();
        Check.True(adapterRefs.Any(reference => reference.Name!.Contains("RimUnity", StringComparison.OrdinalIgnoreCase)),
            "Adapter must reference RimUnity so the Core proof is not vacuous");
    }

    private sealed class RecordingInner(ILumenVfxService inner) : ILumenVfxService
    {
        public ILumenEffectHandle? Last { get; private set; }
        public LumenCapabilities Capabilities => inner.Capabilities;
        public ILumenEffectHandle Play(in LumenPlayRequest request) => Last = inner.Play(request);
    }

    private sealed class SpyExternalPlayer : IRimUnityExternalPlayer
    {
        public int Plays;
        public RimUnityTranslationReport? LastReport;
        public SpyHandle? LastHandle;
        public ILumenEffectHandle Play(in LumenPlayRequest request, RimUnityTranslationReport report)
        {
            Plays++;
            LastReport = report;
            return LastHandle = new SpyHandle();
        }
    }

    private sealed class SpyHandle : ILumenEffectHandle
    {
        public int Triggers, Params, Stops, Disposes;
        public bool LastAllowFadeOut;
        public bool IsAlive { get; private set; } = true;
        public void Trigger(string name) => Triggers++;
        public void GoTo(string stateName) { }
        public void SetParam(string name, float value) => Params++;
        public void Stop(bool allowFadeOut = true) { Stops++; LastAllowFadeOut = allowFadeOut; End(); }
        public void Dispose() { Disposes++; End(); }
        public event Action<string>? Notify { add { } remove { } }
        public event Action<string>? StateChanged { add { } remove { } }
        public event Action? Finished;

        private void End()
        {
            if (!IsAlive) return;
            IsAlive = false;
            Finished?.Invoke();
        }
    }
}
