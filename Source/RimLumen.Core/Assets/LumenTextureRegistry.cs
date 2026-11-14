using System;
using System.Collections.Generic;
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

    // Ensures: removes any explicit registration for key; returns true when removed.
    public static bool Unregister(string key)
    {
        lock (Sync)
        {
            return registered.Remove(key);
        }
    }

    // Ensures: clears explicit registrations and missing-texture warning cache (for isolated tests).
    public static void Clear()
    {
        lock (Sync)
        {
            registered.Clear();
            warnedMissing.Clear();
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
    {
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
            return White();
        }

        hasTexture = true;
        return tex;
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
