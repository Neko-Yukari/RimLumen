using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Waylith.Graphics;
using Waylith.Rendering;
using Waylith.Rendering.PostFx;

namespace RimLumen.Core.Rendering;

internal sealed class RimLumenCompositeFeature : IRenderFeature
{
    private readonly RimLumenPipeline owner;
    private readonly InlinePass pass;
    internal long Draws;

    internal RimLumenCompositeFeature(RimLumenPipeline owner)
    {
        this.owner = owner;
        pass = new InlinePass(this, owner);
        Passes = new IRenderPass[] { pass };
    }

    public bool IsActive => owner.Captured != null;
    public IReadOnlyList<IRenderPass> Passes { get; }
    public object? CaptureFrame(in FrameTiming timing)
    {
        owner.GuardCompositeOrder();
        return owner.Captured;
    }
    public void OnRenderTargetRebuilt(IRenderTarget target) => pass.Invalidate();
    internal void Dispose() => pass.Dispose();

    private sealed class InlinePass : PostEffectPass
    {
        private readonly RimLumenCompositeFeature feature;
        private readonly RimLumenPipeline owner;

        internal InlinePass(RimLumenCompositeFeature feature, RimLumenPipeline owner)
            : base("RimLumenComposite", "RimLumenComposite.frag.glsl", new[] { 0, 1, 2 },
                new BlendState(true, BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add,
                    BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add), pushConstantSize: 16)
        {
            this.feature = feature;
            this.owner = owner;
        }

        public override bool PreBackbuffer => false;
        public override void Record(in FrameContext ctx, object? featurePayload)
        {
            if (featurePayload is not PipelineFrame) return;
            HdrTargets.Slot slot = owner.Targets.Slots[ctx.SubmissionFrameIndex];
            RenderTexture scene = owner.SceneComposite.Source?.Invoke(ctx.SubmissionFrameIndex)
                ?? throw new InvalidOperationException("Scene source disappeared before RimLumen composite.");
            SetInput(0, slot.Color);
            SetInput(1, slot.Bloom);
            SetInput(2, scene);
            Vector4 parameters = new(0.16f, 0f, 1f / scene.Width, 1f / scene.Height);
            SetPushConstants(in parameters);
            RecordInline(in ctx, ctx.RenderPass, ctx.SampleCount);
            Interlocked.Increment(ref feature.Draws);
        }
    }
}
