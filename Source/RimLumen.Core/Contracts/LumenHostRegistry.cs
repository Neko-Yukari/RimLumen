using System;
using System.Collections.Generic;
using System.Threading;
using RimLumen.Core.Assets;

namespace RimLumen.Core.Contracts;

internal enum LumenAssetResolution { Found, NotFound, Rejected }

public static partial class LumenHostRegistry
{
    private static readonly object Sync = new();
    private static readonly List<ILumenFrameContributor> Contributors = new();
    private static ILumenFrameContributor[] contributorSnapshot = Array.Empty<ILumenFrameContributor>();
    private static readonly List<ILumenAssetProvider> AssetProviders = new();
    private static readonly LumenAssetValidationCache ValidatedAssets = new();
    private static readonly ILumenNodeFeatureBackend?[] NodeFeatureBackends = new ILumenNodeFeatureBackend?[Enum.GetValues<LumenNodeFeature>().Length];

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
                Volatile.Write(ref contributorSnapshot, Contributors.ToArray());
            }
        }
    }

    public static void UnregisterContributor(ILumenFrameContributor contributor)
    {
        lock (Sync)
        {
            if (Contributors.Remove(contributor))
                Volatile.Write(ref contributorSnapshot, Contributors.ToArray());
        }
    }

    public static ILumenFrameContributor[] GetContributorsSnapshot()
    {
        ILumenFrameContributor[] snapshot = Volatile.Read(ref contributorSnapshot);
        return snapshot.Length == 0 ? Array.Empty<ILumenFrameContributor>() : (ILumenFrameContributor[])snapshot.Clone();
    }

    // Public callers keep owning their copies; the pipeline only borrows the immutable published contents.
    internal static ReadOnlySpan<ILumenFrameContributor> GetSharedContributorsSnapshot() => Volatile.Read(ref contributorSnapshot);

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

    // Requires: backend is non-null.
    // Ensures: backend drives every node carrying backend.Feature in handles played from now on; registering the same
    //          instance again is a no-op; a different backend for an already registered feature throws
    //          InvalidOperationException.
    public static void RegisterNodeFeatureBackend(ILumenNodeFeatureBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        lock (Sync)
        {
            ILumenNodeFeatureBackend? existing = NodeFeatureBackends[(int)backend.Feature];
            if (existing is not null && !ReferenceEquals(existing, backend))
                throw new InvalidOperationException($"A {backend.Feature} node backend is already registered ({existing.GetType().FullName}).");
            NodeFeatureBackends[(int)backend.Feature] = backend;
        }
    }

    // Ensures: when backend is the registered backend of its feature, later Play calls find none (handles already
    //          playing keep their instances); otherwise no-op.
    public static void UnregisterNodeFeatureBackend(ILumenNodeFeatureBackend backend)
    {
        lock (Sync)
        {
            if (ReferenceEquals(NodeFeatureBackends[(int)backend.Feature], backend))
                NodeFeatureBackends[(int)backend.Feature] = null;
        }
    }

    public static ILumenNodeFeatureBackend? GetNodeFeatureBackend(LumenNodeFeature feature)
    {
        lock (Sync)
        {
            return NodeFeatureBackends[(int)feature];
        }
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
        => ResolveAsset(effectId, out asset) == LumenAssetResolution.Found;

    public static ReadOnlySpan<LumenEnvironmentForce> GetEnvironmentForces(int mapIndex, LumenEnvironmentChannel channels)
        => RimLumen.Core.ParticleWorld.ParticleEnvironment.ForMap(mapIndex, channels).Span;

    public static ReadOnlySpan<LumenCursorSnapshot> GetCursors(int mapIndex)
        => RimLumen.Core.ParticleWorld.ParticleEnvironment.CursorsForMap(mapIndex);

    internal static LumenAssetResolution ResolveAsset(string effectId, out EffectAsset? asset)
    {
        lock (Sync)
        {
            for (int i = AssetProviders.Count - 1; i >= 0; i--)
            {
                if (AssetProviders[i].TryResolveEffect(effectId, out asset) && asset is not null)
                {
                    bool valid = ValidatedAssets.TryValidate(asset, AssetProviders[i], out string? rejection);
                    if (rejection is not null) Verse.Log.Error(rejection);
                    if (valid) return LumenAssetResolution.Found;
                    asset = null;
                    return LumenAssetResolution.Rejected;
                }
            }
        }
        asset = null;
        return LumenAssetResolution.NotFound;
    }
}
