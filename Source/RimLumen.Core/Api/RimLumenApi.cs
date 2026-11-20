using System;
using System.Numerics;
using RimLumen.Core.Contracts;
using RimLumen.Core.Assets;
using RimLumen.Core.Rendering;
using RimLumen.Core.Scene;
using Verse;

namespace RimLumen.Core.Api;

/// <summary>
/// Public single-entry facade for downstream RimWorld mods using RimLumen.
/// </summary>
public static class RimLumenApi
{
    // Ensures: true once the native scene (catalog, simulator, VFX service) is bootstrapped and a VFX service is registered.
    public static bool IsReady => !RimLumenRenderHost.FailSafeTripped && LumenSceneBootstrap.IsInitialized && LumenHostRegistry.VfxService is not null;

    // Ensures: HDR/depth are the render host's last recorded frame facts. GPU availability means a supported
    //          backend, not a dispatch this frame; MaxGpuParticlesPerFrame is the shared slot limit, not usage.
    //          The selected service owns GPU availability/capacity; without a service, returns the host facts.
    //          A service's installed renderer cannot override a host frame reported inactive.
    public static LumenCapabilities Capabilities
    {
        get
        {
            if (RimLumenRenderHost.FailSafeTripped) return default;
            return MergeCapabilities(RimLumenRenderHost.Capabilities, LumenHostRegistry.VfxService?.Capabilities);
        }
    }

    // The selected service owns backend selection; host facts only supply the recorded frame's HDR/depth.
    internal static LumenCapabilities MergeCapabilities(LumenCapabilities host, LumenCapabilities? service)
        => service is { } active
            ? new LumenCapabilities(host.IsHdrActive, host.IsSceneDepthAvailable,
                active.IsGpuComputeAvailable, active.MaxGpuParticlesPerFrame)
            : host;

    // Requires: asset is non-null with non-empty EffectId.
    /// <summary>Validates and registers or replaces an asset in the native catalog.
    /// Treat the asset and its nested arrays as immutable once registered/resolved; replace it with a new instance to reload.
    /// </summary>
    public static void RegisterEffect(EffectAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        LumenAssetRules.Validate(asset, $"effect '{asset.EffectId}'");
        LumenSceneBootstrap.EnsureInitialized();
        LumenSceneBootstrap.Catalog.Register(asset);
    }

    // Ensures: resolves effect asset from any registered provider (native or adapter).
    public static bool TryGetEffect(string effectId, out EffectAsset? asset)
    {
        if (string.IsNullOrEmpty(effectId))
        {
            asset = null;
            return false;
        }

        LumenSceneBootstrap.EnsureInitialized();
        return LumenHostRegistry.TryResolveAsset(effectId, out asset);
    }

    // Requires: main thread; effectId is non-empty.
    // Ensures: ensures scene bootstrap is initialized and forwards request to active ILumenVfxService.
    public static ILumenEffectHandle Play(in LumenPlayRequest request)
    {
        if (RimLumenRenderHost.FailSafeTripped) return LumenVfxService.InertHandle.Instance;
        LumenSceneBootstrap.EnsureInitialized();
        ILumenVfxService service = LumenHostRegistry.VfxService
            ?? throw new InvalidOperationException("RimLumen has no registered ILumenVfxService.");
        return service.Play(request);
    }

    // Convenience overload for RimWorld map cell/world coordinates.
    public static ILumenEffectHandle Play(string effectId, Map? map, Vector3 originWorld, float scale = 1f, float rotationDegrees = 0f, bool realTime = false)
        => Play(new LumenPlayRequest
        {
            EffectId = effectId,
            MapIndex = map?.uniqueID ?? -1,
            OriginWorld = originWorld,
            Scale = scale,
            RotationDegrees = rotationDegrees,
            RealTime = realTime,
        });

    // Convenience overload accepting RimWorld IntVec3 cell center.
    public static ILumenEffectHandle PlayAtCell(string effectId, Map map, IntVec3 cell, float scale = 1f, float rotationDegrees = 0f, bool realTime = false)
    {
        UnityEngine.Vector3 center = cell.ToVector3Shifted();
        return Play(effectId, map, new Vector3(center.x, center.y, center.z), scale, rotationDegrees, realTime);
    }

    public static void EmitEnvironmentPulse(in LumenEnvironmentPulseRequest request)
    {
        RimLumen.Core.ParticleWorld.ParticleEnvironment.ValidatePulseRequest(in request, out int mapIndex);
        if (RimLumenRenderHost.FailSafeTripped) return;
        RimLumen.Core.ParticleWorld.ParticleEnvironment.EmitPulse(mapIndex, in request);
    }

    public static void EmitExplosion(Map? map, Vector3 groundCenterWorld, float radius, float strengthScale = 1f)
    {
        if (!(radius > 0f) || !float.IsFinite(radius))
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "Explosion radius must be finite and > 0.");
        if (!float.IsFinite(strengthScale))
            throw new ArgumentOutOfRangeException(nameof(strengthScale), strengthScale, "strengthScale must be finite.");

        EmitEnvironmentPulse(new LumenEnvironmentPulseRequest
        {
            MapIndex = MapId(map),
            Channel = LumenEnvironmentChannel.Explosion,
            Shape = LumenEnvironmentForceShape.SphericalWave,
            CenterWorld = new Vector3(groundCenterWorld.X, groundCenterWorld.Y - 0.25f * radius, groundCenterWorld.Z),
            Radius = radius,
            Strength = 24f * radius * strengthScale,
            Duration = 0.5f,
            ShellHalfThickness = 0.125f * radius,
            RealTime = false,
        });
    }

    public static ILumenEnvironmentSource CreateEnvironmentSource(in LumenEnvironmentSourceRequest request)
    {
        RimLumen.Core.ParticleWorld.ParticleEnvironment.ValidateSourceRequest(in request, out int mapIndex);
        if (RimLumenRenderHost.FailSafeTripped)
            return new RimLumen.Core.ParticleWorld.ParticleEnvironment.InertEnvironmentSource(mapIndex, request.Channel, request.FollowHostMouse);
        return RimLumen.Core.ParticleWorld.ParticleEnvironment.CreateSource(mapIndex, in request);
    }

    public static ILumenEnvironmentSource CreateMouseForce(Map? map = null, float radius = 5f, float strength = 4f)
        => CreateEnvironmentSource(new LumenEnvironmentSourceRequest
        {
            MapIndex = MapId(map),
            Channel = LumenEnvironmentChannel.Cursor,
            Shape = LumenEnvironmentForceShape.HorizontalRadial,
            Radius = radius,
            Strength = strength,
            FollowHostMouse = true,
            TriggersCursorEvents = true,
        });

    public static ILumenEnvironmentSource CreateCursor(Map? map, Vector3 initialPositionWorld, float radius = 5f, float strength = 0f)
    {
        LumenEnvironmentChannel channel = radius == 0f && strength == 0f
            ? LumenEnvironmentChannel.None
            : LumenEnvironmentChannel.Cursor;

        return CreateEnvironmentSource(new LumenEnvironmentSourceRequest
        {
            MapIndex = MapId(map),
            Channel = channel,
            Shape = LumenEnvironmentForceShape.HorizontalRadial,
            InitialPositionWorld = initialPositionWorld,
            Radius = radius,
            Strength = strength,
            FollowHostMouse = false,
            TriggersCursorEvents = true,
        });
    }

    private static int MapId(Map? map) => map is null ? -1 : map.uniqueID >= 0
        ? map.uniqueID
        : throw new ArgumentOutOfRangeException(nameof(map), "Map must have a non-negative uniqueID.");
}
