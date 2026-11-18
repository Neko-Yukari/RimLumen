using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Assets;
using RimLumen.Core.Contracts;
using RimLumen.Core.Rendering;
using RimLumen.Core.Scene;
using Verse;
using Waylith.Rendering;
using Shader = Waylith.Rendering.Shader;

namespace RimLumen.Core.ScreenDim;

// Node feature backend for EffectNodeSpec.ScreenDim. Each capture combines every visible instance on the current map
// into one coverage and, when it is above 0, enqueues one full-screen quad on RimLumenRenderHost.LumenRenderPassId. The
// quad writes only HDR alpha (colour Zero/One, alpha One/OneMinusSrcAlpha), so the composite scene * (1 - hdr.a) +
// hdr.rgb darkens the scene while every HDR draw keeps its colour. That alpha blend commutes with the particle and
// ribbon blends (alpha Zero/One or One/OneMinusSrcAlpha), so the result equals drawing the dim first even though GPU
// particles record before the routed draws; queue 0 puts it before every other routed draw.
public sealed class LumenScreenDimBackend : ILumenNodeFeatureBackend, ILumenFrameContributor
{
    private const string VertexFile = "ScreenDim/LumenScreenDim.vert.glsl";
    private const string FragmentFile = "ScreenDim/LumenScreenDim.frag.glsl";
    private const int RenderQueue = 0;
    private static readonly BlendState CoverageBlend = new(Enabled: true, BlendFactor.Zero, BlendFactor.One, BlendOp.Add,
        BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add);
    private static readonly int CoverageId = Shader.PropertyToID("_LumenScreenDim");

    private sealed class Instance(ScreenDimSpec spec, LumenNodeFeatureFrame frame)
    {
        internal readonly ScreenDimSpec Spec = spec;
        internal LumenNodeFeatureFrame Frame = frame;
        internal NodeFeatureClock Clock;
    }

    private readonly Dictionary<int, Instance> instances = new();
    private int nextId;
    private Material? material;

    internal static LumenScreenDimBackend Shared { get; } = new();

    public LumenNodeFeature Feature => LumenNodeFeature.ScreenDim;

    public string Name => "LumenScreenDimBackend";

    public bool HasActiveWork => instances.Count > 0;

    public int CreateInstance(EffectNodeSpec node, in LumenNodeFeatureFrame initial, bool realTime, uint seed)
    {
        int id = nextId++;
        instances.Add(id, new Instance(node.ScreenDim!, initial));
        return id;
    }

    public void RestartInstance(int instanceId) => instances[instanceId].Clock.Restart();

    public void UpdateInstance(int instanceId, in LumenNodeFeatureFrame frame)
    {
        Instance instance = instances[instanceId];
        instance.Frame = frame;
        instance.Clock.Update(frame);
        ReleaseIfDrained(instanceId, instance);
    }

    // Ensures: an immediate destroy also succeeds for an id that already drained out; the handle may still hold it.
    public void DestroyInstance(int instanceId, bool immediate)
    {
        if (immediate)
        {
            instances.Remove(instanceId);
            return;
        }
        Instance instance = instances[instanceId];
        instance.Clock.Drain();
        ReleaseIfDrained(instanceId, instance);
    }

    public bool IsInstanceAlive(int instanceId) => instances.ContainsKey(instanceId);

    private void ReleaseIfDrained(int instanceId, Instance instance)
    {
        if (instance.Clock.IsDrained(instance.Spec.Duration)) instances.Remove(instanceId);
    }

    // Ensures: 1 - prod(1 - a_i) over the instances visible on mapIndex, a_i = min(1, Strength * intensity *
    //          StrengthOverTime(t)); 0 when none is visible.
    internal float Coverage(int mapIndex)
    {
        float uncovered = 1f;
        foreach (Instance instance in instances.Values)
        {
            ScreenDimSpec spec = instance.Spec;
            if (instance.Frame.MapIndex != mapIndex || !instance.Clock.IsVisible(spec.Duration)) continue;
            float curve = spec.StrengthOverTime is { } strength ? CurveEvaluator.Evaluate(strength, instance.Clock.Time) : 1f;
            uncovered *= 1f - MathF.Min(1f, spec.Strength * instance.Frame.Intensity * curve);
        }
        return 1f - uncovered;
    }

    // Ensures: one full-screen quad in this frame's draw submission when Coverage(context.CurrentMapIndex) > 0, else nothing.
    public void CaptureMainThread(in LumenCaptureContext context)
    {
        float coverage = Coverage(context.CurrentMapIndex);
        if (coverage <= 0f) return;
        material ??= new Material(ParticleMeshShaders.CreateSingle("RimLumen.ScreenDim", VertexFile, FragmentFile,
            depthTest: false, RenderQueue, CoverageBlend), "RimLumen.ScreenDim");
        Span<DrawProperty> properties = MeshDrawSubmission.ActiveArena.Allocate(1, out DrawPropertyRange range);
        properties[0] = DrawProperty.Vector(CoverageId, new Vector4(coverage, 0f, 0f, 0f));
        MeshDrawSubmission.Enqueue(new MeshDrawCommand(MeshPool.plane10.EngineMesh, material, range, Matrix4x4.Identity, 0,
            RimLumenRenderHost.LumenRenderPassId));
    }

    public void EnqueuePreCompute(in LumenComputeContext context) { }

    public void RecordBufferBarriers(in LumenBarrierContext context) { }

    // Ensures: no-op: the quad enqueued in CaptureMainThread is a routed draw of the HDR pass.
    public void RecordHdrDraws(in LumenHdrDrawContext context) { }
}

// Registers the shared backend once; LumenSceneBootstrap first, so the VFX service advances nodes before capture here.
[StaticConstructorOnStartup]
public static class LumenScreenDimStartupHook
{
    static LumenScreenDimStartupHook()
    {
        LumenSceneBootstrap.EnsureInitialized();
        LumenHostRegistry.RegisterNodeFeatureBackend(LumenScreenDimBackend.Shared);
        LumenHostRegistry.RegisterContributor(LumenScreenDimBackend.Shared);
    }
}
