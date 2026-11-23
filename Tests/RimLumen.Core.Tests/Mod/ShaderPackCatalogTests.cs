using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading.Tasks;
using System.Xml.Linq;
using RimLumen.Core.Mod;
using RimLumen.Core.Rendering;
using RimLumen.Core.Rendering.Logic;

namespace RimLumen.Core.Tests.Mod;

[LumenTestSuite]
public static class ShaderPackCatalogTests
{
    [LumenFact]
    public static void BuiltInShaderPacksValidateAndCompileThroughSlang()
    {
        string packsRoot = Path.Combine(RepoRoot(), "1.7", "ShaderPacks");
        IReadOnlyList<ShaderPackEntry> packs = ShaderPackCatalog.ScanRoots([(packsRoot, "内置")]);

        var expectedIds = new[] { "default", "crisp", "foggy" };
        Equal(expectedIds.Length, packs.Count);
        foreach (string id in expectedIds)
        {
            ShaderPackEntry? entry = packs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            Require(entry is not null, $"Missing built-in shader pack '{id}'.");
            Require(entry!.LoadError is null, $"Built-in shader pack '{id}' failed to load: {entry.LoadError}");
            ShaderPackGraphValidator.Validate(entry.Spec);
        }
        ShaderPackEntry foggy = packs.Single(p => string.Equals(p.Id, "foggy", StringComparison.OrdinalIgnoreCase));
        Equal("Foggy (雾气)", foggy.Name);

        WithSlangLoaded(() =>
        {
            try
            {
                foreach (ShaderPackEntry pack in packs)
                {
                    bool ok = RimLumenRenderHost.ReloadShaderPack(pack.Spec);
                    Require(ok, $"ReloadShaderPack failed for '{pack.Id}': {RimLumenRenderHost.LastShaderPackError}");
                    Require(RimLumenRenderHost.LastShaderPackError is null, $"Unexpected error for '{pack.Id}'.");
                }
            }
            finally
            {
                RimLumenRenderHost.ReloadShaderPack(ShaderPackSpec.Default);
            }
        });
    }

    [LumenFact]
    public static void CatalogScanIsolatesBrokenPacksAndAppliesLocalOverridePriority()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "opencode", "rimlumen-pack-catalog-" + Guid.NewGuid().ToString("N"));
        string modRoot = Path.Combine(tempRoot, "mod");
        string localRoot = Path.Combine(tempRoot, "local");
        try
        {
            WriteSimplePack(Path.Combine(modRoot, "Alpha"), "alpha", "Mod Alpha", 0.25f);
            WriteSimplePack(Path.Combine(localRoot, "Alpha"), "alpha", "Local Alpha", 0.75f);

            string brokenDir = Path.Combine(localRoot, "BrokenJson");
            Directory.CreateDirectory(brokenDir);
            File.WriteAllText(Path.Combine(brokenDir, "pack.json"), "{ not valid json");

            string escapeDir = Path.Combine(localRoot, "PathEscape");
            Directory.CreateDirectory(escapeDir);
            File.WriteAllText(Path.Combine(escapeDir, "pack.json"), """
                {
                  "id": "escape",
                  "compositeShader": "../outside.frag.glsl"
                }
                """);

            string reservedDir = Path.Combine(localRoot, "ReservedDefault");
            Directory.CreateDirectory(reservedDir);
            File.WriteAllText(Path.Combine(reservedDir, "pack.json"), """
                {
                  "id": "default",
                  "compositeShader": "Composite.frag.glsl"
                }
                """);

            IReadOnlyList<ShaderPackEntry> entries = ShaderPackCatalog.ScanRoots([
                (modRoot, "模组"),
                (localRoot, "本地"),
            ]);

            ShaderPackEntry alpha = entries.Single(e => e.Id == "alpha");
            Require(alpha.LoadError is null, $"Expected valid override, got {alpha.LoadError}");
            Equal("Local Alpha", alpha.Name);
            Equal("本地", alpha.SourceLabel);

            ShaderPackEntry broken = entries.Single(e => e.Name == "BrokenJson");
            Require(broken.LoadError is not null, "Broken JSON should produce LoadError.");
            Equal("default", ShaderPackCatalog.FindOrDefault(broken.Id, entries).Id);

            ShaderPackEntry escape = entries.Single(e => e.Name == "PathEscape");
            Require(escape.LoadError is not null, "Parent directory traversal must be rejected.");

            ShaderPackEntry reserved = entries.Single(e => e.Name == "ReservedDefault");
            Require(reserved.LoadError is not null, "Reserved id 'default' must be rejected.");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [LumenFact]
    public static void UnknownSelectedPackFallsBackToStandardWithOriginalIdAndPrunesStaleObjectShadows()
    {
        string packsRoot = Path.Combine(RepoRoot(), "1.7", "ShaderPacks");
        IReadOnlyList<ShaderPackEntry> packs = ShaderPackCatalog.ScanRoots([(packsRoot, "内置")]);

        foreach (string missingId in new[] { "cinematic", "vibrant", "atmospheric", "nonexistent-pack" })
        {
            var settings = new RimLumenSettings
            {
                SelectedShaderPackId = missingId,
                ObjectShadowOverrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    ["default"] = false,
                    ["crisp"] = false,
                    ["foggy"] = true,
                    ["cinematic"] = false,
                    ["vibrant"] = true,
                    ["atmospheric"] = false,
                    ["nonexistent-pack"] = true,
                },
            };

            ShaderPackEntry resolved = settings.ResolvePackSettings(packs, out string? missingPackId);
            Equal("default", resolved.Id);
            Equal("default", settings.SelectedShaderPackId);
            Equal(missingId, missingPackId);
            Equal(3, settings.ObjectShadowOverrides.Count);
            Require(!settings.ObjectShadowOverrides["default"] && !settings.ObjectShadowOverrides["crisp"] && settings.ObjectShadowOverrides["foggy"],
                "Valid pack overrides must survive pruning.");
            Require(!settings.ObjectShadowOverrides.ContainsKey("cinematic")
                && !settings.ObjectShadowOverrides.ContainsKey("vibrant")
                && !settings.ObjectShadowOverrides.ContainsKey("atmospheric")
                && !settings.ObjectShadowOverrides.ContainsKey("nonexistent-pack"),
                "Stale ObjectShadowOverrides entries must be pruned.");
        }

        var validSettings = new RimLumenSettings { SelectedShaderPackId = "FOGGY" };
        ShaderPackEntry foggyResolved = validSettings.ResolvePackSettings(packs, out string? validMissing);
        Equal("foggy", foggyResolved.Id);
        Equal("foggy", validSettings.SelectedShaderPackId);
        Require(validMissing is null, $"Valid pack must not report missing id, got '{validMissing}'.");

        var brokenEntry = new ShaderPackEntry("broken-pack", "Broken", "Author", "", "本地", null, ShaderPackSpec.Default,
            Array.Empty<ShaderPackOptionDefinition>(), "syntax error");
        var brokenSettings = new RimLumenSettings
        {
            SelectedShaderPackId = "broken-pack",
            ObjectShadowOverrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["default"] = true,
                ["broken-pack"] = false,
            },
        };
        ShaderPackEntry brokenResolved = brokenSettings.ResolvePackSettings([ShaderPackCatalog.StandardEntry, brokenEntry], out string? brokenMissing);
        Equal("default", brokenResolved.Id);
        Equal("broken-pack", brokenMissing);
        Equal(1, brokenSettings.ObjectShadowOverrides.Count);
        Require(!brokenSettings.ObjectShadowOverrides.ContainsKey("broken-pack"), "Broken pack override must be discarded.");
    }

    [LumenFact]
    public static void ApplyOptionsToRuntimeResetsPreviousPackParamsAndClampsOverrides()
    {
        var packA = new ShaderPackEntry(
            "pack-a", "Pack A", "Author", "", "测试", null, ShaderPackSpec.Default,
            [
                new ShaderPackOptionDefinition("p0", "P0", ShaderPackOptionTarget.PackParam0, 0.8f, 0f, 1f, 0.05f, null),
                new ShaderPackOptionDefinition("bloom", "Bloom", ShaderPackOptionTarget.BloomIntensity, 0.4f, 0f, 0.8f, 0.01f, null),
            ]);
        var packB = new ShaderPackEntry(
            "pack-b", "Pack B", "Author", "", "测试", null, ShaderPackSpec.Default,
            [
                new ShaderPackOptionDefinition("p3", "P3", ShaderPackOptionTarget.PackParam3, 0.5f, 0f, 1f, 0.1f, null),
            ]);

        var runtime = new ShaderPackRuntimeParams();
        var overrides = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            [ShaderPackCatalog.MakeOverrideKey("pack-a", "p0")] = 1.5f, // should clamp to 1.0
            [ShaderPackCatalog.MakeOverrideKey("pack-b", "p3")] = 0.24f, // should snap to 0.2
        };

        ShaderPackCatalog.ApplyOptionsToRuntime(packA, overrides, runtime);
        Equal(1.0f, runtime.GetPackParam(0));
        Equal(0.4f, runtime.BloomIntensity);

        // Switching to Pack B must reset PackParam0 to 0 and BloomIntensity back to 0.16f.
        ShaderPackCatalog.ApplyOptionsToRuntime(packB, overrides, runtime);
        Equal(0.0f, runtime.GetPackParam(0));
        Equal(0.16f, runtime.BloomIntensity);
        Require(MathF.Abs(runtime.GetPackParam(3) - 0.2f) < 1e-5f, $"Expected 0.2; got {runtime.GetPackParam(3)}");
    }

    [LumenFact]
    public static void ApplyOptionsToRuntimeNeverExposesResetIntermediateToConcurrentReader()
    {
        var packX = new ShaderPackEntry(
            "pack-x", "Pack X", "Author", "", "测试", null, ShaderPackSpec.Default,
            [
                new ShaderPackOptionDefinition("p0", "P0", ShaderPackOptionTarget.PackParam0, 0.8f, 0f, 1f, 0.05f, null),
                new ShaderPackOptionDefinition("bloom", "Bloom", ShaderPackOptionTarget.BloomIntensity, 0.4f, 0f, 0.8f, 0.01f, null),
            ]);
        var packY = new ShaderPackEntry(
            "pack-y", "Pack Y", "Author", "", "测试", null, ShaderPackSpec.Default,
            [
                new ShaderPackOptionDefinition("p3", "P3", ShaderPackOptionTarget.PackParam3, 0.5f, 0f, 1f, 0.1f, null),
            ]);

        var runtime = new ShaderPackRuntimeParams();
        ShaderPackCatalog.ApplyOptionsToRuntime(packX, null, runtime);
        ShaderPackUniformBlockSnapshot stateX = runtime.CaptureSnapshot();
        ShaderPackCatalog.ApplyOptionsToRuntime(packY, null, runtime);
        ShaderPackUniformBlockSnapshot stateY = runtime.CaptureSnapshot();
        Require(stateX != stateY, "Pack X and Pack Y must produce different runtime states.");

        Task writer = Task.Run(() =>
        {
            for (int i = 0; i < 5000; i++)
                ShaderPackCatalog.ApplyOptionsToRuntime(i % 2 == 0 ? packX : packY, null, runtime);
        });
        int torn = 0;
        while (!writer.IsCompleted)
        {
            ShaderPackUniformBlockSnapshot seen = runtime.CaptureSnapshot();
            if (seen != stateX && seen != stateY)
                torn++;
        }
        writer.Wait();
        Equal(0, torn);
    }

    [LumenFact]
    public static void ResolvePackShaderPathRejectsJunctionPointingOutsidePack()
    {
        string tempRoot = NewTempRoot("rimlumen-pack-junction-out-");
        string packDir = Path.Combine(tempRoot, "pack");
        string outsideDir = Path.Combine(tempRoot, "outside");
        string link = Path.Combine(packDir, "linked");
        try
        {
            Directory.CreateDirectory(packDir);
            Directory.CreateDirectory(outsideDir);
            File.WriteAllText(Path.Combine(outsideDir, "Evil.frag.glsl"), "void main(){}");
            CreateJunction(link, outsideDir);

            ThrowsInvalidData(() => ShaderPackManifest.ResolvePackShaderPath(packDir, "linked/Evil.frag.glsl"));
            ThrowsInvalidData(() => ShaderPackManifest.ResolvePackShaderPath(packDir, "linked/Missing.frag.glsl"));
        }
        finally
        {
            DeleteTempRoot(tempRoot, link);
        }
    }

    [LumenFact]
    public static void ResolvePackShaderPathAcceptsJunctionThatStaysInsidePack()
    {
        string tempRoot = NewTempRoot("rimlumen-pack-junction-in-");
        string packDir = Path.Combine(tempRoot, "pack");
        string sharedDir = Path.Combine(packDir, "shared");
        string link = Path.Combine(packDir, "alias");
        try
        {
            Directory.CreateDirectory(sharedDir);
            File.WriteAllText(Path.Combine(sharedDir, "Common.glslinc"), "// shared");
            CreateJunction(link, sharedDir);

            string? resolved = ShaderPackManifest.ResolvePackShaderPath(packDir, "alias/Common.glslinc");
            Equal(Path.GetFullPath(Path.Combine(link, "Common.glslinc")), resolved);
        }
        finally
        {
            DeleteTempRoot(tempRoot, link);
        }
    }

    [LumenFact]
    public static void ResolvePackShaderPathAcceptsPackMountedThroughJunctionedRoot()
    {
        string tempRoot = NewTempRoot("rimlumen-pack-junction-root-");
        string packDir = Path.Combine(tempRoot, "pack");
        string mountedRoot = Path.Combine(tempRoot, "mounted");
        try
        {
            Directory.CreateDirectory(packDir);
            File.WriteAllText(Path.Combine(packDir, "Composite.frag.glsl"), "void main(){}");
            CreateJunction(mountedRoot, packDir);

            string? resolved = ShaderPackManifest.ResolvePackShaderPath(mountedRoot, "Composite.frag.glsl");
            Equal(Path.GetFullPath(Path.Combine(mountedRoot, "Composite.frag.glsl")), resolved);
        }
        finally
        {
            DeleteTempRoot(tempRoot, mountedRoot);
        }
    }

    private static string NewTempRoot(string prefix)
        => Path.Combine(Path.GetTempPath(), "opencode", prefix + Guid.NewGuid().ToString("N"));

    // Junctions need no elevation on Windows, unlike Directory.CreateSymbolicLink.
    private static void CreateJunction(string link, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Require(process.ExitCode == 0, $"mklink /J failed ({process.ExitCode}): {output}");
    }

    // Deleting the junction first keeps the recursive delete from ever following it.
    private static void DeleteTempRoot(string tempRoot, string junction)
    {
        if (Directory.Exists(junction))
            Directory.Delete(junction);
        if (Directory.Exists(tempRoot))
            Directory.Delete(tempRoot, recursive: true);
    }

    private static void ThrowsInvalidData(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("Expected InvalidDataException.");
    }

    private static void WriteSimplePack(string dir, string id, string name, float defaultParam)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Composite.frag.glsl"), "#version 450\nlayout(location=0) out vec4 c;\nvoid main(){c=vec4(0);}\n");
        File.WriteAllText(Path.Combine(dir, "pack.json"), $$"""
            {
              "id": "{{id}}",
              "name": "{{name}}",
              "compositeShader": "Composite.frag.glsl",
              "options": [
                { "key": "strength", "label": "Strength", "target": "packParam0", "default": {{defaultParam.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "min": 0.0, "max": 1.0, "step": 0.05 }
              ]
            }
            """);
    }

    private static void WithSlangLoaded(Action action)
    {
        string repoRoot = RepoRoot();
        string buildProperties = Path.Combine(repoRoot, "Directory.Build.props");
        string gameRoot = Environment.GetEnvironmentVariable("GameRoot")
            ?? XDocument.Load(buildProperties).Descendants("GameRoot").Single().Value;
        string nativePath = Path.Combine(gameRoot, "slang.dll");
        nint ResolveSlang(Assembly assembly, string name)
        {
            if (assembly.GetName().Name != "Brutal.Slang" || name != "slang")
                return 0;
            return NativeLibrary.Load(nativePath, assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
        }
        string optimizerPath = Path.Combine(gameRoot, "slang-glslang.dll");
        nint optimizer = NativeLibrary.Load(optimizerPath, typeof(ShaderPackCatalogTests).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveSlang;
        try
        {
            action();
        }
        finally
        {
            AssemblyLoadContext.Default.ResolvingUnmanagedDll -= ResolveSlang;
            NativeLibrary.Free(optimizer);
        }
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Cannot locate repository root above test output.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; actual {actual}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
