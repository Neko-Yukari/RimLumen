using System;
using System.Collections.Generic;

namespace RimLumen.WebSim.Tests;

internal static class TestAssert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{context}: expected '{expected}', got '{actual}'.");
        }
    }

    public static void Contains(string haystack, string needle, string context)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{context}: expected '{haystack}' to contain '{needle}'.");
        }
    }

    public static TEx Throws<TEx>(Action action, string context) where TEx : Exception
    {
        try
        {
            action();
        }
        catch (TEx ex)
        {
            return ex;
        }
        throw new InvalidOperationException($"{context}: expected exception {typeof(TEx).Name}, none thrown.");
    }
}

public static class Program
{
    public static int Main()
    {
        int passed = 0;
        int failed = 0;
        var cases = new List<(string Name, Action Run)>();
        WebDirectXmlLoaderRulesSuite.Register(cases);
        XmlJsonParitySuite.Register(cases);
        WebSimEngineSuite.Register(cases);

        foreach ((string name, Action run) in cases)
        {
            try
            {
                run();
                Console.WriteLine($"PASS {name}");
                passed++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
                failed++;
            }
        }

        Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
        return failed == 0 ? 0 : 1;
    }
}
