using System;
using System.Collections.Generic;
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

namespace RimLumen.Core.Rendering;

internal sealed class ShaderPackRuntime : IDisposable
{
    private sealed class InlinePostEffectPass : PostEffectPass
    {
        internal InlinePostEffectPass(
            string name,
            string fragmentShaderPath,
            ReadOnlySpan<int> bindings,
            BlendState blend,
            int pushConstantSize,
            int uniformBinding)
            : base(name, fragmentShaderPath, bindings, blend, pushConstantSize, maxPassesPerFrame: 1, uniformBinding: uniformBinding)
        {
        }

        public override bool PreBackbuffer => false;
    }

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
    private readonly PostEffectPass[] bloomDown;
    private readonly PostEffectPass[] bloomUp;
    private readonly PostEffectPass[] customOffscreenPasses;
    private readonly InlinePostEffectPass backbufferPass;
    // Spec-derived facts the per-frame record paths branch on.
    private readonly bool hasCustomBloomShader;
    private readonly bool finalPassUsesBuiltInComposite;
    private readonly Dictionary<string, CustomTargetAllocation> customTargets = new(StringComparer.OrdinalIgnoreCase);

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
        this.app = app;
        Spec = spec;
        framesInFlight = Math.Max(1, app.RenderContext?.FramesInFlight ?? 3);

        hasCustomBloomShader = NullIfBlank(spec.BloomPrefilterShaderPath) is not null
            || NullIfBlank(spec.BloomDownShaderPath) is not null
            || NullIfBlank(spec.BloomUpShaderPath) is not null;
        finalPassUsesBuiltInComposite = spec.Passes.Count > 0 && NullIfBlank(spec.Passes[^1].FragmentShaderPath) is null;

        int mipLevels = spec.BloomMipLevels;
        bloomDown = new PostEffectPass[mipLevels];
        bloomUp = new PostEffectPass[Math.Max(0, mipLevels - 1)];
        customOffscreenPasses = Array.Empty<PostEffectPass>();
        try
        {
            backbufferPass = CreatePasses(spec, mipLevels, out customOffscreenPasses);
        }
        catch
        {
            // A partially constructed runtime already owns real GPU objects; release them before the failure escapes.
            Dispose();
            throw;
        }
    }

    private InlinePostEffectPass CreatePasses(ShaderPackSpec spec, int mipLevels, out PostEffectPass[] offscreenPasses)
    {
        if (mipLevels > 0)
        {
            string prefilterPath = NullIfBlank(spec.BloomPrefilterShaderPath) ?? "RimLumenBloomPrefilter.frag.glsl";
            string downPath = NullIfBlank(spec.BloomDownShaderPath) ?? "RimLumenBloomDown.frag.glsl";
            string upPath = NullIfBlank(spec.BloomUpShaderPath) ?? "RimLumenBloomUp.frag.glsl";

            bloomDown[0] = new PostEffectPass(
                "RimLumenBloomDown0",
                prefilterPath,
                new[] { 0, 2, 3, 4 },
                BlendState.Opaque,
                pushConstantSize: 16,
                maxPassesPerFrame: 1,
                uniformBinding: 5);

            for (int i = 1; i < mipLevels; i++)
            {
                bloomDown[i] = new PostEffectPass(
                    $"RimLumenBloomDown{i}",
                    downPath,
                    new[] { 0, 4 },
                    BlendState.Opaque,
                    pushConstantSize: 16,
                    maxPassesPerFrame: 1,
                    uniformBinding: 5);
            }

            for (int i = 0; i < bloomUp.Length; i++)
            {
                bloomUp[i] = new PostEffectPass(
                    $"RimLumenBloomUp{i}",
                    upPath,
                    new[] { 0, 1, 4 },
                    BlendState.Opaque,
                    pushConstantSize: 16,
                    maxPassesPerFrame: 1,
                    uniformBinding: 5);
            }
        }

        if (spec.Passes.Count == 0)
        {
            offscreenPasses = Array.Empty<PostEffectPass>();
            string compositePath = NullIfBlank(spec.CompositeShaderPath) ?? "RimLumenComposite.frag.glsl";
            return new InlinePostEffectPass(
                "RimLumenComposite",
                compositePath,
                new[] { 0, 1, 2, 3, 4 },
                ToBlendState(ShaderPackBlendMode.PremultipliedAlpha),
                pushConstantSize: 16,
                uniformBinding: 5);
        }

        int offscreenCount = spec.Passes.Count - 1;
        offscreenPasses = new PostEffectPass[offscreenCount];
        for (int i = 0; i < offscreenCount; i++)
        {
            ShaderPackPassSpec passSpec = spec.Passes[i];
            int[] bindings = passSpec.Inputs.Select(static x => x.Binding).ToArray();
            offscreenPasses[i] = new PostEffectPass(
                $"RimLumenPackPass.{i}",
                passSpec.FragmentShaderPath!,
                bindings,
                BlendState.Opaque,
                pushConstantSize: 16,
                maxPassesPerFrame: 1,
                uniformBinding: 5);
        }

        ShaderPackPassSpec finalSpec = spec.Passes[^1];
        string? customFinalPath = NullIfBlank(finalSpec.FragmentShaderPath);
        string finalPath = customFinalPath ?? NullIfBlank(spec.CompositeShaderPath) ?? "RimLumenComposite.frag.glsl";
        int[] finalBindings = customFinalPath is null && finalSpec.Inputs.Count == 0
            ? new[] { 0, 1, 2, 3, 4 }
            : finalSpec.Inputs.Select(static x => x.Binding).ToArray();
        return new InlinePostEffectPass(
            "RimLumenPackComposite",
            finalPath,
            finalBindings,
            ToBlendState(finalSpec.BlendMode),
            pushConstantSize: 16,
            uniformBinding: 5);
    }

    internal void RecordOffscreen(
        in FrameContext ctx,
        HdrTargets.Slot slot,
        RenderTexture scene,
        in ShaderPackUniforms uniforms,
        bool hasHdrDraws,
        bool hasSceneDepth)
    {
        EnsurePlaceholdersAndTargets(ctx.CommandBuffer, scene.Width, scene.Height);

        RenderTexture depthTex = hasSceneDepth ? slot.DepthSnapshot : depth1x1!;
        bool skipBuiltInBloom = Spec.BloomMipLevels == 0 || (!hasHdrDraws && !hasCustomBloomShader);

        if (!skipBuiltInBloom && slot.Down.Length > 0)
        {
            bloomDown[0].SetInput(0, slot.Color);
            bloomDown[0].SetInput(2, scene);
            bloomDown[0].SetInput(3, depthTex);
            BindMapLightField(bloomDown[0], 4);
            bloomDown[0].SetUniforms(in uniforms);
            var p0 = new Vector4(1f / slot.Color.Width, 1f / slot.Color.Height, uniforms.BloomParams.Y, uniforms.BloomParams.Z);
            bloomDown[0].SetPushConstants(in p0);
            bloomDown[0].Output = slot.Down[0];
            bloomDown[0].Record(in ctx, null);

            RenderTexture prev = slot.Down[0];
            for (int i = 1; i < slot.Down.Length; i++)
            {
                bloomDown[i].SetInput(0, prev);
                BindMapLightField(bloomDown[i], 4);
                bloomDown[i].SetUniforms(in uniforms);
                var pi = new Vector4(1f / prev.Width, 1f / prev.Height, uniforms.BloomParams.Y, uniforms.BloomParams.Z);
                bloomDown[i].SetPushConstants(in pi);
                bloomDown[i].Output = slot.Down[i];
                bloomDown[i].Record(in ctx, null);
                prev = slot.Down[i];
            }

            RenderTexture input = prev;
            for (int i = slot.Up.Length - 1; i >= 0; i--)
            {
                bloomUp[i].SetInput(0, input);
                bloomUp[i].SetInput(1, slot.Down[i]);
                BindMapLightField(bloomUp[i], 4);
                bloomUp[i].SetUniforms(in uniforms);
                var pu = new Vector4(1f / input.Width, 1f / input.Height, uniforms.BloomParams.W, 0f);
                bloomUp[i].SetPushConstants(in pu);
                bloomUp[i].Output = slot.Up[i];
                bloomUp[i].Record(in ctx, null);
                input = slot.Up[i];
            }
        }

        RenderTexture bloomTex = skipBuiltInBloom || slot.Bloom is null ? black1x1! : slot.Bloom;
        ulong writeOrdinal = persistentFrameOrdinal;

        for (int i = 0; i < customOffscreenPasses.Length; i++)
        {
            ShaderPackPassSpec passSpec = Spec.Passes[i];
            PostEffectPass pass = customOffscreenPasses[i];
            foreach (ShaderPackInputBinding input in passSpec.Inputs)
            {
                BindInputSource(pass, input.Binding, input.Source, slot, bloomTex, scene, depthTex, ctx.SubmissionFrameIndex, writeOrdinal);
            }
            pass.SetUniforms(in uniforms);
            RenderTexture output = ResolveWriteTarget(passSpec.OutputTarget, ctx.SubmissionFrameIndex, writeOrdinal);
            var pc = new Vector4(1f / output.Width, 1f / output.Height, passSpec.ExtraParams.X, passSpec.ExtraParams.Y);
            pass.SetPushConstants(in pc);
            pass.Output = output;
            pass.Record(in ctx, null);
        }

        lastRenderedPersistentOrdinal = writeOrdinal;
        persistentFrameOrdinal = writeOrdinal + 1;
    }

    internal void RecordComposite(
        in FrameContext ctx,
        HdrTargets.Slot slot,
        RenderTexture scene,
        in ShaderPackUniforms uniforms,
        bool hasHdrDraws,
        bool hasSceneDepth)
    {
        RenderTexture depthTex = hasSceneDepth ? slot.DepthSnapshot : depth1x1!;
        bool skipBuiltInBloom = Spec.BloomMipLevels == 0 || (!hasHdrDraws && !hasCustomBloomShader);
        RenderTexture bloomTex = skipBuiltInBloom || slot.Bloom is null ? black1x1! : slot.Bloom;

        if (Spec.Passes.Count == 0)
        {
            backbufferPass.SetInput(0, slot.Color);
            backbufferPass.SetInput(1, bloomTex);
            backbufferPass.SetInput(2, scene);
            backbufferPass.SetInput(3, depthTex);
            BindMapLightField(backbufferPass, 4);
            backbufferPass.SetUniforms(in uniforms);
            var pc = new Vector4(uniforms.BloomParams.X, 0f, 1f / scene.Width, 1f / scene.Height);
            backbufferPass.SetPushConstants(in pc);
            backbufferPass.RecordInline(in ctx, ctx.RenderPass, ctx.SampleCount);
            return;
        }

        ShaderPackPassSpec finalSpec = Spec.Passes[^1];
        if (finalPassUsesBuiltInComposite && finalSpec.Inputs.Count == 0)
        {
            backbufferPass.SetInput(0, slot.Color);
            backbufferPass.SetInput(1, bloomTex);
            backbufferPass.SetInput(2, scene);
            backbufferPass.SetInput(3, depthTex);
            BindMapLightField(backbufferPass, 4);
        }
        else
        {
            foreach (ShaderPackInputBinding input in finalSpec.Inputs)
            {
                BindInputSource(backbufferPass, input.Binding, input.Source, slot, bloomTex, scene, depthTex, ctx.SubmissionFrameIndex, lastRenderedPersistentOrdinal);
            }
        }
        backbufferPass.SetUniforms(in uniforms);
        Vector4 finalPc = finalPassUsesBuiltInComposite
            ? new Vector4(uniforms.BloomParams.X, 0f, 1f / scene.Width, 1f / scene.Height)
            : new Vector4(1f / scene.Width, 1f / scene.Height, finalSpec.ExtraParams.X, finalSpec.ExtraParams.Y);
        backbufferPass.SetPushConstants(in finalPc);
        backbufferPass.RecordInline(in ctx, ctx.RenderPass, ctx.SampleCount);
    }

    internal void InvalidateOffscreen()
    {
        DisposeCustomTargets();
        foreach (PostEffectPass? pass in bloomDown) pass?.Invalidate();
        foreach (PostEffectPass? pass in bloomUp) pass?.Invalidate();
        foreach (PostEffectPass? pass in customOffscreenPasses) pass?.Invalidate();
    }

    internal void InvalidateInline()
    {
        backbufferPass?.Invalidate();
    }

    private void EnsurePlaceholdersAndTargets(CommandBuffer cmd, int sceneWidth, int sceneHeight)
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
            ClearAndMakeSampleable(cmd, depth1x1, 1f, 0f, 0f, 0f);
            placeholdersCleared = true;
        }

        if (Spec.Targets.Count == 0)
            return;

        if (customTargets.Count > 0 && (targetWidth != sceneWidth || targetHeight != sceneHeight))
        {
            app.Device.WaitIdle();
            DisposeCustomTargets();
        }

        if (customTargets.Count == 0)
        {
            targetWidth = sceneWidth;
            targetHeight = sceneHeight;
            foreach (ShaderPackTargetSpec spec in Spec.Targets)
            {
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
                    customTargets[spec.Name] = new CustomTargetAllocation(spec, ring);
                }
                catch
                {
                    foreach (RenderTexture? tex in ring) tex?.Dispose();
                    DisposeCustomTargets();
                    throw;
                }
            }
        }

        foreach (CustomTargetAllocation alloc in customTargets.Values)
        {
            if (alloc.NeedsPersistentClear)
            {
                foreach (RenderTexture tex in alloc.Ring)
                    ClearAndMakeSampleable(cmd, tex, 0f, 0f, 0f, 0f);
                alloc.NeedsPersistentClear = false;
            }
        }
    }

    private RenderTexture ResolveWriteTarget(string name, int submissionIndex, ulong writeOrdinal)
    {
        CustomTargetAllocation alloc = customTargets[name];
        if (alloc.Spec.Persistent)
        {
            int idx = (int)(writeOrdinal % (ulong)alloc.Ring.Length);
            return alloc.Ring[idx];
        }
        return alloc.Ring[submissionIndex % alloc.Ring.Length];
    }

    private void BindInputSource(
        PostEffectPass pass,
        int binding,
        string source,
        HdrTargets.Slot slot,
        RenderTexture bloomTex,
        RenderTexture scene,
        RenderTexture depthTex,
        int submissionIndex,
        ulong writeOrdinal)
    {
        if (string.Equals(source, "hdr", StringComparison.OrdinalIgnoreCase))
        {
            pass.SetInput(binding, slot.Color);
            return;
        }
        if (string.Equals(source, "bloom", StringComparison.OrdinalIgnoreCase))
        {
            pass.SetInput(binding, bloomTex);
            return;
        }
        if (string.Equals(source, "scene", StringComparison.OrdinalIgnoreCase))
        {
            pass.SetInput(binding, scene);
            return;
        }
        if (string.Equals(source, "depth", StringComparison.OrdinalIgnoreCase))
        {
            pass.SetInput(binding, depthTex);
            return;
        }
        if (string.Equals(source, "mapLightField", StringComparison.OrdinalIgnoreCase))
        {
            BindMapLightField(pass, binding);
            return;
        }
        if (source.StartsWith("prev:", StringComparison.OrdinalIgnoreCase))
        {
            string targetName = source[5..];
            CustomTargetAllocation prevAlloc = customTargets[targetName];
            int prevIdx = (int)((writeOrdinal + (ulong)prevAlloc.Ring.Length - 1UL) % (ulong)prevAlloc.Ring.Length);
            pass.SetInput(binding, prevAlloc.Ring[prevIdx]);
            return;
        }

        CustomTargetAllocation alloc = customTargets[source];
        RenderTexture tex = alloc.Spec.Persistent
            ? alloc.Ring[(int)(writeOrdinal % (ulong)alloc.Ring.Length)]
            : alloc.Ring[submissionIndex % alloc.Ring.Length];
        pass.SetInput(binding, tex);
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

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

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
        foreach (CustomTargetAllocation alloc in customTargets.Values)
            alloc.Dispose();
        customTargets.Clear();
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
        backbufferPass?.Dispose();
    }
}
