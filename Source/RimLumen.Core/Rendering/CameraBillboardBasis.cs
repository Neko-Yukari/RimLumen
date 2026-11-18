using System;
using System.Numerics;
using RimLumen.Core.Scene;

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

    // Ensures: true for the vanilla straight-down 2D camera (no Y squash, screen right and up both lie in the ground
    //          plane, so the view axis is world Y); false for IsoRim, any other tilted camera and a level camera rolled
    //          about its view axis. `right`/`up` are the Extract() axes.
    internal static bool IsStraightTopDown2D(in Vector3 right, in Vector3 up, float yScaleRatio)
        => yScaleRatio >= 0.9f && MathF.Abs(up.Y) <= 0.05f * MathF.Max(1e-6f, up.Length())
            && MathF.Abs(right.Y) <= 0.05f * MathF.Max(1e-6f, right.Length());

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

    // A node's ground anchor: the XZ of its world translation (row-vector matrix). Mirrored by lumenNodeGroundXZ.
    internal static Vector2 NodeGroundXZ(in Matrix4x4 nodeWorld) => new(nodeWorld.M41, nodeWorld.M43);

    // Display rule for both space planes. anchorXZ is the owner's ground position: its node's world XZ (at birth for
    // world-space particles), or a ribbon sample's own XZ.
    // Straight-down 2D: everything draws on the mote layer; Vertical3D pitches about the anchor on world ground, so the
    //   anchor lands on its own cell and only the shape around it is foreshortened.
    // Tilted cameras: GroundXZ stays world geometry; Vertical3D un-squashes world height.
    internal static void ProjectToDisplay(bool isVertical3D, bool isTopDown2D, float yScaleRatio, in Vector3 groundUp,
        Vector2 anchorXZ, float vanillaMoteY, ref Vector3 position, ref Vector3 velocity)
    {
        if (isTopDown2D)
        {
            if (isVertical3D)
            {
                Vector3 offset = ProjectTopDownPitch(position - new Vector3(anchorXZ.X, SpacePlaneTransform.GroundY, anchorXZ.Y), groundUp);
                position = new Vector3(anchorXZ.X + offset.X, 0f, anchorXZ.Y + offset.Z);
                velocity = ProjectTopDownPitch(velocity, groundUp);
            }
            position.Y = vanillaMoteY;
            return;
        }

        if (!isVertical3D) return;
        float invYScale = yScaleRatio > 1e-5f ? 1f / yScaleRatio : 1f;
        position.Y *= invYScale;
    }

    // LumenDepthMask.NorthBehindReference: the eye-distance margin from the mote layer down to the Projectile altitude
    // (just below Pawn). Under the straight-down camera eye distance runs along -Y, so a north fragment whose scene
    // depth lies closer than this margin sits under a pawn-level thing and is discarded.
    internal static float NorthBehindLift(float vanillaMoteY)
        => vanillaMoteY - Verse.Altitudes.AltitudeFor(Verse.AltitudeLayer.Projectile);

    // ParticleVertex.DepthMask: (lift, reference Z) under the straight-down 2D camera for a masked owner; zero otherwise.
    internal static Vector2 DepthMaskAttribute(bool isTopDown2D, float? depthReferenceZ, float vanillaMoteY)
        => isTopDown2D && depthReferenceZ is { } z ? new Vector2(NorthBehindLift(vanillaMoteY), z) : Vector2.Zero;

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
