using Xunit;
using YSharp.TestKit.Tiers;

namespace YSharp.Interop.Tests;

/// <summary>ADR 0015: no Needs=Node test in this assembly is in the Gating tier (the rule and its self-tests live in YSharp.Tests).</summary>
public sealed class TierEnforcementTests
{
    [Fact]
    public void NoNodeTestIsGating() => Assert.Empty(TierRules.NodeTestsInGatingTier(new[] { typeof(TierEnforcementTests).Assembly }));
}
