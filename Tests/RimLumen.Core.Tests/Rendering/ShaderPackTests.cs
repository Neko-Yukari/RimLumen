using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Xml.Linq;
using RimLumen.Core.Lighting;
using RimLumen.Core.Rendering;
using RimLumen.Core.Rendering.Logic;
using RimLumen.Core.Rendering.Probes;
using UnityStubs.Rendering;
using Waylith.Rendering;

namespace RimLumen.Core.Tests.Rendering;

[LumenTestSuite]
public static class ShaderPackTests
{
    [LumenFact]
    public static void ShaderPackUniformsMatchesStd140Layout()
    {
        Require(Marshal.SizeOf<ShaderPackUniforms>() == 176,
            $"Expected 176 bytes, got {Marshal.SizeOf<ShaderPackUniforms>()}.");
        Equal(0, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.InvViewProjection)).ToInt32());
        Equal(64, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.ScreenAndMapSize)).ToInt32());
        Equal(80, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.EnvParams)).ToInt32());
        Equal(96, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.BloomParams)).ToInt32());
        Equal(112, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.PackParam0)).ToInt32());
        Equal(128, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.PackParam1)).ToInt32());
        Equal(144, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.PackParam2)).ToInt32());
        Equal(160, Marshal.OffsetOf<ShaderPackUniforms>(nameof(ShaderPackUniforms.PackParam3)).ToInt32());
    }

    [LumenFact]
    public static void RuntimeParamsIndexesAll16PackParamsAndValidatesBounds()
    {
        var p = new ShaderPackRuntimeParams();
        Equal(0.16f, p.BloomIntensity);
        Equal(1.0f, p.BloomThreshold);
        Equal(0.5f, p.BloomKnee);
        Equal(0.65f, p.BloomScatter);
        for (int i = 0; i < 16; i++)
        {
            p.SetPackParam(i, i * 0.5f + 1.25f);
        }
        for (int i = 0; i < 16; i++)
        {
            Equal(i * 0.5f + 1.25f, p.GetPackParam(i));
        }
        Equal(new Vector4(1.25f, 1.75f, 2.25f, 2.75f), p.PackParam0);
        Equal(new Vector4(7.25f, 7.75f, 8.25f, 8.75f), p.PackParam3);
        Throws<ArgumentOutOfRangeException>(() => p.GetPackParam(-1));
        Throws<ArgumentOutOfRangeException>(() => p.GetPackParam(16));
        Throws<ArgumentOutOfRangeException>(() => p.SetPackParam(-1, 0f));
        Throws<ArgumentOutOfRangeException>(() => p.SetPackParam(16, 0f));
    }

    [LumenFact]
    public static void GraphValidatorAcceptsValidDagAndPersistentHistory()
    {
        ShaderPackGraphValidator.Validate(ShaderPackSpec.Default);

        var valid = new ShaderPackSpec
        {
            Id = "valid-multipass",
            BloomMipLevels = 0,
            RequiresSceneDepth = true,
            RequiresMapLightField = true,
            Targets =
            [
                new ShaderPackTargetSpec("history", ShaderPackTargetFormat.R16G16B16A16SFloat, 1.0f, ShaderPackTargetFilter.Linear, Persistent: true),
                new ShaderPackTargetSpec("blur", ShaderPackTargetFormat.R16SFloat, 0.5f, ShaderPackTargetFilter.Point, Persistent: false),
            ],
            Passes =
            [
                new ShaderPackPassSpec(
                    "accum",
                    "accum.frag.glsl",
                    "history",
                    [
                        new ShaderPackInputBinding(0, "scene"),
                        new ShaderPackInputBinding(1, "prev:history"),
                        new ShaderPackInputBinding(2, "depth"),
                        new ShaderPackInputBinding(3, "mapLightField"),
                    ],
                    ShaderPackBlendMode.Opaque),
                new ShaderPackPassSpec(
                    "blur",
                    "blur.frag.glsl",
                    "blur",
                    [new ShaderPackInputBinding(0, "history")],
                    ShaderPackBlendMode.Opaque),
                new ShaderPackPassSpec(
                    "composite",
                    null,
                    "backbuffer",
                    [
                        new ShaderPackInputBinding(0, "hdr"),
                        new ShaderPackInputBinding(1, "bloom"),
                        new ShaderPackInputBinding(2, "scene"),
                        new ShaderPackInputBinding(3, "blur"),
                    ],
                    ShaderPackBlendMode.PremultipliedAlpha),
            ],
        };
        ShaderPackGraphValidator.Validate(valid);
    }

    [LumenFact]
    public static void GraphValidatorRejectsCyclesSelfReadsReservedTargetsAndBadBindings()
    {
        // Bad mip levels
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec { BloomMipLevels = -1 }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec { BloomMipLevels = 8 }));

        // Reserved target names and prefix
        foreach (string badName in new[] { "hdr", "bloom", "scene", "depth", "mapLightField", "backbuffer", "prev:foo", "" })
        {
            Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
            {
                Targets = [new ShaderPackTargetSpec(badName)],
            }));
        }

        // Duplicate target names & invalid scale
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a"), new ShaderPackTargetSpec("A")],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a", Scale: 0.05f)],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a", Scale: 1.01f)],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a", Scale: float.NaN)],
        }));

        // Last pass must be backbuffer; earlier passes must be opaque with non-empty shader
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a")],
            Passes = [new ShaderPackPassSpec("p0", "a.frag.glsl", "a", [])],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a")],
            Passes =
            [
                new ShaderPackPassSpec("p0", "a.frag.glsl", "backbuffer", []),
                new ShaderPackPassSpec("p1", "b.frag.glsl", "a", []),
            ],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a")],
            Passes =
            [
                new ShaderPackPassSpec("p0", "a.frag.glsl", "a", [], ShaderPackBlendMode.Additive),
                new ShaderPackPassSpec("p1", null, "backbuffer", []),
            ],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a")],
            Passes =
            [
                new ShaderPackPassSpec("p0", null, "a", []),
                new ShaderPackPassSpec("p1", null, "backbuffer", []),
            ],
        }));

        // Binding 5 is reserved for UBO; out-of-range and duplicate bindings rejected
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Passes = [new ShaderPackPassSpec("p0", "c.frag.glsl", "backbuffer", [new ShaderPackInputBinding(5, "scene")])],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Passes = [new ShaderPackPassSpec("p0", "c.frag.glsl", "backbuffer", [new ShaderPackInputBinding(16, "scene")])],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Passes = [new ShaderPackPassSpec("p0", "c.frag.glsl", "backbuffer", [new ShaderPackInputBinding(0, "scene"), new ShaderPackInputBinding(0, "hdr")])],
        }));

        // Forward reference / self-read / cycle / non-persistent prev
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a"), new ShaderPackTargetSpec("b")],
            Passes =
            [
                new ShaderPackPassSpec("p0", "a.frag.glsl", "a", [new ShaderPackInputBinding(0, "b")]),
                new ShaderPackPassSpec("p1", "b.frag.glsl", "b", [new ShaderPackInputBinding(0, "a")]),
                new ShaderPackPassSpec("p2", null, "backbuffer", []),
            ],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a")],
            Passes =
            [
                new ShaderPackPassSpec("p0", "a.frag.glsl", "a", [new ShaderPackInputBinding(0, "a")]),
                new ShaderPackPassSpec("p1", null, "backbuffer", []),
            ],
        }));
        Throws<ArgumentException>(() => ShaderPackGraphValidator.Validate(new ShaderPackSpec
        {
            Targets = [new ShaderPackTargetSpec("a", Persistent: false)],
            Passes =
            [
                new ShaderPackPassSpec("p0", "a.frag.glsl", "a", [new ShaderPackInputBinding(0, "prev:a")]),
                new ShaderPackPassSpec("p1", null, "backbuffer", []),
            ],
        }));
    }

    [LumenFact]
    public static void MapLightFieldEncoderMatchesVanillaGlowAndEnclosure()
    {
        Span<byte> rgba = stackalloc byte[4];

        // Sunlit cell (a == 1): GroundGlow = 255, unroofed => R=0, B=0, unfogged => A=0
        MapLightFieldEncoder.EncodeCell(roofed: false, 10, 20, 30, 1, usesOutdoorTemp: true, fogged: false, rgba);
        Equal((byte)0, rgba[0]);
        Equal((byte)255, rgba[1]);
        Equal((byte)0, rgba[2]);
        Equal((byte)0, rgba[3]);

        // Roofed outdoor (porch) with dark glow, fogged
        MapLightFieldEncoder.EncodeCell(roofed: true, 0, 0, 0, 0, usesOutdoorTemp: true, fogged: true, rgba);
        Equal((byte)255, rgba[0]);
        Equal((byte)0, rgba[1]);
        Equal((byte)128, rgba[2]);
        Equal((byte)255, rgba[3]);

        // Roofed indoor with max torch glow (capped at 0.5 * 255 = 128)
        MapLightFieldEncoder.EncodeCell(roofed: true, 255, 128, 0, 0, usesOutdoorTemp: false, fogged: false, rgba);
        Equal((byte)255, rgba[0]);
        Equal((byte)128, rgba[1]);
        Equal((byte)255, rgba[2]);
        Equal((byte)0, rgba[3]);

        // Mid torch glow: r = 20 => 20/255 * 3.6 = 0.28235 -> round(0.28235 * 255) = 72
        MapLightFieldEncoder.EncodeCell(roofed: false, 20, 0, 0, 0, usesOutdoorTemp: true, fogged: false, rgba);
        Equal((byte)72, rgba[1]);
    }

    [LumenFact]
    public static void HdrFrameGateHonorsRunOnEmptyFrames()
    {
        Equal(false, HdrFrameGate.ShouldRenderHdr(0, false, false));
        Equal(true, HdrFrameGate.ShouldRenderHdr(0, false, true));
        Equal(HdrFrameMode.Skip, HdrFrameGate.Decide(false, 0, false, false));
        Equal(HdrFrameMode.Render, HdrFrameGate.Decide(false, 0, false, true));
    }

    [LumenFact]
    public static void UniformsBuildEncodesInverseScreenSizeFlagsAndLiveParams()
    {
        var runtime = new ShaderPackRuntimeParams { BloomIntensity = 0.3f, BloomThreshold = 1.2f, BloomKnee = 0.4f, BloomScatter = 0.7f };
        runtime.SetPackParam(2, 9f);
        runtime.SetPackParam(15, -3f);
        ShaderPackUniformBlockSnapshot snapshot = runtime.CaptureSnapshot();
        Matrix4x4 vp = Matrix4x4.CreateOrthographic(40f, 22.5f, 0.3f, 200f);

        ShaderPackUniforms u = ShaderPackUniforms.Build(
            in vp, 1920, 1080, 250, 200, 12.5f, 0.75f, 24f,
            hasHdrDraws: true, hasSceneDepth: true, hasMapLightField: false, isPaused: false, in snapshot);

        Equal(1f / 1920f, u.ScreenAndMapSize.X);
        Equal(1f / 1080f, u.ScreenAndMapSize.Y);
        Equal(250f, u.ScreenAndMapSize.Z);
        Equal(200f, u.ScreenAndMapSize.W);
        Equal(12.5f, u.EnvParams.X);
        Equal(0.75f, u.EnvParams.Y);
        Equal(24f, u.EnvParams.Z);
        Equal(3f, u.EnvParams.W);
        Require(Matrix4x4.Invert(vp, out Matrix4x4 inv), "ViewProjection must be invertible.");
        Equal(inv, u.InvViewProjection);
        Equal(new Vector4(0.3f, 1.2f, 0.4f, 0.7f), u.BloomParams);
        Equal(new Vector4(0f, 0f, 9f, 0f), u.PackParam0);
        Equal(new Vector4(0f, 0f, 0f, -3f), u.PackParam3);

        // All four flag bits; bit 3 is paused.
        ShaderPackUniforms all = ShaderPackUniforms.Build(
            in vp, 800, 600, 1, 1, 0f, 1f, 24f,
            hasHdrDraws: true, hasSceneDepth: true, hasMapLightField: true, isPaused: true, in snapshot);
        Equal(15f, all.EnvParams.W);
        ShaderPackUniforms none = ShaderPackUniforms.Build(
            in vp, 0, 0, 0, 0, 0f, 1f, 24f,
            hasHdrDraws: false, hasSceneDepth: false, hasMapLightField: false, isPaused: false, in snapshot);
        Equal(0f, none.EnvParams.W);
        Equal(0f, none.ScreenAndMapSize.X);
    }

    [LumenFact]
    public static void InvViewProjectionUnprojectsScreenUvOntoGroundPlane()
    {
        // Waylith composes camera matrices column-vector style and Slang stores a UBO mat4 RowMajor,
        // so a shader's `M * v` is dot(row, v). Both conventions are pinned against Build() here.
        VerifyGroundPlaneUnproject(Matrix4x4.CreateOrthographic(40f, 22.5f, 0.3f, 200f));

        // IsoRim-style camera: the shim squashes the vertical axis and tilts the view.
        Matrix4x4 squashed = Matrix4x4.CreateOrthographic(40f, 22.5f, 0.3f, 200f);
        squashed.M22 *= 0.62f;
        VerifyGroundPlaneUnproject(squashed);
    }

    [LumenFact]
    public static void ShaderPackFailurePolicyIsolatesCustomPacks()
    {
        Equal(ShaderPackFailureAction.RevertPackToDefault, ShaderPackFailurePolicy.Decide(isDefaultPack: false, verboseDiagnostics: false));
        Equal(ShaderPackFailureAction.RevertPackToDefault, ShaderPackFailurePolicy.Decide(isDefaultPack: false, verboseDiagnostics: true));
        Equal(ShaderPackFailureAction.TripFailSafe, ShaderPackFailurePolicy.Decide(isDefaultPack: true, verboseDiagnostics: false));
        Equal(ShaderPackFailureAction.FailLoud, ShaderPackFailurePolicy.Decide(isDefaultPack: true, verboseDiagnostics: true));

        var custom = new ShaderPackSpec { Id = "custom" };
        Require(RimLumenRenderHost.ShouldRevertPackOnFailure(custom), "Custom pack failures must revert the pack.");
        Require(!RimLumenRenderHost.ShouldTripFailSafeOnFailure(custom), "Custom pack failures must never trip the fail-safe.");
        Require(RimLumenRenderHost.ShouldTripFailSafeOnFailure(ShaderPackSpec.Default), "Default pack failures must trip the fail-safe.");
        Require(!RimLumenRenderHost.ShouldRevertPackOnFailure(ShaderPackSpec.Default), "The default pack cannot revert to itself.");

        bool verbose = RimLumenRenderHost.VerboseDiagnostics;
        try
        {
            RimLumenRenderHost.VerboseDiagnostics = true;
            Require(!RimLumenRenderHost.ShouldTripFailSafeOnFailure(ShaderPackSpec.Default), "Probe runs must stay fail-loud for the default pack.");
            Require(RimLumenRenderHost.ShouldRevertPackOnFailure(custom), "Probe runs must still isolate a custom pack.");
        }
        finally
        {
            RimLumenRenderHost.VerboseDiagnostics = verbose;
        }

        // A failure report for a pack that is no longer active must not touch the live pack.
        Equal(false, RimLumenRenderHost.RevertFailedPackToDefault(custom));
        Equal("default", RimLumenRenderHost.ActiveShaderPack.Id);
    }

    [LumenFact]
    public static void VanillaAdditiveBloomDefaultsOn()
    {
        // Player-facing default; the probe explicitly disables it for the P1-idle frame.
        Equal(true, RimLumenRenderHost.EnableVanillaAdditiveBloom);
    }

    private static void VerifyGroundPlaneUnproject(Matrix4x4 projection)
    {
        // Column-vector camera: view = rotation with the translation in the fourth column, so the shader's
        // `InvViewProjection * v` (dot(row, v)) is the same algebra the C# side composed.
        Vector3 eye = new(51.25f, 30f, 60f);
        Vector3 target = new(51.25f, 0f, 40f);
        Vector3 zAxis = Vector3.Normalize(eye - target);
        Vector3 xAxis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, zAxis));
        Vector3 yAxis = Vector3.Cross(zAxis, xAxis);
        Matrix4x4 view = new(
            xAxis.X, xAxis.Y, xAxis.Z, -Vector3.Dot(xAxis, eye),
            yAxis.X, yAxis.Y, yAxis.Z, -Vector3.Dot(yAxis, eye),
            zAxis.X, zAxis.Y, zAxis.Z, -Vector3.Dot(zAxis, eye),
            0f, 0f, 0f, 1f);
        Matrix4x4 viewProjection = projection * view;

        var runtime = new ShaderPackRuntimeParams();
        runtime.BloomIntensity = 0.3f;
        runtime.SetPackParam(0, 0.375f);
        ShaderPackUniforms uniforms = ShaderPackUniforms.Build(viewProjection, 1920, 1080, 250, 250, 7.5f, 0.8f, 24f,
            hasHdrDraws: true, hasSceneDepth: true, hasMapLightField: true, isPaused: false, runtime.CaptureSnapshot());

        Equal(1f / 1920f, uniforms.ScreenAndMapSize.X);
        Equal(250f, uniforms.ScreenAndMapSize.Z);
        Equal(0.3f, uniforms.BloomParams.X);
        Equal(0.375f, uniforms.PackParam0.X);
        Equal(7.5f, uniforms.EnvParams.X);

        Vector3 ground = new(51.25f, 0f, 36.75f);
        Vector4 clip = SlangMultiply(viewProjection, new Vector4(ground, 1f));
        Vector2 ndc = new(clip.X / clip.W, clip.Y / clip.W);

        Vector3 nearPoint = SlangUnproject(uniforms.InvViewProjection, ndc, 0f);
        Vector3 farPoint = SlangUnproject(uniforms.InvViewProjection, ndc, 1f);
        float span = farPoint.Y - nearPoint.Y;
        float t = MathF.Abs(span) < 1e-6f ? 0f : -nearPoint.Y / span;
        Vector3 recovered = Vector3.Lerp(nearPoint, farPoint, Math.Clamp(t, 0f, 1f));
        Require(Vector3.Distance(ground, recovered) < 5e-3f, $"Expected {ground}; recovered {recovered}.");

        // An accidental transpose moves the translation out of the rows the shader dots with.
        Vector3 transposed = SlangUnproject(Matrix4x4.Transpose(uniforms.InvViewProjection), ndc, 0f);
        float transposedError = float.IsFinite(transposed.X) && float.IsFinite(transposed.Y) && float.IsFinite(transposed.Z)
            ? Vector3.Distance(ground, transposed)
            : float.PositiveInfinity;
        Require(transposedError > 1f, $"Transposed InvViewProjection must not unproject the ground plane (error {transposedError}).");
    }

    // Slang RowMajor: result_i = dot(matrix row i, v).
    private static Vector4 SlangMultiply(Matrix4x4 m, Vector4 v) => new(
        m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z + m.M14 * v.W,
        m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z + m.M24 * v.W,
        m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z + m.M34 * v.W,
        m.M41 * v.X + m.M42 * v.Y + m.M43 * v.Z + m.M44 * v.W);

    private static Vector3 SlangUnproject(Matrix4x4 inverseViewProjection, Vector2 ndc, float z)
    {
        Vector4 p = SlangMultiply(inverseViewProjection, new Vector4(ndc.X, ndc.Y, z, 1f));
        return new Vector3(p.X / p.W, p.Y / p.W, p.Z / p.W);
    }

    [LumenFact]
    public static void ReloadShaderPackFailsSafelyAndCompilesValidSlangPack()
    {
        // Bad DAG fails cleanly without tripping fail-safe
        Equal(false, RimLumenRenderHost.ReloadShaderPack(new ShaderPackSpec { BloomMipLevels = 99 }));
        Require(RimLumenRenderHost.LastShaderPackError != null, "Expected LastShaderPackError after bad DAG.");
        Equal(false, RimLumenRenderHost.FailSafeTripped);

        // Missing shader file fails cleanly without tripping fail-safe
        Equal(false, RimLumenRenderHost.ReloadShaderPack(new ShaderPackSpec
        {
            CompositeShaderPath = "NonExistentShaderPackPass_12345.frag.glsl",
        }));
        Require(RimLumenRenderHost.LastShaderPackError != null, "Expected LastShaderPackError after missing shader.");
        Equal(false, RimLumenRenderHost.FailSafeTripped);

        // Slang compilation tests (valid + broken GLSL)
        WithSlangLoaded(() =>
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "opencode", "rimlumen-shaderpack-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string goodFrag = Path.Combine(tempDir, "TestComposite.frag.glsl");
                File.WriteAllText(goodFrag, """
                    #version 450
                    layout(location = 0) in vec2 vUv;
                    layout(set = 0, binding = 0) uniform sampler2D uHdr;
                    layout(set = 0, binding = 1) uniform sampler2D uBloom;
                    layout(set = 0, binding = 2) uniform sampler2D uScene;
                    layout(set = 0, binding = 3) uniform sampler2D uDepth;
                    layout(set = 0, binding = 4) uniform sampler2D uLightField;
                    layout(std140, set = 0, binding = 5) uniform ShaderPackUniforms
                    {
                        mat4 InvViewProjection;
                        vec4 ScreenAndMapSize;
                        vec4 EnvParams;
                        vec4 BloomParams;
                        vec4 PackParam0;
                        vec4 PackParam1;
                        vec4 PackParam2;
                        vec4 PackParam3;
                    } ubo;
                    layout(push_constant) uniform Push { vec4 value; } pc;
                    layout(location = 0) out vec4 outColor;
                    void main()
                    {
                        vec4 wp = ubo.InvViewProjection * vec4(vUv * 2.0 - 1.0, texture(uDepth, vUv).r, 1.0);
                        vec3 lf = texture(uLightField, wp.xz / max(ubo.ScreenAndMapSize.zw, vec2(1.0))).rgb;
                        vec3 hdr = texture(uHdr, vUv).rgb + texture(uBloom, vUv).rgb * ubo.BloomParams.x;
                        vec3 scene = texture(uScene, vUv).rgb;
                        outColor = vec4(scene + hdr + lf * 0.01 + pc.value.xxx * 0.0 + ubo.PackParam0.xyz * 0.0, 1.0);
                    }
                    """);

                var goodSpec = new ShaderPackSpec
                {
                    Id = "test-valid",
                    CompositeShaderPath = goodFrag,
                    RunOnEmptyFrames = true,
                    RequiresSceneDepth = true,
                    RequiresMapLightField = true,
                    BloomMipLevels = 4,
                };
                Equal(true, RimLumenRenderHost.ReloadShaderPack(goodSpec));
                Equal<string?>(null, RimLumenRenderHost.LastShaderPackError);
                Equal("test-valid", RimLumenRenderHost.ActiveShaderPack.Id);
                Equal(true, RimLumenRenderHost.IsMapLightFieldActive);

                // Bad GLSL must fail cleanly, preserve previous valid pack, and not trip fail-safe
                string badFrag = Path.Combine(tempDir, "Broken.frag.glsl");
                File.WriteAllText(badFrag, "#version 450\nvoid main() { outColor = undefined_symbol; }\n");
                Equal(false, RimLumenRenderHost.ReloadShaderPack(new ShaderPackSpec
                {
                    Id = "test-broken",
                    CompositeShaderPath = badFrag,
                }));
                Require(RimLumenRenderHost.LastShaderPackError != null, "Expected LastShaderPackError after Slang failure.");
                Equal(false, RimLumenRenderHost.FailSafeTripped);
                Equal("test-valid", RimLumenRenderHost.ActiveShaderPack.Id);

                // Reset back to Default
                Equal(true, RimLumenRenderHost.ReloadShaderPack(ShaderPackSpec.Default));
                Equal<string?>(null, RimLumenRenderHost.LastShaderPackError);
                Equal("default", RimLumenRenderHost.ActiveShaderPack.Id);
                Equal(false, RimLumenRenderHost.IsMapLightFieldActive);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        });
    }

    [LumenFact]
    public static void ProbeRouteBFixturesCompileThroughSlang()
    {
        WithSlangLoaded(() =>
        {
            foreach ((string name, string source) in new[]
            {
                ("ProbeAccum", ShaderPackProbeSources.Accum),
                ("ProbeBlur", ShaderPackProbeSources.Blur),
                ("ProbePrefilter", ShaderPackProbeSources.Prefilter),
                ("ProbeFinal", ShaderPackProbeSources.Final),
            })
            {
                byte[] spirv = RuntimeShaderCompiler.Compile(source, name + ".frag.glsl", "main",
                    RuntimeShaderLanguage.Glsl, RuntimeShaderStage.Fragment, out _);
                Require(spirv.Length >= 20 && BitConverter.ToUInt32(spirv) == 0x07230203u, $"{name}: invalid SPIR-V.");
                Console.WriteLine($"[SLANG] probe fixture {name}.frag.glsl: {spirv.Length} bytes");
            }
            // The broken fixture must actually fail; otherwise the real-machine fallback step proves nothing.
            bool threw = false;
            try
            {
                RuntimeShaderCompiler.Compile(ShaderPackProbeSources.Broken, "ProbeBroken.frag.glsl", "main",
                    RuntimeShaderLanguage.Glsl, RuntimeShaderStage.Fragment, out _);
            }
            catch (Exception)
            {
                threw = true;
            }
            Require(threw, "ProbeBroken.frag.glsl compiled cleanly, so the fallback probe would not exercise a failure.");
        });
    }

    // Guards the probe's non-vacuity contract: each channel must be driven by its own input path, so a
    // constant offset accidentally reintroduced here would make the real-machine readback pass for the
    // wrong reason.
    [LumenFact]
    public static void ProbeFixturesDriveEachChannelFromItsOwnInput()
    {
        string accum = ShaderPackProbeSources.Accum;
        Require(accum.Contains("clamp(min(depth, 1.0 - depth) * 10.0, 0.0, 1.0)", StringComparison.Ordinal),
            "Accum must map scene depth to R with no constant offset.");
        Require(accum.Contains("texture(lightField, clampedUv)", StringComparison.Ordinal),
            "Accum must sample the map light field through the unprojected ground position for G.");
        Require(accum.Contains("ubo.InvViewProjection * vec4(ndc, z, 1.0)", StringComparison.Ordinal),
            "Accum must unproject with the UBO inverse view projection.");
        Require(accum.Contains("(uv.x > 0.5 && uv.y > 0.5) ? (ubo.PackParam0.x + pc.value.z) : 0.0", StringComparison.Ordinal),
            "Accum must gate the parameter contribution to one quadrant for B.");
        Require(!accum.Contains("* 0.0", StringComparison.Ordinal) && !accum.Contains("* 0.00", StringComparison.Ordinal),
            "Accum must not contain dead-code multipliers that vanish under optimisation.");

        string blur = ShaderPackProbeSources.Blur;
        Require(blur.Contains("vec4(sum.rgb * 0.5, 1.0)", StringComparison.Ordinal),
            "Blur must write the two-tap average without adding a constant.");

        // The bloom prefilter pass binds exactly {0, 2, 3, 4}; declaring any other sampler (or using one the
        // runtime never writes) would make the real-machine bloom step UB.
        string prefilter = ShaderPackProbeSources.Prefilter;
        Require(prefilter.Contains("binding = 0) uniform sampler2D hdrImage", StringComparison.Ordinal)
            && prefilter.Contains("binding = 2) uniform sampler2D sceneImage", StringComparison.Ordinal)
            && prefilter.Contains("binding = 3) uniform sampler2D depthImage", StringComparison.Ordinal)
            && prefilter.Contains("binding = 4) uniform sampler2D lightField", StringComparison.Ordinal)
            && prefilter.Contains("binding = 1)", StringComparison.Ordinal) == false,
            "Prefilter must declare only the bindings the built-in bloom prefilter pass provides (0, 2, 3, 4).");
        Require(prefilter.Contains("texture(lightField, uv).g * 0.01", StringComparison.Ordinal),
            "Prefilter must mix the light field into the bloom source.");
        Require(prefilter.Contains("step(0.0001, geometry)", StringComparison.Ordinal),
            "Prefilter must mask non-geometry pixels (reverse-Z 0.0 and the 1.0 placeholder).");
    }

    [LumenFact]
    public static void BorrowedProxyAndCollectorReleaseSurviveTeardown()
    {
        // ReplaceShaderPack drops the proxy before the next HDR record rebinds it; Clear must null the borrowed
        // pointer without touching ownership.
        var proxy = new BorrowedRenderTexture();
        proxy.Clear();
        Require(proxy.GpuResource is null, "Clear must null the borrowed GpuResource.");
        Equal(0, proxy.width);
        Equal(0, proxy.height);

        // Faulted-render-thread release path: CPU mirror drops with no device waits and no throw.
        MapLightFieldCollector.Release(null, waitIdle: false);
        Equal(false, MapLightFieldCollector.TryGetDescriptor(out _, out _, out _, out _));
    }

    private static void WithSlangLoaded(Action action)
    {
        string repoRoot = RepoRoot();
        string buildProperties = Path.Combine(repoRoot, "Directory.Build.props");
        string gameRoot = Environment.GetEnvironmentVariable("GameRoot")
            ?? XDocument.Load(buildProperties).Descendants("GameRoot").Single().Value;
        string nativePath = Path.Combine(gameRoot, "slang.dll");
        nint ResolveSlang(Assembly assembly, string name)
        {
            if (assembly.GetName().Name != "Brutal.Slang" || name != "slang")
                return 0;
            return NativeLibrary.Load(nativePath, assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
        }
        string optimizerPath = Path.Combine(gameRoot, "slang-glslang.dll");
        nint optimizer = NativeLibrary.Load(optimizerPath, typeof(ShaderPackTests).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveSlang;
        try
        {
            action();
        }
        finally
        {
            AssemblyLoadContext.Default.ResolvingUnmanagedDll -= ResolveSlang;
            NativeLibrary.Free(optimizer);
        }
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Cannot locate repository root above test output.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; actual {actual}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
