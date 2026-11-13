using System;
using System.Linq;
using System.Reflection;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Tests;

public static class Program
{
    public static int Main(string[] args)
    {
        string? filter = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--filter")
            {
                filter = args[i + 1];
            }
        }

        int passed = 0;
        int failed = 0;

        var suites = typeof(Program).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<LumenTestSuiteAttribute>() is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        foreach (var suite in suites)
        {
            var methods = suite
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<LumenFactAttribute>() is not null)
                .OrderBy(m => m.Name, StringComparer.Ordinal);

            foreach (var method in methods)
            {
                string fullName = $"{suite.Name}.{method.Name}";
                if (!string.IsNullOrEmpty(filter) &&
                    !fullName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    method.Invoke(null, null);
                    passed++;
                    Console.WriteLine($"[PASS] {fullName}");
                }
                catch (TargetInvocationException tie)
                {
                    failed++;
                    var ex = tie.InnerException ?? tie;
                    Console.Error.WriteLine($"[FAIL] {fullName}: {ex.GetType().Name}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.Error.WriteLine($"[FAIL] {fullName}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
        return failed == 0 ? 0 : 1;
    }
}

[LumenTestSuite]
public static class ContractSmokeSuite
{
    [LumenFact]
    public static void HostRegistryStartsEmptyAndResolvesMissCleanly()
    {
        if (LumenHostRegistry.TryResolveAsset("nonexistent.effect", out var asset) || asset is not null)
        {
            throw new InvalidOperationException("Expected missing asset lookup to return false.");
        }
    }
}
