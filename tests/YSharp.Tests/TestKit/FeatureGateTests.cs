using System;
using System.Linq;
using System.Reflection;
using Xunit;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.TestKit;

/// <summary>Self-tests of the declared feature gates (ADR 0020). They never touch the library.</summary>
[LibraryIndependent]
public sealed class FeatureGateTests
{
    // The features named by the TASK-4 plan specs, one per spec that adds a capability.
    private static readonly string[] PlanFeatures =
    {
        "Encoding", "Values", "Json", "Store", "Transactions", "Integrate", "V1", "Gc", "UpdateFunctions", "V2", "V2Functions",
        "TypesCore", "Array", "Map", "Text", "TextFormat", "Xml", "Events", "DocLifecycle", "Snapshots", "Positions", "Undo",
        "Subdocs", "UserData", "Awareness", "Sync", "Sim", "Concurrency", "Fuzz",
    };

    [Fact]
    public void Feature_Enum_ListsExactlyTheFeaturesOfThePlanSpecs() =>
        Assert.Equal(PlanFeatures.OrderBy(x => x, StringComparer.Ordinal), Enum.GetNames<Feature>().OrderBy(x => x, StringComparer.Ordinal));

    [Fact]
    public void Missing_NothingLanded_ReturnsEveryRequiredFeatureOnce() =>
        Assert.Equal(new[] { Feature.Text, Feature.Map }, FeatureGate.Missing(new[] { Feature.Text, Feature.Map, Feature.Text }, new Feature[0]));

    [Fact]
    public void Missing_SomeLanded_ReturnsOnlyTheOthers() =>
        Assert.Equal(new[] { Feature.V2 }, FeatureGate.Missing(new[] { Feature.Text, Feature.V2 }, new[] { Feature.Text, Feature.Map }));

    [Fact]
    public void Missing_AllLanded_ReturnsNothing() =>
        Assert.Empty(FeatureGate.Missing(new[] { Feature.Text }, new[] { Feature.Text }));

    [Fact]
    public void SkipReason_NamesEveryMissingFeature() =>
        Assert.Equal("Features not landed yet (ADR 0020): Text, V2.", FeatureGate.SkipReason(new[] { Feature.Text, Feature.V2 }));

    [Requires(Feature.Text)]
    private sealed class ClassLevel
    {
        [Requires(Feature.V2)]
        public void Both() { }

        public void Inherited() { }

        [LibraryIndependent]
        public void Independent() { }
    }

    private sealed class NoClassRequirement
    {
        [Requires(Feature.Map, Feature.Array)]
        public void Several() { }

        public void None() { }
    }

    private static MethodInfo M<T>(string name) => typeof(T).GetMethod(name)!;

    [Fact]
    public void Required_MethodAddsToTheClassRequirement() =>
        Assert.Equal(new[] { Feature.Text, Feature.V2 }.OrderBy(f => f), FeatureGate.Required(M<ClassLevel>("Both")).OrderBy(f => f));

    [Fact]
    public void Required_MethodWithoutItsOwn_InheritsTheClassRequirement() =>
        Assert.Equal(new[] { Feature.Text }, FeatureGate.Required(M<ClassLevel>("Inherited")));

    [Fact]
    public void Required_AttributeNamingSeveralFeatures_NamesAllOfThem() =>
        Assert.Equal(new[] { Feature.Map, Feature.Array }.OrderBy(f => f), FeatureGate.Required(M<NoClassRequirement>("Several")).OrderBy(f => f));

    [Fact]
    public void Required_NoAttribute_IsEmpty() => Assert.Empty(FeatureGate.Required(M<NoClassRequirement>("None")));

    [Fact]
    public void Features_Landed_ContainsOnlyDefinedFeatures() =>
        Assert.All(Features.Landed, f => Assert.True(Enum.IsDefined(f)));

    /// <summary>The release gate (TASK-4.44): every feature has landed, each listed once.</summary>
    [Fact]
    public void Features_Landed_IsEveryFeature_EachOnce() =>
        Assert.Equal(Enum.GetValues<Feature>().Order(), Features.Landed.Order());

    [Fact]
    public void SkipReasonFor_RequirementNotLanded_NamesTheMissingFeatures() =>
        Assert.Equal("Features not landed yet (ADR 0020): Text, V2.",
            FeatureGate.SkipReasonFor(M<ClassLevel>("Both"), new Feature[0])!.Replace("V2, Text", "Text, V2"));

    [Fact]
    public void SkipReasonFor_PartlyLanded_NamesOnlyTheMissingOne() =>
        Assert.Equal("Features not landed yet (ADR 0020): V2.", FeatureGate.SkipReasonFor(M<ClassLevel>("Both"), new[] { Feature.Text }));

    [Fact]
    public void SkipReasonFor_EverythingLanded_RunsTheTest() =>
        Assert.Null(FeatureGate.SkipReasonFor(M<ClassLevel>("Both"), new[] { Feature.Text, Feature.V2 }));

    [Fact]
    public void SkipReasonFor_NoRequirement_RunsTheTest() =>
        Assert.Null(FeatureGate.SkipReasonFor(M<NoClassRequirement>("None"), new Feature[0]));

    [Fact]
    public void SkipReasonFor_MethodMarkedLibraryIndependent_RunsDespiteTheClassRequirement() =>
        Assert.Null(FeatureGate.SkipReasonFor(M<ClassLevel>("Independent"), new Feature[0]));

    [Fact]
    public void LibraryIndependent_CanBeAppliedToClassesAndMethods()
    {
        var usage = typeof(LibraryIndependentAttribute).GetCustomAttribute<AttributeUsageAttribute>()!;
        Assert.True(usage.ValidOn.HasFlag(AttributeTargets.Class) && usage.ValidOn.HasFlag(AttributeTargets.Method));
    }
}
