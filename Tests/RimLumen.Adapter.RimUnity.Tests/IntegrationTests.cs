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

    internal static void CoreIndependence()
    {
        var coreRefs = typeof(LumenHostRegistry).Assembly.GetReferencedAssemblies();
        Check.True(!coreRefs.Any(reference => reference.Name!.Contains("RimUnity", StringComparison.OrdinalIgnoreCase)),
            "Core references RimUnity: " + string.Join(",", coreRefs.Select(r => r.Name)));
        var adapterRefs = typeof(RimUnityAssetProvider).Assembly.GetReferencedAssemblies();
        Check.True(adapterRefs.Any(reference => reference.Name!.Contains("RimUnity", StringComparison.OrdinalIgnoreCase)),
            "Adapter must reference RimUnity so the Core proof is not vacuous");
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
        public int Triggers, Params, LightIntensityCalls, LightColorCalls, Stops, Disposes;
        public bool LastAllowFadeOut;
        public bool IsAlive { get; private set; } = true;
        public void Trigger(string name) => Triggers++;
        public void GoTo(string stateName) { }
        public void SetParam(string name, float value) => Params++;
        public void SetLightIntensity(float intensity) => LightIntensityCalls++;
        public void SetLightColor(Vector3 colorRgb) => LightColorCalls++;
        public void Stop(bool allowFadeOut = true) { Stops++; LastAllowFadeOut = allowFadeOut; End(); }
        public void Dispose() { Disposes++; End(); }
        public event Action<string>? Notify { add { } remove { } }
        public event Action<LumenInteractionNotification>? InteractionNotify { add { } remove { } }
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
