using System.IO;
using System.Runtime.CompilerServices;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Assets;

// The registry serializes access. Asset identity, not id text, owns the validation result.
internal sealed class LumenAssetValidationCache
{
    private sealed record Validation(bool IsValid);
    private readonly ConditionalWeakTable<EffectAsset, Validation> results = new();

    // Ensures: rejection is produced only on the first rejected resolution, so the caller logs it once.
    internal bool TryValidate(EffectAsset asset, ILumenAssetProvider provider, out string? rejection)
    {
        rejection = null;
        if (results.TryGetValue(asset, out Validation? cached)) return cached.IsValid;
        try
        {
            LumenAssetRules.Validate(asset, $"effect '{asset.EffectId}'");
            results.Add(asset, new Validation(true));
            return true;
        }
        catch (InvalidDataException error)
        {
            results.Add(asset, new Validation(false));
            rejection = $"[RimLumen] Provider {provider.GetType().FullName} rejected effect '{asset.EffectId}': {error.Message}";
            return false;
        }
    }
}
