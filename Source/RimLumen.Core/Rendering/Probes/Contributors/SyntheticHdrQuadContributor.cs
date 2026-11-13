using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi;
using RimLumen.Core.Contracts;
using Waylith;
using Waylith.Rendering;

namespace RimLumen.Core.Rendering.Probes.Contributors;

// P1-active: draws (a) an emissive quad with HDR intensity > 1.0 (bloom + tonemap measurement) and
// (b) a soft-particle quad whose depth straddles the vanilla scene depth (occlusion + feathering measurement),
// both into the RGBA16F target with read-only scene depth.
public sealed class SyntheticHdrQuadContributor : ILumenFrameContributor, IDisposable
{
    private const string EmissiveVertexShader = "Probes/SyntheticEmissive.vert.glsl";
    private const string EmissiveFragmentShader = "Probes/SyntheticEmissive.frag.glsl";
    private const string SoftVertexShader = "Probes/SyntheticSoft.vert.glsl";
    private const string SoftFragmentShader = "Probes/SyntheticSoft.frag.glsl";

    private readonly float hdrIntensity;
    private readonly NormalizedRect emissiveQuad;
    private readonly NormalizedRect softQuad;
    private readonly object gate = new();

    private ProbeDrawResources? emissiveResources;
    private ProbeDrawResources? softResources;
    private bool armed;
    private bool disposed;
    private int drawsRecorded;

    // Requires: hdrIntensity > 1; quads given in normalized screen space [0,1]^2 (origin top-left).
    public SyntheticHdrQuadContributor(float hdrIntensity, NormalizedRect emissiveQuad, NormalizedRect softQuad)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(hdrIntensity, 1f);
        this.hdrIntensity = hdrIntensity;
        this.emissiveQuad = emissiveQuad;
        this.softQuad = softQuad;
    }

    public string Name => "RimLumen.Probe.SyntheticHdrQuad";

    public bool HasActiveWork => Volatile.Read(ref armed);

    public NormalizedRect EmissiveQuad => emissiveQuad;
    public NormalizedRect SoftQuad => softQuad;
    public int DrawsRecorded => Volatile.Read(ref drawsRecorded);

    public void Arm()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            armed = true;
        }
    }

    public void Disarm()
    {
        lock (gate)
        {
            armed = false;
        }
    }

    public void CaptureMainThread(in LumenCaptureContext context)
    {
        if (!HasActiveWork) return;
        lock (gate)
        {
            if (emissiveResources is not null) return;
            EngineApplication app = EngineApplication.Instance ?? throw new InvalidOperationException("Waylith 主循环尚未创建。");
            if (app.GraphicsDevice is null) return;
            DeviceEx device = app.Device;
            int pushBytes = Marshal.SizeOf<QuadPush>();
            emissiveResources = new ProbeDrawResources(device, EmissiveVertexShader, EmissiveFragmentShader,
                depthTest: false, pushBytes, descriptorType: null, setCount: 0);
            softResources = new ProbeDrawResources(device, SoftVertexShader, SoftFragmentShader,
                depthTest: true, pushBytes, VkDescriptorType.CombinedImageSampler, app.RenderContext!.FramesInFlight);
        }
    }

    // Ensures: no compute work (no-op).
    public void EnqueuePreCompute(in LumenComputeContext context)
    {
    }

    // Ensures: no buffer barriers (no-op).
    public void RecordBufferBarriers(in LumenBarrierContext context)
    {
    }

    // Requires: context.SceneColorDepthSource non-null for the soft quad; else the soft quad is skipped.
    public void RecordHdrDraws(in LumenHdrDrawContext context)
    {
        if (!HasActiveWork) return;
        ProbeDrawResources? emissive;
        ProbeDrawResources? soft;
        lock (gate)
        {
            emissive = emissiveResources;
            soft = softResources;
        }
        if (emissive is null || soft is null) return;

        QuadPush push = new()
        {
            Rect = new Vector4(emissiveQuad.X, emissiveQuad.Y, emissiveQuad.Width, emissiveQuad.Height),
            Color = new Vector4(hdrIntensity, 0.55f * hdrIntensity, 0.15f * hdrIntensity, 1f),
            Depth = context.SoftParticleDepthParams
        };
        FrameContext frame = context.Frame;
        emissive.Bind(in frame, -1);
        emissive.Push(in frame, in push);
        frame.CommandBuffer.Draw(6, 1, 0, 0);
        System.Threading.Interlocked.Increment(ref drawsRecorded);

        RenderTexture? scene = context.SceneColorDepthSource;
        if (scene is null) return;

        // Submission slot reuse follows the engine fence, unlike a capture-ordinal modulo after skipped frames.
        int setIndex = frame.SubmissionFrameIndex;
        VkImageView view = scene.DepthSampleView;
        soft.WriteDepth(setIndex, in view, Waylith.Rendering.Compute.ComputeDispatchQueue.PointClampSampler);
        push.Rect = new Vector4(softQuad.X, softQuad.Y, softQuad.Width, softQuad.Height);
        push.Color = new Vector4(0.15f, 0.45f, 1.4f, 0.75f);
        soft.Bind(in frame, setIndex);
        soft.Push(in frame, in push);
        frame.CommandBuffer.Draw(6, 1, 0, 0);
        System.Threading.Interlocked.Increment(ref drawsRecorded);
    }

    public void Dispose()
    {
        ProbeDrawResources? emissive;
        ProbeDrawResources? soft;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            armed = false;
            emissive = emissiveResources;
            soft = softResources;
            emissiveResources = null;
            softResources = null;
        }
        if (emissive is null && soft is null) return;
        EngineApplication? app = EngineApplication.Instance;
        if (app?.GraphicsDevice is null) return;
        app.WaitForRenderThreadIdle();
        app.GraphicsDevice.WaitIdleAndRun(() =>
        {
            emissive?.DestroyAfterIdle();
            soft?.DestroyAfterIdle();
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QuadPush
    {
        public Vector4 Rect;
        public Vector4 Color;
        public Vector4 Depth;
    }
}

public readonly record struct NormalizedRect(float X, float Y, float Width, float Height);
