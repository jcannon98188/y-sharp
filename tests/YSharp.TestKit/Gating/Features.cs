using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace YSharp.TestKit.Gating;

/// <summary>
/// A capability the size of one plan spec (ADR 0020). The list is taken from the TASK-4 implementation plan: each spec
/// delivers exactly the features it names, and adds them to <see cref="Features.Landed"/> in the change that lands them.
/// </summary>
public enum Feature
{
    Encoding,
    Values,
    Json,
    Store,
    Transactions,
    Integrate,
    V1,
    Gc,
    UpdateFunctions,
    V2,
    V2Functions,
    TypesCore,
    Array,
    Map,
    Text,
    TextFormat,
    Xml,
    Events,
    DocLifecycle,
    Snapshots,
    Positions,
    Undo,
    Subdocs,
    UserData,
    Awareness,
    Sync,
    Sim,
    Concurrency,
    Fuzz,
}

/// <summary>The one landed list (ADR 0020). A spec adds its features here in the same change that lands them.</summary>
public static class Features
{
    public static readonly IReadOnlyList<Feature> Landed = new Feature[]
    {
        Feature.Encoding,
        Feature.Values,
        Feature.Values,
        Feature.Json,
        Feature.Store,
        Feature.Transactions,
        Feature.Integrate,
        Feature.V1,
        Feature.Gc,
        Feature.UpdateFunctions,
        Feature.V2,
        Feature.V2Functions,
        Feature.TypesCore,
        Feature.Array,
        Feature.Map,
        Feature.Text,
        Feature.TextFormat,
        Feature.Xml,
        Feature.Events,
        Feature.DocLifecycle,
        Feature.Snapshots, Feature.Positions, Feature.Undo, Feature.Subdocs, Feature.UserData, Feature.Awareness, Feature.Sync, Feature.Sim, Feature.Concurrency,
    };
}

/// <summary>The pure logic behind <see cref="RequiresAttribute"/>.</summary>
public static class FeatureGate
{
    /// <summary>The required features that are not in <paramref name="landed"/>, distinct, in the order first required.</summary>
    public static IReadOnlyList<Feature> Missing(IEnumerable<Feature> required, IEnumerable<Feature> landed)
    {
        var have = landed.ToHashSet();
        return required.Where(f => !have.Contains(f)).Distinct().ToArray();
    }

    /// <summary>The skip reason for <paramref name="method"/> given the <paramref name="landed"/> features, or null when the test should run.</summary>
    public static string? SkipReasonFor(MethodInfo method, IEnumerable<Feature> landed)
    {
        var missing = Missing(Required(method), landed);
        return missing.Count == 0 ? null : SkipReason(missing);
    }

    public static string SkipReason(IEnumerable<Feature> missing) =>
        "Features not landed yet (ADR 0020): " + string.Join(", ", missing) + ".";

    /// <summary>
    /// Everything a test method requires: its own attributes plus those of its class and base classes. A method marked
    /// <see cref="LibraryIndependentAttribute"/> itself requires nothing, whatever its class declares.
    /// </summary>
    public static IReadOnlyList<Feature> Required(MethodInfo method)
    {
        if (method.GetCustomAttributes<LibraryIndependentAttribute>(false).Any()) return Array.Empty<Feature>();
        var all = new List<Feature>();
        for (var t = method.DeclaringType; t != null; t = t.DeclaringType)
            all.InsertRange(0, t.GetCustomAttributes<RequiresAttribute>(true).SelectMany(a => a.Features));
        all.AddRange(method.GetCustomAttributes<RequiresAttribute>(true).SelectMany(a => a.Features));
        return all.Distinct().ToArray();
    }
}

/// <summary>
/// Declares the features a test needs (ADR 0020), on a class (the default for its tests) or a method (added on top). A test
/// whose features have not all landed is skipped before its body runs, naming the missing features.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequiresAttribute : BeforeAfterTestAttribute, ITraitAttribute
{
    public RequiresAttribute(Feature feature, params Feature[] more) => Features = new[] { feature }.Concat(more).ToArray();

    public IReadOnlyList<Feature> Features { get; }

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (FeatureGate.SkipReasonFor(methodUnderTest, Gating.Features.Landed) is { } reason) Assert.Skip(reason);
    }

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        Features.Select(f => new KeyValuePair<string, string>("Requires", f.ToString())).ToArray();
}

/// <summary>Marks a test that never touches the library (the TestKit self-tests and the traceability tests); the checker does not require a feature for it (ADR 0020).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class LibraryIndependentAttribute : Attribute { }
