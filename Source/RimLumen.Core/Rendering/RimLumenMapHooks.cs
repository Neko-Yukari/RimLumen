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
    internal const int SplitQueue = 3152;
    private const string HarmonyId = "NekoYukari.RimLumen.MapSplit";
    private static readonly ConditionalWeakTable<MeshDrawFrame, PipelineFrame> Frames = new();
    private static SceneGrabPoint sentinel = null!;
    private static FieldInfo sceneGrabs = null!;
    private static FieldInfo sceneTargets = null!;
    private static FieldInfo mapRenderer = null!;
    private static bool armed;
    [ThreadStatic] private static RecordScope? current;

    private sealed record RecordScope(FrameContext Context, PipelineFrame Payload, SceneColorTargets Targets, MeshBatchRenderer Renderer);

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
        sceneGrabs = RequireField(typeof(MeshDrawFrame), "<SceneGrabs>k__BackingField", typeof(IReadOnlyList<SceneGrabPoint>));
        sceneTargets = RequireField(mainPass, "sceneTargets", typeof(SceneColorTargets));
        mapRenderer = RequireField(mainPass, "renderer", typeof(MeshBatchRenderer));
        PropertyInfo load = typeof(SceneColorTargets).GetProperty("LoadAttachment", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(typeof(SceneColorTargets).FullName, "LoadAttachment");
        if (load.PropertyType != typeof(PassAttachment) || load.GetMethod is not { IsPublic: true, IsStatic: false } || load.GetIndexParameters().Length != 0)
            throw new InvalidOperationException("SceneColorTargets.LoadAttachment signature changed.");
        sentinel = SceneGrabRegistry.Define("RimLumenSplit", SplitQueue);
        if (sentinel.Queue != SplitQueue) throw new InvalidOperationException("SceneGrabPoint RimLumenSplit queue changed.");
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
        if (__result is not MeshDrawFrame map || RimLumenRenderHost.FailSafeTripped ||
            RimLumenRenderHost.Pipeline?.Captured is not { } payload) return;
        try
        {
            // Keep the frame returned by vanilla (and observed by other postfixes); only replace its grab list.
            sceneGrabs.SetValue(map, SceneGrabSplit.Insert(map.SceneGrabs, sentinel, static point => point.Queue));
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
        }
        catch (Exception ex) { RimLumenRenderHost.Pipeline!.TripRenderFailSafe(ex); }
    }

    private static void RecordFinalizer() => current = null;

    private static bool GrabPrefix(SceneGrabPoint point)
    {
        if (!ReferenceEquals(point, sentinel)) return true;
        RecordScope? scope = current;
        if (scope != null && !RimLumenRenderHost.FailSafeTripped)
        {
            current = null;
            try { RimLumenRenderHost.Pipeline!.MapChain.Record(scope.Context, scope.Payload, scope.Targets); }
            finally
            {
                // The map renderer caches Vulkan bindings across slices; the chain replaced those bindings.
                scope.Renderer.InvalidateCommandBufferBindings();
            }
        }
        return false;
    }
}
