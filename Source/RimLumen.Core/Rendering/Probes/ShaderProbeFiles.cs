using System;
using System.IO;
using System.Text.RegularExpressions;
using Waylith.Rendering;

namespace RimLumen.Core.Rendering.Probes;

internal static class ShaderProbeFiles
{
    // A stage suffix always wins: malformed stage code must reach the compiler, never be skipped.
    internal static RuntimeShaderStage? Classify(string path, string source)
    {
        if (path.EndsWith(".comp.glsl", StringComparison.Ordinal)) return RuntimeShaderStage.Compute;
        if (path.EndsWith(".vert.glsl", StringComparison.Ordinal)) return RuntimeShaderStage.Vertex;
        if (path.EndsWith(".frag.glsl", StringComparison.Ordinal)) return RuntimeShaderStage.Fragment;
        string uncommented = Regex.Replace(source, @"/\*[\s\S]*?\*/|//[^\r\n]*", "");
        if (Regex.IsMatch(uncommented, @"\bvoid\s+main\s*\("))
            throw new InvalidDataException("Shader has an entry point but no recognized stage suffix: " + path);
        return null;
    }
}
