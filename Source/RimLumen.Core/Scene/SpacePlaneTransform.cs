using System;
using System.Numerics;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Scene;

/// <summary>
/// Builds the 2.5D local-to-world matrices for <see cref="EffectNodeSpec"/> chains in RimWorld's
/// top-down world (world X = east, world Y = altitude, world Z = north).
/// All matrices use System.Numerics' row-vector convention: v' = v * M.
/// </summary>
public static class SpacePlaneTransform
{
    // Requires: node is non-null.
    // Ensures: returns the node's matrix relative to its parent's frame, in this order:
    //            S(node.LocalScale) * R(node.LocalRotation) * P(node.SpacePlane) * T(node.LocalPosition)
    //          where P is identity for GroundXZ and Free3D, and R_x(+90 degrees) for HeightVertical90.
    // Ensures: HeightVertical90 therefore maps the node's local +Y onto the parent frame's +Z while
    //          LocalPosition.Y stays an altitude offset (the plane pitch never touches translation).
    // Ensures: Free3D keeps the node's full 3D LocalRotation without an extra plane pitch.
    public static Matrix4x4 BuildLocalMatrix(EffectNodeSpec node)
    {
        var plane = node.SpacePlane == LumenSpacePlane.HeightVertical90
            ? Matrix4x4.CreateRotationX(MathF.PI / 2f)
            : Matrix4x4.Identity;
        return Matrix4x4.CreateScale(node.LocalScale) *
               Matrix4x4.CreateFromQuaternion(node.LocalRotation) *
               plane * Matrix4x4.CreateTranslation(node.LocalPosition);
    }

    // Requires: scale is finite and non-zero.
    // Ensures: returns S(scale) * R_y(rotationDegrees) * T(originWorld): the effect root frame in world space.
    public static Matrix4x4 BuildRootMatrix(Vector3 originWorld, float rotationDegrees, float scale) =>
        Matrix4x4.CreateScale(scale) *
        Matrix4x4.CreateRotationY(rotationDegrees * (MathF.PI / 180f)) *
        Matrix4x4.CreateTranslation(originWorld);

    // Ensures: yaw in degrees, normalized to [0, 360), that points the root's local +Z at the
    //          XZ projection of targetWorld. Returns 0 when the XZ offset is shorter than 1e-5.
    public static float ComputeFacingDegrees(Vector3 originWorld, Vector3 targetWorld)
    {
        var offset = new Vector2(targetWorld.X - originWorld.X, targetWorld.Z - originWorld.Z);
        if (offset.Length() < 1e-5f)
        {
            return 0f;
        }

        float degrees = MathF.Atan2(offset.X, offset.Y) * (180f / MathF.PI);
        if (degrees < 0f)
        {
            degrees += 360f;
        }

        return degrees >= 360f ? 0f : degrees;
    }

    // Requires: asset is non-null; destination.Length >= asset.Nodes.Length; every
    //           Nodes[i].ParentIndex is -1 or in [0, i) (see InMemoryAssetCatalog.Register).
    // Ensures: destination[i] == (Nodes[i].ParentIndex < 0 ? rootWorld : destination[Nodes[i].ParentIndex])
    //          * BuildLocalMatrix(Nodes[i]), so a node inherits its parent's accumulated transform.
    // Ensures: writes exactly Nodes.Length entries and reads nothing past them.
    public static void BuildWorldMatrices(EffectAsset asset, Matrix4x4 rootWorld, Span<Matrix4x4> destination)
    {
        for (int i = 0; i < asset.Nodes.Length; i++)
        {
            var node = asset.Nodes[i];
            var parent = node.ParentIndex < 0 ? rootWorld : destination[node.ParentIndex];
            destination[i] = parent * BuildLocalMatrix(node);
        }
    }
}
