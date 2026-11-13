using System;

namespace RimLumen.Core.Tests;

[AttributeUsage(AttributeTargets.Class)]
public sealed class LumenTestSuiteAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class LumenFactAttribute : Attribute
{
}
