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

    private sealed class CompositeHandle : ILumenEffectHandle
    {
        private readonly ILumenEffectHandle native;
        private readonly ILumenEffectHandle external;
        private bool finished;

        public CompositeHandle(ILumenEffectHandle native, ILumenEffectHandle external)
        {
            this.native = native;
            this.external = external;
            native.Finished += OnPartFinished;
            external.Finished += OnPartFinished;
        }

        public bool IsAlive => native.IsAlive || external.IsAlive;

        // Notify and StateChanged come from the native handle, which owns timeline events and states.
        public event Action<string>? Notify
        {
            add => native.Notify += value;
            remove => native.Notify -= value;
        }

        public event Action<string>? StateChanged
        {
            add => native.StateChanged += value;
            remove => native.StateChanged -= value;
        }

        // Ensures: raised once, when the second of the two parts has finished.
        public event Action? Finished;

        public void Trigger(string name)
        {
            try { native.Trigger(name); }
            finally { external.Trigger(name); }
        }
        public void GoTo(string stateName)
        {
            try { native.GoTo(stateName); }
            finally { external.GoTo(stateName); }
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

        private void OnPartFinished()
        {
            if (finished || native.IsAlive || external.IsAlive) return;
            finished = true;
            Finished?.Invoke();
        }
    }
}
