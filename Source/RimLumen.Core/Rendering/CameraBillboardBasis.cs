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

    // Display-only remap for Vertical3D emitters, whose simulation stays true 3D (+Y up). `baseY` is the effect's
    // play-origin height. Callers pass scaleVelocityY: false to ComputeStretchedAxes afterwards.
    // Ensures: straight-down 2D: height above baseY moves onto screen-up (+Z), the draw altitude is
    //          max(baseY, vanillaMoteY) and velocity.Y is folded into velocity.Z.
    //          Otherwise (3D / IsoRim): height above baseY is divided by the view's Y squash so it reaches the screen
    //          at full size; velocity is untouched.
    internal static void ProjectVertical3D(bool isTopDown2D, float yScaleRatio, float baseY, float vanillaMoteY,
        ref Vector3 position, ref Vector3 velocity)
    {
        if (isTopDown2D)
        {
            float dy = position.Y - baseY;
            position = new Vector3(position.X, MathF.Max(baseY, vanillaMoteY), position.Z + dy);
            velocity = new Vector3(velocity.X, 0f, velocity.Z + velocity.Y);
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
