using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.v3;

namespace YSharp.TestKit.Tiers;

/// <summary>
/// The reflective check behind ADR 0015's rule "tests that need Node carry a <c>Needs=Node</c> trait and are never Gating".
/// Tiers are expressed as the <c>Tier</c> trait (<see cref="TierAttribute"/>, or <c>[Trait("Tier", ...)]</c>) on a method, a class
/// (or an enclosing or base class) or the assembly, nearest scope first; no trait means <see cref="Tier.Gating"/>.
/// </summary>
public static class TierRules
{
    /// <summary>Every test method of the public (xunit-discoverable) types of <paramref name="assemblies"/> that needs Node but resolves to the Gating tier.</summary>
    public static IReadOnlyList<string> NodeTestsInGatingTier(IEnumerable<Assembly> assemblies) =>
        NodeTestsInGatingTier(assemblies.SelectMany(a => a.GetExportedTypes()));

    public static IReadOnlyList<string> NodeTestsInGatingTier(IEnumerable<Type> types)
    {
        var offenders = new List<string>();
        foreach (var type in types)
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!IsTest(method)) continue;
                var scopes = Scopes(method).ToList();
                if (NeedsNode(scopes) && EffectiveTier(scopes) == Tier.Gating)
                    offenders.Add($"{type.FullName}.{method.Name}");
            }
        return offenders;
    }

    /// <summary>Number of test methods that carry <c>Needs=Node</c>; a check that finds none is vacuous.</summary>
    public static int CountNodeTests(IEnumerable<Assembly> assemblies) =>
        assemblies.SelectMany(a => a.GetExportedTypes()).SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Count(m => IsTest(m) && NeedsNode(Scopes(m).ToList()));

    public static Tier EffectiveTier(IReadOnlyList<ICustomAttributeProvider> scopes)
    {
        foreach (var scope in scopes)
            foreach (var (key, value) in Traits(scope))
                if (key == "Tier" && Enum.TryParse<Tier>(value, true, out var tier)) return tier;
        return Tier.Gating;
    }

    private static bool NeedsNode(IReadOnlyList<ICustomAttributeProvider> scopes) =>
        scopes.Any(s => Traits(s).Any(t => t.Key == "Needs" && t.Value == "Node"));

    private static bool IsTest(MethodInfo method) =>
        method.GetCustomAttributes<FactAttribute>(inherit: true).Any() || method.IsDefined(typeof(SyntheticTestAttribute), inherit: false);

    /// <summary>Method, its class, the enclosing and base classes, then the assembly: nearest first.</summary>
    private static IEnumerable<ICustomAttributeProvider> Scopes(MethodInfo method)
    {
        yield return method;
        for (var t = method.DeclaringType; t != null; t = t.DeclaringType)
        {
            for (var b = t; b != null && b != typeof(object); b = b.BaseType) yield return b;
        }
        yield return method.DeclaringType!.Assembly;
    }

    private static IEnumerable<(string Key, string Value)> Traits(ICustomAttributeProvider scope) =>
        scope.GetCustomAttributes(inherit: false).OfType<ITraitAttribute>()
            .SelectMany(a => a.GetTraits()).Select(kv => (kv.Key, kv.Value));
}

/// <summary>
/// Marks a method as a test for <see cref="TierRules"/> only, so its self-tests can build offenders in non-public classes that
/// xunit never discovers or runs.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SyntheticTestAttribute : Attribute { }
