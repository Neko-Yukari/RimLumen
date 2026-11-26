using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RimLumen.Core.ParticlesGpu;
using RimLumen.Core.Rendering;
using Verse;
using Waylith;
using Waylith.Graphics.Meshes;
using Waylith.Rendering;
using Waylith.Rendering.Compute;

namespace RimLumen.Core.Showcase;

// Dev-only switch for the L2 velocity/force arrows. The first enable builds the GPU resources on the main thread; a
// failure is logged and leaves the arrows off.
internal static class ShowcaseForceArrows
{
    private static volatile ShowcaseForceArrowRenderer? renderer;

    internal static bool Enabled { get; private set; }

    // Non-null once enabled at least once; the render thread only acts on frames the main thread armed.
    internal static ShowcaseForceArrowRenderer? Renderer => renderer;

    // Requires: main thread with a live graphics device.
    // Ensures: returns the resulting state; enabling fails (false, Log.Error) when the dev shader sources are absent or rejected.
    internal static bool SetEnabled(bool enabled)
    {
        if (enabled && renderer is null)
        {
            try
            {
                renderer = new ShowcaseForceArrowRenderer(EngineApplication.Instance);
            }
            catch (Exception ex)
            {
                Log.Error($"[RimLumen] Force arrows unavailable: {ex}");
                return Enabled = false;
            }
        }
        Enabled = enabled;
        Log.Message($"[RimLumen] Force arrows = {enabled}");
        return enabled;
    }
}

// Per sampled L2 particle, a compute pass (LumenForceArrows.comp.glsl) splits velocity and the field forces through
// ParticleForces.glsl into arrows in one device-local buffer, which one instanced draw per emitter reads in the HDR
// pass. Nothing is read back. The buffer is shared across frames: the compute pass waits for earlier vertex reads.
internal sealed class ShowcaseForceArrowRenderer
{
    // Mirrors LumenForceArrowTypes.glsl.
    private const int Samples = 32;
    private const int Channels = 7;
    private const int ArrowBytes = 48;
    private const int ArrowsPerEmitter = Samples * Channels;
    // Emitters past this many rows get no arrows.
    private const int MaxEmitters = 64;
    private const int RenderQueue = 3300;
    private const string ComputeFile = "LumenForceArrows.comp.glsl";
    private const string VertexFile = "LumenForceArrow.vert.glsl";
    private const string FragmentFile = "LumenForceArrow.frag.glsl";

    private static readonly ComputeBinding[] Bindings =
    {
        new(0, ComputeBindingType.StorageBuffer),
        new(1, ComputeBindingType.StorageBuffer),
        new(2, ComputeBindingType.StorageBuffer),
        new(3, ComputeBindingType.StorageBuffer)
    };
    private static readonly BlendState Premultiplied = new(Enabled: true, BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add,
        BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add);
    private static readonly int DrawId = Shader.PropertyToID("_LumenArrowDraw");
    private static readonly int CameraRightId = Shader.PropertyToID("_LumenCameraRight");
    private static readonly int CameraUpId = Shader.PropertyToID("_LumenCameraUp");
    private static readonly int ArrowsId = Shader.PropertyToID("_LumenForceArrows");

    [StructLayout(LayoutKind.Sequential)]
    private struct Push
    {
        public uint EmitterCount;
        public uint Padding0;
        public uint Padding1;
        public uint Padding2;
    }

    private readonly ComputeKernel kernel;
    private readonly VkDescriptorSetLayout layout;
    private readonly VkDescriptorPool[] pools;
    // Capture index whose draws read the arrows, per pool slot; the render thread dispatches only for that index.
    private readonly ulong[] armed;
    private readonly GpuComputeBuffer arrows;
    private readonly GpuComputeBuffer[] computeBuffers = new GpuComputeBuffer[4];
    private readonly InstanceStorageBinding[] storage;
    private readonly Mesh mesh;
    private readonly Material material;

    // Requires: main thread with a live graphics device; the mod runs from the repository (dev build).
    internal ShowcaseForceArrowRenderer(EngineApplication app)
    {
        string directory = ShaderDirectory();
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Force-arrow shaders live in the source tree, not found at {directory}.");
        ShaderSourceLoader.RegisterShaderSourceDirectory(directory);
        DeviceEx device = app.Device;
        kernel = ComputeKernelCache.Load(ComputeFile, "main", Bindings, Marshal.SizeOf<Push>());
        material = new Material(ParticleMeshShaders.CreateSingle("RimLumen.ForceArrows", VertexFile, FragmentFile,
            depthTest: false, RenderQueue, Premultiplied), "RimLumen.ForceArrows");
        var bindings = new VkDescriptorSetLayoutBinding[Bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
            bindings[i] = ParticleGpuDrawResources.Binding(i, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit);
        layout = ParticleGpuDrawResources.CreateLayout(device, bindings);
        // Same reuse distance as the engine's frame slots: a pool is reset only after its submission retired.
        int ring = 2 * app.RenderContext!.FramesInFlight + 2;
        pools = new VkDescriptorPool[ring];
        for (int i = 0; i < ring; i++) pools[i] = ParticleGpuDrawResources.CreateComputePool(device, Bindings.Length);
        armed = new ulong[ring];
        Array.Fill(armed, ulong.MaxValue);
        arrows = GpuComputeBuffer.Create(device, app.GraphicsDevice!.Allocator, "RimLumen.ForceArrows",
            (long)MaxEmitters * ArrowsPerEmitter * ArrowBytes, deviceLocal: true);
        storage = new[] { new InstanceStorageBinding(ArrowsId, arrows) };
        mesh = CreateArrowMesh();
        app.BeforeGraphicsShutdown += Dispose;
    }

    // The dev mod root is the repository: 1.7/Assemblies/../../Source/RimLumen.Core/Showcase/Shaders.
    private static string ShaderDirectory() => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(typeof(ShowcaseForceArrowRenderer).Assembly.Location)!, "..", "..", "Source", "RimLumen.Core", "Showcase", "Shaders"));

    // Requires: main thread, in AppendHdrDraws after the frame's particle draws.
    // Ensures: one instanced arrow draw per visible emitter row below MaxEmitters, and arms this capture's compute pass.
    internal void AppendDraws(ParticleFrame snapshot, List<FinalDrawItem> draws, DrawItemFactory maker, DrawPropertyArena arena,
        int queueOrder)
    {
        CameraBillboardBasis.Extract(snapshot.Context.ViewMatrix, out Vector3 right, out Vector3 up, out float yScaleRatio);
        float topDown2D = CameraBillboardBasis.IsStraightTopDown2D(right, up, yScaleRatio) ? 1f : 0f;
        float vanillaMoteY = Altitudes.AltitudeFor(AltitudeLayer.MoteOverhead);
        int count = Math.Min(snapshot.Emitters.Length, MaxEmitters);
        for (int i = 0; i < count; i++)
        {
            ParticleEmitterFrame emitter = snapshot.Emitters[i];
            if (!emitter.IsVisibleOnMap(snapshot.Context.CurrentMapIndex)) continue;
            Span<DrawProperty> properties = arena.Allocate(3, out DrawPropertyRange range);
            properties[0] = DrawProperty.Vector(DrawId,
                new Vector4(i * ArrowsPerEmitter, emitter.IsVertical3D ? 1f : 0f, topDown2D, vanillaMoteY));
            properties[1] = DrawProperty.Vector(CameraRightId, new Vector4(right, 0f));
            properties[2] = DrawProperty.Vector(CameraUpId, new Vector4(up, yScaleRatio));
            var command = new MeshDrawCommand(mesh, material, range, Matrix4x4.Identity, 0, RimLumenRenderHost.LumenRenderPassId,
                InstancedDrawInfo.Direct(ArrowsPerEmitter, storage));
            FinalDrawItem? draw = maker.TryMake(in command, arena, UnityEngine.Shader.GlobalKeywordKey, queueOrder + i);
            if (draw is not null) draws.Add(draw);
        }
        ulong index = snapshot.Context.FrameIndex;
        Volatile.Write(ref armed[(int)(index % (ulong)armed.Length)], index);
    }

    // Requires: render thread, after the simulation's exit barrier of the same capture.
    // Ensures: when AppendDraws armed this capture, fills the arrow buffer and makes it visible to the vertex stage.
    internal void RecordCompute(in FrameContext frame, ParticleFrame snapshot, GpuComputeBuffer particles, ParticleGpuFrame resources)
    {
        ulong index = snapshot.Context.FrameIndex;
        int slot = (int)(index % (ulong)armed.Length);
        if (Volatile.Read(ref armed[slot]) != index) return;
        int count = Math.Min(snapshot.Emitters.Length, MaxEmitters);
        frame.Device.ResetDescriptorPool(pools[slot], 0);
        VkDescriptorSet set = DescriptorSetWriter.AllocateDescriptorSet(frame.Device, pools[slot], layout);
        computeBuffers[0] = particles;
        computeBuffers[1] = resources.Emitters;
        computeBuffers[2] = resources.Fields;
        computeBuffers[3] = arrows;
        ParticleGpuDrawResources.WriteBuffers(frame.Device, set, computeBuffers, VkDescriptorType.StorageBuffer);
        Span<VkBufferMemoryBarrier> entry = stackalloc VkBufferMemoryBarrier[2];
        entry[0] = Barrier(particles, VkAccessFlags.ShaderWriteBit, VkAccessFlags.ShaderReadBit);
        entry[1] = Barrier(arrows, VkAccessFlags.ShaderReadBit, VkAccessFlags.ShaderWriteBit);
        frame.CommandBuffer.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.VertexShaderBit,
            VkPipelineStageFlags.ComputeShaderBit, VkDependencyFlags.None, default(ReadOnlySpan<VkMemoryBarrier>), entry,
            default(ReadOnlySpan<VkImageMemoryBarrier>));
        frame.CommandBuffer.BindPipeline(VkPipelineBindPoint.Compute, kernel.Pipeline);
        frame.CommandBuffer.BindDescriptorSet(VkPipelineBindPoint.Compute, kernel.PipelineLayout, 0, set, default(Span<ByteSize32>));
        var push = new Push { EmitterCount = (uint)count };
        frame.CommandBuffer.PushConstants(kernel.PipelineLayout, VkShaderStageFlags.ComputeBit, 0,
            MemoryMarshal.AsBytes(new ReadOnlySpan<Push>(in push)));
        frame.CommandBuffer.Dispatch((count * Samples + 63) / 64, 1, 1);
        var exit = Barrier(arrows, VkAccessFlags.ShaderWriteBit, VkAccessFlags.ShaderReadBit);
        frame.CommandBuffer.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.VertexShaderBit,
            VkDependencyFlags.None, default(ReadOnlySpan<VkMemoryBarrier>), new ReadOnlySpan<VkBufferMemoryBarrier>(in exit),
            default(ReadOnlySpan<VkImageMemoryBarrier>));
    }

    private static VkBufferMemoryBarrier Barrier(GpuComputeBuffer buffer, VkAccessFlags source, VkAccessFlags destination) => new()
    {
        SrcAccessMask = source, DstAccessMask = destination, SrcQueueFamilyIndex = -1, DstQueueFamilyIndex = -1,
        Buffer = buffer.Buffer, Offset = ByteSize.Zero, Size = buffer.Size
    };

    // Shaft quad (tail -> head base) and head triangle (head base -> tip); see LumenForceArrow.vert.glsl for the encoding.
    private static Mesh CreateArrowMesh()
    {
        Mesh created = new() { DebugName = "RimLumen.ForceArrow" };
        MeshDataArray data = Mesh.AllocateWritableMeshData(1);
        bool applied = false;
        try
        {
            MeshData writable = data[0];
            writable.SetVertexBufferParams(7, new[]
            {
                new VertexAttribute(VertexAttributeSemantic.Position, VertexAttributeFormat.Float32, 3, 0, 0)
            });
            Span<Vector3> positions = writable.GetVertexData<Vector3>().AsSpan();
            positions[0] = new Vector3(0f, -1f, 0f);
            positions[1] = new Vector3(1f, -1f, 0f);
            positions[2] = new Vector3(0f, 1f, 0f);
            positions[3] = new Vector3(1f, 1f, 0f);
            positions[4] = new Vector3(1f, -1f, 1f);
            positions[5] = new Vector3(1f, 1f, 1f);
            positions[6] = new Vector3(2f, 0f, 1f);
            writable.SetIndexBufferParams(9, MeshIndexFormat.UInt32);
            Span<uint> indices = writable.GetIndexData<uint>().AsSpan();
            indices[0] = 0; indices[1] = 1; indices[2] = 2;
            indices[3] = 1; indices[4] = 3; indices[5] = 2;
            indices[6] = 4; indices[7] = 6; indices[8] = 5;
            writable.subMeshCount = 1;
            writable.SetSubMesh(0, new SubMesh(0, 9));
            Mesh.ApplyAndDisposeWritableMeshData(data, created);
            applied = true;
        }
        finally
        {
            if (!applied) data.Dispose();
        }
        return created;
    }

    private void Dispose(EngineApplication app)
    {
        app.WaitForRenderThreadIdle();
        app.Device.WaitIdle();
        DeviceEx device = app.Device;
        foreach (VkDescriptorPool pool in pools) device.DestroyDescriptorPool(pool, device.VkAllocator);
        device.DestroyDescriptorSetLayout(layout, device.VkAllocator);
        arrows.Dispose();
        material.Dispose();
        mesh.Dispose();
    }
}
