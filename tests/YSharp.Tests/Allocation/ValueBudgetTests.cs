using Xunit;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Allocation;

/// <summary>
/// BUD-03 (ADR 0009). It needs only <c>YValue</c>, so it lives apart from <see cref="HotPathBudgetTests"/>, whose class-level gate
/// (Transactions) would keep it skipped long after the values have landed. Same serial collection, Release only.
/// </summary>
[Requires(Feature.Values)]
[Collection("Allocation")]
[Tier(Tier.Gating)]
public sealed class ValueBudgetTests
{
    [Fact, Covers("BUD-03")]
    public void ScalarValueReads_AndAttributeEquals_AllocateNothing()
    {
        YValue number = 42.0, flag = true, text = "abc", other = 42.0;
        var sink = 0.0;
        AllocationBudgets.Check("BUD-03", "", () =>
        {
            _ = number.Kind;
            if (number.TryGetNumber(out var n)) sink += n;
            if (flag.TryGetBool(out var b) && b) sink += 1;
            if (text.TryGetString(out var s)) sink += s.Length;
            if (number.AttributeEquals(other)) sink += 1;
        });
        Assert.True(sink > 0);
    }
}
