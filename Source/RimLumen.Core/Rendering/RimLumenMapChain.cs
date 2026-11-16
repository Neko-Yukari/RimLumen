using System;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RimLumen.Core.Contracts;
using RimLumen.Core.Lighting;
using RimWorldWaylith.Rendering;
using Waylith.Graphics;
using Waylith.Rendering;

namespace RimLumen.Core.Rendering;

internal sealed class RimLumenMapChain(RimLumenPipeline owner) : IDisposable
{
    private readonly MeshBatchRenderer renderer = new(owner.App.GraphicsDevice!, owner.App.RenderContext!);
    private readonly MeshBatchRenderer glowRenderer = new(owner.App.GraphicsDevice!, owner.App.RenderContext!);
    private readonly OffscreenPostEffectPass depthCopy = new("RimLumenDepthCopy", "PostFx/RimLumenDepthCopy.frag.glsl", new[] { 0 });
    private bool labelOpen;
    private bool renderPassOpen;

    internal void Record(in FrameContext ctx, PipelineFrame payload, SceneColorTargets targets)
    {
        try
        {
            if (!payload.RenderHdr)
            {
                var barriers = new LumenBarrierContext { Frame = ctx, FrameIndex = payload.Index };
                foreach (ILumenFrameContributor contributor in payload.Contributors) contributor.RecordBufferBarriers(in barriers);
                return;
            }
            RecordChain(in ctx, payload, targets);
        }
        catch (Exception ex) { owner.TripRenderFailSafe(ex); }
        finally
        {
            try
            {
                if (renderPassOpen) ctx.CommandBuffer.EndRenderPass();
                if (labelOpen) ctx.CommandBuffer.EndDebugLabel();
            }
            catch (Exception ex)
            {
                owner.App.FrameRenderer.RenderToMainThread.Enqueue(() =>
                    Verse.Log.Error($"[RimLumen] Closing the failed map record left the command buffer unbalanced: {ex}"));
            }
            renderPassOpen = false;
            labelOpen = false;
        }
    }

    private void RecordChain(in FrameContext ctx, PipelineFrame payload, SceneColorTargets targets)
    {
        RenderTexture scene = targets.Current(ctx.SubmissionFrameIndex);
        if (!scene.HasDepth) throw new InvalidOperationException("RimLumen requires scene depth at the map split.");
        PassAttachment attachment = targets.LoadAttachment
            ?? throw new InvalidOperationException("SceneColorTargets.LoadAttachment is unavailable at the map split.");
        HdrTargets.Slot slot = owner.Targets.Ensure(ctx.SubmissionFrameIndex, scene, payload.BloomMipLevels);
        ctx.CommandBuffer.BeginDebugLabel("RimLumen/MapSplit\0"u8, new float4(1f, 0.5f, 0f, 1f));
        labelOpen = true;
        SceneColorSnapshot.Record(ctx.CommandBuffer, scene, slot.SceneSnapshot);
        if (payload.HasSceneDepth)
        {
            HdrDepthSnapshot.Record(in ctx, scene, slot.DepthSnapshot, depthCopy);
            owner.DepthProxy.Bind(slot.DepthSnapshot);
        }
        MapLightFieldCollector.PrepareForRender(in ctx);
        ShaderPackUniforms uniforms = payload.Uniforms;
        uniforms.ScreenAndMapSize = new System.Numerics.Vector4(
            1f / scene.Width, 1f / scene.Height, uniforms.ScreenAndMapSize.Z, uniforms.ScreenAndMapSize.W);
        var frame = new FrameContext(ctx.RenderContext, ctx.Resources, ctx.CommandBuffer, owner.Targets.RenderPass,
            slot.Framebuffer, slot.Color.Extent, VkSampleCountFlags._1Bit, VkFormat.R16G16B16A16SFloat, scene.DepthFormat);
        var barrierContext = new LumenBarrierContext { Frame = frame, FrameIndex = payload.Index };
        foreach (ILumenFrameContributor contributor in payload.Contributors) contributor.RecordBufferBarriers(in barrierContext);
        if (payload.HasGlowDraws)
        {
            var glowFrame = new FrameContext(ctx.RenderContext, ctx.Resources, ctx.CommandBuffer, owner.Targets.RenderPass,
                slot.GlowFramebuffer, slot.Glow.Extent, VkSampleCountFlags._1Bit, VkFormat.R16G16B16A16SFloat, scene.DepthFormat);
            BeginPass(in glowFrame, clear: true);
            glowRenderer.InvalidateCommandBufferBindings();
            glowRenderer.RecordPass(in glowFrame, payload.GlowMesh, RimLumenRenderHost.LumenRenderPassId);
            EndPass(in glowFrame);
            slot.Glow.Color.SetMipLayout(0, VkImageLayout.ShaderReadOnlyOptimal);
        }
        BeginPass(in frame, clear: true);
        var draw = new LumenHdrDrawContext
        {
            Frame = frame, FrameIndex = payload.Index, HdrColorTarget = slot.Color,
            CurrentMapIndex = payload.MapIndex, SceneColorDepthSource = payload.HasSceneDepth ? slot.DepthSnapshot : null,
            ViewProjection = payload.Mesh.View.ViewProjection, SoftParticleDepthParams = payload.DepthParams
        };
        foreach (ILumenFrameContributor contributor in payload.Contributors) contributor.RecordHdrDraws(in draw);
        if (payload.Mesh.Draws.Count > 0)
        {
            renderer.InvalidateCommandBufferBindings();
            renderer.RecordPass(in frame, payload.Mesh, RimLumenRenderHost.LumenRenderPassId);
        }
        EndPass(in frame);
        slot.Color.Color.SetMipLayout(0, VkImageLayout.ShaderReadOnlyOptimal);
        ShaderPackSpec pack = owner.ShaderPack.Spec;
        try
        {
            owner.ShaderPack.RecordOffscreen(in ctx, slot, slot.SceneSnapshot, in uniforms,
                payload.HasHdrDraws, payload.HasGlowDraws, payload.HasSceneDepth);
            FrameContext composite = attachment.MakeFrameContext(in ctx);
            BeginPass(in composite, clear: false);
            try
            {
                owner.ShaderPack.RecordComposite(in composite, slot, slot.SceneSnapshot, in uniforms,
                    payload.HasHdrDraws, payload.HasGlowDraws, payload.HasSceneDepth);
            }
            finally { EndPass(in composite); }
        }
        catch (Exception ex)
        {
            if (RimLumenRenderHost.ShouldRevertPackOnFailure(pack)) RimLumenRenderHost.ReportRuntimePackFailure(pack, ex);
            else owner.TripRenderFailSafe(ex);
            return;
        }
        owner.Recorded();
    }

    private unsafe void BeginPass(in FrameContext frame, bool clear)
    {
        VkClearValue value = default;
        VkRenderPassBeginInfo begin = new()
        {
            RenderPass = frame.RenderPass, Framebuffer = frame.Framebuffer, RenderArea = new VkRect2D(frame.Extent),
            ClearValueCount = clear ? 1 : 0, ClearValues = clear ? &value : null
        };
        frame.CommandBuffer.BeginRenderPass(in begin, VkSubpassContents.Inline);
        renderPassOpen = true;
        VkViewport viewport = new() { Width = frame.Extent.Width, Height = frame.Extent.Height, MinDepth = 0, MaxDepth = 1 };
        frame.CommandBuffer.SetViewport(0, new ReadOnlySpan<VkViewport>(in viewport));
        VkRect2D scissor = new(frame.Extent);
        frame.CommandBuffer.SetScissor(0, new ReadOnlySpan<VkRect2D>(in scissor));
    }

    private void EndPass(in FrameContext frame)
    {
        frame.CommandBuffer.EndRenderPass();
        renderPassOpen = false;
    }

    internal void OnRenderTargetRebuilt(IRenderTarget target)
    {
        owner.Targets.Dispose();
        owner.DepthProxy.Clear();
        renderer.OnRenderContextRebuilt(owner.App.RenderContext!);
        glowRenderer.OnRenderContextRebuilt(owner.App.RenderContext!);
        depthCopy.Invalidate();
        owner.ShaderPack.InvalidateOffscreen();
        owner.ShaderPack.InvalidateInline();
    }

    public void Dispose()
    {
        renderer.Dispose();
        glowRenderer.Dispose();
        depthCopy.Dispose();
    }
}
