using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimLumen.Core.Contracts;
using RimLumen.Core.Rendering.Logic;
using RimLumen.Core.Rendering.Probes.Contributors;
using UnityStubs.Rendering;
using Verse;
using Waylith.Rendering;

namespace RimLumen.Core.Rendering.Probes;

internal sealed partial class ProbeSession
{
    private readonly Dictionary<string, byte[]> captures = new();
    private readonly Dictionary<string, (int Draws, int Persistent)> capturedCounts = new();
    private int width = 1920, height = 1080;
    private ProbeNoOpFeature? orderProbe;

    private void CheckFailed(string id, Exception error)
    {
        ProbeCheck? prior = checks.FirstOrDefault(check => check.Id == id);
        checks.RemoveAll(check => check.Id == id);
        checks.Add(new ProbeCheck(id, false, error.ToString(), prior?.Metrics ?? new Dictionary<string, double>()));
    }

    private IEnumerator GuardedFrames()
    {
        // Each independent check has its own exception boundary and cleanup. A failed assertion
        // stays in the result; it never terminates the rest of the schedule.
        foreach (var step in new (string Id, Func<IEnumerator> Run)[]
        {
            ("P1-idle", IdleFrames), ("feature-order", FeatureOrderFrames), ("P1-active", ActiveFrames), ("T1b", BarrierFrames),
            ("P2-L2-10k", () => GpuWorkload(10_000)), ("P2-L2-50k", () => GpuWorkload(50_000)),
            ("P2-L1-Glower", L1Workload), ("Compat-VanillaAdditive", CompatFrames), ("ShaderPack-RouteB", ShaderPackFrames)
        })
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(step.Run());
            while (stack.Count > 0)
            {
                bool more = false;
                object? next = null;
                try
                {
                    more = stack.Peek().MoveNext();
                    if (more) next = stack.Peek().Current;
                }
                catch (Exception ex)
                {
                    CheckFailed(step.Id, ex);
                    break;
                }
                if (!more)
                {
                    try { (stack.Pop() as IDisposable)?.Dispose(); }
                    catch (Exception ex) { CheckFailed(step.Id + "/cleanup", ex); }
                    continue;
                }
                if (next is IEnumerator nested) { stack.Push(nested); continue; }
                yield return next;
            }
            while (stack.Count > 0)
            {
                try { (stack.Pop() as IDisposable)?.Dispose(); }
                catch (Exception ex) { CheckFailed(step.Id + "/cleanup", ex); }
            }
            try { ResetProbeActivity(); }
            catch (Exception ex) { CheckFailed(step.Id + "/cleanup", ex); }
            yield return new UnityEngine.WaitForEndOfFrame(); // Reclaim stopped L2 slots before next step.
        }
        checks.Add(new ProbeCheck("T2", pipeline.Pre.ActiveFrames > 0,
            "Shared scene depth attached read-only; final validation attribution belongs to the harness.",
            new Dictionary<string, double> { ["activeFrames"] = pipeline.Pre.ActiveFrames, ["clears"] = pipeline.Pre.Clears }));
        Finish();
    }

    private void ResetProbeActivity()
    {
        pipeline.MeasureContributors = false;
        app.OnUpdate -= SubmitCompatQuad;
        RimLumenRenderHost.EnableVanillaAdditiveBloom = false;
        workload?.Stop(allowFadeOut: false);
        workload = null;
        synthetic?.Disarm();
        ssbo?.Disarm();
        if (orderProbe != null) { app.FrameRenderer.RemoveFeature(orderProbe); orderProbe = null; }
        if (!app.FrameRenderer.Features.Contains(pipeline.Pre)) pipeline.Attach();
    }

    private IEnumerator CaptureScreen(string name, int settleFrames = 0)
    {
        for (int i = 0; i < settleFrames; i++) yield return new UnityEngine.WaitForEndOfFrame();
        var backend = ScreenCaptureBackend.Current ?? throw new InvalidOperationException("Screen capture backend unavailable.");
        if (!backend.RequestCapture()) throw new InvalidOperationException("Swapchain transfer readback unsupported.");
        yield return new UnityEngine.WaitForEndOfFrame();
        if (!backend.TryReadScreen(out byte[] rgba, out int w, out int h)) throw new InvalidOperationException("TryReadScreen failed: " + name);
        if (w != width || h != height) throw new InvalidOperationException($"Expected 1920x1080; actual {w}x{h}.");
        captures[name] = rgba;
        capturedCounts[name] = (mapDraws, mapPersistent);
        File.WriteAllBytes(Path.Combine(options.OutputDirectory, name + ".rgba"), rgba);
        if (!checks.Any(check => check.Id == "T3"))
            checks.Add(new ProbeCheck("T3", true, "Full-screen RGBA8 read in host EndOfFrame.",
                new Dictionary<string, double> { ["width"] = w, ["height"] = h }));
    }

    private IEnumerator IdleFrames()
    {
        int realTimeMotes = RealTime.moteList.allMotes.Count(mote => mote.Spawned && mote.Map == Find.CurrentMap && mote.def.mote.realTime);
        if (realTimeMotes != 0) throw new InvalidOperationException($"Cannot freeze {realTimeMotes} realtime motes.");
        long clearDelta = 0, drawDelta = 0;
        foreach (string stage in new[] { "A", "B", "Aprime" })
        {
            if (stage == "B") pipeline.Attach(); else pipeline.Detach();
            long clears = pipeline.Pre.Clears, draws = pipeline.Composite.Draws;
            yield return CaptureScreen(stage, 6);
            if (UnityEngine.Time.time != frozenTime) throw new InvalidOperationException("Unity render time advanced.");
            if (stage == "B") { clearDelta = pipeline.Pre.Clears - clears; drawDelta = pipeline.Composite.Draws - draws; }
        }
        PixelDiffResult noise = PixelMetrics.Compare(captures["A"], captures["Aprime"], width, height);
        PixelDiffResult idle = PixelMetrics.Compare(captures["A"], captures["B"], width, height);
        bool equalCounts = capturedCounts["A"] == capturedCounts["B"] && capturedCounts["A"] == capturedCounts["Aprime"];
        var metrics = new Dictionary<string, double>
        {
            ["noiseMaxAbsDelta"] = noise.MaxAbsDelta, ["noiseDifferingPixels"] = noise.DifferingPixels,
            ["idleMaxAbsDelta"] = idle.MaxAbsDelta, ["idleDifferingPixels"] = idle.DifferingPixels,
            ["width"] = width, ["height"] = height, ["idleClears"] = clearDelta, ["idleCompositeDraws"] = drawDelta,
            ["frozenUnityTime"] = frozenTime, ["realTimeMotes"] = realTimeMotes, ["hiddenWindowCount"] = windowVisibility.Count
        };
        foreach (string stage in new[] { "A", "B", "Aprime" })
        {
            metrics[stage + ".mapMainSubmittedDrawItems.Draws"] = capturedCounts[stage].Draws;
            metrics[stage + ".mapMainSubmittedDrawItems.Persistent"] = capturedCounts[stage].Persistent;
        }
        checks.Add(new ProbeCheck("P1-idle", noise.MaxAbsDelta == 0 && idle.MaxAbsDelta == 0 && equalCounts && clearDelta == 0 && drawDelta == 0,
            "Exact full-frame A/B/A; paused camera/time and host screenshot mode. MapMain counts are pre-batching submitted items, not vkCmdDraw (MeshDrawStats.cs:43).", metrics));
    }

    private IEnumerator ActiveFrames()
    {
        yield return CaptureScreen("P1-active-reference", 6);
        var emissive = new NormalizedRect(0.18f, 0.25f, 0.18f, 0.20f);
        var soft = new NormalizedRect(0.62f, 0.25f, 0.20f, 0.20f);
        synthetic = new SyntheticHdrQuadContributor(8f, emissive, soft);
        LumenHostRegistry.RegisterContributor(synthetic);
        synthetic.Arm();
        yield return CaptureScreen("active", 6);
        var slot = pipeline.Targets.Slots[pipeline.LastRenderedSlot];
        byte[] hdr = GpuTextureService.ReadbackTexture2D(slot.Color.Color, width, height, 8) ?? throw new InvalidOperationException("RGBA16F readback failed.");
        File.WriteAllBytes(Path.Combine(options.OutputDirectory, "active.rgba16f"), hdr);
        MeasureActive(captures["P1-active-reference"], captures["active"], hdr, width, height, emissive, soft);
    }

    private IEnumerator FeatureOrderFrames()
    {
        orderProbe = new ProbeNoOpFeature();
        int imgui = Array.FindIndex(app.FrameRenderer.Features.ToArray(), feature => feature.GetType().Name.Contains("ImGui", StringComparison.OrdinalIgnoreCase));
        app.FrameRenderer.InsertFeature(imgui, orderProbe);
        string[] before = app.FrameRenderer.Features.Select(feature => feature.GetType().Name).ToArray();
        for (int i = 0; i < 6; i++) yield return new UnityEngine.WaitForEndOfFrame();
        string[] after = app.FrameRenderer.Features.Select(feature => feature.GetType().Name).ToArray();
        int self = Array.IndexOf(after, nameof(RimLumenCompositeFeature));
        checks.Add(new ProbeCheck("feature-order", self >= 0 && self + 1 < after.Length && after[self + 1].Contains("ImGui", StringComparison.OrdinalIgnoreCase),
            "Before: " + string.Join(" -> ", before) + "; After: " + string.Join(" -> ", after), new Dictionary<string, double>()));

    }

    private IEnumerator BarrierFrames()
    {
        ssbo = new SsboBarrierProbeContributor(options.DisableBarrier);
        LumenHostRegistry.RegisterContributor(ssbo);
        ssbo.Arm();
        for (int i = 0; i < 7; i++) yield return new UnityEngine.WaitForEndOfFrame();
        app.WaitForRenderThreadIdle();
        checks.Add(new ProbeCheck("T1b", ssbo.DispatchesEnqueued > 0 && ssbo.DrawsRecorded > 0 &&
            (options.DisableBarrier ? ssbo.BarriersRecorded == 0 : ssbo.BarriersRecorded > 0),
            "Independent enqueue/barrier/draw check; does not require screen readback or T4 success.",
            new Dictionary<string, double> { ["dispatches"] = ssbo.DispatchesEnqueued, ["barriers"] = ssbo.BarriersRecorded, ["draws"] = ssbo.DrawsRecorded }));
    }
}
