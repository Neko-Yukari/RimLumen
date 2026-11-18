using System;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using RimLumen.Core.Contracts;
using RimLumen.Core.Distortion;
using RimLumen.Core.Lighting;
using RimLumen.Core.Rendering.Logic;
using RimWorldWaylith.Rendering;
using Waylith.Graphics;
using Waylith.Rendering;
using Waylith.Rendering.PostFx;

namespace RimLumen.Core.Rendering;

internal sealed class RimLumenMapChain(RimLumenPipeline owner) : IDisposable
{
    private readonly MeshBatchRenderer renderer = new(owner.App.GraphicsDevice!, owner.App.RenderContext!);
    private readonly MeshBatchRenderer glowRenderer = new(owner.App.GraphicsDevice!, owner.App.RenderContext!);
    private readonly OffscreenPostEffectPass depthCopy = new("RimLumenDepthCopy", "PostFx/RimLumenDepthCopy.frag.glsl", new[] { 0 });
    private readonly MeshBatchRenderer lightRenderer = new(owner.App.GraphicsDevice!, owner.App.RenderContext!);
    // scene * (1 + compress(sum)): DstColor, One on colour; Zero, One keeps destination alpha.
    private readonly PostEffectPass lightComposite = new("RimLumenLightPoolComposite", LightPoolRenderer.CompositeFile, new[] { 0 },
        new BlendState(true, BlendFactor.DstColor, BlendFactor.One, BlendOp.Add, BlendFactor.Zero, BlendFactor.One, BlendOp.Add));
    private DistortionPass? distortionPass;
    private bool labelOpen;
    private bool renderPassOpen;

    internal void Record(in FrameContext ctx, PipelineFrame payload, SceneColorTargets targets, GlowGroup group)
    {
        try
        {
            if (group != GlowGroup.Early)
            {
                RecordLate(in ctx, payload, targets, group);
                return;
            }
            if (!payload.RenderHdr)
            {
                var barriers = new LumenBarrierContext { Frame = ctx, FrameIndex = payload.Index };
                foreach (ILumenFrameContributor contributor in payload.Contributors) contributor.RecordBufferBarriers(in barriers);
                return;
            }
            RecordChain(in ctx, payload, targets);
        }
        catch (Exception ex) { owner.TripRenderFailSafe(ex); }
        finally { CloseOpenScopes(in ctx); }
    }

    // Requires: the queue-3101 split of a frame with light pools; no render pass open.
    // Ensures: the pools are summed linearly into the slot's LightSum, then one full-screen pass multiplies the scene
    //          by (1 + compress(sum)); scene alpha and depth are untouched.
    internal void RecordLightPools(in FrameContext ctx, PipelineFrame payload, SceneColorTargets targets)
    {
        try
        {
            RenderTexture scene = targets.Current(ctx.SubmissionFrameIndex);
            if (!scene.HasDepth) throw new InvalidOperationException("RimLumen requires scene depth at the light-pool split.");
            PassAttachment attachment = targets.LoadAttachment
                ?? throw new InvalidOperationException("SceneColorTargets.LoadAttachment is unavailable at the light-pool split.");
            HdrTargets.Slot slot = owner.Targets.Ensure(ctx.SubmissionFrameIndex, scene, payload.BloomMipLevels);
            ctx.CommandBuffer.BeginDebugLabel("RimLumen/LightPools\0"u8, new float4(1f, 0.5f, 0f, 1f));
            labelOpen = true;
            var frame = new FrameContext(ctx.RenderContext, ctx.Resources, ctx.CommandBuffer, owner.Targets.RenderPass,
                owner.Targets.EnsureLightSum(slot), slot.LightSum!.Extent, VkSampleCountFlags._1Bit, VkFormat.R16G16B16A16SFloat,
                scene.DepthFormat);
            BeginPass(in frame, clear: true, ref renderPassOpen);
            if (payload.LightMesh is { } lights)
            {
                lightRenderer.InvalidateCommandBufferBindings();
                lightRenderer.RecordPass(in frame, lights, RimLumenRenderHost.LumenRenderPassId);
            }
            EndPass(in frame, ref renderPassOpen);
            slot.LightSum.Color.SetMipLayout(0, VkImageLayout.ShaderReadOnlyOptimal);
            if (payload.Takeover)
            {
                owner.PoolShadows.LightSum.Bind(slot.LightSum);
                return; // The takeover LightOverlay (3100) and sun shade (3170) read this sum.
            }
            FrameContext composite = attachment.MakeFrameContext(in ctx);
            BeginPass(in composite, clear: false, ref renderPassOpen);
            lightComposite.SetInput(0, slot.LightSum);
            lightComposite.RecordInline(in composite, composite.RenderPass, composite.SampleCount);
            EndPass(in composite, ref renderPassOpen);
            // Only now do this frame's lifted sun shadows (3170/3175) see the pools; until here they read no pool.
            owner.PoolShadows.LightSum.Bind(slot.LightSum);
        }
        catch (Exception ex) { owner.TripRenderFailSafe(ex); }
        finally { CloseOpenScopes(in ctx); }
    }

    private void CloseOpenScopes(in FrameContext ctx)
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
        if (payload.HasGlowDraws) RecordGlow(in ctx, payload, scene, slot, GlowGroup.Early);
        BeginPass(in frame, clear: true, ref renderPassOpen);
        var draw = new LumenHdrDrawContext
        {
            Frame = frame, FrameIndex = payload.Index, HdrColorTarget = slot.Color,
            CurrentMapIndex = payload.MapIndex, ColorBlendSpace = payload.ColorBlendSpace,
            SceneColorDepthSource = payload.HasSceneDepth ? slot.DepthSnapshot : null,
            ViewProjection = payload.View.ViewProjection, SoftParticleDepthParams = payload.DepthParams
        };
        foreach (ILumenFrameContributor contributor in payload.Contributors) contributor.RecordHdrDraws(in draw);
        if (payload.Mesh is { } mesh && mesh.Draws.Count > 0)
        {
            renderer.InvalidateCommandBufferBindings();
            renderer.RecordPass(in frame, mesh, RimLumenRenderHost.LumenRenderPassId);
        }
        EndPass(in frame, ref renderPassOpen);
        slot.Color.Color.SetMipLayout(0, VkImageLayout.ShaderReadOnlyOptimal);
        // The layer stays in the frame's blend space; uniforms.ColorBlendSpace tells bloom and the composite which.
        RenderTexture hdrSource = slot.Color;
        RenderTexture? distortion = payload.DistortionMesh is { } offsets
            ? (distortionPass ??= new DistortionPass(owner.App)).Record(in ctx, offsets, scene) : null;
        ShaderPackSpec pack = owner.ShaderPack.Spec;
        try
        {
            owner.ShaderPack.RecordOffscreen(in ctx, slot, hdrSource, slot.SceneSnapshot, distortion, in uniforms,
                payload.HasHdrDraws, payload.HasGlowDraws, payload.HasSceneDepth);
            FrameContext composite = attachment.MakeFrameContext(in ctx);
            BeginPass(in composite, clear: false, ref renderPassOpen);
            try
            {
                owner.ShaderPack.RecordComposite(in composite, slot, hdrSource, slot.SceneSnapshot, distortion, in uniforms,
                    payload.HasHdrDraws, payload.HasGlowDraws, payload.HasSceneDepth);
            }
            finally { EndPass(in composite, ref renderPassOpen); }
        }
        catch (Exception ex)
        {
            if (RimLumenRenderHost.ShouldRevertPackOnFailure(pack)) RimLumenRenderHost.ReportRuntimePackFailure(pack, ex);
            else owner.TripRenderFailSafe(ex);
            return;
        }
        owner.Recorded();
    }

    private void RecordLate(in FrameContext ctx, PipelineFrame payload, SceneColorTargets targets, GlowGroup group)
    {
        RenderTexture scene = targets.Current(ctx.SubmissionFrameIndex);
        if (!scene.HasDepth) throw new InvalidOperationException("RimLumen requires scene depth at the late glow split.");
        PassAttachment attachment = targets.LoadAttachment
            ?? throw new InvalidOperationException("SceneColorTargets.LoadAttachment is unavailable at the late glow split.");
        HdrTargets.Slot slot = owner.Targets.Ensure(ctx.SubmissionFrameIndex, scene, payload.BloomMipLevels);
        ctx.CommandBuffer.BeginDebugLabel("RimLumen/LateBloom\0"u8, new float4(1f, 0.5f, 0f, 1f));
        labelOpen = true;
        RecordGlow(in ctx, payload, scene, slot, group);
        ShaderPackUniforms uniforms = payload.Uniforms;
        owner.ShaderPack.RecordLateBloom(in ctx, slot, in uniforms, group);
        FrameContext composite = attachment.MakeFrameContext(in ctx);
        BeginPass(in composite, clear: false, ref renderPassOpen);
        try { owner.ShaderPack.RecordLateBloomComposite(in composite, slot, in uniforms, group); }
        finally { EndPass(in composite, ref renderPassOpen); }
    }

    private void RecordGlow(in FrameContext ctx, PipelineFrame payload, RenderTexture scene, HdrTargets.Slot slot, GlowGroup group)
    {
        var frame = new FrameContext(ctx.RenderContext, ctx.Resources, ctx.CommandBuffer, owner.Targets.RenderPass,
            slot.GlowFramebuffer, slot.Glow.Extent, VkSampleCountFlags._1Bit, VkFormat.R16G16B16A16SFloat, scene.DepthFormat);
        BeginPass(in frame, clear: true, ref renderPassOpen);
        GlowQueueRange range = GlowGroups.Range(group);
        glowRenderer.InvalidateCommandBufferBindings();
        glowRenderer.RecordPass(in frame, payload.GlowMesh!, RimLumenRenderHost.LumenRenderPassId,
            queueMin: range.MinInclusive, queueMax: range.MaxExclusive);
        EndPass(in frame, ref renderPassOpen);
        slot.Glow.Color.SetMipLayout(0, VkImageLayout.ShaderReadOnlyOptimal);
    }

    internal static unsafe void BeginPass(in FrameContext frame, bool clear, ref bool renderPassOpen)
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

    internal static void EndPass(in FrameContext frame, ref bool renderPassOpen)
    {
        frame.CommandBuffer.EndRenderPass();
        renderPassOpen = false;
    }

    internal void OnRenderTargetRebuilt(IRenderTarget target)
    {
        owner.Targets.Dispose();
        owner.DepthProxy.Clear();
        owner.PoolShadows.LightSum.Clear();
        renderer.OnRenderContextRebuilt(owner.App.RenderContext!);
        glowRenderer.OnRenderContextRebuilt(owner.App.RenderContext!);
        lightRenderer.OnRenderContextRebuilt(owner.App.RenderContext!);
        distortionPass?.OnRenderTargetRebuilt();
        depthCopy.Invalidate();
        lightComposite.Invalidate();
        owner.ShaderPack.InvalidateOffscreen();
        owner.ShaderPack.InvalidateInline();
    }

    public void Dispose()
    {
        renderer.Dispose();
        glowRenderer.Dispose();
        lightRenderer.Dispose();
        depthCopy.Dispose();
        lightComposite.Dispose();
        distortionPass?.Dispose();
    }
}
