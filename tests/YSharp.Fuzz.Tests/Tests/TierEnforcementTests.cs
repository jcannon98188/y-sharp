using Xunit;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Gating;

namespace YSharp.Fuzz;

/// <summary>ADR 0015: no Needs=Node test in this assembly is in the Gating tier (the rule and its self-tests live in YSharp.Tests).</summary>
[LibraryIndependent]
public sealed class TierEnforcementTests
{
    [Fact]
    public void Tiers_NodeTests_AreNeverGating() => Assert.Empty(TierRules.NodeTestsInGatingTier(new[] { typeof(TierEnforcementTests).Assembly }));
}
