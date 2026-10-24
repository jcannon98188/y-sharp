using System.Linq;
using System.Reflection;
using Xunit;
using YSharp.TestKit.Tiers;

namespace YSharp.Tests.TestKit;

/// <summary>
/// ADR 0015: a test tagged <c>Needs=Node</c> is never in the Gating tier (CI's gating job has no Node). Tiers are the <c>Tier</c> trait
/// ([Tier] on a method, class or assembly; absent means Gating). The interop and fuzz projects hold the same one-line check over their
/// own assemblies; this class checks this assembly and proves the rule can fail.
/// </summary>
public sealed class TierEnforcementTests
{
    [Fact]
    public void ThisAssembly_NoNodeTestIsGating() =>
        Assert.Empty(TierRules.NodeTestsInGatingTier(new[] { typeof(TierEnforcementTests).Assembly }));

    [Fact]
    public void ThisAssembly_NodeTestsExist_SoTheCheckIsNotVacuous() =>
        Assert.True(TierRules.CountNodeTests(new[] { typeof(TierEnforcementTests).Assembly }) > 0);

    // Non-public so xunit does not discover them: only the rule sees these.
    private sealed class DefaultTierNodeOffender
    {
        [SyntheticTest, NeedsNode] public void Test() { }
    }

    [Tier(Tier.Gating)]
    private sealed class ExplicitGatingNodeOffender
    {
        [SyntheticTest, NeedsNode] public void Test() { }
    }

    [NeedsNode]
    private sealed class ClassLevelNodeOffender
    {
        [SyntheticTest] public void Test() { }
    }

    [NeedsNode]
    private sealed class MethodOverridesToGatingOffender
    {
        [SyntheticTest, Tier(Tier.Gating)] public void Test() { }
    }

    [NeedsNode, Tier(Tier.Smoke)]
    private sealed class SmokeNode
    {
        [SyntheticTest] public void Test() { }
    }

    [NeedsNode, Tier(Tier.Smoke)]
    private sealed class MethodTierBeatsClassTier
    {
        [SyntheticTest] public void A() { }
        [SyntheticTest, Tier(Tier.Nightly)] public void B() { }
    }

    private sealed class NodeFreeGating
    {
        [SyntheticTest] public void Test() { }
        public void NotATest() { }
    }

    [Fact]
    public void Rule_FlagsEveryWayANodeTestCanBeGating()
    {
        var offenders = TierRules.NodeTestsInGatingTier(new[]
        {
            typeof(DefaultTierNodeOffender), typeof(ExplicitGatingNodeOffender),
            typeof(ClassLevelNodeOffender), typeof(MethodOverridesToGatingOffender),
        });
        Assert.Equal(4, offenders.Count);
    }

    [Fact]
    public void Rule_AcceptsNodeTestsInOtherTiers_AndGatingTestsWithoutNode() =>
        Assert.Empty(TierRules.NodeTestsInGatingTier(new[] { typeof(SmokeNode), typeof(MethodTierBeatsClassTier), typeof(NodeFreeGating) }));

    [Fact]
    public void Rule_NamesTheOffendingMethod() =>
        Assert.Contains(TierRules.NodeTestsInGatingTier(new[] { typeof(DefaultTierNodeOffender) }), o => o.EndsWith("DefaultTierNodeOffender.Test"));
}
