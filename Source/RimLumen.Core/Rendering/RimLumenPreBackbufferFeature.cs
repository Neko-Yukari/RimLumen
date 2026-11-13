using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.Numerics;
using RimLumen.Core.Contracts;
using Waylith.Graphics;
using Waylith.Rendering;
using Waylith.Rendering.PostFx;

namespace RimLumen.Core.Rendering;

internal sealed class RimLumenPreBackbufferFeature : IRenderFeature, IEarlyMainThreadRenderFeature
{
    private readonly RimLumenPipeline owner;
    private readonly HdrPass pass;
    internal long ActiveFrames;
    internal long Clears;
    internal long RoutedDrawItems;

    internal RimLumenPreBackbufferFeature(RimLumenPipeline owner)
    {
        this.owner = owner;
        pass = new HdrPass(this, owner);
        Passes = new IRenderPass[] { pass };
    }

    public bool IsActive => owner.Captured != null;
    public IReadOnlyList<IRenderPass> Passes { get; }
    public void BeginMainThreadFrame(in FrameTiming timing) => owner.BeginCapture(in timing);
    public object? CaptureFrame(in FrameTiming timing) => owner.Capture(in timing);
    public void OnRenderTargetRebuilt(IRenderTarget renderTarget) => pass.OnRenderTargetRebuilt(renderTarget);
    internal void Dispose() => pass.Dispose();

    private sealed class HdrPass : IRenderPass
    {
        private readonly RimLumenPreBackbufferFeature feature;
        private readonly RimLumenPipeline owner;
        private readonly MeshBatchRenderer renderer;
        private readonly PostEffectPass[] down = new PostEffectPass[5];
        private readonly PostEffectPass[] up = new PostEffectPass[4];

        internal HdrPass(RimLumenPreBackbufferFeature feature, RimLumenPipeline owner)
        {
            this.feature = feature;
            this.owner = owner;
            renderer = new MeshBatchRenderer(owner.App.GraphicsDevice!, owner.App.RenderContext!);
            for (int i = 0; i < down.Length; i++)
                down[i] = new PostEffectPass($"RimLumenBloomDown{i}", i == 0 ? "RimLumenBloomPrefilter.frag.glsl" : "RimLumenBloomDown.frag.glsl",
                    new[] { 0 }, pushConstantSize: 16);
            for (int i = 0; i < up.Length; i++)
                up[i] = new PostEffectPass($"RimLumenBloomUp{i}", "RimLumenBloomUp.frag.glsl", new[] { 0, 1 }, pushConstantSize: 16);
        }

        public byte PassId => RimLumenRenderHost.LumenRenderPassId;
        public PassAttachment? Attachment => null;
        public bool PreBackbuffer => true;

        public unsafe void Record(in FrameContext ctx, object? featurePayload)
        {
            if (featurePayload is not PipelineFrame payload) return;
            RenderTexture scene = owner.SceneComposite.Source?.Invoke(ctx.SubmissionFrameIndex)
                ?? throw new InvalidOperationException("Active RimLumen frame has no scene color/depth source.");
            if (!scene.HasDepth) throw new InvalidOperationException("RimLumen requires scene depth.");
            HdrTargets.Slot slot = owner.Targets.Ensure(ctx.SubmissionFrameIndex, scene);
            var frame = new FrameContext(ctx.RenderContext, ctx.Resources, ctx.CommandBuffer, owner.Targets.RenderPass,
                slot.Framebuffer, slot.Color.Extent, VkSampleCountFlags._1Bit, VkFormat.R16G16B16A16SFloat, scene.DepthFormat);
            frame.CommandBuffer.BeginDebugLabel("RimLumen/HDR\0"u8, new float4(1f, 0.5f, 0f, 1f));
            var barrierContext = new LumenBarrierContext { Frame = frame, FrameIndex = payload.Index };
            foreach (ILumenFrameContributor contributor in payload.Contributors)
                contributor.RecordBufferBarriers(in barrierContext);
            VkClearValue clear = default;
            VkRenderPassBeginInfo begin = new()
            {
                RenderPass = frame.RenderPass, Framebuffer = frame.Framebuffer,
                RenderArea = new VkRect2D(frame.Extent), ClearValueCount = 1, ClearValues = &clear
            };
            frame.CommandBuffer.BeginRenderPass(in begin, VkSubpassContents.Inline);
            VkViewport viewport = new() { Width = frame.Extent.Width, Height = frame.Extent.Height, MinDepth = 0, MaxDepth = 1 };
            frame.CommandBuffer.SetViewport(0, new ReadOnlySpan<VkViewport>(in viewport));
            VkRect2D scissor = new(frame.Extent);
            frame.CommandBuffer.SetScissor(0, new ReadOnlySpan<VkRect2D>(in scissor));
            var drawContext = new LumenHdrDrawContext
            {
                Frame = frame, FrameIndex = payload.Index, HdrColorTarget = slot.Color,
                SceneColorDepthSource = scene, ViewProjection = payload.Mesh.View.ViewProjection,
                SoftParticleDepthParams = payload.DepthParams
            };
            owner.L1Renderer.BeginRecord(in drawContext);
            try
            {
                foreach (ILumenFrameContributor contributor in payload.Contributors)
                    contributor.RecordHdrDraws(in drawContext);
            }
            finally { owner.L1Renderer.EndRecord(); }
            if (payload.Mesh.Draws.Count > 0)
            {
                renderer.InvalidateCommandBufferBindings();
                renderer.RecordPass(in frame, payload.Mesh, PassId);
            }
            frame.CommandBuffer.EndRenderPass();
            slot.Color.Color.SetMipLayout(0, VkImageLayout.ShaderReadOnlyOptimal);
            RecordBloom(in ctx, slot);
            frame.CommandBuffer.EndDebugLabel();
            Interlocked.Increment(ref feature.ActiveFrames);
            Interlocked.Increment(ref feature.Clears);
            Interlocked.Add(ref feature.RoutedDrawItems, payload.Mesh.Draws.Count);
            owner.Recorded(ctx.SubmissionFrameIndex);
        }

        private void RecordBloom(in FrameContext ctx, HdrTargets.Slot slot)
        {
            RenderTexture input = slot.Color;
            for (int i = 0; i < slot.Down.Length; i++)
            {
                Vector4 parameters = new(1f / input.Width, 1f / input.Height, 1f, 0.5f);
                down[i].SetInput(0, input);
                down[i].SetPushConstants(in parameters);
                down[i].Output = slot.Down[i];
                down[i].Record(in ctx, null);
                input = slot.Down[i];
            }
            for (int i = slot.Up.Length - 1; i >= 0; i--)
            {
                Vector4 parameters = new(1f / input.Width, 1f / input.Height, 0.65f, 0f);
                up[i].SetInput(0, input);
                up[i].SetInput(1, slot.Down[i]);
                up[i].SetPushConstants(in parameters);
                up[i].Output = slot.Up[i];
                up[i].Record(in ctx, null);
                input = slot.Up[i];
            }
        }

        public void OnRenderTargetRebuilt(IRenderTarget target)
        {
            owner.Targets.Dispose();
            renderer.OnRenderContextRebuilt(owner.App.RenderContext!);
            foreach (PostEffectPass effect in down) effect.Invalidate();
            foreach (PostEffectPass effect in up) effect.Invalidate();
        }

        public void Dispose()
        {
            renderer.Dispose();
            foreach (PostEffectPass effect in down) effect.Dispose();
            foreach (PostEffectPass effect in up) effect.Dispose();
        }
    }
}
