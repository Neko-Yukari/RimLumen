using System.Numerics;
using System.Runtime.InteropServices;

namespace RimLumen.Core.ParticlesGpu;

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct GpuParticle
{
    public Vector4 PositionAndAge;
    public Vector4 VelocityAndLifetime;
    public Vector4 StartColor;
    public Vector4 SizeSeedEmitterFlags;
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
    public Vector4 ShapePosition; // node-local cells xyz, unused w
    public Vector4 ShapeRotation; // unit quaternion xyzw; rotates position and direction
    public Vector4 ShapeScale; // dimensionless xyz; affects sampled position only, unused w
    public Vector4 ShapeOptions; // offset 256: radiusThickness, arc radians, arcSpread, radiusSpread
    public Vector4 ShapeMix; // offset 272: randomDirection, sphericalDirection, randomPosition cells, coneLength cells
    public Vector4 BoxThicknessDonut; // offset 288: boxThickness xyz, donutRadius cells
    public Vector4 WorldRotation; // offset 304: unit world quaternion xyzw, independent of World scale/shear; stride 320
}

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
    public uint Padding1; // floor height as float bits
}

internal static class GpuParticleProperties
{
    public const string Particles = "_LumenParticles";
    public const string Emitters = "_LumenEmitters";
    public const string Curves = "_LumenCurves";
    public const string Texture = "_LumenParticleTexture";
    public const string SceneDepth = "_LumenSceneDepth";
}
