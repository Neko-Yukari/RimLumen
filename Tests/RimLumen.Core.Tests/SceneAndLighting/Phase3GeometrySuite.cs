using System;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class Phase3GeometrySuite
{
    [LumenFact]
    public static void ConeVolumeExtendsItsCorrelatedBaseAlongTheSampledRay()
    {
        uint baseSeed = 1u, volumeSeed = 1u;
        ParticleShapeSampler.Sample(Cone(LumenShapeKind.Cone, 1f), ref baseSeed, out Vector3 basePosition, out Vector3 baseDirection);
        ParticleShapeSampler.Sample(Cone(LumenShapeKind.ConeVolume, 1f), ref volumeSeed, out Vector3 volumePosition, out Vector3 volumeDirection);
        // Upstream Shapes.cs:63-67: at 45 degrees, dx/dy equals the sampled base radius, here baseX/2.
        Check(basePosition.X > 0f && basePosition.X < 2f, "Filled cone samples inside the base radius.");
        Near(0f, basePosition.Y); Near(0f, basePosition.Z);
        Near(basePosition.X / 2f, baseDirection.X / baseDirection.Y);
        Vector(baseDirection, volumeDirection);
        Check(volumePosition.Y > 0f && volumePosition.Y < 3f, "Volume axial travel is bounded by ConeLength.");
        Near(basePosition.X + basePosition.X * volumePosition.Y / 2f, volumePosition.X);
        Near(0f, volumePosition.Z);
    }

    [LumenFact]
    public static void ConeShellAndZeroThicknessRetainTheBaseRimAndVolumeShellRay()
    {
        uint shellSeed = 1u, thinSeed = 1u, volumeSeed = 1u;
        ParticleShapeSampler.Sample(Cone(LumenShapeKind.ConeShell, 1f), ref shellSeed, out Vector3 shellPosition, out Vector3 shellDirection);
        ParticleShapeSampler.Sample(Cone(LumenShapeKind.Cone, 0f), ref thinSeed, out Vector3 thinPosition, out Vector3 thinDirection);
        ParticleShapeSampler.Sample(Cone(LumenShapeKind.ConeVolumeShell, 1f), ref volumeSeed, out Vector3 volumePosition, out Vector3 volumeDirection);
        Vector(new Vector3(2f, 0f, 0f), shellPosition);
        Vector(shellPosition, thinPosition);
        float diagonal = MathF.Sqrt(0.5f);
        Vector(new Vector3(diagonal, diagonal, 0f), shellDirection);
        Vector(shellDirection, thinDirection); Vector(shellDirection, volumeDirection);
        Near(2f + volumePosition.Y, volumePosition.X);
        Near(0f, volumePosition.Z);
        Check(volumePosition.Y > 0f && volumePosition.Y < 3f, "Volume shell remains on the cone's side ray.");
    }

    [LumenFact]
    public static void BoxWeightsUseAbsoluteScaledExtentsForFacesAndEdges()
    {
        uint choiceSeed = 1u;
        _ = ParticleRandom.Next01(ref choiceSeed);
        _ = ParticleRandom.Next01(ref choiceSeed);
        _ = ParticleRandom.Next01(ref choiceSeed);
        _ = ParticleRandom.Next01(ref choiceSeed);
        float axisChoice = ParticleRandom.Next01(ref choiceSeed);
        Check(axisChoice > 0.5f && axisChoice < 4f / 7f, "The controlled seed distinguishes weighted faces and edges from uniform selection.");
        uint shellSeed = 1u, edgeSeed = 1u;
        var shell = new EmitterSpec { Name = "box-shell", Shape = LumenShapeKind.BoxShell, ShapeScale = new Vector3(-1f, 2f, 4f) };
        var edge = new EmitterSpec { Name = "box-edge", Shape = LumenShapeKind.BoxEdge, ShapeScale = new Vector3(-1f, 2f, 4f) };
        ParticleShapeSampler.Sample(shell, ref shellSeed, out Vector3 facePosition, out _);
        ParticleShapeSampler.Sample(edge, ref edgeSeed, out Vector3 edgePosition, out _);
        // Seed 1's fifth uniform lies in (.5, 4/7): face weights 8:4:2 choose X; edge weights 1:2:4 choose Z.
        Near(1f, MathF.Abs(facePosition.X));
        Check(MathF.Abs(facePosition.Y) < 2f && MathF.Abs(facePosition.Z) < 4f, "The selected X face must not collapse to an edge.");
        Near(1f, MathF.Abs(edgePosition.X)); Near(2f, MathF.Abs(edgePosition.Y));
        Check(MathF.Abs(edgePosition.Z) < 4f, "The selected Z edge must retain its free coordinate.");
    }

    [LumenFact]
    public static void DonutTubeAndRectangleRespectTheirDistinctShapeDimensions()
    {
        uint donutSeed = 1u, rectangleSeed = 1u;
        var donut = new EmitterSpec
        {
            Name = "donut", Shape = LumenShapeKind.Donut, ArcDegrees = 0f,
            ShapeExtents = new Vector3(3f, 9f, 5f), DonutRadius = 2f, RadiusThickness = 0f,
        };
        ParticleShapeSampler.Sample(donut, ref donutSeed, out Vector3 donutPosition, out Vector3 donutDirection);
        Near(2f, Vector3.Distance(new Vector3(3f, 0f, 0f), donutPosition));
        Vector(donutDirection * 2f, donutPosition - new Vector3(3f, 0f, 0f));
        Near(0f, donutPosition.Z); Near(1f, donutDirection.Length());
        var rectangle = new EmitterSpec
        {
            Name = "rectangle", Shape = LumenShapeKind.Rectangle, ShapeExtents = new Vector3(2f, 0f, 3f),
            ShapeScale = new Vector3(2f, 3f, -1f), ShapePosition = new Vector3(5f, 6f, 7f),
            ShapeRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f),
        };
        ParticleShapeSampler.Sample(rectangle, ref rectangleSeed, out Vector3 rectanglePosition, out Vector3 rectangleDirection);
        Near(7f, rectanglePosition.Z);
        Check(MathF.Abs(rectanglePosition.X - 5f) < 4f && MathF.Abs(rectanglePosition.Y - 6f) < 3f, "Rotated rectangle has the authored X/Z half-extents.");
        Vector(Vector3.UnitZ, rectangleDirection);
    }

    [LumenFact]
    public static void SphericalDirectionUsesPostTrsOffsetBeforeRandomPosition()
    {
        uint centerSeed = 1u, offsetSeed = 1u;
        ParticleShapeSampler.Sample(MixedCircle(0f), ref centerSeed, out Vector3 centerPosition, out Vector3 centerDirection);
        ParticleShapeSampler.Sample(MixedCircle(2f), ref offsetSeed, out Vector3 offsetPosition, out Vector3 offsetDirection);
        Vector(new Vector3(10f, 16f, 30f), centerPosition);
        Vector(-Vector3.UnitY, centerDirection);
        Vector(centerDirection, offsetDirection);
        float displacement = Vector3.Distance(centerPosition, offsetPosition);
        Check(displacement > 0f && displacement < 2f, "Random position is a final bounded offset, not part of spherical direction.");
    }

    [LumenFact]
    public static void FullyRandomDirectionIsIndependentOfTheShapeRotation()
    {
        uint firstSeed = 1u, secondSeed = 1u;
        ParticleShapeSampler.Sample(new EmitterSpec { Name = "random-a", RandomDirectionAmount = 1f }, ref firstSeed, out _, out Vector3 first);
        ParticleShapeSampler.Sample(new EmitterSpec
        {
            Name = "random-b", RandomDirectionAmount = 1f,
            ShapeRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
        }, ref secondSeed, out _, out Vector3 second);
        Near(1f, first.Length());
        Vector(first, second);
    }

    [LumenFact]
    public static void WorldRotationChainIgnoresNonuniformZeroNegativeScaleAndIncludesRootYaw()
    {
        var asset = new EffectAsset
        {
            EffectId = "geometry-rotation",
            Nodes = new[]
            {
                new EffectNodeSpec { Name = "parent", LocalScale = new Vector3(-2f, 0f, 3f), LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f) },
                new EffectNodeSpec { Name = "child", ParentIndex = 0, LocalScale = new Vector3(4f, 5f, 6f), LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f) },
            },
        };
        var rotations = new Quaternion[2];
        var matrices = new Matrix4x4[2];
        SpacePlaneTransform.BuildWorldRotations(asset, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f), rotations);
        SpacePlaneTransform.BuildWorldMatrices(asset, SpacePlaneTransform.BuildRootMatrix(Vector3.Zero, 90f, 1f), matrices);
        // Child Rx90, parent Rz90, root Ry90: X -> Y, Y -> X, Z -> -Z, even though the matrix loses Z.
        Vector(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, rotations[1]));
        Vector(Vector3.UnitX, Vector3.Transform(Vector3.UnitY, rotations[1]));
        Vector(-Vector3.UnitZ, Vector3.Transform(Vector3.UnitZ, rotations[1]));
        Vector(Vector3.Zero, Vector3.TransformNormal(Vector3.UnitZ, matrices[1]));
    }

    [LumenFact]
    public static void LocalQuaternionAxesAreProjectedExactlyOnceWithoutRenormalizing()
    {
        float diagonal = MathF.Sqrt(0.5f);
        CheckLocal(false, false, 0.25f, new Vector3(diagonal, diagonal, 0f), new Vector3(-diagonal, diagonal, 0f));
        CheckLocal(true, false, 0.25f, new Vector3(diagonal, 4f * diagonal, 0f), new Vector3(-diagonal, 4f * diagonal, 0f));
        CheckLocal(true, true, 1f, new Vector3(diagonal, 0f, diagonal), new Vector3(-diagonal, 0f, diagonal));
    }

    [LumenFact]
    public static void Vertical3DKeepsTheProvidedCompensatedCameraAxes()
    {
        Vector3 cameraRight = new(0.8f, 0f, -0.6f), cameraUp = new(0.36f, 400f, 0.48f);
        ParticleBillboardMath.Vertical(cameraRight, cameraUp, 0.002f, true, out Vector3 right, out Vector3 up);
        Vector(cameraRight, right); Vector(cameraUp, up);
    }

    private static EmitterSpec Cone(LumenShapeKind shape, float thickness) => new()
    {
        Name = "cone", Shape = shape, ArcDegrees = 0f, ConeAngleDegrees = 45f, ConeLength = 3f,
        ShapeExtents = new Vector3(2f, 1f, 4f), RadiusThickness = thickness,
    };

    private static EmitterSpec MixedCircle(float randomPosition) => new()
    {
        Name = "mixed", Shape = LumenShapeKind.CircleEdgeXZ, ArcDegrees = 0f, ShapeExtents = new Vector3(2f),
        ShapeScale = new Vector3(-2f, 3f, 4f), ShapePosition = new Vector3(10f, 20f, 30f),
        ShapeRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
        RandomDirectionAmount = 1f, SphericalDirectionAmount = 1f, RandomPositionAmount = randomPosition,
    };

    private static void CheckLocal(bool vertical, bool topDown, float yScale, Vector3 expectedRight, Vector3 expectedUp)
    {
        ParticleBillboardMath.Local(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4f), vertical, topDown, yScale, out Vector3 right, out Vector3 up);
        Vector(expectedRight, right); Vector(expectedUp, up);
    }

    private static void Vector(Vector3 expected, Vector3 actual)
    {
        Near(expected.X, actual.X); Near(expected.Y, actual.Y); Near(expected.Z, actual.Z);
    }

    private static void Near(float expected, float actual)
        => Check(float.IsFinite(actual) && MathF.Abs(expected - actual) < 1e-4f, $"Expected {expected}, got {actual}.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
