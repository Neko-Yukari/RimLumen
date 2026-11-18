using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimLumen.Core.ParticleWorld;

internal static class ParticleEnvironmentHooks
{
    internal static void Arm()
    {
        MethodInfo explosion = typeof(Explosion).GetMethod(nameof(Explosion.StartExplosion), new[] { typeof(SoundDef), typeof(List<Thing>) })
            ?? throw new MissingMethodException(typeof(Explosion).FullName, nameof(Explosion.StartExplosion));
        MethodInfo dispose = typeof(Map).GetMethod(nameof(Map.Dispose), Type.EmptyTypes)
            ?? throw new MissingMethodException(typeof(Map).FullName, nameof(Map.Dispose));
        var harmony = new Harmony("NekoYukari.RimLumen.ParticleWorld");
        harmony.Patch(explosion, postfix: new HarmonyMethod(typeof(ParticleEnvironmentHooks), nameof(ExplosionStarted)));
        harmony.Patch(dispose, prefix: new HarmonyMethod(typeof(ParticleEnvironmentHooks), nameof(MapRemoved)));
    }

    private static void ExplosionStarted(Explosion __instance)
    {
        if (!__instance.Spawned) return; // StartExplosion itself rejects unspawned explosions.
        ParticleEnvironment.RecordExplosion(__instance.Map.uniqueID,
            new System.Numerics.Vector3(__instance.Position.x + 0.5f, 0f, __instance.Position.z + 0.5f), __instance.radius);
    }

    private static void MapRemoved(Map __instance)
    {
        ParticleCollisionFields.Forget(__instance.uniqueID);
        RimLumen.Core.Lighting.Occlusion.OcclusionField.Forget(__instance.uniqueID);
    }
}
