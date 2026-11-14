using System;
using System.Collections.Generic;

namespace RimLumen.Core.Rendering.Logic;

// Parsed from the process command line; absent => probe mode inactive (production path).
public sealed record ProbeOptions(string OutputDirectory, bool DisableBarrier, bool BenchmarkOnly)
{
    public ProbeOptions(string outputDirectory, bool disableBarrier) : this(outputDirectory, disableBarrier, false) { }
}

public static class ProbeOptionsParser
{
    // Requires: args is the raw argument list (Environment.GetCommandLineArgs()).
    // Ensures: true iff "-rimlumen-probe=<dir>" or "-rimlumen-bench=<dir>" (case-insensitive key,
    //   optional quotes around a non-empty dir) exists; DisableBarrier is true iff "-rimlumen-probe-nobarrier"
    //   is present. Without either directory key, returns false and options is null.
    public static bool TryParse(IReadOnlyList<string> args, out ProbeOptions? options)
    {
        const string probePrefix = "-rimlumen-probe=";
        const string benchPrefix = "-rimlumen-bench=";
        string? probeDir = null;
        string? benchDir = null;
        bool disableBarrier = false;
        foreach (string arg in args)
        {
            if (arg.Equals("-rimlumen-probe-nobarrier", StringComparison.OrdinalIgnoreCase))
            {
                disableBarrier = true;
            }
            else if (probeDir is null && TryParseDirectory(arg, probePrefix, out string? parsedProbe))
            {
                probeDir = parsedProbe;
            }
            else if (benchDir is null && TryParseDirectory(arg, benchPrefix, out string? parsedBench))
            {
                benchDir = parsedBench;
            }
        }
        if (probeDir is not null)
        {
            options = new ProbeOptions(probeDir, disableBarrier, false);
            return true;
        }
        if (benchDir is not null)
        {
            options = new ProbeOptions(benchDir, false, true);
            return true;
        }
        options = null;
        return false;
    }

    private static bool TryParseDirectory(string arg, string prefix, out string? directory)
    {
        directory = null;
        if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        string value = arg.Substring(prefix.Length);
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value.Substring(1, value.Length - 2);
        if (value.Length == 0) return false;
        directory = value;
        return true;
    }
}
