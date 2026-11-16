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

    // Ensures: the HDR pipeline's capabilities merged with the active VFX service's backends; all-false before install.
    public static LumenCapabilities Capabilities
    {
        get
        {
            if (RimLumenRenderHost.FailSafeTripped) return default;
            LumenCapabilities host = RimLumenRenderHost.Capabilities;
            LumenCapabilities? service = LumenHostRegistry.VfxService?.Capabilities;
            if (service is not { } active)
            {
                return host;
            }

            return new LumenCapabilities(
                host.IsHdrActive || active.IsHdrActive,
                host.IsSceneDepthAvailable || active.IsSceneDepthAvailable,
                host.IsGpuComputeAvailable || active.IsGpuComputeAvailable,
                Math.Max(host.MaxGpuParticlesPerFrame, active.MaxGpuParticlesPerFrame));
        }
    }

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
}
