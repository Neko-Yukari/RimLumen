using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Brutal.VulkanApi;
using HarmonyLib;
using RimWorldWaylith.Rendering;
using Waylith.Rendering;
using RimLumen.Core.Rendering.Logic;

namespace RimLumen.Core.Rendering;

internal static class RimLumenMapHooks
{
    private const string HarmonyId = "NekoYukari.RimLumen.MapSplit";
    private static readonly ConditionalWeakTable<MeshDrawFrame, PipelineFrame> Frames = new();
    private static readonly SceneGrabPoint[] sentinels = new SceneGrabPoint[4];
    // Right after vanilla LightOverlay: the light-pool composite runs before every queue-3101 draw.
    private static SceneGrabPoint lightPoolSentinel = null!, takeoverSentinel = null!;
    private static FieldInfo sceneGrabs = null!;
    private static FieldInfo sceneTargets = null!;
    private static FieldInfo mapRenderer = null!;
    private static bool armed;
    [ThreadStatic] private static RecordScope? current;

    private sealed record RecordScope(FrameContext Context, PipelineFrame Payload, SceneColorTargets Targets, MeshBatchRenderer Renderer)
    {
        internal GlowGroupMask Recorded;
        internal bool LightPoolsRecorded;
    }

    internal static void Arm()
    {
        if (armed) return;
        Type map = typeof(MapRenderFeature);
        Type mainPass = map.GetNestedType("MapMainPass", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(map.FullName, "MapMainPass");
        MethodInfo capture = RequireMethod(map, "CaptureFrame", typeof(object), typeof(FrameTiming).MakeByRefType());
        MethodInfo record = RequireMethod(mainPass, "Record", typeof(void), typeof(FrameContext).MakeByRefType(), typeof(object));
        MethodInfo grab = RequireMethod(typeof(SceneGrabTargets), "RecordGrab", typeof(void),
            typeof(CommandBuffer), typeof(SceneGrabPoint), typeof(VkImage));
        Lighting.LightPoolShadows.BindEngineMembers();
        Lighting.LightingTakeover.BindEngineMembers();
        Lighting.Occlusion.OcclusionField.Arm();
        sceneGrabs = RequireField(typeof(MeshDrawFrame), "<SceneGrabs>k__BackingField", typeof(IReadOnlyList<SceneGrabPoint>));
        sceneTargets = RequireField(mainPass, "sceneTargets", typeof(SceneColorTargets));
        mapRenderer = RequireField(mainPass, "renderer", typeof(MeshBatchRenderer));
        PropertyInfo load = typeof(SceneColorTargets).GetProperty("LoadAttachment", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(typeof(SceneColorTargets).FullName, "LoadAttachment");
        if (load.PropertyType != typeof(PassAttachment) || load.GetMethod is not { IsPublic: true, IsStatic: false } || load.GetIndexParameters().Length != 0)
            throw new InvalidOperationException("SceneColorTargets.LoadAttachment signature changed.");
        for (int i = 0; i < sentinels.Length; i++)
        {
            GlowGroup group = (GlowGroup)i;
            string name = group == GlowGroup.Early ? "RimLumenSplit" : "RimLumenSplit" + group;
            int queue = GlowGroups.Range(group).MaxExclusive;
            sentinels[i] = SceneGrabRegistry.Define(name, queue);
            if (sentinels[i].Queue != queue) throw new InvalidOperationException($"SceneGrabPoint {name} queue changed.");
        }
        lightPoolSentinel = SceneGrabRegistry.Define("RimLumenLightPool", Lighting.LightPoolRenderer.RenderQueue);
        if (lightPoolSentinel.Queue != Lighting.LightPoolRenderer.RenderQueue)
            throw new InvalidOperationException("SceneGrabPoint RimLumenLightPool queue changed.");
        takeoverSentinel = SceneGrabRegistry.Define("RimLumenLightingTakeover", 3100);
        if (takeoverSentinel.Queue != 3100) throw new InvalidOperationException("SceneGrabPoint RimLumenLightingTakeover queue changed.");
        var harmony = new Harmony(HarmonyId);
        string target = capture.DeclaringType!.FullName + "." + capture.Name;
        try
        {
            harmony.Patch(capture, postfix: Patch(nameof(CapturePostfix)));
            target = record.DeclaringType!.FullName + "." + record.Name;
            harmony.Patch(record, prefix: Patch(nameof(RecordPrefix)), finalizer: Patch(nameof(RecordFinalizer)));
            target = grab.DeclaringType!.FullName + "." + grab.Name;
            harmony.Patch(grab, prefix: Patch(nameof(GrabPrefix)));
            armed = true;
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(HarmonyId);
            throw new InvalidOperationException("Could not patch " + target, ex);
        }
    }

    private static HarmonyMethod Patch(string name) => new(typeof(RimLumenMapHooks).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!);

    private static MethodInfo RequireMethod(Type type, string name, Type result, params Type[] parameters)
    {
        MethodInfo? method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
            null, parameters, null);
        if (method == null || method.ReturnType != result || method.IsGenericMethod ||
            (parameters.Length > 0 && parameters[0].IsByRef && !method.GetParameters()[0].IsIn))
            throw new MissingMethodException(type.FullName, name + " (expected signature)");
        return method;
    }

    private static FieldInfo RequireField(Type type, string name, Type fieldType)
    {
        FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        if (field == null || field.FieldType != fieldType)
            throw new MissingFieldException(type.FullName, name + " (expected " + fieldType.Name + ")");
        return field;
    }

    private static void CapturePostfix(object? __result)
    {
        if (__result is not MeshDrawFrame map) return;
        // Vanilla may hand back the same MeshDrawFrame instance on a later capture (observed in a validation run),
        // so drop the earlier capture's payload binding and split sentinels before deciding on this one.
        Frames.Remove(map);
        if (map.SceneGrabs is List<SceneGrabPoint> previous)
            previous.RemoveAll(point => point == takeoverSentinel || point == lightPoolSentinel || Array.IndexOf(sentinels, point) >= 0);
        if (RimLumenRenderHost.FailSafeTripped || RimLumenRenderHost.Pipeline?.Captured is not { } payload) return;
        try
        {
            // Keep the frame returned by vanilla (and observed by other postfixes); only replace its grab list.
            IReadOnlyList<SceneGrabPoint>? grabs = map.SceneGrabs;
            for (int i = 0; i < sentinels.Length; i++)
                if (payload.ShouldSplit((GlowGroup)i)) grabs = SceneGrabSplit.Insert(grabs, sentinels[i], static point => point.Queue);
            if (payload.Takeover)
            {
                grabs = SceneGrabSplit.Insert(grabs, takeoverSentinel, static point => point.Queue);
                RimLumenRenderHost.Pipeline!.Takeover.Rebind(map);
            }
            else if (payload.HasLightPools)
            {
                grabs = SceneGrabSplit.Insert(grabs, lightPoolSentinel, static point => point.Queue);
                RimLumenRenderHost.Pipeline!.PoolShadows.Rebind(map);
            }
            sceneGrabs.SetValue(map, grabs);
            Frames.Add(map, payload);
        }
        catch (Exception ex) { RimLumenRenderHost.Pipeline!.TripRenderFailSafe(ex); }
    }

    private static void RecordPrefix(object __instance, in FrameContext ctx, object? featurePayload)
    {
        current = null;
        if (RimLumenRenderHost.FailSafeTripped || featurePayload is not MeshDrawFrame map || !Frames.TryGetValue(map, out PipelineFrame? payload)) return;
        try
        {
            current = new RecordScope(ctx, payload, (SceneColorTargets)sceneTargets.GetValue(__instance)!,
                (MeshBatchRenderer)mapRenderer.GetValue(__instance)!);
            // The lifted shadows read no pool unless this record's queue-3101 pass rebinds the light sum.
            if (payload.Takeover || payload.HasLightPools) RimLumenRenderHost.Pipeline!.PoolShadows.LightSum.Clear();
        }
        catch (Exception ex) { RimLumenRenderHost.Pipeline!.TripRenderFailSafe(ex); }
    }

    private static void RecordFinalizer() => current = null;

    private static bool GrabPrefix(SceneGrabPoint point)
    {
        if (point == lightPoolSentinel || point == takeoverSentinel)
        {
            RecordScope? lightScope = current;
            if (lightScope != null && !RimLumenRenderHost.FailSafeTripped && !lightScope.LightPoolsRecorded)
            {
                lightScope.LightPoolsRecorded = true;
                try { RimLumenRenderHost.Pipeline!.MapChain.RecordLightPools(lightScope.Context, lightScope.Payload, lightScope.Targets); }
                finally { lightScope.Renderer.InvalidateCommandBufferBindings(); }
            }
            return false;
        }
        int index = Array.IndexOf(sentinels, point);
        if (index < 0) return true;
        GlowGroup group = (GlowGroup)index;
        RecordScope? scope = current;
        if (scope != null && !RimLumenRenderHost.FailSafeTripped && (scope.Recorded & GlowGroups.Mask(group)) == 0)
        {
            scope.Recorded |= GlowGroups.Mask(group);
            try { RimLumenRenderHost.Pipeline!.MapChain.Record(scope.Context, scope.Payload, scope.Targets, group); }
            finally
            {
                // The map renderer caches Vulkan bindings across slices; the chain replaced those bindings.
                scope.Renderer.InvalidateCommandBufferBindings();
            }
        }
        return false;
    }
}
