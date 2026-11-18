using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using RimLumen.Core.Rendering;
using Verse;
using Waylith.Rendering;
using Shader = Waylith.Rendering.Shader;

namespace RimLumen.Core.Lighting;

// Vanilla sun and moon shadows (SunShadow at 3170, SunShadowFade at 3175, MultiplyDestination) run after the
// light-pool composite (3101) and would multiply a pool back down to vanilla darkness. On frames with pools, Rebind
// replaces this frame's vanilla shadow draws with copies whose fragment lifts the shadow under a pool
// (RimLumenSunShadow*.frag.glsl, lumenShadowOverPool). Mesh, matrix, pass, layer, sort key, uniforms, instancing and
// pipeline state (blend, depth, stencil) are the vanilla draw's; MatBases and persistent draw items are never mutated.
// Only draws of the vanilla shadow material through the vanilla program are lifted; a draw at vanilla's shadow queue
// that runs another program (RimIris's water gate) is left alone and logged once.
internal sealed class LightPoolShadows
{
    private delegate FinalDrawItem RentDraw(Matrix4x4 matrix, byte passId, MeshDrawGeometry meshDraw,
        MaterialBinding materialBinding, bool hasOverride, in MaterialUniformBytes overrideUniforms,
        in MaterialTextureResourceSet<Texture?> overrideResources, int queueOrder, int renderQueue,
        InstancedDrawInfo? instancing, int layer, ushort hostOrder, InstanceStorageBinding[]? storage);

    private static RentDraw? rent;
    private static Action<FinalDrawItem, DrawSortKey>? setSortKey;
    private static readonly MethodInfo CloneProgram = typeof(object)
        .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly int LightSumPropertyId = Shader.PropertyToID("_LumenLightSum");

    // Requires: main thread, install time (RimLumenMapHooks.Arm), before any capture.
    // Ensures: binds the two Waylith members this file reaches; throws when either was renamed or changed signature,
    //          so an engine update fails at install instead of tripping the render fail-safe on the first frame with
    //          light pools.
    internal static void BindEngineMembers()
    {
        MethodInfo rentMethod = RequireMember(typeof(FinalDrawItem), "Rent", BindingFlags.Static | BindingFlags.NonPublic,
            typeof(FinalDrawItem), typeof(Matrix4x4), typeof(byte), typeof(MeshDrawGeometry), typeof(MaterialBinding),
            typeof(bool), typeof(MaterialUniformBytes).MakeByRefType(),
            typeof(MaterialTextureResourceSet<Texture>).MakeByRefType(), typeof(int), typeof(int),
            typeof(InstancedDrawInfo), typeof(int), typeof(ushort), typeof(InstanceStorageBinding[]));
        rent = (RentDraw)rentMethod.CreateDelegate(typeof(RentDraw));
        PropertyInfo sortKey = typeof(FinalDrawItem).GetProperty("SortKey",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly) ?? throw new MissingMemberException(typeof(FinalDrawItem).FullName, "SortKey");
        MethodInfo setter = sortKey.GetSetMethod(nonPublic: true) ?? throw new MissingMethodException(typeof(FinalDrawItem).FullName, "SortKey set");
        if (sortKey.PropertyType != typeof(DrawSortKey)) throw new InvalidOperationException("FinalDrawItem.SortKey type changed.");
        setSortKey = (Action<FinalDrawItem, DrawSortKey>)setter.CreateDelegate(typeof(Action<FinalDrawItem, DrawSortKey>));
    }

    private static MethodInfo RequireMember(Type type, string name, BindingFlags flags, Type result, params Type[] parameters)
    {
        MethodInfo? method = type.GetMethod(name, flags, null, parameters, null);
        if (method == null || method.ReturnType != result || method.IsGenericMethod)
            throw new MissingMethodException(type.FullName, name + " (expected signature)");
        ParameterInfo[] actual = method.GetParameters();
        for (int i = 0; i < parameters.Length; i++)
            if (parameters[i].IsByRef && !actual[i].IsIn)
                throw new MissingMethodException(type.FullName, $"{name} (parameter {i} is no longer an in parameter)");
        return method;
    }

    internal sealed class Kind(string defName, string vertexFile, string fragmentFile, string? liftedFile,
        UnityEngine.Material material, int bindingId, bool takeover = false, string? liftedVertex = null)
    {
        internal readonly string DefName = defName;
        internal readonly bool IsTakeover = takeover;
        internal readonly UnityEngine.Material Material = material;
        internal Shader Vanilla = null!;
        internal Shader? Lifted;
        internal int MaterialId;
        internal bool OriginalProgram;
        internal int LightSumSlot;
        internal MaterialBinding? Binding;
        internal bool SkipLogged;

        // Ensures: Vanilla is the registered program. When that program still reads vanilla's own files from Core,
        //          OriginalProgram is set (and Lifted when a lifted file was given) and null is returned; otherwise the
        //          override's source paths or its precompiled registry key and stages, for the caller to report.
        internal string? Resolve(string coreShaders)
        {
            if (!ShaderRegistry.TryResolve(DefName, out Shader? vanilla)) throw new InvalidOperationException($"ShaderDef {DefName} is not registered.");
            Vanilla = vanilla;
            MaterialId = Material.engineMaterial.Id;
            if (Vanilla.PrecompiledStages is { } stages)
                return $"'{Vanilla.Key}' (precompiled SPIR-V: vertex {stages.Vertex.Length} bytes, fragment {stages.Fragment.Length} bytes)";
            ShaderProgramDefinition program = Vanilla.Program;
            if (program.vertexShaderFileName != vertexFile || program.fragmentShaderFileName != fragmentFile ||
                !(vertexFile == LightPoolRenderer.VertexFile
                    ? string.Equals(ShaderSourceLoader.ResolveShaderPath(vertexFile), Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Rendering", "Shaders", vertexFile)), StringComparison.OrdinalIgnoreCase)
                    : FromCore(vertexFile, coreShaders)) || !FromCore(fragmentFile, coreShaders))
            {
                return $"({program.vertexShaderFileName} -> {ShaderSourceLoader.ResolveShaderPath(program.vertexShaderFileName)}, " +
                    $"{program.fragmentShaderFileName} -> {ShaderSourceLoader.ResolveShaderPath(program.fragmentShaderFileName)})";
            }
            OriginalProgram = true;
            if (liftedFile is null) return null; // Takeover suppresses SunShadow rather than replacing its program.
            var lifted = (ShaderProgramDefinition)CloneProgram.Invoke(program, null)!;
            if (liftedVertex is not null) lifted.vertexShaderFileName = liftedVertex;
            lifted.fragmentShaderFileName = liftedFile;
            Shader shader = LightPoolRenderer.CreateShader("RimLumen." + (IsTakeover ? "Takeover." : string.Empty) + DefName, lifted, Vanilla.PipelineState);
            if (!(IsTakeover ? LightingTakeover.LayoutMatches(shader.MaterialLayout, Vanilla.MaterialLayout, DefName == "LightOverlay") : LiftedLayoutMatches(shader.MaterialLayout, Vanilla.MaterialLayout)))
                throw new InvalidOperationException($"[RimLumen] {liftedFile} no longer matches the material layout of vanilla {fragmentFile}.");
            if (!IsTakeover) LightSumSlot = shader.MaterialLayout.TextureSlots[0].ResourceSlot;
            Lifted = shader;
            return null;
        }

        // Ensures: one lifted binding per shadow kind, following the vanilla binding's pipeline state.
        internal MaterialBinding BindingFor(MaterialBinding source)
        {
            if (Binding is null)
                Binding = new MaterialBinding(bindingId, source.MaterialId, source.MaterialVersion, Lifted!.Key, Lifted,
                    Lifted.MaterialLayout, source.Uniforms, default, source.RenderQueue, Variant(), source.PipelineState,
                    source.WrapModeOverride, usesGlobals: false);
            else if (Binding.PipelineState != source.PipelineState)
                Binding.Update(source.MaterialVersion, Lifted!.Key, Lifted, Lifted.MaterialLayout, source.Uniforms, default,
                    source.RenderQueue, Variant(), source.PipelineState, source.WrapModeOverride, usesGlobals: false);
            return Binding;
        }

        private ShaderVariantKey Variant() => new(Lifted!, 0, string.Empty, Lifted!.UseTexture);

        internal void SkipOnce(string reason)
        {
            if (SkipLogged) return;
            SkipLogged = true;
            Log.Warning(IsTakeover
                ? $"[RimLumen] Lighting takeover leaves the {DefName} draw unchanged: {reason}."
                : $"[RimLumen] Light pools do not lift {DefName} shadows: {reason}. Those shadows stay vanilla.");
        }

        // Ensures: reports once when a draw at vanilla's shadow queue multiplies the destination through another
        //          program (RimIris's water gate). The queue is only a prefilter: FogOfWar shares queue 3175 but
        //          alpha-blends, and a report is not built once this kind has logged.
        internal void ReportForeignShadow(FinalDrawItem draw)
        {
            if (SkipLogged || draw.RenderQueue != Vanilla.DefaultRenderQueue) return;
            if (draw.Binding.PipelineState.Blend != (IsTakeover ? Vanilla.PipelineState.Blend : BlendState.MultiplyDestination)) return;
            SkipOnce($"a draw at queue {draw.RenderQueue} uses shader '{draw.Binding.Shader.Key}'");
        }

        private static bool FromCore(string file, string coreShaders) =>
            ShaderSourceLoader.ResolveShaderPath(file).StartsWith(coreShaders, StringComparison.OrdinalIgnoreCase);
    }

    // The lifted draw carries vanilla's resolved uniform bytes and resource set, so both layouts must agree byte for
    // byte and the light-sum sampler must be the only texture.
    internal static bool LiftedLayoutMatches(MaterialLayout lifted, MaterialLayout vanilla) =>
        lifted.UniformSize == vanilla.UniformSize && vanilla.TextureSlots.Length == 0 && lifted.TextureSlots.Length == 1 &&
        lifted.TextureSlots[0].PropertyId == LightSumPropertyId &&
        lifted.TextureSlots[0].Default == MaterialTextureDefault.Black &&
        lifted.UniformSlots.Select(s => (s.Offset, s.Type, s.PropertyName))
            .SequenceEqual(vanilla.UniformSlots.Select(s => (s.Offset, s.Type, s.PropertyName)));

    // Set 2 binding 1 of both lifted materials. RimLumenMapHooks clears it before each map record with pools and
    // RimLumenMapChain.RecordLightPools points it at the submission's LightSum once that pass succeeded; cleared, the
    // slot resolves to Waylith's black fallback and the lifted shadow equals vanilla.
    internal readonly BorrowedRenderTexture LightSum = new();
    private Kind[]? kinds;

    // Requires: main thread, MapRenderFeature.CaptureFrame postfix of a frame with light pools.
    // Ensures: every vanilla shadow draw of the frame is replaced by its lifted copy. Dynamic originals return to the
    //          pool; persistent originals stay with their source and leave only the frame's PersistentDraws list. All
    //          copies join Draws (returned with the frame) under the original sort key, so the renderer's sort-key
    //          merge of the two lists still draws them where the originals were.
    internal void Rebind(MeshDrawFrame frame) => Rewrite(frame, kinds ??= ResolveKinds(), (draw, kind, _) =>
    {
        MaterialTextureResourceSet<Texture?> resources = draw.Resources;
        resources.Set(kind.LightSumSlot, LightSum);
        return CopyDraw(draw, kind, in draw.Uniforms, in resources);
    });

    // Requires: main thread, MapRenderFeature.CaptureFrame postfix; every kind resolved.
    // Ensures: each draw of a kind's vanilla material through its original program is passed to replace (with whether
    //          it is persistent) and leaves its list; a returned copy takes its place, null drops it. Dynamic originals
    //          return to the pool and their copies keep their position in Draws; persistent originals stay with their
    //          source and their copies are appended to Draws (returned with the frame) under the original sort key, so
    //          the renderer's sort-key merge of the two lists still draws them where the originals were. Other draws are
    //          untouched; foreign programs at a kind's queue and foreign materials are logged once per kind.
    internal static void Rewrite(MeshDrawFrame frame, Kind[] kinds, Func<FinalDrawItem, Kind, bool, FinalDrawItem?> replace)
    {
        List<FinalDrawItem> draws = frame.Draws;
        int dynamicCount = draws.Count, kept = 0;
        for (int i = 0; i < dynamicCount; i++)
        {
            FinalDrawItem item = draws[i];
            if (Match(kinds, item) is not { } kind) { draws[kept++] = item; continue; }
            if (replace(item, kind, false) is { } copy) draws[kept++] = copy;
            FinalDrawItem.Return(item);
        }
        draws.RemoveRange(kept, dynamicCount - kept);
        List<FinalDrawItem> persistent = frame.PersistentDraws;
        kept = 0;
        for (int i = 0; i < persistent.Count; i++)
        {
            FinalDrawItem item = persistent[i];
            if (Match(kinds, item) is not { } kind) { persistent[kept++] = item; continue; }
            if (replace(item, kind, true) is { } copy) draws.Add(copy);
        }
        persistent.RemoveRange(kept, persistent.Count - kept);
    }

    private static Kind? Match(Kind[] kinds, FinalDrawItem draw)
    {
        foreach (Kind kind in kinds)
        {
            if (draw.Binding.Shader != kind.Vanilla)
            {
                kind.ReportForeignShadow(draw);
                continue;
            }
            if (!kind.OriginalProgram) return null;
            if (draw.Binding.MaterialId == kind.MaterialId) return kind;
            kind.SkipOnce($"a draw uses material '{draw.Binding.DebugName}' instead of the vanilla {(kind.IsTakeover ? kind.DefName : "shadow")} material");
            return null;
        }
        return null;
    }

    internal static FinalDrawItem CopyDraw(FinalDrawItem draw, Kind kind, in MaterialUniformBytes uniforms, in MaterialTextureResourceSet<Texture?> resources)
    {
        FinalDrawItem copy = rent!(draw.Matrix, draw.PassId, draw.Mesh, kind.BindingFor(draw.Binding), hasOverride: true,
            in uniforms, in resources, 0, draw.RenderQueue, draw.Instancing, draw.Layer, 0, draw.Storage);
        setSortKey!(copy, draw.SortKey);
        return copy;
    }

    internal static string CoreShaderRoot()
    {
        ModContentPack core = LoadedModManager.RunningModsListForReading.Find(mod => mod.IsCoreMod)
            ?? throw new InvalidOperationException("The Core content pack is not loaded.");
        return Path.GetFullPath(Path.Combine(core.RootDir, "Shaders")) + Path.DirectorySeparatorChar;
    }

    private static Kind[] ResolveKinds()
    {
        string coreShaders = CoreShaderRoot();
        Kind[] result =
        [
            new("SunShadow", "SunShadow.vert.glsl", "SunShadow.frag.glsl", "RimLumen/RimLumenSunShadow.frag.glsl",
                MatBases.SunShadow, -1),
            new("SunShadowFade", "SunShadowFade.vert.glsl", "SunShadowFade.frag.glsl",
                "RimLumen/RimLumenSunShadowFade.frag.glsl", MatBases.SunShadowFade, -2),
        ];
        foreach (Kind kind in result)
            if (kind.Resolve(coreShaders) is { } overridden) kind.SkipOnce("its program is overridden " + overridden);
        return result;
    }
}
