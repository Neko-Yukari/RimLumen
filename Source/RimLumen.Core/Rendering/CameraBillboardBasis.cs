using System;
using System.Numerics;

namespace RimLumen.Core.Rendering;

// Extracts camera billboard basis vectors in world space for column-vector host view matrices.
// Scaling by the X-axis view scale (col0Len) preserves uniform zoom while canceling non-uniform
// axis squashes (such as IsoRim's Y-axis 0.003 compression) so billboards stay square on screen.
internal static class CameraBillboardBasis
{
    internal static void Extract(in Matrix4x4 view, out Vector3 right, out Vector3 up, out float yScaleRatio)
    {
        float col0Len = new Vector3(view.M11, view.M21, view.M31).Length();
        float col1Len = new Vector3(view.M12, view.M22, view.M32).Length();
        if (col0Len <= 1e-6f || col1Len <= 1e-6f || !Matrix4x4.Invert(view, out Matrix4x4 inv))
        {
            right = Vector3.UnitX;
            up = Vector3.UnitY;
            yScaleRatio = 1f;
            return;
        }
        right = new Vector3(inv.M11, inv.M21, inv.M31) * col0Len;
        up = new Vector3(inv.M12, inv.M22, inv.M32) * col0Len;
        yScaleRatio = col1Len / col0Len;
    }

    // Ensures: true for the vanilla straight-down 2D camera (no Y squash, screen-up lies in the ground plane);
    //          false for IsoRim and any other tilted camera. `up` is the Extract() screen-up.
    internal static bool IsStraightTopDown2D(in Vector3 up, float yScaleRatio)
        => yScaleRatio >= 0.9f && MathF.Abs(up.Y) <= 0.05f * MathF.Max(1e-6f, up.Length());

    // Vertical3D under the straight-down 2D camera is drawn as a true orthographic view pitched 60 degrees.
    internal const float SinPitch = 0.8660254f;
    internal const float CosPitch = 0.5f;

    // Ensures: unit screen-up in the ground plane, normalize(up.X, 0, up.Z); +Z when up has no ground component.
    internal static Vector3 GroundUp(in Vector3 up)
    {
        float lengthSq = up.X * up.X + up.Z * up.Z;
        return lengthSq < 1e-12f ? Vector3.UnitZ : new Vector3(up.X, 0f, up.Z) / MathF.Sqrt(lengthSq);
    }

    // Linear part of the 60-degree pitch for the straight-down 2D camera; groundUp comes from GroundUp.
    // Ensures: the ground component along groundUp shrinks by SinPitch, height adds CosPitch along groundUp,
    //          the camera-right component is kept and the result lies in the ground plane (Y = 0).
    internal static Vector3 ProjectTopDownPitch(in Vector3 value, in Vector3 groundUp)
    {
        float along = value.X * groundUp.X + value.Z * groundUp.Z;
        return new Vector3(value.X, 0f, value.Z) + (along * (SinPitch - 1f) + value.Y * CosPitch) * groundUp;
    }

    // Display-only remap for Vertical3D emitters, whose simulation stays true 3D (+Y up). `anchorXZ` and `baseY` are
    // the 3D pivot: the play origin's ground position (at birth for world-space particles) and height.
    // Callers pass scaleVelocityY: false to ComputeStretchedAxes afterwards.
    // Ensures: straight-down 2D: the offset from the pivot goes through ProjectTopDownPitch, so a sphere draws as a
    //          circle; the draw altitude is max(baseY, vanillaMoteY) and velocity goes through the same operator.
    //          Otherwise (3D / IsoRim): height above baseY is divided by the view's Y squash so it reaches the screen
    //          at full size; velocity is untouched.
    internal static void ProjectVertical3D(bool isTopDown2D, float yScaleRatio, in Vector3 groundUp, Vector2 anchorXZ,
        float baseY, float vanillaMoteY, ref Vector3 position, ref Vector3 velocity)
    {
        if (isTopDown2D)
        {
            Vector3 offset = ProjectTopDownPitch(
                new Vector3(position.X - anchorXZ.X, position.Y - baseY, position.Z - anchorXZ.Y), groundUp);
            position = new Vector3(anchorXZ.X + offset.X, MathF.Max(baseY, vanillaMoteY), anchorXZ.Y + offset.Z);
            velocity = ProjectTopDownPitch(velocity, groundUp);
            return;
        }

        float invYScale = yScaleRatio > 1e-5f ? 1f / yScaleRatio : 1f;
        position = new Vector3(position.X, baseY + (position.Y - baseY) * invYScale, position.Z);
    }

    // scaleVelocityY: legacy emitters pre-squash world Y so a velocity along world Y does not vanish on screen;
    // Vertical3D passes false because its velocity is already a true 3D direction.
    internal static void ComputeStretchedAxes(in Vector3 right, in Vector3 up, float yScaleRatio, in Vector3 velocity,
        out Vector3 stretchedRight, out Vector3 stretchedUp, bool scaleVelocityY = true)
    {
        float yScale = yScaleRatio > 0f ? yScaleRatio : 1f;
        var rUnscaled = new Vector3(right.X, right.Y * yScale, right.Z);
        var uUnscaled = new Vector3(up.X, up.Y * yScale, up.Z);
        var vRot = new Vector3(velocity.X, scaleVelocityY ? velocity.Y * yScale : velocity.Y, velocity.Z);
        float vx = Vector3.Dot(vRot, rUnscaled);
        float vy = Vector3.Dot(vRot, uUnscaled);
        float lenSq = vx * vx + vy * vy;
        if (lenSq <= 1e-10f)
        {
            stretchedRight = right;
            stretchedUp = up;
            return;
        }
        float invLen = 1f / MathF.Sqrt(lenSq);
        float dx = vx * invLen;
        float dy = vy * invLen;
        stretchedUp = right * dx + up * dy;
        stretchedRight = right * dy - up * dx;
    }
}
