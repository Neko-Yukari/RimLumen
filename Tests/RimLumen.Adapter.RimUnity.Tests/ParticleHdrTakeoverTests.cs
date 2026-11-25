using RU = global::RimUnity;

namespace RimLumen.Adapter.RimUnity.Tests;

internal static class ParticleHdrTakeoverTests
{
    internal static void SubmitContextGateMatrix()
    {
        Check.True(RimUnityParticleHdrTakeover.CanRouteParticleSubmitContext(
            mainMapScene: true, isMainCamera: true, hasTargetTexture: false, isUiParticle: false),
            "Eligible main-map world particle submit context must be allowed");

        Check.True(!RimUnityParticleHdrTakeover.CanRouteParticleSubmitContext(
            mainMapScene: false, isMainCamera: true, hasTargetTexture: false, isUiParticle: false),
            "Inactive main map scene must reject particle submit context");
        Check.True(!RimUnityParticleHdrTakeover.CanRouteParticleSubmitContext(
            mainMapScene: true, isMainCamera: false, hasTargetTexture: false, isUiParticle: false),
            "Non-main camera must reject particle submit context");
        Check.True(!RimUnityParticleHdrTakeover.CanRouteParticleSubmitContext(
            mainMapScene: true, isMainCamera: true, hasTargetTexture: true, isUiParticle: false),
            "Offscreen targetTexture camera must reject particle submit context");
        Check.True(!RimUnityParticleHdrTakeover.CanRouteParticleSubmitContext(
            mainMapScene: true, isMainCamera: true, hasTargetTexture: false, isUiParticle: true),
            "UI/Canvas particle must reject particle submit context");
    }

    internal static void DrawMeshGateMatrix()
    {
        // Direct world renderer (MeshRenderer / SkinnedMeshRenderer / TrailRenderer / LineRenderer)
        Check.True(RimUnityParticleHdrTakeover.ShouldRouteDrawMesh(
            mainMapScene: true, inParticleSubmitScope: false, inBatchFlush: false, submitContextCanRoute: false,
            layer: 0, isMapMainLayer: true, effectiveBloomLayerMask: 0, capturing: false,
            passCount: 1, usesTriangleEdges: false, materialEligible: true),
            "Eligible direct world renderer draw must route via ExternalHdrPassId");

        // Unbatched world particle (including MaterialPropertyBlock path)
        Check.True(RimUnityParticleHdrTakeover.ShouldRouteDrawMesh(
            mainMapScene: true, inParticleSubmitScope: true, inBatchFlush: false, submitContextCanRoute: true,
            layer: 3, isMapMainLayer: true, effectiveBloomLayerMask: 0, capturing: false,
            passCount: 1, usesTriangleEdges: false, materialEligible: true),
            "Eligible unbatched world particle (including native layer 3 without global layer claim) must route");

        // Batched world particle flushed during an enclosing UI Submit must still route its world segment.
        Check.True(RimUnityParticleHdrTakeover.ShouldRouteDrawMesh(
            mainMapScene: true, inParticleSubmitScope: true, inBatchFlush: true, submitContextCanRoute: false,
            layer: 0, isMapMainLayer: true, effectiveBloomLayerMask: 0, capturing: false,
            passCount: 1, usesTriangleEdges: false, materialEligible: true),
            "ParticleDrawBatch.Flush triggered inside UI Submit must still evaluate buffered world segments");

        // Unbatched UI particle Submit must stay blocked.
        Check.True(!RimUnityParticleHdrTakeover.ShouldRouteDrawMesh(
            mainMapScene: true, inParticleSubmitScope: true, inBatchFlush: false, submitContextCanRoute: false,
            layer: 0, isMapMainLayer: true, effectiveBloomLayerMask: 0, capturing: false,
            passCount: 1, usesTriangleEdges: false, materialEligible: true),
            "Rejected ParticleSystemRenderer.Submit (e.g. UI particle) must stay unrouted");

        // RimUnity independent per-layer Bloom mask: skip claimed layers while keeping unclaimed layers active.
        int bloomMaskLayer8 = 1 << 8;
        Check.True(!RimUnityParticleHdrTakeover.ShouldRouteDrawMesh(
            mainMapScene: true, inParticleSubmitScope: false, inBatchFlush: false, submitContextCanRoute: false,
            layer: 8, isMapMainLayer: true, effectiveBloomLayerMask: bloomMaskLayer8, capturing: false,
            passCount: 1, usesTriangleEdges: false, materialEligible: true),
            "Layer claimed by RimUnity EffectiveLayerMask Bloom must not be hijacked");
        Check.True(RimUnityParticleHdrTakeover.ShouldRouteDrawMesh(
            mainMapScene: true, inParticleSubmitScope: false, inBatchFlush: false, submitContextCanRoute: false,
            layer: 0, isMapMainLayer: true, effectiveBloomLayerMask: bloomMaskLayer8, capturing: false,
            passCount: 1, usesTriangleEdges: false, materialEligible: true),
            "Unclaimed layer must still route when another layer uses RimUnity Bloom");

        // Remaining safety guards.
        Check.True(!RimUnityParticleHdrTakeover.ShouldRouteDrawMesh(
            mainMapScene: false, inParticleSubmitScope: false, inBatchFlush: false, submitContextCanRoute: false,
            layer: 0, isMapMainLayer: true, effectiveBloomLayerMask: 0, capturing: false,
            passCount: 1, usesTriangleEdges: false, materialEligible: true),
            "Outside main-map DrawScene must block routing");
        Check.True(!RimUnityParticleHdrTakeover.ShouldRouteDrawMesh(
            mainMapScene: true, inParticleSubmitScope: false, inBatchFlush: false, submitContextCanRoute: false,
            layer: 32, isMapMainLayer: true, effectiveBloomLayerMask: 0, capturing: false,
            passCount: 1, usesTriangleEdges: false, materialEligible: true),
            "Out-of-range layer must block routing");
        Check.True(!RimUnityParticleHdrTakeover.ShouldRouteDrawMesh(
            mainMapScene: true, inParticleSubmitScope: false, inBatchFlush: false, submitContextCanRoute: false,
            layer: 0, isMapMainLayer: false, effectiveBloomLayerMask: 0, capturing: false,
            passCount: 1, usesTriangleEdges: false, materialEligible: true),
            "Custom routed layer (e.g. distortion/native HDR) must not be hijacked");
        Check.True(!RimUnityParticleHdrTakeover.ShouldRouteDrawMesh(
            mainMapScene: true, inParticleSubmitScope: false, inBatchFlush: false, submitContextCanRoute: false,
            layer: 0, isMapMainLayer: true, effectiveBloomLayerMask: 0, capturing: true,
            passCount: 1, usesTriangleEdges: false, materialEligible: true),
            "Graphics.Capture session must block routing");
        Check.True(!RimUnityParticleHdrTakeover.ShouldRouteDrawMesh(
            mainMapScene: true, inParticleSubmitScope: false, inBatchFlush: false, submitContextCanRoute: false,
            layer: 0, isMapMainLayer: true, effectiveBloomLayerMask: 0, capturing: false,
            passCount: 2, usesTriangleEdges: false, materialEligible: true),
            "Multi-pass material must block routing");
        Check.True(!RimUnityParticleHdrTakeover.ShouldRouteDrawMesh(
            mainMapScene: true, inParticleSubmitScope: false, inBatchFlush: false, submitContextCanRoute: false,
            layer: 0, isMapMainLayer: true, effectiveBloomLayerMask: 0, capturing: false,
            passCount: 1, usesTriangleEdges: true, materialEligible: true),
            "triangle-edges geometry shader must block routing");
        Check.True(!RimUnityParticleHdrTakeover.ShouldRouteDrawMesh(
            mainMapScene: true, inParticleSubmitScope: false, inBatchFlush: false, submitContextCanRoute: false,
            layer: 0, isMapMainLayer: true, effectiveBloomLayerMask: 0, capturing: false,
            passCount: 1, usesTriangleEdges: false, materialEligible: false),
            "Ineligible material (including MaterialPropertyBlock HDR-off override) must block routing");
    }

    internal static void RimUnityPatchTargetsBindCleanly()
    {
        bool ok = RimUnityParticleHdrTakeover.TryBindTargets(
            typeof(RU.ParticleSystemRenderer).Assembly,
            typeof(RU.Graphics).Assembly,
            out RimUnityParticleHdrTakeover.BoundTargets? targets,
            out string? error);
        Check.True(ok && targets is not null && error is null,
            "Expected all RimUnity world HDR hook targets to bind cleanly: " + error);
        Check.Equal("material", targets!.DrawMesh.GetParameters()[2].Name ?? string.Empty,
            "RimUnity.Graphics.DrawMesh parameter 2 must remain named 'material' for Harmony binding");
        Check.Equal("layer", targets.DrawMesh.GetParameters()[3].Name ?? string.Empty,
            "RimUnity.Graphics.DrawMesh parameter 3 must remain named 'layer' for Harmony binding");
        Check.Equal("properties", targets.DrawMesh.GetParameters()[5].Name ?? string.Empty,
            "RimUnity.Graphics.DrawMesh parameter 5 must remain named 'properties' for Harmony binding");
        Check.True(!targets.IsCapturing(), "Graphics.capture should be null when idle");
    }
}
