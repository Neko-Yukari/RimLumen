using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Lighting;
using RimLumen.Core.ParticlesGpu;
using RimLumen.Core.Rendering.Logic;
using RimLumen.Core.Scene;
using UnityStubs.Rendering;
using Verse;
using Waylith.Rendering;

namespace RimLumen.Core.Rendering.Probes;

internal sealed partial class ProbeSession
{
    private ILumenEffectHandle? workload;
    private UnityEngine.Material? compatMaterial;
    private UnityEngine.Vector3 compatPosition;

    private IEnumerator ParticleWorkloads(byte[] idle, int width, int height)
    {
        var gpu = LumenHostRegistry.GpuParticleEngine as LumenGpuParticleEngine
            ?? throw new InvalidOperationException("Native GPU engine was not bootstrapped.");
        IntVec3 cell = Current.CameraDriver.MapPosition;
        if (!cell.InBounds(Find.CurrentMap)) throw new InvalidOperationException("Camera centre is outside the map.");
        Vector3 origin = new(cell.x + 0.5f, 0f, cell.z + 0.5f);
        var backend = ScreenCaptureBackend.Current ?? throw new InvalidOperationException("Screen readback unavailable.");
        for (int i = 0; i < 6; i++) yield return new UnityEngine.WaitForEndOfFrame();

        foreach (int count in new[] { 10_000, 50_000 })
        {
            string id = "P2-L2-" + (count / 1000) + "k";
            RegisterWorkload(id, count, LumenParticleTier.L2_GpuCompute, false);
            workload = LumenSceneBootstrap.Service.Play(new LumenPlayRequest
            { EffectId = id, MapIndex = Find.CurrentMap.uniqueID, OriginWorld = origin });
            if (!workload.IsAlive) throw new InvalidOperationException("Workload failed to play: " + id);
            // Warm-up allocation/compilation is not mixed into steady-frame CPU timing.
            for (int i = 0; i < 3; i++) yield return new UnityEngine.WaitForEndOfFrame();
            pipeline.ContributorMilliseconds.Clear();
            pipeline.MeasureContributors = true;
            var frameMs = new List<double>();
            for (int i = 0; i < 12; i++)
            {
                long start = Stopwatch.GetTimestamp();
                yield return new UnityEngine.WaitForEndOfFrame();
                frameMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            pipeline.MeasureContributors = false;
            var metrics = new Dictionary<string, double>
            {
                ["MaxParticles"] = count, ["measuredFrames"] = frameMs.Count,
                ["frameMeanMs"] = frameMs.Average(), ["frameMaxMs"] = frameMs.Max()
            };
            foreach (var pair in pipeline.ContributorMilliseconds)
            {
                metrics[pair.Key + ".captureEnqueueMeanMs"] = pair.Value.Average();
                metrics[pair.Key + ".captureEnqueueMaxMs"] = pair.Value.Max();
                metrics[pair.Key + ".samples"] = pair.Value.Count;
            }
            // All synchronization and readback are AFTER the timing window.
            int live = gpu.CountLiveParticles();
            metrics["liveParticles"] = live;
            if (!backend.RequestCapture()) throw new InvalidOperationException("Screen request failed: " + id);
            yield return new UnityEngine.WaitForEndOfFrame();
            if (!backend.TryReadScreen(out byte[] rgba, out int w, out int h) || w != width || h != height)
                throw new InvalidOperationException("Screen dimensions changed: " + id);
            ReadWorkloadPixels(id, idle, rgba, width, height, metrics);
            workload.Stop(allowFadeOut: false);
            yield return new UnityEngine.WaitForEndOfFrame(); // Required capture reclaims the 10k range before 50k.
            bool drained = !gpu.HasActiveWork && !LumenSceneBootstrap.Service.HasActiveWork;
            metrics["drainedToIdle"] = drained ? 1 : 0;
            bool measured = pipeline.ContributorMilliseconds.TryGetValue(gpu.Name, out var gpuSamples) && gpuSamples.Count >= 10;
            bool passed = measured && live == count && drained && metrics["hdrEmissivePixels"] > 0 && metrics["bloomPixels"] > 0 && metrics["swapchainBrightenedPixels"] > 0;
            checks.Add(new ProbeCheck(id, passed,
                "Native VFX L2; 3 warm-up + 12 measured EndOfFrame intervals (host wall time, not GPU timestamps). Live SSBO/staging and image readbacks excluded from timing. Immediate stop + one capture before next pool allocation.", metrics));
            LumenSceneBootstrap.Catalog.Unregister(id);
            workload = null;
            if (live != count) throw new InvalidOperationException($"{id}: actual live={live}, requested={count}; no parameter adjustment attempted.");
            if (!drained) throw new InvalidOperationException(id + " did not drain to idle.");
        }

        var driver = Find.CurrentMap.GetComponent<LumenMapLightingComponent>().Driver;
        int beforeActive = driver.ActiveLightCount, beforeRegister = driver.RegisterCallCount, beforeDeregister = driver.DeregisterCallCount;
        long verticesBefore = pipeline.L1Renderer.VerticesRecorded;
        int drawsBefore = pipeline.L1Renderer.DrawsRecorded;
        RegisterWorkload("P2-L1-Glower", 128, LumenParticleTier.L1_NativeCpu, true);
        workload = LumenSceneBootstrap.Service.Play(new LumenPlayRequest
        { EffectId = "P2-L1-Glower", MapIndex = Find.CurrentMap.uniqueID, OriginWorld = origin });
        for (int i = 0; i < 12; i++) yield return new UnityEngine.WaitForEndOfFrame();
        bool lightPresent = driver.ActiveLightCount == beforeActive + 1 && driver.RegisterCallCount > beforeRegister;
        if (!backend.RequestCapture()) throw new InvalidOperationException("L1 screen request failed.");
        yield return new UnityEngine.WaitForEndOfFrame();
        if (!backend.TryReadScreen(out byte[] l1Screen, out int lw, out int lh) || lw != width || lh != height)
            throw new InvalidOperationException("L1 readback failed.");
        var lightMetrics = new Dictionary<string, double>
        {
            ["verticesRecorded"] = pipeline.L1Renderer.VerticesRecorded - verticesBefore,
            ["drawsRecorded"] = pipeline.L1Renderer.DrawsRecorded - drawsBefore,
            ["liveParticles"] = LumenSceneBootstrap.Simulator.LiveParticleCount,
            ["registrationPresent"] = lightPresent ? 1 : 0,
            ["registerCalls"] = driver.RegisterCallCount - beforeRegister
        };
        ReadWorkloadPixels("P2-L1-Glower", idle, l1Screen, width, height, lightMetrics);
        workload.Stop(allowFadeOut: false);
        yield return new UnityEngine.WaitForEndOfFrame();
        bool lightAbsent = driver.ActiveLightCount == beforeActive && driver.DeregisterCallCount > beforeDeregister;
        lightMetrics["registrationAbsentAfterStop"] = lightAbsent ? 1 : 0;
        lightMetrics["deregisterCalls"] = driver.DeregisterCallCount - beforeDeregister;
        lightMetrics["drainedToIdle"] = !LumenSceneBootstrap.Simulator.HasActiveWork && !LumenSceneBootstrap.Service.HasActiveWork ? 1 : 0;
        checks.Add(new ProbeCheck("P2-L1-Glower", lightPresent && lightAbsent && lightMetrics["verticesRecorded"] > 0 &&
            lightMetrics["drawsRecorded"] > 0 && lightMetrics["hdrEmissivePixels"] > 0 && lightMetrics["drainedToIdle"] == 1,
            "Live map component's MapGlowerDriver/VerseGlowGridSink Register->Deregister round trip; not a mocked sink. Chosen because quicktest sky/glow is not forced dark. Sink calls are synchronous; errors propagate and fail this session.", lightMetrics));
        workload = null;
        LumenSceneBootstrap.Catalog.Unregister("P2-L1-Glower");

        // Real vanilla shader/material; normal layer 0 keeps the original MapMain draw intact.
        compatMaterial = MaterialPool.MatFrom("Other/ForceField", ShaderDatabase.MoteGlow);
        compatPosition = new UnityEngine.Vector3(origin.X, AltitudeLayer.MoteOverhead.AltitudeFor(), origin.Z);
        long copied = pipeline.CompatCapturedItems, optOut = pipeline.CompatOptedOut, unresolved = pipeline.CompatUnresolvedDef;
        long routed = pipeline.Pre.RoutedDrawItems;
        RimLumenRenderHost.EnableVanillaAdditiveBloom = true;
        app.OnUpdate += SubmitCompatQuad;
        for (int i = 0; i < 6; i++) yield return new UnityEngine.WaitForEndOfFrame();
        app.WaitForRenderThreadIdle();
        app.OnUpdate -= SubmitCompatQuad;
        RimLumenRenderHost.EnableVanillaAdditiveBloom = false;
        var compatMetrics = new Dictionary<string, double>
        {
            ["redrawnItems"] = pipeline.Pre.RoutedDrawItems - routed,
            ["capturedItems"] = pipeline.CompatCapturedItems - copied,
            ["optedOut"] = pipeline.CompatOptedOut - optOut,
            ["unresolvedDef"] = pipeline.CompatUnresolvedDef - unresolved
        };
        // Request the first newly captured frame after switching off (no settling/masking).
        if (!backend.RequestCapture()) throw new InvalidOperationException("Post-compat idle request failed.");
        yield return new UnityEngine.WaitForEndOfFrame();
        if (!backend.TryReadScreen(out byte[] finalIdle, out int fw, out int fh) || fw != width || fh != height)
            throw new InvalidOperationException("Post-compat idle readback failed.");
        PixelDiffResult diff = PixelMetrics.Compare(idle, finalIdle, width, height);
        compatMetrics["idleMaxAbsDelta"] = diff.MaxAbsDelta;
        compatMetrics["idleDifferingPixels"] = diff.DifferingPixels;
        File.WriteAllBytes(Path.Combine(options.OutputDirectory, "post-compat-idle.rgba"), finalIdle);
        checks.Add(new ProbeCheck("Compat-VanillaAdditive", compatMetrics["redrawnItems"] > 0 && diff.MaxAbsDelta == 0 && diff.DifferingPixels == 0,
            "Vanilla Other/ForceField with ShaderDatabase.MoteGlow submitted on MapMain; HDR copy then flag=false. Exact first post-disable idle frame versus pre-workload idle. ShaderDef opt-out only; HdrMultiplier not applied.", compatMetrics));
    }

    private void SubmitCompatQuad() => UnityEngine.Graphics.DrawMesh(MeshPool.plane10,
        UnityEngine.Matrix4x4.TRS(compatPosition, UnityEngine.Quaternion.identity, new UnityEngine.Vector3(4f, 1f, 4f)), compatMaterial!, 0);

    private static void RegisterWorkload(string id, int count, LumenParticleTier tier, bool light)
    {
        LumenSceneBootstrap.Catalog.Register(new EffectAsset
        {
            EffectId = id, DefaultDuration = 120f,
            Nodes = new[]
            {
                new EffectNodeSpec
                {
                    Name = "particles", LocalPosition = new Vector3(0, AltitudeLayer.MoteOverhead.AltitudeFor(), 0),
                    Emitter = new EmitterSpec
                    {
                        Name = id, PreferredTier = tier, MaxParticles = count, Duration = 120f, Looping = true,
                        RateOverTime = count, Bursts = new[] { (0f, count) }, WorldSpaceSimulation = false,
                        Shape = LumenShapeKind.Box, ShapeExtents = new Vector3(3f, 0.15f, 3f),
                        StartLifetimeMin = 120f, StartLifetimeMax = 120f, StartSizeMin = 0.15f, StartSizeMax = 0.3f,
                        StartSpeedMin = 0.1f, StartSpeedMax = 0.2f, StartColorLinear = new Vector4(1f, 0.4f, 0.1f, 1f),
                        HdrIntensity = 4f, SoftParticleFadeDistance = 0.5f,
                        ForceFields = new[]
                        {
                            new ForceFieldSpec(ForceFieldKind.CurlTurbulence, Vector3.Zero, Vector3.Zero, 8f, 0.2f, 0.8f),
                            new ForceFieldSpec(ForceFieldKind.Vortex, Vector3.UnitY, Vector3.Zero, 8f, 0.2f),
                            new ForceFieldSpec(ForceFieldKind.PointAttractor, Vector3.Zero, Vector3.Zero, 8f, 0.1f)
                        }
                    }
                },
                new EffectNodeSpec { Name = "light", PointLight = light ? new PointLightSpec(8f, 0f, new Vector3(1f, 0.4f, 0.1f)) : null }
            }
        });
    }

    private void ReadWorkloadPixels(string id, byte[] baseline, byte[] screen, int width, int height, Dictionary<string, double> metrics)
    {
        app.WaitForRenderThreadIdle();
        HdrTargets.Slot slot = pipeline.Targets.Slots[pipeline.LastRenderedSlot];
        byte[] hdr = GpuTextureService.ReadbackTexture2D(slot.Color.Color, width, height, 8)
            ?? throw new InvalidOperationException("HDR readback failed: " + id);
        RenderTexture bloom = slot.Bloom;
        byte[] glow = GpuTextureService.ReadbackTexture2D(bloom.Color, bloom.Width, bloom.Height, 8)
            ?? throw new InvalidOperationException("Bloom readback failed: " + id);
        int emissive = 0, halo = 0, brightened = 0;
        for (int i = 0; i < hdr.Length; i += 8)
            if ((float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(hdr, i)) > 0f ||
                (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(hdr, i + 2)) > 0f ||
                (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(hdr, i + 4)) > 0f) emissive++;
        for (int i = 0; i < glow.Length; i += 8)
            if ((float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(glow, i)) > 0f) halo++;
        for (int i = 0; i < screen.Length; i += 4)
            if (screen[i] > baseline[i] || screen[i + 1] > baseline[i + 1] || screen[i + 2] > baseline[i + 2]) brightened++;
        metrics["hdrEmissivePixels"] = emissive;
        metrics["bloomPixels"] = halo;
        metrics["swapchainBrightenedPixels"] = brightened;
        File.WriteAllBytes(Path.Combine(options.OutputDirectory, id + ".rgba"), screen);
        File.WriteAllBytes(Path.Combine(options.OutputDirectory, id + ".rgba16f"), hdr);
    }
}
