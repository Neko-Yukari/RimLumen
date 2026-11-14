using System;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Scene;

namespace RimLumen.Core.Tests.SceneAndLighting;

[LumenTestSuite]
public static class SpacePlaneTransformSuite
{
    [LumenFact]
    public static void HeightPlaneMapsLocalYToZWithoutPitchingTranslation()
    {
        var node = new EffectNodeSpec
        {
            Name = "height", SpacePlane = LumenSpacePlane.HeightVertical90,
            LocalPosition = new Vector3(5f, 7f, 11f),
        };
        var matrix = SpacePlaneTransform.BuildLocalMatrix(node);
        AssertClose(new Vector3(5f, 7f, 11f), Vector3.Transform(Vector3.Zero, matrix));
        AssertClose(Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitX, matrix));
        AssertClose(Vector3.UnitZ, Vector3.TransformNormal(Vector3.UnitY, matrix));
        AssertClose(-Vector3.UnitY, Vector3.TransformNormal(Vector3.UnitZ, matrix));
        AssertClose(new Vector3(5f, 7f, 12f), Vector3.Transform(Vector3.UnitY, matrix));
    }

    [LumenFact]
    public static void LocalMatrixAppliesScaleThenRotationThenPlaneThenTranslation()
    {
        var node = new EffectNodeSpec
        {
            Name = "ordered", SpacePlane = LumenSpacePlane.HeightVertical90,
            LocalScale = new Vector3(2f, 3f, 4f),
            LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
            LocalPosition = new Vector3(5f, 7f, 11f),
        };
        var matrix = SpacePlaneTransform.BuildLocalMatrix(node);
        AssertClose(new Vector3(5f, 7f, 11f), Vector3.Transform(Vector3.Zero, matrix));
        AssertClose(new Vector3(0f, 0f, 2f), Vector3.TransformNormal(Vector3.UnitX, matrix));
        AssertClose(new Vector3(-3f, 0f, 0f), Vector3.TransformNormal(Vector3.UnitY, matrix));
        AssertClose(new Vector3(0f, -4f, 0f), Vector3.TransformNormal(Vector3.UnitZ, matrix));
        AssertClose(new Vector3(2f, 3f, 13f), Vector3.Transform(Vector3.One, matrix));
    }

    [LumenFact]
    public static void GroundAndFree3DPreserveQuaternionRotationWithoutExtraPitch()
    {
        var ground = SpacePlaneTransform.BuildLocalMatrix(new EffectNodeSpec { Name = "ground" });
        if (ground != Matrix4x4.Identity)
        {
            throw new InvalidOperationException("A default ground node must be the identity transform.");
        }
        foreach (var plane in new[] { LumenSpacePlane.GroundXZ, LumenSpacePlane.Free3D })
        {
            var matrix = SpacePlaneTransform.BuildLocalMatrix(new EffectNodeSpec
            {
                Name = "rotated", SpacePlane = plane,
                LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f),
            });
            AssertClose(Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitX, matrix));
            AssertClose(Vector3.UnitZ, Vector3.TransformNormal(Vector3.UnitY, matrix));
            AssertClose(-Vector3.UnitY, Vector3.TransformNormal(Vector3.UnitZ, matrix));
        }
    }

    [LumenFact]
    public static void RootScalesAndYawsBeforeTranslating()
    {
        var matrix = SpacePlaneTransform.BuildRootMatrix(new Vector3(10f, 20f, 30f), 90f, 2f);
        AssertClose(new Vector3(10f, 20f, 30f), Vector3.Transform(Vector3.Zero, matrix));
        AssertClose(new Vector3(0f, 0f, -2f), Vector3.TransformNormal(Vector3.UnitX, matrix));
        AssertClose(new Vector3(0f, 2f, 0f), Vector3.TransformNormal(Vector3.UnitY, matrix));
        AssertClose(new Vector3(2f, 0f, 0f), Vector3.TransformNormal(Vector3.UnitZ, matrix));
        AssertClose(new Vector3(12f, 20f, 30f), Vector3.Transform(Vector3.UnitZ, matrix));
        var mirrored = SpacePlaneTransform.BuildRootMatrix(Vector3.Zero, 0f, -2f);
        AssertClose(new Vector3(-2f, -2f, -2f), Vector3.Transform(Vector3.One, mirrored));
    }

    [LumenFact]
    public static void FacingUsesXZCardinalsDiagonalsAndShortOffsetThreshold()
    {
        var origin = new Vector3(10f, 50f, 20f);
        AssertClose(0f, SpacePlaneTransform.ComputeFacingDegrees(origin, new Vector3(10f, -10f, 21f)));
        AssertClose(90f, SpacePlaneTransform.ComputeFacingDegrees(origin, new Vector3(11f, -10f, 20f)));
        AssertClose(180f, SpacePlaneTransform.ComputeFacingDegrees(origin, new Vector3(10f, -10f, 19f)));
        AssertClose(270f, SpacePlaneTransform.ComputeFacingDegrees(origin, new Vector3(9f, -10f, 20f)));
        AssertClose(45f, SpacePlaneTransform.ComputeFacingDegrees(origin, new Vector3(11f, -10f, 21f)));
        AssertClose(135f, SpacePlaneTransform.ComputeFacingDegrees(origin, new Vector3(11f, -10f, 19f)));
        AssertClose(225f, SpacePlaneTransform.ComputeFacingDegrees(origin, new Vector3(9f, -10f, 19f)));
        AssertClose(315f, SpacePlaneTransform.ComputeFacingDegrees(origin, new Vector3(9f, -10f, 21f)));
        AssertClose(0f, SpacePlaneTransform.ComputeFacingDegrees(origin, new Vector3(10f, 1000f, 20f)));
        AssertClose(0f, SpacePlaneTransform.ComputeFacingDegrees(Vector3.Zero, new Vector3(0.000005f, 0f, 0f)));
        AssertClose(90f, SpacePlaneTransform.ComputeFacingDegrees(Vector3.Zero, new Vector3(0.00001f, 0f, 0f)));
        float nearNorth = SpacePlaneTransform.ComputeFacingDegrees(Vector3.Zero, new Vector3(-1e-8f, 0f, 1f));
        if (!(nearNorth >= 0f && nearNorth < 360f))
        {
            throw new InvalidOperationException($"Facing must remain in [0,360), got {nearNorth}.");
        }
    }

    [LumenFact]
    public static void WorldMatricesComposeLocalThenParentAndWriteOnlyNodeSlots()
    {
        var asset = new EffectAsset
        {
            EffectId = "chain",
            Nodes = new[]
            {
                new EffectNodeSpec { Name = "first", LocalPosition = new Vector3(1f, 2f, 3f) },
                new EffectNodeSpec
                {
                    Name = "child", ParentIndex = 0, LocalScale = new Vector3(2f, 3f, 4f),
                    LocalPosition = new Vector3(5f, 6f, 7f),
                },
                new EffectNodeSpec
                {
                    Name = "grandchild", ParentIndex = 1, SpacePlane = LumenSpacePlane.HeightVertical90,
                    LocalPosition = new Vector3(0f, 10f, 0f),
                },
                new EffectNodeSpec { Name = "sibling root", LocalPosition = new Vector3(-1f, 0f, 0f) },
            },
        };
        var root = SpacePlaneTransform.BuildRootMatrix(new Vector3(10f, 20f, 30f), 90f, 2f);
        Span<Matrix4x4> storage = stackalloc Matrix4x4[6];
        var sentinel = Matrix4x4.CreateScale(777f);
        storage.Fill(sentinel);
        var destination = storage[1..];
        SpacePlaneTransform.BuildWorldMatrices(asset, root, destination);
        // first: (0,0,0) + (1,2,3) = (1,2,3), x2 = (2,4,6), yaw 90 = (6,4,-2), + (10,20,30).
        AssertClose(new Vector3(16f, 24f, 28f), Vector3.Transform(Vector3.Zero, destination[0]));
        // child: (5,6,7) + (1,2,3) = (6,8,10), x2 = (12,16,20), yaw 90 = (20,16,-12), + (10,20,30).
        AssertClose(new Vector3(30f, 36f, 18f), Vector3.Transform(Vector3.Zero, destination[1]));
        // child axes: local scale (2,3,4) once, then the root's x2 and yaw 90.
        AssertClose(new Vector3(0f, 0f, -4f), Vector3.TransformNormal(Vector3.UnitX, destination[1]));
        AssertClose(new Vector3(0f, 6f, 0f), Vector3.TransformNormal(Vector3.UnitY, destination[1]));
        AssertClose(new Vector3(8f, 0f, 0f), Vector3.TransformNormal(Vector3.UnitZ, destination[1]));
        // grandchild: origin (0,10,0) x child scale (2,3,4) = (0,30,0), +(5,6,7)+(1,2,3) = (6,38,10).
        AssertClose(new Vector3(30f, 96f, 18f), Vector3.Transform(Vector3.Zero, destination[2]));
        AssertClose(new Vector3(0f, 0f, -4f), Vector3.TransformNormal(Vector3.UnitX, destination[2]));
        AssertClose(new Vector3(8f, 0f, 0f), Vector3.TransformNormal(Vector3.UnitY, destination[2]));
        AssertClose(new Vector3(0f, -6f, 0f), Vector3.TransformNormal(Vector3.UnitZ, destination[2]));
        // sibling root: (-1,0,0) x2 = (-2,0,0), yaw 90 = (0,0,2), + (10,20,30).
        AssertClose(new Vector3(10f, 20f, 32f), Vector3.Transform(Vector3.Zero, destination[3]));
        if (storage[0] != sentinel || storage[^1] != sentinel)
        {
            throw new InvalidOperationException("BuildWorldMatrices wrote outside the node slots.");
        }
        var empty = new EffectAsset { EffectId = "empty" };
        SpacePlaneTransform.BuildWorldMatrices(empty, root, Span<Matrix4x4>.Empty);
        SpacePlaneTransform.BuildWorldMatrices(empty, root, storage[..1]);
        if (storage[0] != sentinel)
        {
            throw new InvalidOperationException("An empty asset must write no entries.");
        }
    }

    [LumenFact]
    public static void TwoLevelChainCompoundsScaleAndYaw()
    {
        var asset = new EffectAsset
        {
            EffectId = "compound",
            Nodes = new[]
            {
                new EffectNodeSpec
                {
                    Name = "child", LocalScale = new Vector3(3f),
                    LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f),
                    LocalPosition = new Vector3(1f, 0f, 0f),
                },
                new EffectNodeSpec { Name = "grandchild", ParentIndex = 0, LocalPosition = new Vector3(1f, 0f, 0f) },
            },
        };
        Span<Matrix4x4> destination = stackalloc Matrix4x4[2];
        var root = SpacePlaneTransform.BuildRootMatrix(Vector3.Zero, 90f, 2f);
        SpacePlaneTransform.BuildWorldMatrices(asset, root, destination);
        // child origin: (1,0,0) x2 = (2,0,0), yaw 90 = (0,0,-2).
        AssertClose(new Vector3(0f, 0f, -2f), Vector3.Transform(Vector3.Zero, destination[0]));
        // child axes: x3 in its own frame, then root x2 and yaw 90; two yaws = 180 degrees.
        AssertClose(new Vector3(-6f, 0f, 0f), Vector3.TransformNormal(Vector3.UnitX, destination[0]));
        AssertClose(new Vector3(0f, 0f, -6f), Vector3.TransformNormal(Vector3.UnitZ, destination[0]));
        // grandchild offset (1,0,0) is length 6 along child +X, from child origin (0,0,-2).
        AssertClose(new Vector3(-6f, 0f, -2f), Vector3.Transform(Vector3.Zero, destination[1]));
    }

    [LumenFact]
    public static void ParentYawRotatesChildOffsetWithoutScalingItByChildLocalScale()
    {
        var asset = new EffectAsset
        {
            EffectId = "rotate-offset",
            Nodes = new[]
            {
                new EffectNodeSpec { Name = "child", LocalScale = new Vector3(3f), LocalPosition = new Vector3(1f, 0f, 0f) },
            },
        };
        Span<Matrix4x4> destination = stackalloc Matrix4x4[1];
        var root = SpacePlaneTransform.BuildRootMatrix(new Vector3(10f, 0f, 20f), 90f, 1f);
        SpacePlaneTransform.BuildWorldMatrices(asset, root, destination);
        // Position is not multiplied by the node's own scale: (1,0,0) -> yaw 90 -> (0,0,-1), + origin.
        AssertClose(new Vector3(10f, 0f, 19f), Vector3.Transform(Vector3.Zero, destination[0]));
        // The local scale applies exactly once to the child's own axes: 3, then yaw 90 onto -Z.
        AssertClose(new Vector3(0f, 0f, -3f), Vector3.TransformNormal(Vector3.UnitX, destination[0]));
    }

    [LumenFact]
    public static void ComposedChildMatrixEqualsLocalMatrixThenRootWorld()
    {
        var rootNode = new EffectNodeSpec { Name = "root" };
        var child = new EffectNodeSpec
        {
            Name = "child", ParentIndex = 0,
            LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f),
            LocalPosition = new Vector3(2f, 0f, 0f),
        };
        var asset = new EffectAsset { EffectId = "convention", Nodes = new[] { rootNode, child } };
        var rootWorld = SpacePlaneTransform.BuildRootMatrix(Vector3.Zero, 30f, 1f);
        Span<Matrix4x4> destination = stackalloc Matrix4x4[2];
        SpacePlaneTransform.BuildWorldMatrices(asset, rootWorld, destination);
        // local (2,0,0) yawed 30 degrees: (2 cos 30, 0, -2 sin 30).
        AssertClose(new Vector3(1.7320508f, 0f, -1f), Vector3.Transform(Vector3.Zero, destination[1]));
        var localThenRoot = Vector3.Transform(
            Vector3.Transform(Vector3.Zero, SpacePlaneTransform.BuildLocalMatrix(child)), rootWorld);
        AssertClose(localThenRoot, Vector3.Transform(Vector3.Zero, destination[1]));
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        AssertClose(expected.X, actual.X);
        AssertClose(expected.Y, actual.Y);
        AssertClose(expected.Z, actual.Z);
    }

    private static void AssertClose(float expected, float actual)
    {
        if (!(MathF.Abs(expected - actual) <= 0.00002f))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }
}
