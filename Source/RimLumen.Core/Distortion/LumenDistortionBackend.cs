using System;
using System.Collections.Generic;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Rendering;
using RimLumen.Core.Scene;
using Verse;
using Waylith.Rendering;
using Shader = Waylith.Rendering.Shader;

namespace RimLumen.Core.Distortion;

// Node feature backend for EffectNodeSpec.Distortion. During capture every visible instance on the current map becomes
// one quad enqueued on RimLumenRenderHost.DistortionPassId, the route a third-party layer-14 draw takes, so
// DistortionPass sums both into the `distortion` input. The quad spans 2R x 2R around the node's ground position (the
// Vertical3D display projection shared with particles, anchored at the play origin's XZ on the ground; LumenDistortion.frag.glsl turns the
// quad's DistortionFrame into a pixel offset through the quad's own screen derivatives, so any camera gets the
// projected footprint.
public sealed class LumenDistortionBackend : ILumenNodeFeatureBackend, ILumenFrameContributor
{
    private const string VertexFile = "Distortion/LumenDistortion.vert.glsl";
    private const string FragmentFile = "Distortion/LumenDistortion.frag.glsl";
    private static readonly int RingRadiiId = Shader.PropertyToID("_LumenRingRadii");
    private static readonly int RingAmplitudesId = Shader.PropertyToID("_LumenRingAmplitudes");
    private static readonly int ShapeId = Shader.PropertyToID("_LumenDistortionShape");

    private sealed class Instance(EffectNodeSpec node, DistortionSpec spec, LumenNodeFeatureFrame frame, uint seed)
    {
        internal readonly EffectNodeSpec Node = node;
        internal readonly DistortionSpec Spec = spec;
        // Offsets the haze noise in time so two hazes never shimmer in lockstep.
        internal readonly float HazeSeedPhase = seed % 4096u * 1.618034f;
        internal LumenNodeFeatureFrame Frame = frame;
        internal bool Started;
        internal bool Active;
        internal bool Draining;
        internal float Time;

        internal bool IsVisible => DistortionTimeline.IsVisible(Spec, Started, Time, Active);
    }

    private readonly Dictionary<int, Instance> instances = new();
    private int nextId;
    private Material? material;

    internal static LumenDistortionBackend Shared { get; } = new();

    public LumenNodeFeature Feature => LumenNodeFeature.Distortion;

    public string Name => "LumenDistortionBackend";

    public bool HasActiveWork => instances.Count > 0;

    public int CreateInstance(EffectNodeSpec node, in LumenNodeFeatureFrame initial, bool realTime, uint seed)
    {
        int id = nextId++;
        instances.Add(id, new Instance(node, node.Distortion!, initial, seed));
        return id;
    }

    public void RestartInstance(int instanceId)
    {
        Instance instance = instances[instanceId];
        // A draining node produces no new output, including a Trigger after Stop(true).
        if (instance.Draining) return;
        instance.Started = true;
        instance.Active = true;
        instance.Time = 0f;
    }

    public void UpdateInstance(int instanceId, in LumenNodeFeatureFrame frame)
    {
        Instance instance = instances[instanceId];
        instance.Frame = frame;
        instance.Active = frame.Active && !instance.Draining;
        if (instance.Started) instance.Time += frame.DeltaTime;
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
        instance.Draining = true;
        instance.Active = false;
        ReleaseIfDrained(instanceId, instance);
    }

    public bool IsInstanceAlive(int instanceId) => instances.ContainsKey(instanceId);

    private void ReleaseIfDrained(int instanceId, Instance instance)
    {
        if (instance.Draining && !instance.IsVisible) instances.Remove(instanceId);
    }

    // Ensures: one additive quad per visible instance of context.CurrentMapIndex, in this frame's draw submission.
    public void CaptureMainThread(in LumenCaptureContext context)
    {
        CameraBillboardBasis.Extract(context.ViewMatrix, out _, out Vector3 up, out float yScaleRatio);
        bool isTopDown2D = CameraBillboardBasis.IsStraightTopDown2D(up, yScaleRatio);
        Vector3 groundUp = CameraBillboardBasis.GroundUp(up);
        float moteY = Altitudes.AltitudeFor(AltitudeLayer.MoteOverhead);
        foreach (Instance instance in instances.Values)
        {
            if (instance.Frame.MapIndex != context.CurrentMapIndex || !instance.IsVisible) continue;
            material ??= new Material(ParticleMeshShaders.Create("RimLumen.Distortion", VertexFile, FragmentFile,
                depthTest: false).Additive, "RimLumen.Distortion");
            Enqueue(instance, isTopDown2D, yScaleRatio, groundUp, moteY);
        }
    }

    private void Enqueue(Instance instance, bool isTopDown2D, float yScaleRatio, Vector3 groundUp, float moteY)
    {
        DistortionSpec spec = instance.Spec;
        DistortionFrame frame = DistortionTimeline.Evaluate(spec, instance.Time, instance.Frame.Intensity);
        Matrix4x4 world = instance.Frame.Transform.World;
        float diameter = 2f * spec.Radius * new Vector3(world.M11, world.M12, world.M13).Length();
        Vector3 center = world.Translation;
        Vector3 axisX = new(diameter, 0f, 0f);
        Vector3 axisZ = new(0f, 0f, diameter);
        if (instance.Node.SpacePlane == LumenSpacePlane.Vertical3D)
        {
            Vector3 origin = instance.Frame.Origin;
            Vector3 velocity = Vector3.Zero;
            CameraBillboardBasis.ProjectVertical3D(isTopDown2D, yScaleRatio, groundUp, new Vector2(origin.X, origin.Z),
                moteY, ref center, ref velocity);
            if (isTopDown2D)
            {
                axisX = CameraBillboardBasis.ProjectTopDownPitch(axisX, groundUp);
                axisZ = CameraBillboardBasis.ProjectTopDownPitch(axisZ, groundUp);
            }
        }
        else
        {
            center.Y = moteY;
        }
        Span<DrawProperty> properties = MeshDrawSubmission.ActiveArena.Allocate(3, out DrawPropertyRange range);
        properties[0] = DrawProperty.Vector(RingRadiiId, frame.RingRadii / spec.Radius);
        properties[1] = DrawProperty.Vector(RingAmplitudesId, frame.RingAmplitudes / spec.Radius);
        properties[2] = DrawProperty.Vector(ShapeId, new Vector4(spec.Thickness / spec.Radius,
            frame.HazeAmplitude / spec.Radius, spec.Frequency * spec.Radius, frame.HazePhase + instance.HazeSeedPhase));
        // plane10 spans [-0.5, 0.5] on X and Z; the host takes column-vector matrices (translation in M14..M34).
        var quad = new Matrix4x4(
            axisX.X, axisX.Y, axisX.Z, 0f,
            0f, 1f, 0f, 0f,
            axisZ.X, axisZ.Y, axisZ.Z, 0f,
            center.X, center.Y, center.Z, 1f);
        MeshDrawSubmission.Enqueue(new MeshDrawCommand(MeshPool.plane10.EngineMesh, material!, range,
            Matrix4x4.Transpose(quad), 0, RimLumenRenderHost.DistortionPassId));
    }

    public void EnqueuePreCompute(in LumenComputeContext context) { }

    public void RecordBufferBarriers(in LumenBarrierContext context) { }

    // Ensures: no-op: the quads enqueued in CaptureMainThread are recorded by DistortionPass, never into the HDR layer.
    public void RecordHdrDraws(in LumenHdrDrawContext context) { }
}

// Registers the shared backend once; LumenSceneBootstrap first, so the VFX service advances nodes before capture here.
[StaticConstructorOnStartup]
public static class LumenDistortionStartupHook
{
    static LumenDistortionStartupHook()
    {
        LumenSceneBootstrap.EnsureInitialized();
        LumenHostRegistry.RegisterNodeFeatureBackend(LumenDistortionBackend.Shared);
        LumenHostRegistry.RegisterContributor(LumenDistortionBackend.Shared);
    }
}
