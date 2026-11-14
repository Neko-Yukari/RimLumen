using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using RimLumen.Core.Contracts;
using RimLumen.Core.Lighting;
using RimLumen.Core.Mod;
using RimLumen.Core.Rendering.Probes.Contributors;
using RimWorld;
using RimWorldWaylith.Rendering;
using Verse;
using Waylith.Rendering;

namespace RimLumen.Core.Rendering.Probes;

internal sealed partial class ProbeSession
{
    // Route B ShaderPacks host: multi-pass custom targets, persistent history, forced scene depth and
    // map light field, 6 bloom mips, RunOnEmptyFrames; then the documented rejection fallback. The pack is
    // restored to Default before the scheduler's final leak check.
    private IEnumerator ShaderPackFrames()
    {
        string root = Path.Combine(Path.GetTempPath(), "opencode", "rimlumen-probe-shaderpack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        float savedPackParam0 = RimLumenRenderHost.ShaderPackParams.GetPackParam(0);
        Map? map = Find.CurrentMap;
        var priorRoofs = new Dictionary<IntVec3, RoofDef?>();
        CellRect roofPatch = default;
        bool defaultRestored = false;
        try
        {
            string accum = Path.Combine(root, "ProbeAccum.frag.glsl");
            string blur = Path.Combine(root, "ProbeBlur.frag.glsl");
            string final = Path.Combine(root, "ProbeFinal.frag.glsl");
            string prefilter = Path.Combine(root, "ProbePrefilter.frag.glsl");
            File.WriteAllText(accum, ShaderPackProbeSources.Accum);
            File.WriteAllText(blur, ShaderPackProbeSources.Blur);
            File.WriteAllText(final, ShaderPackProbeSources.Final);
            File.WriteAllText(prefilter, ShaderPackProbeSources.Prefilter);
            // A non-default UBO value the accum shader adds into its blue channel, so the readback also proves
            // the runtime PackParam0 reaches the GPU uniform block.
            RimLumenRenderHost.ShaderPackParams.SetPackParam(0, 0.125f);

            var spec = new ShaderPackSpec
            {
                Id = "probe-routeb",
                BloomMipLevels = 6,
                RunOnEmptyFrames = true,
                RequiresSceneDepth = true,
                RequiresMapLightField = true,
                // A custom prefilter defeats the no-HDR-draw bloom bypass, so all 6 down + 5 up mips run every
                // frame of this probe instead of only when routed draws exist.
                BloomPrefilterShaderPath = prefilter,
                Targets =
                [
                    new ShaderPackTargetSpec("history", ShaderPackTargetFormat.R16G16B16A16SFloat,
                        Filter: ShaderPackTargetFilter.Linear, Persistent: true),
                    new ShaderPackTargetSpec("blur", ShaderPackTargetFormat.R8G8B8A8UNorm,
                        Scale: 0.5f, Filter: ShaderPackTargetFilter.Point),
                ],
                Passes =
                [
                    new ShaderPackPassSpec("accum", accum, "history",
                    [
                        new ShaderPackInputBinding(0, "hdr"),
                        new ShaderPackInputBinding(1, "prev:history"),
                        new ShaderPackInputBinding(2, "depth"),
                        new ShaderPackInputBinding(3, "mapLightField"),
                    ], ExtraParams: new System.Numerics.Vector2(0.5f, 0.25f)),
                    new ShaderPackPassSpec("blur", blur, "blur", [new ShaderPackInputBinding(0, "history")]),
                    new ShaderPackPassSpec("final", final, "backbuffer",
                    [
                        new ShaderPackInputBinding(0, "hdr"),
                        new ShaderPackInputBinding(1, "bloom"),
                        new ShaderPackInputBinding(2, "blur"),
                    ]),
                ],
            };

            if (!RimLumenRenderHost.ReloadShaderPack(spec))
                throw new InvalidOperationException("Valid Route B pack was rejected: " + RimLumenRenderHost.LastShaderPackError);
            // The light field only collects while a pack requires it, so roof the camera patch after the reload:
            // a 3x3 constructed roof guarantees non-zero light-field bytes inside the camera view even on an
            // empty grass spawn. SetRoof dirties the map mesh; the mirror refresh makes the very next upload carry it.
            if (map != null)
            {
                roofPatch = RoofCameraPatch(map);
                foreach (IntVec3 cell in roofPatch.Cells)
                    priorRoofs[cell] = map.roofGrid.RoofAt(cell);
                foreach (IntVec3 cell in roofPatch.Cells)
                    map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                MapLightFieldCollector.UpdateSection(map, roofPatch);
            }
            // A reload swaps the live runtime; read counters from the installed instance.
            long offscreenBefore = pipeline.ShaderPack.OffscreenRecords;
            long bloomBefore = pipeline.ShaderPack.BloomRecords;
            long compositeBefore = pipeline.ShaderPack.CompositeRecords;
            long uploadsBefore = MapLightFieldCollector.GpuUploads;
            for (int i = 0; i < 8; i++) yield return new UnityEngine.WaitForEndOfFrame();
            app.WaitForRenderThreadIdle();
            long offscreen = pipeline.ShaderPack.OffscreenRecords - offscreenBefore;
            long bloomRecords = pipeline.ShaderPack.BloomRecords - bloomBefore;
            long composite = pipeline.ShaderPack.CompositeRecords - compositeBefore;
            long uploads = MapLightFieldCollector.GpuUploads - uploadsBefore;

            // Counters prove recording; the readback proves each input path actually contributed pixels on the GPU.
            RenderTexture? blurTarget = pipeline.ShaderPack.TryGetTargetTexture("blur", pipeline.LastRenderedSlot);
            if (blurTarget is null)
                throw new InvalidOperationException("Custom target 'blur' is unavailable after the Route B frames.");
            byte[] blurPixels = GpuTextureService.ReadbackTexture2D(blurTarget.Color, blurTarget.Width, blurTarget.Height, 4)
                ?? throw new InvalidOperationException("Custom target 'blur' readback failed.");
            int depthDeltaPixels = 0, lightFieldPixels = 0, paramQuadrantPixels = 0, paramLeftNonZeroPixels = 0;
            int width = blurTarget.Width;
            int height = blurTarget.Height;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    if (blurPixels[i] > 0) depthDeltaPixels++;
                    if (blurPixels[i + 1] > 0) lightFieldPixels++;
                    if (blurPixels[i + 2] > 0)
                    {
                        if (x < width * 0.4f) paramLeftNonZeroPixels++;
                        if (x > width * 0.6f && y > height * 0.6f) paramQuadrantPixels++;
                    }
                }
            }

            checks.Add(new ProbeCheck("ShaderPack-RouteB",
                RimLumenRenderHost.ActiveShaderPack.Id == "probe-routeb"
                && RimLumenRenderHost.LastShaderPackError == null
                && RimLumenRenderHost.IsMapLightFieldActive
                && !RimLumenRenderHost.FailSafeTripped
                && offscreen > 0 && bloomRecords > 0 && composite > 0 && uploads > 0
                && depthDeltaPixels > 0 && lightFieldPixels > 0
                && paramQuadrantPixels > 0 && paramLeftNonZeroPixels == 0,
                "Multi-pass graph with persistent history, depth, map light field and 6 bloom mips recorded over 8 forced (RunOnEmptyFrames) frames; the 0.5-scale 'blur' target read back: R>0 (scene depth sampled), G>0 (light field sampled through InvViewProjection over a temporary roof patch), B>0 only in the bottom-right quadrant (PackParam0 + push constant), B==0 across the left 40%.",
                new Dictionary<string, double>
                {
                    ["offscreenPassRecords"] = offscreen,
                    ["bloomPassRecords"] = bloomRecords,
                    ["compositeRecords"] = composite,
                    ["mapLightFieldUploads"] = uploads,
                    ["depthDeltaPixels"] = depthDeltaPixels,
                    ["lightFieldPixels"] = lightFieldPixels,
                    ["paramQuadrantPixels"] = paramQuadrantPixels,
                    ["paramLeftNonZeroPixels"] = paramLeftNonZeroPixels,
                    ["blurPixels"] = width * height,
                }));

            string broken = Path.Combine(root, "ProbeBroken.frag.glsl");
            File.WriteAllText(broken, ShaderPackProbeSources.Broken);
            bool brokenAccepted = RimLumenRenderHost.ReloadShaderPack(new ShaderPackSpec { Id = "probe-broken", CompositeShaderPath = broken });
            bool dagRejected = !RimLumenRenderHost.ReloadShaderPack(new ShaderPackSpec
            {
                Id = "probe-bad-dag",
                Targets = [new ShaderPackTargetSpec("history", Persistent: false)],
                Passes =
                [
                    new ShaderPackPassSpec("self-read", final, "history", [new ShaderPackInputBinding(0, "history")]),
                    new ShaderPackPassSpec("final", final, "backbuffer", [new ShaderPackInputBinding(0, "hdr")]),
                ],
            });
            checks.Add(new ProbeCheck("ShaderPack-RouteB-fallback",
                !brokenAccepted && dagRejected
                && RimLumenRenderHost.LastShaderPackError != null
                && !RimLumenRenderHost.FailSafeTripped
                && RimLumenRenderHost.ActiveShaderPack.Id == "probe-routeb",
                "Broken GLSL and a self-reading DAG are rejected without tripping the fail-safe; the live pack stays installed.",
                new Dictionary<string, double>
                {
                    ["brokenAccepted"] = brokenAccepted ? 1 : 0,
                    ["dagRejected"] = dagRejected ? 1 : 0,
                    ["failSafeTripped"] = RimLumenRenderHost.FailSafeTripped ? 1 : 0,
                }));

            // Real-GPU VVL pass over every shipped ShaderPack (including multi-pass Cinematic with prev:adapt +
            // Common.glslinc and Atmospheric with depth + MapLightField + 7 bloom mips).
            if (synthetic is null)
            {
                synthetic = new SyntheticHdrQuadContributor(
                    8f,
                    new NormalizedRect(0.18f, 0.25f, 0.18f, 0.20f),
                    new NormalizedRect(0.62f, 0.25f, 0.20f, 0.20f));
                LumenHostRegistry.RegisterContributor(synthetic);
            }
            string builtInRoot = Path.Combine(modRoot, "1.7", "ShaderPacks");
            IReadOnlyList<ShaderPackEntry> catalog = ShaderPackCatalog.ScanRoots([(builtInRoot, "内置")]);
            var builtInMetrics = new Dictionary<string, double>();
            bool builtInsOk = true;
            synthetic.Arm();
            foreach (string packId in new[] { "atmospheric", "cinematic", "crisp", "vibrant" })
            {
                ShaderPackEntry? entry = null;
                foreach (ShaderPackEntry candidate in catalog)
                {
                    if (string.Equals(candidate.Id, packId, StringComparison.OrdinalIgnoreCase))
                    {
                        entry = candidate;
                        break;
                    }
                }
                if (entry is null || entry.LoadError is not null || !RimLumenRenderHost.ReloadShaderPack(entry.Spec))
                {
                    builtInsOk = false;
                    builtInMetrics[packId + ".loaded"] = 0;
                    continue;
                }

                ShaderPackCatalog.ApplyOptionsToRuntime(entry, null, RimLumenRenderHost.ShaderPackParams);
                long packOffBefore = pipeline.ShaderPack.OffscreenRecords;
                long packCompBefore = pipeline.ShaderPack.CompositeRecords;
                long packBloomBefore = pipeline.ShaderPack.BloomRecords;
                for (int frame = 0; frame < 3; frame++) yield return new UnityEngine.WaitForEndOfFrame();
                app.WaitForRenderThreadIdle();
                long offDelta = pipeline.ShaderPack.OffscreenRecords - packOffBefore;
                long compDelta = pipeline.ShaderPack.CompositeRecords - packCompBefore;
                long bloomDelta = pipeline.ShaderPack.BloomRecords - packBloomBefore;
                int expectedOffscreenPerFrame = Math.Max(0, entry.Spec.Passes.Count - 1);
                bool packOk = RimLumenRenderHost.ActiveShaderPack.Id == entry.Id
                    && RimLumenRenderHost.LastShaderPackError is null
                    && !RimLumenRenderHost.FailSafeTripped
                    && compDelta == 3
                    && bloomDelta > 0
                    && (expectedOffscreenPerFrame == 0 || offDelta == expectedOffscreenPerFrame * 3);
                builtInsOk &= packOk;
                builtInMetrics[packId + ".loaded"] = 1;
                builtInMetrics[packId + ".offscreen"] = offDelta;
                builtInMetrics[packId + ".bloom"] = bloomDelta;
                builtInMetrics[packId + ".composite"] = compDelta;
            }
            synthetic.Disarm();
            checks.Add(new ProbeCheck("ShaderPack-BuiltIns", builtInsOk,
                "All 4 shipped ShaderPacks (atmospheric, cinematic, crisp, vibrant) reloaded and rendered 3 frames each under VVL.",
                builtInMetrics));
        }
        finally
        {
            synthetic?.Disarm();
            ShaderPackCatalog.ApplyOptionsToRuntime(ShaderPackCatalog.StandardEntry, null, RimLumenRenderHost.ShaderPackParams);
            RimLumenRenderHost.ShaderPackParams.SetPackParam(0, savedPackParam0);
            if (map != null && priorRoofs.Count > 0)
            {
                try
                {
                    foreach (KeyValuePair<IntVec3, RoofDef?> prior in priorRoofs)
                        map.roofGrid.SetRoof(prior.Key, prior.Value);
                    MapLightFieldCollector.UpdateSection(map, roofPatch);
                }
                catch (Exception ex)
                {
                    Log.Error($"[RimLumen] ShaderPack probe could not restore the temporary roof patch: {ex}");
                }
            }
            // Never throw from here: it would mask the probe body's own exception. The failure is logged now and
            // raised after the finally when the body itself succeeded.
            defaultRestored = RimLumenRenderHost.ReloadShaderPack(ShaderPackSpec.Default);
            if (!defaultRestored)
                Log.Error($"[RimLumen] ShaderPack probe could not restore the default pack: {RimLumenRenderHost.LastShaderPackError}");
            try { Directory.Delete(root, recursive: true); } catch (Exception) { }
        }
        if (!defaultRestored)
            throw new InvalidOperationException($"ShaderPack probe failed to reload the default pack: {RimLumenRenderHost.LastShaderPackError}");
        if (RimLumenRenderHost.ActiveShaderPack.Id != "default")
            throw new InvalidOperationException($"ShaderPack probe did not restore the default pack: {RimLumenRenderHost.ActiveShaderPack.Id}");
        yield return new UnityEngine.WaitForEndOfFrame();
    }

    // 3x3 cells centred on the camera, clamped so the rect stays inside the map.
    private static CellRect RoofCameraPatch(Map map)
    {
        IntVec3 center = Current.CameraDriver?.MapPosition ?? map.Center;
        center.x = Math.Clamp(center.x, 1, map.Size.x - 2);
        center.z = Math.Clamp(center.z, 1, map.Size.z - 2);
        return CellRect.CenteredOn(center, 1).ClipInsideMap(map);
    }
}
