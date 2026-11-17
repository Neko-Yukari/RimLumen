using System;
using System.Numerics;
using RimLumen.Core.Rendering;

namespace RimLumen.Core.Scene;

internal static class ParticleBillboardMath
{
    // Requires: compensated display camera axes from CameraBillboardBasis.Extract and its positive Y scale ratio.
    // Ensures: Vertical3D returns those axes unchanged; GroundXZ uses raw world forward, world up,
    // and the exact-overhead camera-right limit, preserving edge-on projection without NaN.
    internal static void Vertical(in Vector3 cameraRight, in Vector3 cameraUp, float yScaleRatio,
        bool isVertical3D, out Vector3 right, out Vector3 up)
    {
        if (isVertical3D)
        {
            right = cameraRight;
            up = cameraUp;
            return;
        }

        Vector3 rawRight = new(cameraRight.X, cameraRight.Y * yScaleRatio, cameraRight.Z);
        Vector3 rawUp = new(cameraUp.X, cameraUp.Y * yScaleRatio, cameraUp.Z);
        Vector3 horizontal = Vector3.Cross(Vector3.UnitY, Vector3.Cross(rawRight, rawUp));
        float length = horizontal.Length();
        right = length > 1e-6f ? horizontal / length : Vector3.Normalize(rawRight);
        up = Vector3.UnitY;
    }

    // Requires: unit world quaternion composed independently of scale; validated display-space inputs.
    // Ensures: Local axes are rotation * X/Y, projected once for Vertical3D, never screen-facing;
    // top-down uses CameraBillboardBasis.ProjectTopDownPitch, tilted uses inverse-Y once without renormalization.
    internal static void Local(in Quaternion worldRotation, bool isVertical3D, bool isTopDown2D,
        float yScaleRatio, in Vector3 groundUp, out Vector3 right, out Vector3 up)
    {
        right = Vector3.Transform(Vector3.UnitX, worldRotation);
        up = Vector3.Transform(Vector3.UnitY, worldRotation);
        if (isVertical3D && isTopDown2D)
        {
            right = CameraBillboardBasis.ProjectTopDownPitch(right, groundUp);
            up = CameraBillboardBasis.ProjectTopDownPitch(up, groundUp);
        }
        else if (isVertical3D)
        {
            right.Y /= yScaleRatio;
            up.Y /= yScaleRatio;
        }
    }
}
