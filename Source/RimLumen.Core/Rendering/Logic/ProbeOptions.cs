using System;
using System.Collections.Generic;

namespace RimLumen.Core.Rendering.Logic;

// Parsed from the process command line; absent => probe mode inactive (production path).
public sealed record ProbeOptions(string OutputDirectory, bool DisableBarrier);

public static class ProbeOptionsParser
{
    // Requires: args is the raw argument list (Environment.GetCommandLineArgs()).
    // Ensures: true iff an argument "-rimlumen-probe=<dir>" (case-insensitive key, optional quotes
    //   around a non-empty dir) exists; DisableBarrier is true iff "-rimlumen-probe-nobarrier" is present.
    //   Without the probe key, returns false and options is null even if the nobarrier flag is present.
    public static bool TryParse(IReadOnlyList<string> args, out ProbeOptions? options)
    {
        const string prefix = "-rimlumen-probe=";
        string? outputDirectory = null;
        bool disableBarrier = false;
        foreach (string arg in args)
        {
            if (arg.Equals("-rimlumen-probe-nobarrier", StringComparison.OrdinalIgnoreCase))
            {
                disableBarrier = true;
            }
            else if (outputDirectory is null && arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string directory = arg.Substring(prefix.Length);
                if (directory.Length >= 2 && directory[0] == '"' && directory[^1] == '"')
                {
                    directory = directory.Substring(1, directory.Length - 2);
                }
                if (directory.Length > 0)
                {
                    outputDirectory = directory;
                }
            }
        }
        options = outputDirectory is null ? null : new ProbeOptions(outputDirectory, disableBarrier);
        return options is not null;
    }
}
