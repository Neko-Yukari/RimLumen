using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimLumen.Core.Contracts;
using RimLumen.Core.Rendering;
using Verse;
using Waylith.Rendering;
using RU = global::RimUnity;

namespace RimLumen.Adapter.RimUnity;

/// <summary>
/// Strategy A draw-only HDR takeover for third-party RimUnity world-space renderers inside
/// <c>RimUnity.ParticleFrame.DrawScene(Camera.main)</c>: <c>ParticleSystemRenderer</c> (billboards, mesh particles,
/// sprite batches, particle trails), <c>MeshRenderer</c> (including <c>SpriteRenderer</c>'s backing renderer),
/// <c>SkinnedMeshRenderer</c>, <c>TrailRenderer</c> and <c>LineRenderer</c>. Simulation stays in RimUnity; eligible
/// emissive main-map draws keep their original Unity layer untouched and receive RimLumen's private
/// adapter pass inside <c>RimUnity.Graphics.DrawMesh</c>. At capture time,
/// RimLumen adapts them into <c>RimLumenHdr</c> or restores any rejected command's passId back to <c>MapMain</c> so
/// no unrelated layer is ever claimed and no rejected draw is ever dropped.
/// </summary>
internal static class RimUnityParticleHdrTakeover
{
    private const string HarmonyId = "NekoYukari.RimLumen.RimUnity.ParticleHdr";
    private const byte SubmitFlagActive = 1;
    private const byte SubmitFlagCanRoute = 2;

    private readonly record struct SceneState(bool Active, int EffectiveBloomLayerMask);

    internal sealed record BoundTargets(
        MethodInfo DrawScene,
        MethodInfo Submit,
        MethodInfo Flush,
        MethodInfo DrawMesh,
        MethodInfo LayerPassResolve,
        Func<RU.ParticleSystemRenderer, bool> IsUiRenderer,
        Func<bool> IsCapturing);

    private static readonly MethodInfo ResolveDrawMeshPassMethod =
        typeof(RimUnityParticleHdrTakeover).GetMethod(nameof(ResolveDrawMeshPass), BindingFlags.Static | BindingFlags.NonPublic)!;

    private static RimUnityRuntime runtime = null!;
    private static Func<RU.ParticleSystemRenderer, bool> isUiRenderer = null!;
    private static Func<bool> isCapturing = null!;
    private static MethodInfo layerPassResolveTarget = null!;
    private static byte drawPassId;

    [ThreadStatic] private static bool mainMapSceneActive;
    [ThreadStatic] private static int activeBloomLayerMask;
    [ThreadStatic] private static bool inParticleSubmit;
    [ThreadStatic] private static bool activeSubmitCanRoute;
    [ThreadStatic] private static bool inParticleBatchFlush;
    [ThreadStatic] private static bool overrideNextDrawMeshPass;

    // Pure submission-time gate for ParticleSystemRenderer.Submit context (camera + UI checks).
    internal static bool CanRouteParticleSubmitContext(
        bool mainMapScene,
        bool isMainCamera,
        bool hasTargetTexture,
        bool isUiParticle) =>
        mainMapScene
        && isMainCamera
        && !hasTargetTexture
        && !isUiParticle;

    // Pure routing gate for RimUnity.Graphics.DrawMesh inside ParticleFrame.DrawScene(Camera.main):
    // covers batched particle flushes, unbatched particles (including per-instance MaterialPropertyBlock),
    // MeshRenderer / SpriteRenderer, SkinnedMeshRenderer, TrailRenderer and LineRenderer.
    internal static bool ShouldRouteDrawMesh(
        bool mainMapScene,
        bool inParticleSubmitScope,
        bool inBatchFlush,
        bool submitContextCanRoute,
        int layer,
        bool isMapMainLayer,
        int effectiveBloomLayerMask,
        bool capturing,
        int passCount,
        bool usesTriangleEdges,
        bool materialEligible) =>
        mainMapScene
        && (uint)layer <= 31
        && (!inParticleSubmitScope || inBatchFlush || submitContextCanRoute)
        && isMapMainLayer
        && (effectiveBloomLayerMask & (1 << layer)) == 0
        && !capturing
        && passCount == 1
        && !usesTriangleEdges
        && materialEligible;

    // Pure IL rewrite helper shared by DrawMeshTranspiler and offline unit tests.
    internal static List<CodeInstruction> RewriteResolvePassCalls(
        IEnumerable<CodeInstruction> instructions,
        MethodInfo originalResolve,
        MethodInfo replacementResolve,
        out int replacedCount)
    {
        var list = new List<CodeInstruction>();
        replacedCount = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(originalResolve))
            {
                var rewritten = new CodeInstruction(OpCodes.Call, replacementResolve);
                rewritten.labels.AddRange(instruction.labels);
                rewritten.blocks.AddRange(instruction.blocks);
                list.Add(rewritten);
                replacedCount++;
            }
            else
            {
                list.Add(instruction);
            }
        }
        return list;
    }

    // Validates every RimUnity internal method/field signature before any Harmony patch is applied.
    internal static bool TryBindTargets(Assembly rimUnityAssembly, Assembly rimUnityCoreAssembly,
        out BoundTargets? targets, out string? error)
    {
        targets = null;
        const BindingFlags staticAny = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        const BindingFlags instanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        Type? particleFrame = rimUnityAssembly.GetType("RimUnity.ParticleFrame");
        MethodInfo? drawScene = particleFrame?.GetMethod("DrawScene", staticAny, null, [typeof(RU.Camera)], null);
        if (drawScene is null || drawScene.ReturnType != typeof(void) || drawScene.GetParameters()[0].Name != "camera")
        {
            error = "Missing RimUnity.ParticleFrame.DrawScene(Camera camera).";
            return false;
        }

        Type rendererType = typeof(RU.ParticleSystemRenderer);
        MethodInfo? submit = rendererType.GetMethod("Submit", instanceNonPublic, null,
            [typeof(RU.Mesh).MakeByRefType(), typeof(RU.Material), typeof(RU.Camera)], null);
        if (submit is null || submit.ReturnType != typeof(void)
            || submit.GetParameters()[1].Name != "mat"
            || submit.GetParameters()[2].Name != "camera")
        {
            error = "Missing RimUnity.ParticleSystemRenderer.Submit(ref Mesh, Material mat, Camera camera).";
            return false;
        }

        FieldInfo? uiEntry = rendererType.GetField("uiEntry", instanceNonPublic);
        FieldInfo? cameraUiEntry = rendererType.GetField("cameraUiEntry", instanceNonPublic);
        if (uiEntry is null || uiEntry.FieldType.IsValueType || cameraUiEntry is null || cameraUiEntry.FieldType.IsValueType)
        {
            error = "Missing reference fields uiEntry/cameraUiEntry on RimUnity.ParticleSystemRenderer.";
            return false;
        }

        Type? batchType = rimUnityAssembly.GetType("RimUnity.ParticleDrawBatch");
        MethodInfo? flush = batchType?.GetMethod("Flush", staticAny, null, Type.EmptyTypes, null);
        if (flush is null || flush.ReturnType != typeof(void))
        {
            error = "Missing RimUnity.ParticleDrawBatch.Flush().";
            return false;
        }

        Type graphicsType = rimUnityCoreAssembly.GetType("RimUnity.Graphics") ?? typeof(RU.Graphics);
        MethodInfo? drawMesh = graphicsType.GetMethod("DrawMesh", staticAny, null,
            [typeof(RU.Mesh), typeof(RU.Matrix4x4), typeof(RU.Material), typeof(int), typeof(int), typeof(RU.MaterialPropertyBlock)], null);
        if (drawMesh is null || drawMesh.ReturnType != typeof(void)
            || drawMesh.GetParameters()[2].Name != "material"
            || drawMesh.GetParameters()[3].Name != "layer"
            || drawMesh.GetParameters()[5].Name != "properties")
        {
            error = "Missing RimUnity.Graphics.DrawMesh(Mesh, Matrix4x4, Material material, int layer, int, MaterialPropertyBlock properties).";
            return false;
        }

        MethodInfo? layerPassResolve = typeof(LayerPassRouter).GetMethod(nameof(LayerPassRouter.Resolve),
            BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly, null, [typeof(int)], null);
        if (layerPassResolve is null || layerPassResolve.ReturnType != typeof(byte))
        {
            error = "Missing Waylith.Rendering.LayerPassRouter.Resolve(int).";
            return false;
        }

        FieldInfo? captureField = graphicsType.GetField("capture", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        if (captureField is null || captureField.FieldType.IsValueType)
        {
            error = "Missing static reference field RimUnity.Graphics.capture.";
            return false;
        }

        targets = new BoundTargets(
            drawScene,
            submit,
            flush,
            drawMesh,
            layerPassResolve,
            BuildIsUiRenderer(rendererType, uiEntry, cameraUiEntry),
            BuildIsCapturing(captureField));
        error = null;
        return true;
    }

    internal static bool Install(RimUnityRuntime rimUnity)
    {
        if (drawPassId != 0) return true;

        var harmony = new Harmony(HarmonyId);
        try
        {
            if (!TryBindTargets(typeof(RU.ParticleSystemRenderer).Assembly, typeof(RU.Graphics).Assembly,
                    out BoundTargets? targets, out string? error) || targets is null)
            {
                Log.Warning($"[RimLumen.Adapter.RimUnity] RimUnity {rimUnity.Version} (provided by {rimUnity.Provider.Name}) " +
                    $"world HDR draw takeover disabled: {error}");
                return false;
            }

            runtime = rimUnity;
            isUiRenderer = targets.IsUiRenderer;
            isCapturing = targets.IsCapturing;
            layerPassResolveTarget = targets.LayerPassResolve;

            harmony.Patch(targets.DrawScene, prefix: Patch(nameof(DrawScenePrefix)), finalizer: Patch(nameof(DrawSceneFinalizer)));
            harmony.Patch(targets.Submit, prefix: Patch(nameof(SubmitPrefix)), finalizer: Patch(nameof(SubmitFinalizer)));
            harmony.Patch(targets.Flush, prefix: Patch(nameof(FlushPrefix)), finalizer: Patch(nameof(FlushFinalizer)));
            harmony.Patch(targets.DrawMesh,
                prefix: Patch(nameof(DrawMeshPrefix)),
                transpiler: Patch(nameof(DrawMeshTranspiler)),
                finalizer: Patch(nameof(DrawMeshFinalizer)));
            MethodInfo? reload = AccessTools.DeclaredMethod(typeof(Shader), "ReloadLayouts", Type.EmptyTypes);
            if (reload?.ReturnType == typeof(void)) harmony.Patch(reload, postfix: Patch(nameof(ReloadLayoutsPostfix)));
            drawPassId = LumenHostRegistry.RegisterDrawAdapter("RimUnity", new ExternalHdrDrawAdapter(rimUnity.ReadEffectiveBloomLayerMask));
            return true;
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(HarmonyId);
            Log.Error($"[RimLumen.Adapter.RimUnity] Failed to install RimUnity world HDR draw takeover; leaving RimUnity world draws unchanged: {ex}");
            return false;
        }
    }

    private static void ReloadLayoutsPostfix(Shader __instance) => ExternalHdrAlphaClampShaders.Invalidate(__instance);

    private static HarmonyMethod Patch(string name) =>
        new(typeof(RimUnityParticleHdrTakeover).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!);

    private static Func<RU.ParticleSystemRenderer, bool> BuildIsUiRenderer(Type rendererType, FieldInfo uiEntry, FieldInfo cameraUiEntry)
    {
        ParameterExpression instance = Expression.Parameter(rendererType, "renderer");
        BinaryExpression hasUi = Expression.NotEqual(Expression.Field(instance, uiEntry), Expression.Constant(null, uiEntry.FieldType));
        BinaryExpression hasCameraUi = Expression.NotEqual(Expression.Field(instance, cameraUiEntry), Expression.Constant(null, cameraUiEntry.FieldType));
        return Expression.Lambda<Func<RU.ParticleSystemRenderer, bool>>(Expression.OrElse(hasUi, hasCameraUi), instance).Compile();
    }

    private static Func<bool> BuildIsCapturing(FieldInfo captureField)
    {
        BinaryExpression active = Expression.NotEqual(Expression.Field(null, captureField), Expression.Constant(null, captureField.FieldType));
        return Expression.Lambda<Func<bool>>(active).Compile();
    }

    private static void DrawScenePrefix(RU.Camera? camera, out SceneState? __state)
    {
        __state = new SceneState(mainMapSceneActive, activeBloomLayerMask);
        bool active = camera != null
            && ReferenceEquals(camera, RU.Camera.main)
            && camera.targetTexture == null
            && RimLumenRenderHost.CanRouteAdapterDrawsNow
            && runtime.IsMapHdrOff();
        mainMapSceneActive = active;
        activeBloomLayerMask = active ? runtime.ReadEffectiveBloomLayerMask() : ~0;
    }

    private static void DrawSceneFinalizer(SceneState? __state)
    {
        if (__state.HasValue)
        {
            mainMapSceneActive = __state.Value.Active;
            activeBloomLayerMask = __state.Value.EffectiveBloomLayerMask;
        }
    }

    private static void SubmitPrefix(RU.ParticleSystemRenderer __instance, RU.Camera? camera, out byte? __state)
    {
        __state = (byte)((inParticleSubmit ? SubmitFlagActive : 0) | (activeSubmitCanRoute ? SubmitFlagCanRoute : 0));
        inParticleSubmit = true;
        if (!mainMapSceneActive)
        {
            activeSubmitCanRoute = false;
            return;
        }

        bool isMainCamera = camera != null && ReferenceEquals(camera, RU.Camera.main);
        bool hasTargetTexture = camera != null && camera.targetTexture != null;
        bool isUi = isUiRenderer(__instance);
        activeSubmitCanRoute = CanRouteParticleSubmitContext(mainMapSceneActive, isMainCamera, hasTargetTexture, isUi);
    }

    private static void SubmitFinalizer(byte? __state)
    {
        if (__state.HasValue)
        {
            inParticleSubmit = (__state.Value & SubmitFlagActive) != 0;
            activeSubmitCanRoute = (__state.Value & SubmitFlagCanRoute) != 0;
        }
    }

    // ParticleDrawBatch only ever buffers non-UI main-scene particles; a UI Submit may flush previously buffered
    // world segments before emitting its own UI draw. Track Flush so DrawMesh evaluates flushed segments without
    // inheriting the enclosing UI Submit's rejection flag.
    private static void FlushPrefix(out bool? __state)
    {
        __state = inParticleBatchFlush;
        inParticleBatchFlush = true;
    }

    private static void FlushFinalizer(bool? __state)
    {
        if (__state.HasValue) inParticleBatchFlush = __state.Value;
    }

    private static void DrawMeshPrefix(RU.Material? material, int layer, out bool? __state)
    {
        __state = overrideNextDrawMeshPass;
        if (!mainMapSceneActive || material == null || (uint)layer > 31)
        {
            overrideNextDrawMeshPass = false;
            return;
        }

        bool submitContextOk = !inParticleSubmit || inParticleBatchFlush || activeSubmitCanRoute;
        bool isMapMainLayer = submitContextOk && RimLumenRenderHost.IsMapMainLayer(layer);
        int bloomMask = activeBloomLayerMask;
        bool bloomFree = isMapMainLayer && (bloomMask & (1 << layer)) == 0;
        bool capturing = !bloomFree || isCapturing();
        int passCount = capturing ? 0 : material.passCount;
        bool triangleEdges = passCount == 1 && material.shader != null && material.shader.triangleEdges;
        bool materialEligible = passCount == 1
            && !triangleEdges
            && material.engineMaterial is not null
            && ExternalHdrMaterialRules.IsMaterialEligible(material.engineMaterial,
                RimLumenRenderHost.CurrentFrameColorBlendSpace);

        overrideNextDrawMeshPass = ShouldRouteDrawMesh(
            mainMapSceneActive,
            inParticleSubmit,
            inParticleBatchFlush,
            activeSubmitCanRoute,
            layer,
            isMapMainLayer,
            bloomMask,
            capturing,
            passCount,
            triangleEdges,
            materialEligible);
    }

    private static void DrawMeshFinalizer(bool? __state)
    {
        if (__state.HasValue) overrideNextDrawMeshPass = __state.Value;
    }

    private static IEnumerable<CodeInstruction> DrawMeshTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> rewritten = RewriteResolvePassCalls(
            instructions,
            layerPassResolveTarget,
            ResolveDrawMeshPassMethod,
            out int replaced);
        if (replaced != 1)
            throw new InvalidOperationException($"Expected exactly 1 LayerPassRouter.Resolve call in RimUnity.Graphics.DrawMesh; found {replaced}.");
        return rewritten;
    }

    private static byte ResolveDrawMeshPass(int layer) =>
        overrideNextDrawMeshPass ? drawPassId : LayerPassRouter.Resolve(layer);
}
