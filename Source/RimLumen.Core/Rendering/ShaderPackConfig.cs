using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace RimLumen.Core.Rendering;

public enum ShaderPackTargetFormat
{
    R16G16B16A16SFloat,
    R8G8B8A8UNorm,
    R16SFloat,
}

public enum ShaderPackTargetFilter
{
    Linear,
    Point,
}

public enum ShaderPackBlendMode
{
    Opaque,
    PremultipliedAlpha,
    Additive,
}

public sealed record ShaderPackTargetSpec(
    string Name,
    ShaderPackTargetFormat Format = ShaderPackTargetFormat.R16G16B16A16SFloat,
    float Scale = 1.0f,
    ShaderPackTargetFilter Filter = ShaderPackTargetFilter.Linear,
    bool Persistent = false);

public sealed record ShaderPackInputBinding(int Binding, string Source);

public sealed record ShaderPackPassSpec(
    string Name,
    string? FragmentShaderPath,
    string OutputTarget,
    IReadOnlyList<ShaderPackInputBinding> Inputs,
    ShaderPackBlendMode BlendMode = ShaderPackBlendMode.Opaque,
    Vector2 ExtraParams = default);

public sealed class ShaderPackSpec
{
    public static ShaderPackSpec Default { get; } = new();

    public string Id { get; init; } = "default";
    public string? CompositeShaderPath { get; init; }
    public string? BloomPrefilterShaderPath { get; init; }
    public string? BloomDownShaderPath { get; init; }
    public string? BloomUpShaderPath { get; init; }
    public bool RunOnEmptyFrames { get; init; }
    public bool RequiresSceneDepth { get; init; }
    public bool RequiresMapLightField { get; init; }
    public int BloomMipLevels { get; init; } = 5;
    public IReadOnlyList<ShaderPackTargetSpec> Targets { get; init; } = Array.Empty<ShaderPackTargetSpec>();
    public IReadOnlyList<ShaderPackPassSpec> Passes { get; init; } = Array.Empty<ShaderPackPassSpec>();
}

public sealed class ShaderPackRuntimeParams
{
    private readonly object sync = new();
    private float bloomIntensity = 0.16f;
    private float bloomThreshold = 1.0f;
    private float bloomKnee = 0.5f;
    private float bloomScatter = 0.65f;
    private Vector4 packParam0;
    private Vector4 packParam1;
    private Vector4 packParam2;
    private Vector4 packParam3;

    public float BloomIntensity
    {
        get { lock (sync) return bloomIntensity; }
        set { lock (sync) bloomIntensity = value; }
    }

    public float BloomThreshold
    {
        get { lock (sync) return bloomThreshold; }
        set { lock (sync) bloomThreshold = value; }
    }

    public float BloomKnee
    {
        get { lock (sync) return bloomKnee; }
        set { lock (sync) bloomKnee = value; }
    }

    public float BloomScatter
    {
        get { lock (sync) return bloomScatter; }
        set { lock (sync) bloomScatter = value; }
    }

    public Vector4 PackParam0
    {
        get { lock (sync) return packParam0; }
        set { lock (sync) packParam0 = value; }
    }

    public Vector4 PackParam1
    {
        get { lock (sync) return packParam1; }
        set { lock (sync) packParam1 = value; }
    }

    public Vector4 PackParam2
    {
        get { lock (sync) return packParam2; }
        set { lock (sync) packParam2 = value; }
    }

    public Vector4 PackParam3
    {
        get { lock (sync) return packParam3; }
        set { lock (sync) packParam3 = value; }
    }

    public float GetPackParam(int index)
    {
        if ((uint)index >= 16u) throw new ArgumentOutOfRangeException(nameof(index), index, "PackParam index must be in [0, 15].");
        lock (sync)
        {
            return (index >> 2) switch
            {
                0 => GetLane(packParam0, index & 3),
                1 => GetLane(packParam1, index & 3),
                2 => GetLane(packParam2, index & 3),
                _ => GetLane(packParam3, index & 3),
            };
        }
    }

    public void SetPackParam(int index, float value)
    {
        if ((uint)index >= 16u) throw new ArgumentOutOfRangeException(nameof(index), index, "PackParam index must be in [0, 15].");
        lock (sync)
        {
            switch (index >> 2)
            {
                case 0: packParam0 = SetLane(packParam0, index & 3, value); break;
                case 1: packParam1 = SetLane(packParam1, index & 3, value); break;
                case 2: packParam2 = SetLane(packParam2, index & 3, value); break;
                default: packParam3 = SetLane(packParam3, index & 3, value); break;
            }
        }
    }

    // Ensures: all eight values become visible to CaptureSnapshot together, never a mix of old and new.
    internal void SetAll(
        float bloomIntensity, float bloomThreshold, float bloomKnee, float bloomScatter,
        in Vector4 p0, in Vector4 p1, in Vector4 p2, in Vector4 p3)
    {
        lock (sync)
        {
            this.bloomIntensity = bloomIntensity;
            this.bloomThreshold = bloomThreshold;
            this.bloomKnee = bloomKnee;
            this.bloomScatter = bloomScatter;
            packParam0 = p0;
            packParam1 = p1;
            packParam2 = p2;
            packParam3 = p3;
        }
    }

    internal ShaderPackUniformBlockSnapshot CaptureSnapshot()
    {
        lock (sync)
        {
            return new ShaderPackUniformBlockSnapshot(
                bloomIntensity, bloomThreshold, bloomKnee, bloomScatter,
                packParam0, packParam1, packParam2, packParam3);
        }
    }

    private static float GetLane(Vector4 v, int lane) => lane switch
    {
        0 => v.X,
        1 => v.Y,
        2 => v.Z,
        _ => v.W,
    };

    private static Vector4 SetLane(Vector4 v, int lane, float value) => lane switch
    {
        0 => new Vector4(value, v.Y, v.Z, v.W),
        1 => new Vector4(v.X, value, v.Z, v.W),
        2 => new Vector4(v.X, v.Y, value, v.W),
        _ => new Vector4(v.X, v.Y, v.Z, value),
    };
}

internal readonly record struct ShaderPackUniformBlockSnapshot(
    float BloomIntensity,
    float BloomThreshold,
    float BloomKnee,
    float BloomScatter,
    Vector4 PackParam0,
    Vector4 PackParam1,
    Vector4 PackParam2,
    Vector4 PackParam3);

// 208-byte std140 UBO bound at set=0, binding=5 across ShaderPack passes.
[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct ShaderPackUniforms
{
    public Matrix4x4 InvViewProjection; // 0 (64B)
    public Vector4 ScreenAndMapSize;    // 64 (16B): (1/screenW, 1/screenH, mapSizeX, mapSizeZ)
    public float GameSeconds;          // 80
    public float RealSeconds;          // 84
    public float SkyGlow;              // 88
    public float ZoomRootSize;         // 92
    public float HasHdrDraws;          // 96
    public float HasSceneDepth;        // 100
    public float HasMapLightField;     // 104
    public float IsPaused;             // 108
    public float DepthValid;           // 112: 1 for a real boundary snapshot, 0 for placeholder
    public float GameDeltaSeconds;     // 116
    public float RealDeltaSeconds;     // 120
    public float ColorBlendSpace;      // 124: 0 Legacy (hdr/bloom in stored units), 1 Linear (linear radiance)
    public Vector4 BloomParams;         // 128
    public Vector4 PackParam0;          // 144
    public Vector4 PackParam1;          // 160
    public Vector4 PackParam2;          // 176
    public Vector4 PackParam3;          // 192

    internal static ShaderPackUniforms Build(
        in Matrix4x4 viewProjection,
        int screenWidth,
        int screenHeight,
        int mapSizeX,
        int mapSizeZ,
        float gameSeconds,
        float realSeconds,
        float gameDeltaSeconds,
        float realDeltaSeconds,
        float skyGlow,
        float zoomRootSize,
        bool hasHdrDraws,
        bool hasSceneDepth,
        bool hasMapLightField,
        bool isPaused,
        bool linearBlendSpace,
        in ShaderPackUniformBlockSnapshot runtime)
    {
        Matrix4x4 invViewProj = Matrix4x4.Invert(viewProjection, out Matrix4x4 inv) ? inv : Matrix4x4.Identity;
        return new ShaderPackUniforms
        {
            InvViewProjection = invViewProj,
            ScreenAndMapSize = new Vector4(
                screenWidth > 0 ? 1f / screenWidth : 0f,
                screenHeight > 0 ? 1f / screenHeight : 0f,
                mapSizeX,
                mapSizeZ),
            GameSeconds = gameSeconds, RealSeconds = realSeconds,
            GameDeltaSeconds = gameDeltaSeconds, RealDeltaSeconds = realDeltaSeconds,
            SkyGlow = skyGlow, ZoomRootSize = zoomRootSize,
            HasHdrDraws = hasHdrDraws ? 1f : 0f, HasSceneDepth = hasSceneDepth ? 1f : 0f,
            HasMapLightField = hasMapLightField ? 1f : 0f, IsPaused = isPaused ? 1f : 0f,
            DepthValid = hasSceneDepth ? 1f : 0f, ColorBlendSpace = linearBlendSpace ? 1f : 0f,
            BloomParams = new Vector4(runtime.BloomIntensity, runtime.BloomThreshold, runtime.BloomKnee, runtime.BloomScatter),
            PackParam0 = runtime.PackParam0,
            PackParam1 = runtime.PackParam1,
            PackParam2 = runtime.PackParam2,
            PackParam3 = runtime.PackParam3,
        };
    }
}
