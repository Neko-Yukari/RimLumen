using System;
using System.Collections.Generic;
using System.Numerics;

namespace RimLumen.Core.Lighting.Casters;

// Where each light's shadow goes: mask channel Channel (0-3, RGBA) of round Round.
internal readonly record struct ShadowSlot(int Round, int Channel)
{
    internal static readonly ShadowSlot None = new(0, -1);

    internal bool HasChannel => Channel >= 0;

    // The colour a caster writes and a pool reads: 1 in Channel, 0 elsewhere (RimLumenObjectShadow.frag.glsl,
    // RimLumenLightPoolAccumulate.frag.glsl).
    internal Vector4 Selector => new(Channel == 0 ? 1f : 0f, Channel == 1 ? 1f : 0f, Channel == 2 ? 1f : 0f, Channel == 3 ? 1f : 0f);
}

// Channel assignment of the object-shadow masks (design object-shadows Q1). Each round draws its casters into one
// RGBA8 mask with blend Max, then its pools sample it; a channel is shared only by lights whose pool boxes stay apart,
// and every caster fragment outside its light's box is discarded, so no light reads another's shadow.
internal static class ShadowChannels
{
    // Round 0's channel A holds the takeover sun.
    internal const int SunChannel = 3;

    // Ensures: one slot per light (x, z, radius), in order, each the first slot (round-major, channel-minor) holding no
    //          light whose box [x - r, x + r] x [z - r, z + r] overlaps or touches its own; with sun, round 0's SunChannel
    //          is never given out.
    internal static ShadowSlot[] Assign(IReadOnlyList<Vector3> lights, bool sun)
    {
        var slots = new ShadowSlot[lights.Count];
        var holders = new List<List<Vector3>>();
        for (int i = 0; i < lights.Count; i++)
            slots[i] = Allocate(holders, lights[i], sun);
        return slots;
    }

    // Ensures: one slot per light in order; lights with CastShadows == false (or any light when objectShadows == false)
    //          receive ShadowSlot.None without occupying a mask channel or bumping shadowed lights to later rounds.
    internal static ShadowSlot[] Assign(IReadOnlyList<VisualPointLight> lights, bool objectShadows, bool sun)
    {
        var slots = new ShadowSlot[lights.Count];
        if (!objectShadows)
        {
            Array.Fill(slots, ShadowSlot.None);
            return slots;
        }
        var holders = new List<List<Vector3>>();
        for (int i = 0; i < lights.Count; i++)
        {
            VisualPointLight light = lights[i];
            slots[i] = light.CastShadows
                ? Allocate(holders, new Vector3(light.X, light.Z, light.Radius), sun)
                : ShadowSlot.None;
        }
        return slots;
    }

    private static ShadowSlot Allocate(List<List<Vector3>> holders, Vector3 light, bool sun)
    {
        int index = 0;
        for (; ; index++)
        {
            if (index == holders.Count) holders.Add(new List<Vector3>());
            if (sun && index == SunChannel) continue;
            if (!holders[index].Exists(other => MathF.Abs(other.X - light.X) <= other.Z + light.Z && MathF.Abs(other.Y - light.Y) <= other.Z + light.Z)) break;
        }
        holders[index].Add(light);
        return new ShadowSlot(index / 4, index % 4);
    }
}
