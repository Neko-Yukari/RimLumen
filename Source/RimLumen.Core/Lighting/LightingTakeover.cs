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
    private const string ShadeFile = "RimLumen/RimLumenSunShadowTakeover.frag.glsl";
    private static AccessTools.FieldRef<GlowGrid, NativeList<GlowLight>> lights = null!;
    private static Func<bool>? isoViewReady;
    private static LightPoolShadows.Kind lightOverlay = null!, sunShadow = null!, sunShadowFade = null!;
    private static LightPoolShadows.Kind[] kinds = null!;
    private static Shader shade = null!;
    private static MaterialBinding? shadeBinding;
    private static bool? programsOriginal; // null until the first frame with the setting on
    private readonly List<FinalDrawItem> shades = new();

    // Requires: main thread, install time (RimLumenMapHooks.Arm).
    // Ensures: binds GlowGrid.lights (private NativeList<GlowLight>) and, when IsoRim (packageId Peren.IsoRim17) is
    //          active, IsoRim.Api.ViewReady. Throws when any bound member is missing or changed.
    internal static void BindEngineMembers()
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        FieldInfo? field = typeof(GlowGrid).GetField("lights", instance);
        if (field?.FieldType != typeof(NativeList<GlowLight>)) throw new MissingFieldException(typeof(GlowGrid).FullName, "lights (NativeList<GlowLight>)");
        lights = AccessTools.FieldRefAccess<GlowGrid, NativeList<GlowLight>>(field);
        ModContentPack? iso = LoadedModManager.RunningModsListForReading.Find(mod => string.Equals(mod.PackageIdPlayerFacing, "Peren.IsoRim17", StringComparison.OrdinalIgnoreCase));
        if (iso is null) return;
        Type api = iso.assemblies.loadedAssemblies.Select(a => a.GetType("IsoRim.Api")).FirstOrDefault(t => t is not null)
            ?? throw new TypeLoadException("IsoRim.Api");
        var getter = api.GetProperty("ViewReady", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)?.GetMethod;
        if (getter is not { IsPublic: true } || getter.ReturnType != typeof(bool) || getter.GetParameters().Length != 0)
            throw new MissingMemberException(api.FullName, "public static bool ViewReady");
        isoViewReady = getter.CreateDelegate<Func<bool>>();
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
            into.Add(new VisualPointLight(light.position.x + 0.5f, VisualPointLight.VanillaHeight, light.position.z + 0.5f, light.glowRadius,
                new Vector3(c.r, c.g, c.b) / 255f, VisualPointLightKind.Vanilla));
        }
    }

    // Requires: main thread, MapRenderFeature.CaptureFrame postfix of an effective takeover frame (LightPoolShadows.Rebind
    //           does not run on such frames).
    // Ensures: design 3.2: every LightOverlay draw (vanilla material id) is replaced by a copy running
    //          RimLumenLightTakeover.frag.glsl (sky and pools, unshadowed) with vanilla mesh, matrix, sort key and
    //          pipeline state, plus a sun shade at SunShadow's queue: the same mesh lowered to the Shadows altitude
    //          through RimLumenSunShadowTakeover.frag.glsl with vanilla SunShadow's pipeline state minus its stencil
    //          write. SunShadow draws and persistent SunShadowFade draws are removed; dynamic SunShadowFade draws are
    //          replaced by copies running RimLumenSunShadowFadeTakeover.{vert,frag}.glsl with vanilla stencil state.
    //          Other draws are untouched; foreign programs on these queues are logged once and left alone
    //          (LightPoolShadows.Rewrite).
    internal void Rebind(MeshDrawFrame frame)
    {
        var cast = UnityEngine.Shader.GetGlobalVector(ShaderPropertyIDs.MapSunLightDirection);
        var color = MatBases.SunShadow.color;
        var castValue = MaterialUniformValue.FromVector(MaterialUniformType.Vector4, new Vector4(cast.x, cast.y, cast.z, cast.w));
        var colorValue = MaterialUniformValue.FromVector(MaterialUniformType.Vector4, new Vector4(color.r, color.g, color.b, color.a));
        // Object shadows of the sun: round 0's mask channel (LightPoolRenderer), zero on frames without them.
        var channelValue = MaterialUniformValue.FromVector(MaterialUniformType.Vector4, pools.FrameSunChannel);
        int fadeChannelOffset = sunShadowFade.Vanilla.MaterialLayout.UniformSize;
        // SectionLayer_LightingOverlay prints at LightingOverlay; SectionLayer_SunShadows prints at Shadows.
        float lower = AltitudeLayer.Shadows.AltitudeFor() - AltitudeLayer.LightingOverlay.AltitudeFor();
        LightPoolShadows.Rewrite(frame, kinds, (item, kind, persistent) =>
        {
            MaterialTextureResourceSet<Texture?> resources = item.Resources;
            if (kind == sunShadowFade && !persistent)
            {
                pools.BindOcclusion(ref resources);
                resources.Set(3, pools.FirstMask);
                MaterialUniformBytes fadeUniforms = item.Uniforms;
                fadeUniforms.Write(fadeChannelOffset, channelValue);
                return LightPoolShadows.CopyDraw(item, kind, in fadeUniforms, in resources);
            }
            if (kind != lightOverlay) return null;
            MaterialUniformBytes uniforms = item.Uniforms;
            uniforms.Write(16, castValue);
            uniforms.Write(32, colorValue);
            uniforms.Write(48, channelValue);
            MaterialTextureResourceSet<Texture?> shadeResources = resources;
            pools.BindOcclusion(ref shadeResources);
            shadeResources.Set(3, lightSum);
            shadeResources.Set(4, pools.FirstMask);
            shades.Add(LightPoolShadows.MoveDraw(item, ShadeBinding(item.Binding), shade.DefaultRenderQueue, lower, in uniforms, in shadeResources));
            resources.Set(0, lightSum);
            return LightPoolShadows.CopyDraw(item, kind, in item.Uniforms, in resources);
        });
        frame.Draws.AddRange(shades);
        shades.Clear();
    }

    private static MaterialBinding ShadeBinding(MaterialBinding overlay) => shadeBinding ??= new MaterialBinding(-6, overlay.MaterialId,
        overlay.MaterialVersion, shade.Key, shade, shade.MaterialLayout, overlay.Uniforms, default, shade.DefaultRenderQueue,
        new ShaderVariantKey(shade, 0, string.Empty, shade.UseTexture), shade.PipelineState, overlay.WrapModeOverride, usesGlobals: false);

    private static bool ResolveKinds()
    {
        lightOverlay = new("LightOverlay", LightPoolRenderer.VertexFile, "LightOverlay.frag.glsl", "RimLumen/RimLumenLightTakeover.frag.glsl",
            [LightPoolShadows.LightSumTexture], MatBases.LightOverlay, -3, takeover: true);
        sunShadow = new("SunShadow", "SunShadow.vert.glsl", "SunShadow.frag.glsl", null, [], MatBases.SunShadow, -4, takeover: true);
        sunShadowFade = new("SunShadowFade", "SunShadowFade.vert.glsl", "SunShadowFade.frag.glsl", "RimLumen/RimLumenSunShadowFadeTakeover.frag.glsl",
            LightPoolShadows.FadeTextures, MatBases.SunShadowFade, -5, takeover: true, liftedVertex: "RimLumen/RimLumenSunShadowFadeTakeover.vert.glsl",
            liftedUniforms: LightPoolShadows.FadeUniforms);
        kinds = [lightOverlay, sunShadow, sunShadowFade];
        string core = LightPoolShadows.CoreShaderRoot();
        var overridden = new List<string>();
        foreach (var kind in kinds)
            if (kind.Resolve(core) is { } files) overridden.Add($"{kind.DefName} {files}");
        if (overridden.Count == 0)
        {
            shade = CreateShade();
            return true;
        }
        Log.Warning($"[RimLumen] Lighting takeover is off for this session: another mod overrides vanilla's {string.Join("; ", overridden)}.");
        return false;
    }

    // The sun shade runs vanilla SunShadow's registered program state (Shaders.Map.xml SunShadow: MultiplyDestination,
    // depth test on, depth write off, stencil Greater ref 12 read mask 4) with LightOverlay's vertex stage and write mask 0:
    // bit 4 still keeps it off sun-masked cells, and SunShadowFade (3175, Greater ref 4 read mask 4) still runs on top.
    private static Shader CreateShade()
    {
        ShaderProgramDefinition program = LightPoolShadows.Clone(sunShadow.Vanilla.Program);
        program.vertexShaderFileName = LightPoolRenderer.VertexFile;
        program.fragmentShaderFileName = ShadeFile;
        PipelineState vanilla = sunShadow.Vanilla.PipelineState;
        Shader shader = LightPoolRenderer.CreateShader("RimLumen.Takeover.SunShade", program, vanilla with { Stencil = vanilla.Stencil with { WriteMask = 0 } });
        if (!LightPoolShadows.LayoutMatches(shader.MaterialLayout, lightOverlay.Vanilla.MaterialLayout,
                LightPoolShadows.ShadeTextures, LightPoolShadows.ShadeUniforms))
            throw new InvalidOperationException($"[RimLumen] {ShadeFile} no longer matches the material layout of vanilla LightOverlay.frag.glsl.");
        return shader;
    }
}
