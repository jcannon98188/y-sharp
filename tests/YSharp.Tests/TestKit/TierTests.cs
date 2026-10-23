using System.Linq;
using Xunit;
using YSharp.TestKit.Tiers;

namespace YSharp.Tests.TestKit;

public class TierTests
{
    [Fact]
    public void TierAttribute_ExposesTierTrait()
    {
        var traits = new TierAttribute(Tier.Nightly).GetTraits();
        Assert.Equal(new[] { ("Tier", "Nightly") }, traits.Select(t => (t.Key, t.Value)));
    }

    [Fact]
    public void NeedsNodeAttribute_ExposesNeedsTrait()
    {
        var traits = new NeedsNodeAttribute().GetTraits();
        Assert.Equal(new[] { ("Needs", "Node") }, traits.Select(t => (t.Key, t.Value)));
    }

    [Theory]
    [InlineData(null, Tier.Gating)]
    [InlineData("", Tier.Gating)]
    [InlineData("Smoke", Tier.Smoke)]
    [InlineData("nightly", Tier.Nightly)]
    public void Env_Tier_DefaultsToGatingAndParsesCaseInsensitively(string? value, Tier expected)
    {
        Assert.Equal(expected, YSharpEnv.ParseTier(value));
    }

    [Fact]
    public void Env_Tier_UnknownValue_Throws()
    {
        Assert.Throws<System.ArgumentException>(() => YSharpEnv.ParseTier("weekly"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("42", 42u)]
    public void Env_Seed_ParsesOptionalUnsigned(string? value, uint? expected)
    {
        Assert.Equal(expected, YSharpEnv.ParseSeed(value));
    }
}
