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
    public static Matrix4x4 BuildLocalMatrix(EffectNodeSpec node) => throw new NotImplementedException();

    // Requires: scale is finite and non-zero.
    // Ensures: returns S(scale) * R_y(rotationDegrees) * T(originWorld): the effect root frame in world space.
    public static Matrix4x4 BuildRootMatrix(Vector3 originWorld, float rotationDegrees, float scale) => throw new NotImplementedException();

    // Ensures: yaw in degrees, normalized to [0, 360), that points the root's local +Z at the
    //          XZ projection of targetWorld. Returns 0 when the XZ offset is shorter than 1e-5.
    public static float ComputeFacingDegrees(Vector3 originWorld, Vector3 targetWorld) => throw new NotImplementedException();

    // Requires: asset is non-null; destination.Length >= asset.Nodes.Length; every
    //           Nodes[i].ParentIndex is -1 or in [0, i) (see InMemoryAssetCatalog.Register).
    // Ensures: destination[i] == (Nodes[i].ParentIndex < 0 ? rootWorld : destination[Nodes[i].ParentIndex])
    //          * BuildLocalMatrix(Nodes[i]), so a node inherits its parent's accumulated transform.
    // Ensures: writes exactly Nodes.Length entries and reads nothing past them.
    public static void BuildWorldMatrices(EffectAsset asset, Matrix4x4 rootWorld, Span<Matrix4x4> destination) =>
        throw new NotImplementedException();
}
