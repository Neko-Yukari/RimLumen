using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;

namespace RimLumen.Adapter.RimUnity.Tests;

internal static class IntegrationTests
{
    private const string Wotf = "NekoYukari.WhisperOfTheForest";
    private const string WotfData = "E:/SteamLibrary/steamapps/common/RimWorld/Mods/Whisper-of-the-Forest/1.7/Data/RimUnity";

    private static readonly (string Path, int Nodes, int Report, int Native, int L0, int L1, int L2)[] Expected =
    [
        ("Effects/FireTornado", 1, 1, 0, 1, 0, 0),
        ("Effects/Lightning", 10, 6, 0, 6, 0, 0),
        ("Effects/Linger", 4, 4, 0, 4, 0, 0),
        ("Effects/Orbit", 3, 3, 1, 2, 1, 0),
        ("Effects/Probe", 1, 1, 1, 0, 0, 1),
        ("Effects/Rune", 5, 4, 0, 4, 0, 0),
        ("Effects/Tornado", 35, 34, 0, 34, 0, 0),
    ];

    private static ulong frame;
    private static readonly List<string> Log = new();

    internal static void WotfIntegration()
    {
        LumenSceneBootstrap.EnsureInitialized();
        var catalog = new RimUnityPackageCatalog(Wotf, WotfData);
        Check.Equal(50, catalog.Manifest.assets.Count, "current WotF catalog assets");
        Check.Equal(7, catalog.Manifest.assets.Count(a => a.type == "Prefab"), "current WotF prefabs");
        var provider = new RimUnityAssetProvider();
        provider.RegisterPackageRoot(Wotf, WotfData);
        Check.Equal(1, provider.PackageCount, "provider packages");

        foreach (var expected in Expected)
        {
            string id = expected.Path;
            Check.True(provider.TryResolveEffect(id, out var asset) && asset is not null, "resolve " + id);
            Check.True(provider.TryInspectTranslation(id, out var report) && report is not null, "report " + id);
            Check.Equal(expected.Nodes, asset!.Nodes.Length, id + " node count");
            Check.Equal(expected.Nodes, report!.Prefab.nodes.Count, id + " prefab nodes");
            Check.Equal(expected.Report, report.Nodes.Count, id + " report count");
            Check.True(report.Nodes.Select(n => n.NodeIndex).SequenceEqual(report.Nodes.Select(n => n.NodeIndex).Order()), id + " report order");
            Check.Equal(report.Nodes.Count, report.Nodes.Select(n => n.NodeIndex).Distinct().Count(), id + " unique report indexes");
            Check.Equal(expected.Native, asset.Nodes.Count(n => n.Emitter is not null), id + " native emitters");
            Check.Equal(expected.L0, report.Nodes.Count(n => n.Tier == LumenParticleTier.L0_ExternalCpu), id + " L0");
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
                if (node.Tier != LumenParticleTier.L0_ExternalCpu) continue;
                Check.True(asset.Nodes[node.NodeIndex].Emitter is null && asset.Nodes[node.NodeIndex].PointLight is null,
                    id + " L0 owns both particles and lights: " + node.NodeId);
            }
            foreach (var node in asset.Nodes.Where(n => n.Emitter is not null))
            {
                var e = node.Emitter!;
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
                $"L0={report.Nodes.Count(n => n.Tier == LumenParticleTier.L0_ExternalCpu)} L1={report.Nodes.Count(n => n.Tier == LumenParticleTier.L1_NativeCpu)} L2={report.Nodes.Count(n => n.Tier == LumenParticleTier.L2_GpuCompute)}");
        }

        Check.True(!provider.TryResolveEffect("Effects/DoesNotExist", out var miss) && miss is null, "miss");
        Check.True(provider.TryInspectTranslation("Effects/FireTornado", out var fire), "FireTornado report");
        Check.Reasons(["shape.coneRadius", "shape.coneDistribution"], fire!.Nodes.Single().Reasons, "FireTornado raw kind=4 radius=1 angle=25 speed=0");

        Check.True(provider.Textures.ResolvedTextureKeys.Count > 0, "offline texture metadata recorded");
        Check.True(provider.Textures.ResolvedTextureKeys.All(key => catalog.Find(key) is { type: "Texture2D" }), "texture keys resolve to catalog records");
        string textureKey = provider.Textures.ResolvedTextureKeys.First();
        Check.Equal(textureKey, provider.Textures.ResolveTextureKey(catalog, textureKey), "texture key preserved offline");

        var probe = Resolve(provider, "Effects/Probe");
        var emitter = probe.Nodes.Single(n => n.Emitter is not null).Emitter!;
        Check.Equal(1000, emitter.MaxParticles, "Probe 30aff maxParticles");
        Check.Near(40, emitter.RateOverTime, "Probe 30aff rate");
        Check.Near(.8f, emitter.StartLifetimeMax, "Probe 30aff lifetime");
        Check.Equal(LumenParticleTier.L2_GpuCompute, emitter.PreferredTier, "Probe tier");

        LumenHostRegistry.RegisterAssetProvider(provider);
        var recording = new RecordingInner(LumenSceneBootstrap.Service);
        var spy = new SpyExternalPlayer();
        var hybrid = new RimUnityHybridVfxService(recording, provider, spy);
        Check.Equal(recording.Capabilities, hybrid.Capabilities, "capabilities delegated");
        LumenHostRegistry.RegisterVfxService(hybrid);

        var request = new LumenPlayRequest { EffectId = "Effects/Probe", OriginWorld = new Vector3(3, 0, 4) };
        var handle = LumenHostRegistry.VfxService!.Play(request);
        Check.True(ReferenceEquals(recording.Last, handle), "no-L0 hybrid returns the direct inner handle");
        Check.Equal(0, spy.Plays, "no-L0 never calls external");
        var instance = handle as EffectInstanceHandle ?? throw new InvalidOperationException("Probe handle is not the native instance.");
        Step(1f / 60f, 20);
        int spawned = LumenSceneBootstrap.Simulator.LiveParticleCount;
        Check.True(spawned > 5, "native frames spawn probe particles: " + spawned);
        Vector3 before = LumenSceneBootstrap.Simulator.LiveParticles[0].Position;
        Step(1f / 60f, 5);
        Vector3 after = LumenSceneBootstrap.Simulator.LiveParticles[0].Position;
        Check.True(Vector3.Distance(before, after) > 0.0001f, $"live particle advances: before={before} after={after}");
        Console.WriteLine($"Probe actual frames=25 liveAt20={spawned} before={before} after={after} displacement={Vector3.Distance(before, after)}");
        Check.Near(25f / 60f, instance.PlaybackTime, "playback advanced", 0.0001f);
        Check.True(instance.IsAlive && instance.CpuEmitterCount == 1, "probe bound to L1 backend without GPU");
        handle.Stop(false);
        Check.True(!handle.IsAlive, "stop false kills probe handle");
        Check.Equal(0, LumenSceneBootstrap.Simulator.LiveParticleCount, "stop false releases probe particles");

        var lightningRequest = new LumenPlayRequest { EffectId = "Effects/Lightning" };
        var lightning = LumenHostRegistry.VfxService!.Play(lightningRequest);
        int lightningFinished = 0;
        lightning.Finished += () => lightningFinished++;
        Check.Equal(1, spy.Plays, "Lightning delegates L0 to external");
        Check.Equal(6, spy.LastReport!.Nodes.Count(n => n.Tier == LumenParticleTier.L0_ExternalCpu), "external report L0 count");
        var lightningInstance = recording.Last as EffectInstanceHandle ?? throw new InvalidOperationException("Lightning inner handle is not native.");
        Check.Equal(0, lightningInstance.CpuEmitterCount, "no duplicate native L0 emitters");
        Check.Equal(0, lightningInstance.GpuEmitterCount, "no L2 engine offline");
        Step(1f / 60f, 10);
        Check.True(lightning.IsAlive, "hybrid handle alive while external plays");
        lightning.Trigger("state");
        lightning.SetParam("intensity", .5f);
        Check.Equal(1, spy.LastHandle!.Triggers, "Trigger forwarded");
        Check.Equal(1, spy.LastHandle.Params, "SetParam forwarded");
        lightning.Stop(true);
        Check.Equal(1, spy.LastHandle.Stops, "Stop forwarded");
        Check.True(spy.LastHandle.LastAllowFadeOut, "Stop arg forwarded");
        Step(1f / 60f, 1);
        Check.Equal(1, lightningFinished, "composite Finished once both parts ended");
        lightning.Dispose();
        Check.Equal(1, spy.LastHandle.Disposes, "Dispose forwarded");
        Check.Equal(1, lightningFinished, "composite Finished raised exactly once");

        int beforeLinger = spy.Plays;
        var linger = LumenHostRegistry.VfxService!.Play(new LumenPlayRequest { EffectId = "Effects/Linger" });
        Check.Equal(beforeLinger + 1, spy.Plays, "Linger delegates its four L0 nodes");
        Check.Equal(4, spy.LastReport!.Nodes.Count(n => n.Tier == LumenParticleTier.L0_ExternalCpu), "Linger L0 count");
        Check.Equal(0, (recording.Last as EffectInstanceHandle)!.CpuEmitterCount, "Linger has no native emitters");
        linger.Dispose();

        int beforeOrbit = spy.Plays;
        var orbit = LumenHostRegistry.VfxService!.Play(new LumenPlayRequest { EffectId = "Effects/Orbit" });
        Check.Equal(beforeOrbit + 1, spy.Plays, "Orbit delegates external subset");
        Check.Equal(2, spy.LastReport!.Nodes.Count(n => n.Tier == LumenParticleTier.L0_ExternalCpu), "Orbit L0 count");
        Check.Equal(1, (recording.Last as EffectInstanceHandle)!.CpuEmitterCount, "Orbit retains only its representable native node");
        Step(1f / 60f, 10);
        orbit.Stop(false);
        Check.Equal(0, LumenSceneBootstrap.Simulator.LiveParticleCount, "mixed Orbit stop releases native particles");
        Check.True(!orbit.IsAlive && !spy.LastHandle!.IsAlive, "mixed Orbit stop terminates both handles");

        foreach (var remaining in new[] { ("Effects/FireTornado", 1), ("Effects/Rune", 4), ("Effects/Tornado", 34) })
        {
            int plays = spy.Plays;
            using var externalOnly = LumenHostRegistry.VfxService!.Play(new LumenPlayRequest { EffectId = remaining.Item1 });
            Check.Equal(plays + 1, spy.Plays, remaining.Item1 + " external delegation");
            Check.Equal(remaining.Item2, spy.LastReport!.Nodes.Count(n => n.Tier == LumenParticleTier.L0_ExternalCpu), remaining.Item1 + " external node count");
            Check.Equal(0, (recording.Last as EffectInstanceHandle)!.CpuEmitterCount, remaining.Item1 + " no duplicate native emitter");
            externalOnly.Stop(false);
            Check.True(!externalOnly.IsAlive && !spy.LastHandle!.IsAlive, remaining.Item1 + " stopped");
        }
        Console.WriteLine("HYBRID all7 played; Lightning native=0/L0=6, Orbit native=1/L0=2; lifecycle forwarded; no-L0 direct handle");

        var inert = LumenHostRegistry.VfxService.Play(new LumenPlayRequest { EffectId = "Effects/DoesNotExist" });
        Check.True(!inert.IsAlive, "missing effect stays inert");

        Console.WriteLine(string.Join("; ", Log));
        Console.WriteLine($"WotF textures={provider.Textures.ResolvedTextureKeys.Count}");
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
