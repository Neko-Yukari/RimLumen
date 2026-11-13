using System;
using System.Collections.Generic;

namespace RimLumen.Core.Contracts;

public static class LumenHostRegistry
{
    private static readonly object Sync = new();
    private static readonly List<ILumenFrameContributor> Contributors = new();
    private static readonly List<ILumenAssetProvider> AssetProviders = new();

    public static ILumenVfxService? VfxService { get; private set; }
    public static ILumenGpuParticleEngine? GpuParticleEngine { get; private set; }

    // Requires: contributor is non-null; idempotent if same instance registered twice.
    // Ensures: contributor is visible to the HDR render pipeline snapshot.
    public static void RegisterContributor(ILumenFrameContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        lock (Sync)
        {
            if (!Contributors.Contains(contributor))
            {
                Contributors.Add(contributor);
            }
        }
    }

    public static void UnregisterContributor(ILumenFrameContributor contributor)
    {
        lock (Sync)
        {
            Contributors.Remove(contributor);
        }
    }

    public static ILumenFrameContributor[] GetContributorsSnapshot()
    {
        lock (Sync)
        {
            return Contributors.Count == 0 ? Array.Empty<ILumenFrameContributor>() : Contributors.ToArray();
        }
    }

    public static void RegisterGpuParticleEngine(ILumenGpuParticleEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        GpuParticleEngine = engine;
        RegisterContributor(engine);
    }

    public static void RegisterVfxService(ILumenVfxService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        VfxService = service;
    }

    public static void RegisterAssetProvider(ILumenAssetProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Sync)
        {
            if (!AssetProviders.Contains(provider))
            {
                AssetProviders.Add(provider);
            }
        }
    }

    public static bool TryResolveAsset(string effectId, out EffectAsset? asset)
    {
        lock (Sync)
        {
            for (int i = AssetProviders.Count - 1; i >= 0; i--)
            {
                if (AssetProviders[i].TryResolveEffect(effectId, out asset) && asset is not null)
                {
                    return true;
                }
            }
        }
        asset = null;
        return false;
    }
}
