using System;
using System.Linq;
using RimLumen.Core.Contracts;

namespace RimLumen.Adapter.RimUnity;

public sealed class RimUnityHybridVfxService : ILumenVfxService
{
    private readonly ILumenVfxService inner;
    private readonly RimUnityAssetProvider provider;
    private readonly IRimUnityExternalPlayer externalPlayer;

    public RimUnityHybridVfxService(ILumenVfxService inner, RimUnityAssetProvider provider, IRimUnityExternalPlayer externalPlayer)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.externalPlayer = externalPlayer ?? throw new ArgumentNullException(nameof(externalPlayer));
    }

    public LumenCapabilities Capabilities => inner.Capabilities;

    public ILumenEffectHandle Play(in LumenPlayRequest request)
    {
        if (!provider.TryInspectTranslation(request.EffectId, out var report) || report is null ||
            !report.Nodes.Any(node => node.Tier == LumenParticleTier.L0_ExternalCpu)) return inner.Play(request);
        ILumenEffectHandle native = inner.Play(request);
        try
        {
            return new CompositeHandle(native, externalPlayer.Play(request, report));
        }
        catch
        {
            native.Dispose();
            throw;
        }
    }

    private sealed class CompositeHandle(ILumenEffectHandle native, ILumenEffectHandle external) : ILumenEffectHandle
    {
        public bool IsAlive => native.IsAlive || external.IsAlive;
        public void Trigger(string eventOrStateName)
        {
            try { native.Trigger(eventOrStateName); }
            finally { external.Trigger(eventOrStateName); }
        }
        public void SetParam(string name, float value)
        {
            try { native.SetParam(name, value); }
            finally { external.SetParam(name, value); }
        }
        public void Stop(bool allowFadeOut = true)
        {
            try { native.Stop(allowFadeOut); }
            finally { external.Stop(allowFadeOut); }
        }
        public void Dispose()
        {
            try { native.Dispose(); }
            finally { external.Dispose(); }
        }
    }
}
