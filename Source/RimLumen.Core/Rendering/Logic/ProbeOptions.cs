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
        throw new NotImplementedException();
    }
}
