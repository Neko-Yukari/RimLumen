using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using HarmonyLib;
using RimLumen.Core.Mod;
using RimLumen.Core.Rendering;
using Unity.Collections;
using Verse.Glow;
using Verse;
using Waylith.Rendering;

namespace RimLumen.Core.Lighting;

// Host boundary of the lighting takeover (design 3 and 4.1). Owns the IsoRim gate, GlowGrid.lights reading and the
// rebinding of vanilla lighting draws; the shading itself is SunShadowComposition / SunTransport / LightTransport.
internal sealed class LightingTakeover(LightPoolRenderer pools, BorrowedRenderTexture lightSum)
{
    private static AccessTools.FieldRef<GlowGrid, NativeList<GlowLight>> lights = null!;
    private static Func<Map, IntVec3, bool> hideCommon = null!, hideRainFog = null!;
    private static Func<bool>? isoViewReady;
    private static LightPoolShadows.Kind lightOverlay = null!, sunShadowFade = null!;
    private static LightPoolShadows.Kind[] kinds = null!;
    private static bool? programsOriginal; // null until the first frame with the setting on
    // Requires: main thread, install time (RimLumenMapHooks.Arm).
    // Ensures: binds GlowGrid.lights (private NativeList<GlowLight>), SectionLayer_IndoorMask.HideCommon and
    //          HideRainFogOverlay (private static, exact signatures) and, when IsoRim (packageId Peren.IsoRim17) is
    //          active, IsoRim.Api.ViewReady. Throws when any bound member is missing or changed.
    internal static void BindEngineMembers()
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        FieldInfo? field = typeof(GlowGrid).GetField("lights", instance);
        if (field?.FieldType != typeof(NativeList<GlowLight>)) throw new MissingFieldException(typeof(GlowGrid).FullName, "lights (NativeList<GlowLight>)");
        lights = AccessTools.FieldRefAccess<GlowGrid, NativeList<GlowLight>>(field);
        hideCommon = BindMask("HideCommon");
        hideRainFog = BindMask("HideRainFogOverlay");
        ModContentPack? iso = LoadedModManager.RunningModsListForReading.Find(mod => string.Equals(mod.PackageIdPlayerFacing, "Peren.IsoRim17", StringComparison.OrdinalIgnoreCase));
        if (iso is null) return;
        Type api = iso.assemblies.loadedAssemblies.Select(a => a.GetType("IsoRim.Api")).FirstOrDefault(t => t is not null)
            ?? throw new TypeLoadException("IsoRim.Api");
        var getter = api.GetProperty("ViewReady", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)?.GetMethod;
        if (getter is not { IsPublic: true } || getter.ReturnType != typeof(bool) || getter.GetParameters().Length != 0)
            throw new MissingMemberException(api.FullName, "public static bool ViewReady");
        isoViewReady = getter.CreateDelegate<Func<bool>>();
    }

    private static Func<Map, IntVec3, bool> BindMask(string name)
    {
        MethodInfo? method = typeof(SectionLayer_IndoorMask).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            null, [typeof(Map), typeof(IntVec3)], null);
        if (method is not { IsPrivate: true } || method.ReturnType != typeof(bool)) throw new MissingMethodException(typeof(SectionLayer_IndoorMask).FullName, name);
        return method.CreateDelegate<Func<Map, IntVec3, bool>>();
    }

    // Requires: main thread, once per RimLumenPipeline.BeginCapture of a playing frame.
    // Ensures: Settings.LightingTakeover && !IsoRim ViewReady, evaluated once for the frame (design 3.1). The first call
    //          with the setting on resolves the three vanilla programs; when any is not Core's own, takeover stays off
    //          for the session and one warning names each overridden program and its files.
    internal static bool EvaluateFrame()
    {
        if (!RimLumenMod.Settings.LightingTakeover) return false;
        programsOriginal ??= ResolveKinds();
        return programsOriginal.Value && isoViewReady?.Invoke() != true;
    }

    // Requires: main thread, effective takeover frame.
    // Ensures: appends one Vanilla pool per GlowGrid.lights entry of map (design 3.5); GlowGrid is only read.
    internal static void CollectVanillaPools(Map map, List<VisualPointLight> into)
    {
        NativeList<GlowLight> mapLights = lights(map.glowGrid);
        for (int i = 0; i < mapLights.Length; i++)
        {
            GlowLight light = mapLights[i];
            ColorInt c = light.glowColor;
            into.Add(new VisualPointLight(light.position.x + 0.5f, light.position.z + 0.5f, light.glowRadius,
                new Vector3(c.r, c.g, c.b) / 255f, VisualPointLightKind.Vanilla));
        }
    }

    // Requires: main thread; the cell box lies inside the map.
    // Ensures: true where vanilla's stencil bit 4 keeps sun shadows off the cell (design 3.9): HideCommon ||
    //          HideRainFogOverlay, or terrain dontRender. Fed to OcclusionBuilder.SetSunMask on Buildings | Roofs |
    //          FogOfWar | Terrain dirt.
    internal static bool SunMasked(Map map, IntVec3 cell) => hideCommon(map, cell) || hideRainFog(map, cell) || map.terrainGrid.TerrainAt(cell).dontRender;

    // Requires: main thread, MapRenderFeature.CaptureFrame postfix of an effective takeover frame (LightPoolShadows.Rebind
    //           does not run on such frames).
    // Ensures: design 3.2: every LightOverlay draw (vanilla material id) is replaced by a copy running
    //          RimLumenLightTakeover.frag.glsl with vanilla mesh, matrix, sort key and pipeline state; SunShadow draws and
    //          persistent SunShadowFade draws are removed; dynamic SunShadowFade draws are replaced by copies running
    //          RimLumenSunShadowFadeTakeover.{vert,frag}.glsl with vanilla stencil state. Other draws are untouched;
    //          foreign programs on these queues are logged once and left alone (LightPoolShadows.Rewrite).
    internal void Rebind(MeshDrawFrame frame)
    {
        var cast = UnityEngine.Shader.GetGlobalVector(ShaderPropertyIDs.MapSunLightDirection);
        var color = MatBases.SunShadow.color;
        var castValue = MaterialUniformValue.FromVector(MaterialUniformType.Vector4, new Vector4(cast.x, cast.y, cast.z, cast.w));
        var colorValue = MaterialUniformValue.FromVector(MaterialUniformType.Vector4, new Vector4(color.r, color.g, color.b, color.a));
        LightPoolShadows.Rewrite(frame, kinds, (item, kind, persistent) =>
        {
            bool overlay = kind == lightOverlay;
            if (!overlay && (persistent || kind != sunShadowFade)) return null;
            MaterialUniformBytes uniforms = item.Uniforms;
            MaterialTextureResourceSet<Texture?> resources = item.Resources;
            pools.BindOcclusion(ref resources);
            if (overlay)
            {
                uniforms.Write(16, castValue);
                uniforms.Write(32, colorValue);
                resources.Set(3, lightSum);
            }
            return LightPoolShadows.CopyDraw(item, kind, in uniforms, in resources);
        });
    }

    private static bool ResolveKinds()
    {
        lightOverlay = new("LightOverlay", LightPoolRenderer.VertexFile, "LightOverlay.frag.glsl", "RimLumen/RimLumenLightTakeover.frag.glsl", MatBases.LightOverlay, -3, takeover: true);
        var sunShadow = new LightPoolShadows.Kind("SunShadow", "SunShadow.vert.glsl", "SunShadow.frag.glsl", null, MatBases.SunShadow, -4, takeover: true);
        sunShadowFade = new("SunShadowFade", "SunShadowFade.vert.glsl", "SunShadowFade.frag.glsl", "RimLumen/RimLumenSunShadowFadeTakeover.frag.glsl", MatBases.SunShadowFade, -5,
            takeover: true, liftedVertex: "RimLumen/RimLumenSunShadowFadeTakeover.vert.glsl");
        kinds = [lightOverlay, sunShadow, sunShadowFade];
        string core = LightPoolShadows.CoreShaderRoot();
        var overridden = new List<string>();
        foreach (var kind in kinds)
            if (kind.Resolve(core) is { } files) overridden.Add($"{kind.DefName} {files}");
        if (overridden.Count == 0) return true;
        Log.Warning($"[RimLumen] Lighting takeover is off for this session: another mod overrides vanilla's {string.Join("; ", overridden)}.");
        return false;
    }

    internal static bool LayoutMatches(MaterialLayout clone, MaterialLayout vanilla, bool overlay)
    {
        string[] textures = overlay ? ["_LumenOcclusion", "_LumenOcclusionCoarse", "_LumenDoorOpen", "_LumenLightSum"] : ["_LumenOcclusion", "_LumenOcclusionCoarse", "_LumenDoorOpen"];
        if (vanilla.TextureSlots.Length != 0 || clone.TextureSlots.Length != textures.Length || clone.StorageBufferSlots.Length != 0 ||
            !clone.UniformSlots.Take(vanilla.UniformSlots.Length).SequenceEqual(vanilla.UniformSlots)) return false;
        for (int i = 0; i < textures.Length; i++)
        {
            MaterialTextureSlot slot = clone.TextureSlots[i];
            if (slot.ResourceSlot != i || slot.Binding != i + 1 || slot.PropertyName != textures[i] ||
                slot.Kind != MaterialTextureKind.Combined || slot.Default != MaterialTextureDefault.Black) return false;
        }
        if (!overlay) return clone.UniformSize == vanilla.UniformSize && clone.UniformSlots.Length == vanilla.UniformSlots.Length;
        return vanilla.UniformSize == 16 && clone.UniformSize == 48 && clone.UniformSlots.Skip(vanilla.UniformSlots.Length)
            .Select(slot => (slot.PropertyName, slot.Offset, slot.Type)).SequenceEqual(new[]
            { ("_LumenCastVect", 16, MaterialUniformType.Vector4), ("_LumenSunShadowColor", 32, MaterialUniformType.Vector4) });
    }
}
