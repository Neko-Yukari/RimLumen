using System;
using System.Collections.Generic;
using RimLumen.Core.Contracts;
using Waylith.Rendering;

namespace RimLumen.Core.Assets;

/// <summary>
/// Shared main-thread particle/effect texture registry used by both L1 and L2 renderers.
/// </summary>
public static class LumenTextureRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Texture> registered = new(StringComparer.Ordinal);
    private static readonly HashSet<string> warnedMissing = new(StringComparer.Ordinal);

    // Requires: main thread; key is non-empty; texture is non-null.
    // Ensures: associates key with texture; overwrites any prior registration for key.
    public static void Register(string key, Texture texture)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(texture);
        lock (Sync)
        {
            registered[key] = texture;
            warnedMissing.Remove(key);
        }
    }

    // Ensures: looks up only explicitly registered textures without touching host ContentFinder (safe offline).
    public static bool TryGetRegistered(string key, out Texture? texture)
    {
        lock (Sync)
        {
            if (registered.TryGetValue(key, out var found))
            {
                texture = found;
                return true;
            }
        }

        texture = null;
        return false;
    }

    // Requires: main thread with initialized graphics/host when resolving ContentFinder fallback.
    // Ensures: resolves explicit registration first, then ContentFinder<UnityEngine.Texture2D>.Get(key, false)?.CoreTexture;
    //          ensures SampledReady + resident GPU resource; falls back to Texture2D.whiteTexture with hasTexture=false and one-shot warning.
    public static Texture ResolveForSamplingOnMainThread(string? key, out bool hasTexture)
        => ResolveForSamplingOnMainThread(key, LumenColorSpace.Stored, out hasTexture, out _);

    internal static Texture ResolveForSamplingOnMainThread(string? key, LumenColorSpace colorSpace,
        out bool hasTexture, out bool textureSrgb)
    {
        textureSrgb = false;
        if (string.IsNullOrEmpty(key))
        {
            hasTexture = false;
            return White();
        }

        Texture? tex;
        bool found;
        lock (Sync)
        {
            found = registered.TryGetValue(key, out tex);
        }

        if (!found)
        {
            tex = ResolveHostTexture(key);
        }

        if (tex is not null)
        {
            textureSrgb = TextureIsSrgb(key, tex, colorSpace);
            Texture.EnsureSampledReady(tex);
            if (tex.GpuResource is null && !tex.EnsureResident())
            {
                tex = null;
            }
        }

        if (tex is null)
        {
            bool firstWarning;
            lock (Sync)
            {
                firstWarning = warnedMissing.Add(key);
            }

            if (firstWarning)
            {
                Verse.Log.Warning($"[RimLumen] Particle texture '{key}' was not found; falling back to procedural shape.");
            }

            hasTexture = false;
            textureSrgb = false;
            return White();
        }

        hasTexture = true;
        return tex;
    }

    // Main-thread metadata boundary. Stored does not consume this flag; Linear never guesses it.
    // RimUnity MaterialColorSpace.cs:16 at 45ca90987f7d3721715b45c021cab986ee14c468 uses !Linear;
    // D33 deliberately rejects missing metadata instead of adopting that upstream null default.
    internal static bool TextureIsSrgb(string key, Texture texture, LumenColorSpace colorSpace)
    {
        if (texture is Texture2D { CpuData: { } data }) return !data.Linear;
        if (colorSpace == LumenColorSpace.Linear)
            throw new InvalidOperationException($"[RimLumen] Particle texture '{key}' colour metadata missing: Linear materials require Texture2D.CpuData.Linear.");
        return false;
    }

    // Kept out of Register/TryGetRegistered so pure registry tests never enter the host Content system.
    private static Texture? ResolveHostTexture(string key) =>
        Verse.ContentFinder<UnityEngine.Texture2D>.Get(key, reportFailure: false)?.CoreTexture;

    private static Texture White()
    {
        var white = Texture2D.whiteTexture;
        Texture.EnsureSampledReady(white);
        white.EnsureResident();
        return white;
    }
}
