using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Lighting;
using Waylith.Graphics;
using Waylith.Rendering;
using Waylith.Rendering.PostFx;

namespace RimLumen.Core.Rendering;

internal sealed class RimLumenPreBackbufferFeature : IRenderFeature, IEarlyMainThreadRenderFeature
{
    private readonly RimLumenPipeline owner;
    private readonly HdrPass pass;

    internal RimLumenPreBackbufferFeature(RimLumenPipeline owner)
    {
        this.owner = owner;
        pass = new HdrPass(owner);
        Passes = new IRenderPass[] { pass };
    }

    public bool IsActive => owner.Captured != null;
    public IReadOnlyList<IRenderPass> Passes { get; }
    public void BeginMainThreadFrame(in FrameTiming timing)
    {
        if (RimLumenRenderHost.FailSafeTripped) return;
        try { owner.BeginCapture(in timing); }
        catch (Exception ex) { owner.TripRenderFailSafe(ex); }
    }

    public object? CaptureFrame(in FrameTiming timing)
    {
        if (!RimLumenRenderHost.FailSafeTripped)
        {
            try { return owner.Capture(in timing); }
            catch (Exception ex) { owner.TripRenderFailSafe(ex); }
        }
        owner.Captured = null;
        return null;
    }

    public void OnRenderTargetRebuilt(IRenderTarget renderTarget) => pass.OnRenderTargetRebuilt(renderTarget);
    internal void Dispose() => pass.Dispose();

    private sealed class HdrPass : IRenderPass
    {
        private readonly RimLumenPipeline owner;
        private readonly MeshBatchRenderer renderer;
        private readonly PostEffectPass depthCopy = new("RimLumenDepthCopy", "PostFx/RimLumenDepthCopy.frag.glsl", new[] { 0 });
        // Render thread only: what RecordHdr has opened in the command buffer, so the fail-safe can leave it balanced.
        private bool labelOpen;
        private bool renderPassOpen;

        internal HdrPass(RimLumenPipeline owner)
        {
            this.owner = owner;
            renderer = new MeshBatchRenderer(owner.App.GraphicsDevice!, owner.App.RenderContext!);
        }

        public byte PassId => RimLumenRenderHost.LumenRenderPassId;
        public PassAttachment? Attachment => null;
        public bool PreBackbuffer => true;

        public void Record(in FrameContext ctx, object? featurePayload)
        {
            if (featurePayload is not PipelineFrame payload || RimLumenRenderHost.FailSafeTripped) return;
            try
            {
                if (!payload.RenderHdr)
                {
                    var barrierContext = new LumenBarrierContext { Frame = ctx, FrameIndex = payload.Index };
                    foreach (ILumenFrameContributor contributor in payload.Contributors)
                        contributor.RecordBufferBarriers(in barrierContext);
                    return;
                }
                RecordHdr(in ctx, payload);
            }
            catch (Exception ex)
            {
                owner.TripRenderFailSafe(ex);
            }
            finally
            {
                // The engine keeps recording into this command buffer: close what the failed record left open (best effort).
                try
                {
                    if (renderPassOpen) ctx.CommandBuffer.EndRenderPass();
                    if (labelOpen) ctx.CommandBuffer.EndDebugLabel();
                }
                catch (Exception ex)
                {
                    // Closing a half-recorded pass must not mask the original failure; report it from the main thread.
                    owner.App.FrameRenderer.RenderToMainThread.Enqueue(() =>
                        Verse.Log.Error($"[RimLumen] Closing the failed HDR record left the command buffer unbalanced: {ex}"));
                }
                renderPassOpen = false;
                labelOpen = false;
            }
        }

        private unsafe void RecordHdr(in FrameContext ctx, PipelineFrame payload)
        {
            RenderTexture scene = owner.SceneComposite.Source?.Invoke(ctx.SubmissionFrameIndex)
                ?? throw new InvalidOperationException("Active RimLumen frame has no scene color/depth source.");
            if (!scene.HasDepth) throw new InvalidOperationException("RimLumen requires scene depth.");
            HdrTargets.Slot slot = owner.Targets.Ensure(ctx.SubmissionFrameIndex, scene, payload.BloomMipLevels);
            if (payload.HasSceneDepth)
            {
                HdrDepthSnapshot.Record(in ctx, scene, slot.DepthSnapshot, depthCopy);
                // Particle materials sample the snapshot through this non-owning proxy; rebind every frame so a
                // rebuilt target never leaves a stale image behind.
                owner.DepthProxy.Bind(slot.DepthSnapshot);
            }
            MapLightFieldCollector.PrepareForRender(in ctx);
            // Screen size in the UBO is the scene target's, not the swapchain extent: a render-scale
            // difference would desync custom pack passes from the built-in composite push constants.
            ShaderPackUniforms frameUniforms = payload.Uniforms;
            frameUniforms.ScreenAndMapSize = new System.Numerics.Vector4(
                1f / scene.Width, 1f / scene.Height, frameUniforms.ScreenAndMapSize.Z, frameUniforms.ScreenAndMapSize.W);
            payload.Uniforms = frameUniforms;
            var frame = new FrameContext(ctx.RenderContext, ctx.Resources, ctx.CommandBuffer, owner.Targets.RenderPass,
                slot.Framebuffer, slot.Color.Extent, VkSampleCountFlags._1Bit, VkFormat.R16G16B16A16SFloat, scene.DepthFormat);
            frame.CommandBuffer.BeginDebugLabel("RimLumen/HDR\0"u8, new float4(1f, 0.5f, 0f, 1f));
            labelOpen = true;
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
            renderPassOpen = true;
            VkViewport viewport = new() { Width = frame.Extent.Width, Height = frame.Extent.Height, MinDepth = 0, MaxDepth = 1 };
            frame.CommandBuffer.SetViewport(0, new ReadOnlySpan<VkViewport>(in viewport));
            VkRect2D scissor = new(frame.Extent);
            frame.CommandBuffer.SetScissor(0, new ReadOnlySpan<VkRect2D>(in scissor));
            var drawContext = new LumenHdrDrawContext
            {
                Frame = frame, FrameIndex = payload.Index, HdrColorTarget = slot.Color,
                CurrentMapIndex = payload.MapIndex,
                SceneColorDepthSource = payload.HasSceneDepth ? slot.DepthSnapshot : null,
                ViewProjection = payload.Mesh.View.ViewProjection,
                SoftParticleDepthParams = payload.DepthParams
            };
            foreach (ILumenFrameContributor contributor in payload.Contributors)
                contributor.RecordHdrDraws(in drawContext);
            if (payload.Mesh.Draws.Count > 0)
            {
                renderer.InvalidateCommandBufferBindings();
                renderer.RecordPass(in frame, payload.Mesh, PassId);
            }
            frame.CommandBuffer.EndRenderPass();
            renderPassOpen = false;
            slot.Color.Color.SetMipLayout(0, VkImageLayout.ShaderReadOnlyOptimal);
            ShaderPackUniforms uniforms = payload.Uniforms;
            // Custom pack data must never take the whole pipeline down: a non-default pack failure reverts the pack
            // (main thread) and only the built-in default may trip the fail-safe.
            ShaderPackSpec packSpec = owner.ShaderPack.Spec;
            try
            {
                owner.ShaderPack.RecordOffscreen(in ctx, slot, scene, in uniforms, payload.HasHdrDraws, payload.HasSceneDepth);
            }
            catch (Exception ex)
            {
                if (RimLumenRenderHost.ShouldRevertPackOnFailure(packSpec))
                    RimLumenRenderHost.ReportRuntimePackFailure(packSpec, ex);
                else
                    owner.TripRenderFailSafe(ex);
                return;
            }
            frame.CommandBuffer.EndDebugLabel();
            labelOpen = false;
            owner.Recorded();
        }

        public void OnRenderTargetRebuilt(IRenderTarget target)
        {
            owner.Targets.Dispose();
            renderer.OnRenderContextRebuilt(owner.App.RenderContext!);
            depthCopy.Invalidate();
            owner.ShaderPack.InvalidateOffscreen();
        }

        public void Dispose()
        {
            renderer.Dispose();
            depthCopy.Dispose();
        }
    }
}
