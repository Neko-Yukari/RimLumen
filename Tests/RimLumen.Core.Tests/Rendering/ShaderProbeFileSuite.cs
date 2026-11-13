using System;
using System.IO;
using RimLumen.Core.Rendering.Probes;
using Waylith.Rendering;

namespace RimLumen.Core.Tests.Rendering;

[LumenTestSuite]
public static class ShaderProbeFileSuite
{
    [LumenFact]
    public static void IncludeOnlySourcesAreNotStandaloneStages()
    {
        if (ShaderProbeFiles.Classify("Particles/ParticleTypes.glsl", "#ifndef TYPES\n#define TYPES\nstruct Particle { vec4 p; };\n#endif") != null)
            throw new InvalidOperationException("Struct include must be skipped.");
        if (ShaderProbeFiles.Classify("Particles/ParticleMaterial.glsl", "// void main() is supplied by the consumer\nlayout(set=2,binding=0) uniform M { vec4 p; } m;") != null)
            throw new InvalidOperationException("Uniform include must be skipped.");
    }

    [LumenFact]
    public static void StagesAreNeverSkippedEvenIfTheirSourceIsBroken()
    {
        if (ShaderProbeFiles.Classify("Particles/L1ParticleQuad.vert.glsl", "broken") != RuntimeShaderStage.Vertex ||
            ShaderProbeFiles.Classify("Particles/ParticleBillboard.frag.glsl", "") != RuntimeShaderStage.Fragment ||
            ShaderProbeFiles.Classify("Particles/ParticleSim.comp.glsl", "void main() {}") != RuntimeShaderStage.Compute)
            throw new InvalidOperationException("Every supported stage suffix must reach Slang, even if source has an error.");
    }

    [LumenFact]
    public static void EntryPointWithoutStageSuffixFailsInsteadOfSilentlySkipping()
    {
        try { ShaderProbeFiles.Classify("MisspelledStage.glsl", "/* include? */ void main (void) {} "); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("A real entry point must not be treated as an include.");
    }
}
