using System.Numerics;
using System.Runtime.InteropServices;

namespace RimLumen.Core.ParticlesGpu;

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct GpuParticle
{
    public Vector4 PositionAndAge;
    public Vector4 VelocityAndLifetime;
    public Vector4 StartColor;
    public float Size;
    public uint Seed;
    public uint ActiveEmitter; // 0 dead; emitter index + 1 while alive
    public uint SpawnAnchorXZ; // world-space birth ground anchor (node, or sub-emitter event point): int16 X | int16 Z << 16, 1/32 cell units
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct GpuEmitterParams
{
    public Matrix4x4 World;
    public Vector4 Slots; // base slot, capacity, spawn budget, spawn cursor
    public Vector4 Shape; // kind, extents xyz
    public Vector4 Ranges; // speed min/max, lifetime min/max
    public Vector4 SizeCone; // size min/max, cone radians, world-space flag
    public Vector4 StartColor;
    public Vector4 Flipbook; // tiles x/y, cycles, billboard mode
    public Vector4 Appearance; // stretch, length, HDR intensity, node-world size scale
    public Vector4 Tables; // LUT row, field offset, field count, random seed
    public Vector4 State; // dt, soft fade distance, clear range flag, intensity multiplier
    public Vector4 ShapePosition; // node-local cells xyz, w LumenDepthMask on (1) or off (0)
    public Vector4 ShapeRotation; // unit quaternion xyzw; rotates position and direction
    public Vector4 ShapeScale; // dimensionless xyz; affects sampled position only, w LumenDepthMask reference Z
    public Vector4 ShapeOptions; // offset 256: radiusThickness, arc radians, arcSpread, radiusSpread
    public Vector4 ShapeMix; // offset 272: randomDirection, sphericalDirection, randomPosition cells, coneLength cells
    public Vector4 BoxThicknessDonut; // offset 288: boxThickness xyz, donutRadius cells
    public Vector4 WorldRotation; // offset 304: unit world quaternion xyzw, independent of World scale/shear; world-physics extension begins at 320
    public Matrix4x4 WorldInverse;
    public Vector4 WorldPhysics; // node time, world-interaction enabled, unused xy
    public Vector4 Collision; // mode, bounce, dampen, lifetime loss
    public Vector4 Map; // cell offset, width, height, kill under roof
    public Vector4 Events; // event offset, capacity, GPU burst carry owner, unused
    public uint DensityQ;
    public uint ResetVersion;
    public uint ExplicitOffset;
    public uint ExplicitCount;
    public uint SourceLinkOffset;
    public uint SourceLinkCount;
    public uint StateSlot;
    public uint EmitEvents;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct GpuSubEvent
{
    public Vector4 PositionAndTrigger;
    public Vector4 Color;
    public Vector4 Size;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct GpuSubLink
{
    public uint Source, Trigger, Count, Inherit;
    public float Probability, Padding0, Padding1, Padding2;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct GpuExplicitBurst(uint Count, uint DensityQ, uint ResetVersion, uint Padding);

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct GpuForceField
{
    public Vector4 CenterOrAxisAndKind;
    public Vector4 ParametersAndRadius;
    public Vector4 StrengthAndFrequency;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct GpuCurveSample
{
    public Vector4 Color;
    public Vector4 Size;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct GpuSimPushConstants
{
    public uint EmitterIndex;
    public uint FrameSeed;
    public uint Padding0; // bit 0: KillBelowFloor; bits 1+: simulation phase
    public uint Padding1; // reserved
}

internal static class GpuParticleProperties
{
    public const string Particles = "_LumenParticles";
    public const string Emitters = "_LumenEmitters";
    public const string Curves = "_LumenCurves";
    public const string Texture = "_LumenParticleTexture";
    public const string SceneDepth = "_LumenSceneDepth";
}

// 80 bytes (5 * 16B), std430 matching ParticleTrailTypes.glsl GpuTrailHeader.
[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct GpuTrailHeader
{
    public Vector4 LiveHeadAndTotalLength;
    public Vector4 ClippedTailAndWidth;
    public Vector4 Color;
    public uint RingHead;
    public uint RingCount;
    public uint VisibleCount;
    public uint Flags;
    public uint ParticleSlot;
    public uint SpawnAnchorXZ;
    public float HeadCumDistance;
    public float ClippedTailCumDist;
}

// 32 bytes (2 * 16B), std430 matching ParticleTrailTypes.glsl GpuTrailPoint.
[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct GpuTrailPoint
{
    public Vector4 PositionAndRemaining;
    public Vector4 DistanceAndLifetime;
}

// 160 bytes (10 * 16B), std430 matching ParticleTrailTypes.glsl GpuTrailEmitterParams.
[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct GpuTrailEmitterParams
{
    public Matrix4x4 World;
    public Vector4 Ranges;
    public Vector4 PointsAndCurve;
    public Vector4 Params;
    public Vector4 Appearance;
    public Vector4 DepthAndParticle;
    public uint MapBase;
    public uint Flags;
    public uint BankCount;
    public uint RandomSeed;
}

internal static class GpuTrailProperties
{
    public const string Headers = "_LumenTrailHeaders";
    public const string Points = "_LumenTrailPoints";
    public const string Emitters = "_LumenTrailEmitters";
}
