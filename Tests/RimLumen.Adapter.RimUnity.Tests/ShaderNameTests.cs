using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using RimLumen.Core.Contracts;
using Waylith.Rendering;
using RU = global::RimUnity;

namespace RimLumen.Adapter.RimUnity.Tests;

internal static class ShaderNameTests
{
    internal static void BrightSmoke() => CheckMaterial("RimUnity_ParticleAlphaBack", false, material =>
        Check.True(!ExternalHdrMaterialRules.IsMaterialEligible(material, LumenColorBlendSpace.Legacy),
            "AlphaBack smoke with _Color.r=1.977 was routed to HDR."));

    internal static void MappedParticle() => CheckMaterial("RimUnity_ParticleAdditiveBack", true, material =>
    {
        foreach (var space in new[] { LumenColorBlendSpace.Linear, LumenColorBlendSpace.Legacy })
            Check.True(ExternalHdrMaterialRules.IsMaterialEligible(material, space),
                $"{material.Shader.Key} with CoverageAlpha blending was rejected in {space}.");
    });

    private static void CheckMaterial(string key, bool linear, Action<Material> check)
    {
        string game = Environment.GetEnvironmentVariable("RIMWORLD_DIR")!;
        const DllImportSearchPath flags = DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories;
        nint Resolve(Assembly assembly, string name) => name == "slang" && assembly.GetName().Name == "Brutal.Slang"
            ? NativeLibrary.Load(Path.Combine(game, "slang.dll"), assembly, flags) : 0;
        nint optimizer = NativeLibrary.Load(Path.Combine(game, "slang-glslang.dll"), typeof(ShaderNameTests).Assembly, flags);
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += Resolve;
        try
        {
            ShaderSourceLoader.RegisterShaderSourceDirectory(Path.Combine(game, "Rendering/Shaders"));
            ShaderSourceLoader.RegisterShaderSourceDirectory(Path.GetFullPath(Path.Combine(game, "../../workshop/content/294100/3819570122/1.7/Shaders")));
            var program = new ShaderProgramDefinition { vertexShaderFileName = "RimUnity/Particle.vert.glsl",
                fragmentShaderFileName = "RimUnity/Particle.frag.glsl", transparent = true, depthWrite = false };
            var shader = new Shader(key, key, program);
            if (linear)
            {
                var wrap = typeof(RU.ParticleSystemRenderer).Assembly.GetType("RimUnity.Rendering.MappedColorSpaceShader", true)!
                    .GetMethod("Wrap", BindingFlags.Static | BindingFlags.NonPublic)!;
                shader = ((RU.Shader)wrap.Invoke(null, [shader, true])!).EngineShader;
            }
            var material = new Material(shader) { RenderQueue = 3000 };
            material.SetFloat("_BlendSrc", 5);
            material.SetFloat("_BlendDst", 10);
            material.SetVector("_Color", new Vector4(1.977f, 1f, 1f, 1f));
            check(material);
        }
        finally
        {
            AssemblyLoadContext.Default.ResolvingUnmanagedDll -= Resolve;
            NativeLibrary.Free(optimizer);
        }
    }
}
