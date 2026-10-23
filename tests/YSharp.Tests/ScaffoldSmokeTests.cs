using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace YSharp.Tests;

public class ScaffoldSmokeTests
{
    [Fact]
    public void ApiStub_PublicSurface_Exposes_CoreTypes()
    {
        var names = typeof(YException).Assembly.GetExportedTypes().Select(t => t.Name).ToHashSet();
        foreach (var expected in new[] { "YValue", "Doc", "YText", "YArray", "YMap", "YError", "YLimits" })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public void ApiStub_Members_Throw_NotImplemented()
    {
        Assert.Throws<NotImplementedException>(() => YValue.Null);
    }

    [Fact]
    public void InternalsVisibleTo_IsLimited_ToSpecifiedAssemblies()
    {
        var friends = typeof(YException).Assembly
            .GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>()
            .Select(a => a.AssemblyName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "YSharp.Benchmarks", "YSharp.Fuzz.Tests", "YSharp.Tests" }, friends);
    }
}
