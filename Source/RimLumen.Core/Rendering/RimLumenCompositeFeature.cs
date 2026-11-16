using System;
using System.Collections.Generic;
using Brutal.Numerics;
using Brutal.VulkanApi.Abstractions;
using Waylith.Graphics;
using Waylith.Rendering;

namespace RimLumen.Core.Rendering;

internal sealed class RimLumenCompositeFeature : IRenderFeature
{
    private readonly RimLumenPipeline owner;
    private readonly InlinePass pass;

    internal RimLumenCompositeFeature(RimLumenPipeline owner)
    {
        this.owner = owner;
        pass = new InlinePass(owner);
        Passes = new IRenderPass[] { pass };
    }

    public bool IsActive => owner.Captured is { RenderHdr: true };
    public IReadOnlyList<IRenderPass> Passes { get; }
    public object? CaptureFrame(in FrameTiming timing)
    {
        if (RimLumenRenderHost.FailSafeTripped) return null;
        // Second look per frame: a feature captured after Pre may have reordered the list since Pre checked it.
        owner.GuardCompositeOrder();
        return owner.Captured is { RenderHdr: true } frame ? frame : null;
    }
    public void OnRenderTargetRebuilt(IRenderTarget target) => owner.ShaderPack.InvalidateInline();
    internal void Dispose() { }

    private sealed class InlinePass(RimLumenPipeline owner) : IRenderPass
    {
        public byte PassId => RimLumenRenderHost.LumenRenderPassId;
        public PassAttachment? Attachment => null;
        public bool PreBackbuffer => false;

        public void Record(in FrameContext ctx, object? featurePayload)
        {
            if (featurePayload is not PipelineFrame { RenderHdr: true } payload || RimLumenRenderHost.FailSafeTripped) return;
            try
            {
                HdrTargets.Slot slot = owner.Targets.Slots[ctx.SubmissionFrameIndex];
                RenderTexture scene = owner.SceneComposite.Source?.Invoke(ctx.SubmissionFrameIndex)
                    ?? throw new InvalidOperationException("Scene source disappeared before RimLumen composite.");
                ShaderPackUniforms uniforms = payload.Uniforms;
                ShaderPackSpec packSpec = owner.ShaderPack.Spec;
                ctx.CommandBuffer.BeginDebugLabel("RimLumen/Composite\0"u8, new float4(1f, 0.5f, 0f, 1f));
                try
                {
                    try
                    {
                        owner.ShaderPack.RecordComposite(in ctx, slot, scene, in uniforms, payload.HasHdrDraws, payload.HasSceneDepth);
                    }
                    catch (Exception ex)
                    {
                        if (RimLumenRenderHost.ShouldRevertPackOnFailure(packSpec))
                            RimLumenRenderHost.ReportRuntimePackFailure(packSpec, ex);
                        else
                            owner.TripRenderFailSafe(ex);
                    }
                }
                finally
                {
                    ctx.CommandBuffer.EndDebugLabel();
                }
            }
            catch (Exception ex) { owner.TripRenderFailSafe(ex); }
        }
        public void OnRenderTargetRebuilt(IRenderTarget target) => owner.ShaderPack.InvalidateInline();

        public void Dispose() { }
    }
}
