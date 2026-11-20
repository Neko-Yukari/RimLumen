using System;
using System.Numerics;

namespace RimLumen.Core.ParticleWorld;

// Pure screen-space cursor velocity math (screen heights per real second, isotropic X/Y divided by viewport height).
// Keeps zero dependency on Verse, UnityEngine, or Waylith host state so offline tests exercise exact formulas directly.
internal static class CursorVelocityMath
{
    internal const float TauSeconds = 0.05f;
    internal const float MaxContinuousDeltaSeconds = 0.25f;
    internal const float MinStepSeconds = 1e-5f;

    internal static Vector2 ScreenPixelsToHeightUnits(Vector2 pixelPosition, int screenHeight)
        => screenHeight > 0 ? pixelPosition / screenHeight : Vector2.Zero;

    // Column-vector camera projection matching Waylith Camera.WorldToScreenPoint / ViewProjection:
    // clip = viewProjection * [world.X, world.Y, world.Z, 1]^T, mapped to isotropic screen-height coordinates
    // (+X right in [0, aspect], +Y up in [0, 1]).
    internal static bool TryProjectWorldToHeightUnits(
        Vector3 worldPosition,
        in Matrix4x4 viewProjection,
        int viewportWidth,
        int viewportHeight,
        out Vector2 screenPosition)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            screenPosition = default;
            return false;
        }

        float clipX = viewProjection.M11 * worldPosition.X + viewProjection.M12 * worldPosition.Y + viewProjection.M13 * worldPosition.Z + viewProjection.M14;
        float clipY = viewProjection.M21 * worldPosition.X + viewProjection.M22 * worldPosition.Y + viewProjection.M23 * worldPosition.Z + viewProjection.M24;
        float clipW = viewProjection.M41 * worldPosition.X + viewProjection.M42 * worldPosition.Y + viewProjection.M43 * worldPosition.Z + viewProjection.M44;
        if (!(clipW > 1e-6f) || !float.IsFinite(clipX) || !float.IsFinite(clipY) || !float.IsFinite(clipW))
        {
            screenPosition = default;
            return false;
        }

        float invW = 1f / clipW;
        float aspect = (float)viewportWidth / viewportHeight;
        screenPosition = new Vector2((clipX * invW + 1f) * 0.5f * aspect, (1f - clipY * invW) * 0.5f);
        return true;
    }

    internal static Vector2 StepScreenVelocity(
        Vector2 previousScreenPosition,
        Vector2 currentScreenPosition,
        bool hasPrevious,
        Vector2 previousVelocity,
        float unscaledDeltaTime)
    {
        if (!hasPrevious || !float.IsFinite(unscaledDeltaTime) || unscaledDeltaTime > MaxContinuousDeltaSeconds)
            return Vector2.Zero;
        if (!(unscaledDeltaTime > MinStepSeconds))
            return previousVelocity;

        Vector2 instantaneous = (currentScreenPosition - previousScreenPosition) / unscaledDeltaTime;
        float alpha = 1f - MathF.Exp(-unscaledDeltaTime / TauSeconds);
        return previousVelocity + alpha * (instantaneous - previousVelocity);
    }

    // Projects both previousWorld and currentWorld through this frame's viewProjection so camera pan/zoom alone
    // induces zero artificial cursor velocity when a world-space cursor has not moved.
    internal static Vector2 StepWorldCursorVelocity(
        Vector3 previousWorld,
        Vector3 currentWorld,
        bool hasPrevious,
        Vector2 previousVelocity,
        in Matrix4x4 viewProjection,
        int viewportWidth,
        int viewportHeight,
        float unscaledDeltaTime,
        out bool projected)
    {
        if (!TryProjectWorldToHeightUnits(currentWorld, in viewProjection, viewportWidth, viewportHeight, out Vector2 currentScreen))
        {
            projected = false;
            return Vector2.Zero;
        }

        projected = true;
        if (!hasPrevious || !TryProjectWorldToHeightUnits(previousWorld, in viewProjection, viewportWidth, viewportHeight, out Vector2 previousScreen))
            return Vector2.Zero;

        return StepScreenVelocity(previousScreen, currentScreen, hasPrevious: true, previousVelocity, unscaledDeltaTime);
    }
}
