using System;
using System.Linq;
using System.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Verse;
using Waylith;
using Waylith.Graphics;
using Waylith.Rendering;
using Waylith.Rendering.PostFx;
using RimLumen.Core.Lighting;
using RimLumen.Core.Rendering.Logic;
using static RimLumen.Core.Rendering.Logic.ShaderPackGraphValidator;

namespace RimLumen.Core.Rendering;

internal sealed class ShaderPackRuntime : IDisposable
{
    private sealed class CustomTargetAllocation : IDisposable
    {
        internal readonly ShaderPackTargetSpec Spec;
        internal readonly RenderTexture[] Ring;
        internal bool NeedsPersistentClear;

        internal CustomTargetAllocation(ShaderPackTargetSpec spec, RenderTexture[] ring)
        {
            Spec = spec;
            Ring = ring;
            NeedsPersistentClear = spec.Persistent;
        }

        public void Dispose()
        {
            foreach (RenderTexture tex in Ring)
                tex?.Dispose();
        }
    }

    internal readonly ShaderPackSpec Spec;
    private readonly EngineApplication app;
    private readonly int framesInFlight;
    // Each PostEffectPass resets its descriptor pool on RecordInline: never share one across groups in a frame.
    private sealed class LateBloomPasses(int mipLevels)
    {
        internal readonly PostEffectPass[] Down = new PostEffectPass[mipLevels];
        internal readonly PostEffectPass[] Up = new PostEffectPass[Math.Max(0, mipLevels - 1)];
        internal PostEffectPass Composite = null!;
    }

    private readonly LateBloomPasses?[] lateBloom = new LateBloomPasses[3];
    private readonly PostEffectPass[] bloomDown;
    private readonly PostEffectPass[] bloomUp;
    private readonly PostEffectPass[] customOffscreenPasses;
    private readonly PostEffectPass compositePass;
    private readonly ExecutionPlan plan;
    private readonly CustomTargetAllocation[] customTargets;

    private RenderTexture? black1x1;
    private RenderTexture? depth1x1;
    private bool placeholdersCleared;
    private int targetWidth;
    private int targetHeight;
    private ulong persistentFrameOrdinal;
    private ulong lastRenderedPersistentOrdinal;
    private bool disposed;

    internal ShaderPackRuntime(EngineApplication app, ShaderPackSpec spec)
    {
        plan = ExecutionPlan.Compile(spec);
        this.app = app;
        Spec = spec;
        customTargets = new CustomTargetAllocation[plan.Targets.Count];
        framesInFlight = Math.Max(1, app.RenderContext?.FramesInFlight ?? 3);

        int mipLevels = plan.BloomMipLevels;
        bloomDown = new PostEffectPass[mipLevels];
        bloomUp = new PostEffectPass[Math.Max(0, mipLevels - 1)];
        customOffscreenPasses = Array.Empty<PostEffectPass>();
        try
        {
            compositePass = CreatePasses(mipLevels, out customOffscreenPasses);
        }
        catch
        {
            // A partially constructed runtime already owns real GPU objects; release them before the failure escapes.
            Dispose();
            throw;
        }
    }

    private PostEffectPass CreatePasses(int mipLevels, out PostEffectPass[] offscreenPasses)
    {
        if (mipLevels > 0)
        {
            bloomDown[0] = new OffscreenPostEffectPass(
                "RimLumenBloomDown0",
                plan.BloomPrefilterShaderPath,
                new[] { 0, 1, 2, 3, 4 },
                BlendState.Opaque,
                pushConstantSize: 16,
                maxPassesPerFrame: 1,
                uniformBinding: 5);

            for (int i = 1; i < mipLevels; i++)
            {
                bloomDown[i] = new OffscreenPostEffectPass(
                    $"RimLumenBloomDown{i}",
                    plan.BloomDownShaderPath,
                    new[] { 0, 4 },
                    BlendState.Opaque,
                    pushConstantSize: 16,
                    maxPassesPerFrame: 1,
                    uniformBinding: 5);
            }

            for (int i = 0; i < bloomUp.Length; i++)
            {
                bloomUp[i] = new OffscreenPostEffectPass(
                    $"RimLumenBloomUp{i}",
                    plan.BloomUpShaderPath,
                    new[] { 0, 1, 4 },
                    BlendState.Opaque,
                    pushConstantSize: 16,
                    maxPassesPerFrame: 1,
                    uniformBinding: 5);
            }
        }

        int offscreenCount = plan.Passes.Count - 1;
        offscreenPasses = new PostEffectPass[offscreenCount];
        for (int i = 0; i < offscreenCount; i++)
        {
            PlannedPass passSpec = plan.Passes[i];
            int[] bindings = passSpec.Inputs.Select(static x => x.Binding).ToArray();
            offscreenPasses[i] = new OffscreenPostEffectPass(
                passSpec.RuntimeName,
                passSpec.FragmentShaderPath,
                bindings,
                BlendState.Opaque,
                pushConstantSize: 16,
                maxPassesPerFrame: 1,
                uniformBinding: 5);
        }

        PlannedPass finalSpec = plan.Passes[^1];
        int[] finalBindings = finalSpec.Inputs.Select(static x => x.Binding).ToArray();
        return new PostEffectPass(
            finalSpec.RuntimeName,
            finalSpec.FragmentShaderPath,
            finalBindings,
            ToBlendState(finalSpec.BlendMode),
            pushConstantSize: 16,
            uniformBinding: 5);
    }

    internal void RecordOffscreen(
        in FrameContext ctx,
        HdrTargets.Slot slot,
        RenderTexture hdrSource,
        RenderTexture scene,
        in ShaderPackUniforms uniforms,
        bool hasHdrDraws,
        bool hasGlowDraws,
        bool hasSceneDepth)
    {
        EnsurePlaceholdersAndTargets(ctx.CommandBuffer, scene.Width, scene.Height);

        RenderTexture depthTex = hasSceneDepth ? slot.DepthSnapshot : depth1x1!;
        RenderTexture glowTex = hasGlowDraws ? slot.Glow : black1x1!;
        bool skipBuiltInBloom = plan.BloomMipLevels == 0 || (!hasHdrDraws && !plan.HasCustomBloomShader);

        if (!skipBuiltInBloom && slot.Down.Length > 0)
            RecordBloom(in ctx, slot, in uniforms, bloomDown, bloomUp, hdrSource, glowTex, scene, depthTex);

        RenderTexture bloomTex = skipBuiltInBloom || slot.Bloom is null ? black1x1! : slot.Bloom;
        ulong writeOrdinal = persistentFrameOrdinal;

        for (int i = 0; i < customOffscreenPasses.Length; i++)
        {
            PlannedPass passSpec = plan.Passes[i];
            PostEffectPass pass = customOffscreenPasses[i];
            foreach (PlannedInput input in passSpec.Inputs)
            {
                BindInputSource(pass, input, hdrSource, glowTex, bloomTex, scene, depthTex, ctx.SubmissionFrameIndex, writeOrdinal);
            }
            pass.SetUniforms(in uniforms);
            RenderTexture output = ResolveWriteTarget(passSpec.OutputTargetIndex, ctx.SubmissionFrameIndex, writeOrdinal);
            var pc = new Vector4(1f / output.Width, 1f / output.Height, passSpec.ExtraParams.X, passSpec.ExtraParams.Y);
            pass.SetPushConstants(in pc);
            pass.Output = output;
            pass.Record(in ctx, null);
        }

        lastRenderedPersistentOrdinal = writeOrdinal;
        persistentFrameOrdinal = writeOrdinal + 1;
    }

    private void RecordBloom(in FrameContext ctx, HdrTargets.Slot slot, in ShaderPackUniforms uniforms,
        PostEffectPass[] down, PostEffectPass[] up, RenderTexture hdr, RenderTexture glow,
        RenderTexture? scene = null, RenderTexture? depth = null)
    {
        down[0].SetInput(0, hdr);
        down[0].SetInput(1, glow);
        if (scene != null)
        {
            down[0].SetInput(2, scene);
            down[0].SetInput(3, depth!);
        }
        var p0 = new Vector4(1f / slot.Glow.Width, 1f / slot.Glow.Height, uniforms.BloomParams.Y, uniforms.BloomParams.Z);
        down[0].SetPushConstants(in p0);
        down[0].Output = slot.Down[0];
        PrepareBloomPass(down[0], in uniforms, scene != null);
        down[0].Record(in ctx, null);
        RenderTexture prev = slot.Down[0];
        for (int i = 1; i < slot.Down.Length; i++)
        {
            down[i].SetInput(0, prev);
            PrepareBloomPass(down[i], in uniforms, scene != null);
            var pc = new Vector4(1f / prev.Width, 1f / prev.Height, uniforms.BloomParams.Y, uniforms.BloomParams.Z);
            down[i].SetPushConstants(in pc);
            down[i].Output = slot.Down[i];
            down[i].Record(in ctx, null);
            prev = slot.Down[i];
        }
        RenderTexture input = prev;
        for (int i = slot.Up.Length - 1; i >= 0; i--)
        {
            up[i].SetInput(0, input);
            up[i].SetInput(1, slot.Down[i]);
            PrepareBloomPass(up[i], in uniforms, scene != null);
            var pc = new Vector4(1f / input.Width, 1f / input.Height, uniforms.BloomParams.W, 0f);
            up[i].SetPushConstants(in pc);
            up[i].Output = slot.Up[i];
            up[i].Record(in ctx, null);
            input = slot.Up[i];
        }
    }

    private void PrepareBloomPass(PostEffectPass pass, in ShaderPackUniforms uniforms, bool packPass)
    {
        if (!packPass) return;
        BindMapLightField(pass, 4);
        pass.SetUniforms(in uniforms);
    }

    internal void RecordComposite(
        in FrameContext ctx,
        HdrTargets.Slot slot,
        RenderTexture hdrSource,
        RenderTexture scene,
        in ShaderPackUniforms uniforms,
        bool hasHdrDraws,
        bool hasGlowDraws,
        bool hasSceneDepth)
    {
        RenderTexture depthTex = hasSceneDepth ? slot.DepthSnapshot : depth1x1!;
        RenderTexture glowTex = hasGlowDraws ? slot.Glow : black1x1!;
        bool skipBuiltInBloom = plan.BloomMipLevels == 0 || (!hasHdrDraws && !plan.HasCustomBloomShader);
        RenderTexture bloomTex = skipBuiltInBloom || slot.Bloom is null ? black1x1! : slot.Bloom;

        PlannedPass finalSpec = plan.Passes[^1];
        foreach (PlannedInput input in finalSpec.Inputs)
        {
            BindInputSource(compositePass, input, hdrSource, glowTex, bloomTex, scene, depthTex, ctx.SubmissionFrameIndex, lastRenderedPersistentOrdinal);
        }
        compositePass.SetUniforms(in uniforms);
        Vector4 finalPc = finalSpec.UsesBuiltInComposite
            ? new Vector4(uniforms.BloomParams.X, 0f, 1f / scene.Width, 1f / scene.Height)
            : new Vector4(1f / scene.Width, 1f / scene.Height, finalSpec.ExtraParams.X, finalSpec.ExtraParams.Y);
        compositePass.SetPushConstants(in finalPc);
        compositePass.RecordInline(in ctx, ctx.RenderPass, ctx.SampleCount);
    }

    // Requires: a scheduled non-Early group, nonempty mip targets, and no open render pass; called once per group/frame.
    // Ensures: host bloom uses black HDR/current Glow and captured parameters; scene, HDR and pack history are untouched.
    internal void RecordLateBloom(in FrameContext ctx, HdrTargets.Slot slot, in ShaderPackUniforms uniforms, GlowGroup group)
    {
        EnsurePlaceholders(ctx.CommandBuffer);
        LateBloomPasses passes = GetLatePasses(group);
        // Late bloom adds onto the stored scene, so its glow stays in stored units whatever the frame's blend space.
        ShaderPackUniforms stored = uniforms;
        stored.ColorBlendSpace = 0f;
        passes.Down[0].SetUniforms(in stored);
        RecordBloom(in ctx, slot, in uniforms, passes.Down, passes.Up, black1x1!, slot.Glow);
    }

    // Requires: this group's bloom was recorded and a scene LOAD pass is open; called once per group/frame.
    // Ensures: adds only bloom RGB times intensity, preserving destination alpha and all pack state.
    internal void RecordLateBloomComposite(in FrameContext ctx, HdrTargets.Slot slot, in ShaderPackUniforms uniforms, GlowGroup group)
    {
        PostEffectPass pass = lateBloom[(int)group - 1]!.Composite;
        pass.SetInput(0, slot.Bloom!);
        var pc = new Vector4(uniforms.BloomParams.X, 0f, 0f, 0f);
        pass.SetPushConstants(in pc);
        pass.RecordInline(in ctx, ctx.RenderPass, ctx.SampleCount);
    }

    private LateBloomPasses GetLatePasses(GlowGroup group)
    {
        int index = (int)group - 1;
        if (lateBloom[index] is { } existing) return existing;
        var passes = new LateBloomPasses(plan.BloomMipLevels);
        lateBloom[index] = passes;
        for (int i = 0; i < passes.Down.Length; i++)
            passes.Down[i] = new OffscreenPostEffectPass($"RimLumen{group}BloomDown{i}",
                i == 0 ? "PostFx/RimLumenBloomPrefilter.frag.glsl" : "PostFx/RimLumenBloomDown.frag.glsl",
                i == 0 ? new[] { 0, 1 } : new[] { 0 }, BlendState.Opaque, pushConstantSize: 16,
                uniformBinding: i == 0 ? 5 : -1);
        for (int i = 0; i < passes.Up.Length; i++)
            passes.Up[i] = new OffscreenPostEffectPass($"RimLumen{group}BloomUp{i}",
                "PostFx/RimLumenBloomUp.frag.glsl", new[] { 0, 1 }, BlendState.Opaque, pushConstantSize: 16);
        passes.Composite = new PostEffectPass($"RimLumen{group}BloomAdd", "PostFx/RimLumenBloomAdd.frag.glsl",
            new[] { 0 }, ToBlendState(ShaderPackBlendMode.Additive), pushConstantSize: 16);
        return passes;
    }

    internal void InvalidateOffscreen()
    {
        DisposeCustomTargets();
        foreach (PostEffectPass? pass in bloomDown) pass?.Invalidate();
        foreach (PostEffectPass? pass in bloomUp) pass?.Invalidate();
        foreach (PostEffectPass? pass in customOffscreenPasses) pass?.Invalidate();
        foreach (LateBloomPasses? passes in lateBloom)
        {
            if (passes == null) continue;
            foreach (PostEffectPass? pass in passes.Down) pass?.Invalidate();
            foreach (PostEffectPass? pass in passes.Up) pass?.Invalidate();
        }
    }

    internal void InvalidateInline()
    {
        compositePass?.Invalidate();
        foreach (LateBloomPasses? passes in lateBloom) passes?.Composite?.Invalidate();
    }

    private void EnsurePlaceholders(CommandBuffer cmd)
    {
        if (black1x1 is null)
        {
            black1x1 = CreateRenderTexture(1, 1, VkFormat.R8G8B8A8UNorm, RenderTextureFilter.Bilinear, "RimLumen.Placeholder.Black1x1");
            placeholdersCleared = false;
        }
        if (depth1x1 is null)
        {
            depth1x1 = CreateRenderTexture(1, 1, VkFormat.R32SFloat, RenderTextureFilter.Point, "RimLumen.Placeholder.Depth1x1");
            placeholdersCleared = false;
        }
        if (!placeholdersCleared)
        {
            ClearAndMakeSampleable(cmd, black1x1, 0f, 0f, 0f, 0f);
            ClearAndMakeSampleable(cmd, depth1x1, 0f, 0f, 0f, 0f);
            placeholdersCleared = true;
        }

    }

    private void EnsurePlaceholdersAndTargets(CommandBuffer cmd, int sceneWidth, int sceneHeight)
    {
        EnsurePlaceholders(cmd);
        if (plan.Targets.Count == 0)
            return;

        if (targetWidth != 0 && (targetWidth != sceneWidth || targetHeight != sceneHeight))
        {
            app.Device.WaitIdle();
            DisposeCustomTargets();
        }

        if (targetWidth == 0)
        {
            targetWidth = sceneWidth;
            targetHeight = sceneHeight;
            for (int targetIndex = 0; targetIndex < plan.Targets.Count; targetIndex++)
            {
                ShaderPackTargetSpec spec = plan.Targets[targetIndex];
                int w = Math.Max(1, (int)MathF.Round(sceneWidth * spec.Scale));
                int h = Math.Max(1, (int)MathF.Round(sceneHeight * spec.Scale));
                int ringCount = spec.Persistent ? Math.Max(2, framesInFlight) : framesInFlight;
                VkFormat format = ToVkFormat(spec.Format);
                RenderTextureFilter filter = ToFilter(spec.Filter);
                var ring = new RenderTexture[ringCount];
                try
                {
                    for (int i = 0; i < ringCount; i++)
                    {
                        ring[i] = CreateRenderTexture(w, h, format, filter, $"RimLumen.PackTarget.{spec.Name}.{i}");
                    }
                    customTargets[targetIndex] = new CustomTargetAllocation(spec, ring);
                }
                catch
                {
                    foreach (RenderTexture? tex in ring) tex?.Dispose();
                    DisposeCustomTargets();
                    throw;
                }
            }
        }

        foreach (CustomTargetAllocation alloc in customTargets)
        {
            if (alloc.NeedsPersistentClear)
            {
                foreach (RenderTexture tex in alloc.Ring)
                    ClearAndMakeSampleable(cmd, tex, 0f, 0f, 0f, 0f);
                alloc.NeedsPersistentClear = false;
            }
        }
    }

    private RenderTexture ResolveWriteTarget(int targetIndex, int submissionIndex, ulong writeOrdinal)
    {
        CustomTargetAllocation alloc = customTargets[targetIndex];
        if (alloc.Spec.Persistent)
        {
            int idx = (int)(writeOrdinal % (ulong)alloc.Ring.Length);
            return alloc.Ring[idx];
        }
        return alloc.Ring[submissionIndex % alloc.Ring.Length];
    }

    private void BindInputSource(
        PostEffectPass pass,
        PlannedInput input,
        RenderTexture hdrSource,
        RenderTexture glowTex,
        RenderTexture bloomTex,
        RenderTexture scene,
        RenderTexture depthTex,
        int submissionIndex,
        ulong writeOrdinal)
    {
        switch (input.Kind)
        {
            case InputKind.Hdr: pass.SetInput(input.Binding, hdrSource); return;
            case InputKind.Glow: pass.SetInput(input.Binding, glowTex); return;
            case InputKind.Bloom: pass.SetInput(input.Binding, bloomTex); return;
            case InputKind.Scene: pass.SetInput(input.Binding, scene); return;
            case InputKind.Depth: pass.SetInput(input.Binding, depthTex); return;
            case InputKind.MapLightField: BindMapLightField(pass, input.Binding); return;
            case InputKind.Target:
                CustomTargetAllocation alloc = customTargets[input.TargetIndex];
                int index = alloc.Spec.Persistent
                    ? (int)((writeOrdinal + (ulong)alloc.Ring.Length - (ulong)input.HistoryFramesAgo) % (ulong)alloc.Ring.Length)
                    : submissionIndex % alloc.Ring.Length;
                pass.SetInput(input.Binding, alloc.Ring[index]);
                return;
            default: throw new ArgumentOutOfRangeException(nameof(input));
        }
    }

    private void BindMapLightField(PostEffectPass pass, int binding)
    {
        if (RimLumenRenderHost.IsMapLightFieldActive
            && MapLightFieldCollector.TryGetDescriptor(out VkImageView view, out VkSampler sampler, out _, out _))
        {
            pass.SetInput(binding, view, sampler);
        }
        else
        {
            pass.SetInput(binding, black1x1!);
        }
    }

    private static unsafe void ClearAndMakeSampleable(CommandBuffer cmd, RenderTexture target, float r, float g, float b, float a)
    {
        GpuTextureAllocation color = target.Color;
        var range = new VkImageSubresourceRange
        {
            AspectMask = VkImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };
        var toTransfer = new VkImageMemoryBarrier
        {
            SrcAccessMask = VkAccessFlags.None,
            DstAccessMask = VkAccessFlags.TransferWriteBit,
            OldLayout = VkImageLayout.Undefined,
            NewLayout = VkImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = -1,
            DstQueueFamilyIndex = -1,
            Image = color.Image,
            SubresourceRange = range,
        };
        cmd.PipelineBarrier(VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None,
            default, default, new ReadOnlySpan<VkImageMemoryBarrier>(in toTransfer));

        var clearColor = new VkClearColorValue();
        clearColor.Float32[0] = r;
        clearColor.Float32[1] = g;
        clearColor.Float32[2] = b;
        clearColor.Float32[3] = a;
        cmd.ClearColorImage(color.Image, VkImageLayout.TransferDstOptimal, &clearColor,
            new ReadOnlySpan<VkImageSubresourceRange>(in range));

        var toSample = new VkImageMemoryBarrier
        {
            SrcAccessMask = VkAccessFlags.TransferWriteBit,
            DstAccessMask = VkAccessFlags.ShaderReadBit,
            OldLayout = VkImageLayout.TransferDstOptimal,
            NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = -1,
            DstQueueFamilyIndex = -1,
            Image = color.Image,
            SubresourceRange = range,
        };
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None,
            default, default, new ReadOnlySpan<VkImageMemoryBarrier>(in toSample));
        color.SetMipLayout(0, VkImageLayout.ShaderReadOnlyOptimal);
    }

    private static RenderTexture CreateRenderTexture(int width, int height, VkFormat format, RenderTextureFilter filter, string name)
    {
        var tex = new RenderTexture(width, height, colorFormat: format, filter: filter, wrap: RenderTextureWrap.Clamp, name: name);
        try
        {
            tex.Create();
            return tex;
        }
        catch
        {
            tex.Dispose();
            throw;
        }
    }

    private static VkFormat ToVkFormat(ShaderPackTargetFormat format) => format switch
    {
        ShaderPackTargetFormat.R16G16B16A16SFloat => VkFormat.R16G16B16A16SFloat,
        ShaderPackTargetFormat.R8G8B8A8UNorm => VkFormat.R8G8B8A8UNorm,
        ShaderPackTargetFormat.R16SFloat => VkFormat.R16SFloat,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    private static RenderTextureFilter ToFilter(ShaderPackTargetFilter filter) => filter switch
    {
        ShaderPackTargetFilter.Linear => RenderTextureFilter.Bilinear,
        ShaderPackTargetFilter.Point => RenderTextureFilter.Point,
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, null),
    };

    private static BlendState ToBlendState(ShaderPackBlendMode mode) => mode switch
    {
        ShaderPackBlendMode.Opaque => BlendState.Opaque,
        ShaderPackBlendMode.PremultipliedAlpha => new BlendState(
            true,
            BlendFactor.One,
            BlendFactor.OneMinusSrcAlpha,
            BlendOp.Add,
            BlendFactor.One,
            BlendFactor.OneMinusSrcAlpha,
            BlendOp.Add),
        ShaderPackBlendMode.Additive => new BlendState(
            true,
            BlendFactor.One,
            BlendFactor.One,
            BlendOp.Add,
            BlendFactor.Zero,
            BlendFactor.One,
            BlendOp.Add),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    private void DisposeCustomTargets()
    {
        foreach (CustomTargetAllocation? alloc in customTargets)
            alloc?.Dispose();
        Array.Clear(customTargets);
        targetWidth = 0;
        targetHeight = 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        DisposeCustomTargets();
        black1x1?.Dispose();
        black1x1 = null;
        depth1x1?.Dispose();
        depth1x1 = null;
        placeholdersCleared = false;
        foreach (PostEffectPass? pass in bloomDown) pass?.Dispose();
        foreach (PostEffectPass? pass in bloomUp) pass?.Dispose();
        foreach (PostEffectPass? pass in customOffscreenPasses) pass?.Dispose();
        compositePass?.Dispose();
        foreach (LateBloomPasses? passes in lateBloom)
        {
            if (passes == null) continue;
            foreach (PostEffectPass? pass in passes.Down) pass?.Dispose();
            foreach (PostEffectPass? pass in passes.Up) pass?.Dispose();
            passes.Composite?.Dispose();
        }
    }
}
