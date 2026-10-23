using System;
using Xunit;
using Xunit.Sdk;
using YSharp.TestKit.Allocation;

namespace YSharp.Tests.TestKit;

public class AllocationAssertTests
{
    private static object? _sink;

    [Fact]
    public void AtMost_NonAllocatingOperation_PassesAtZero()
    {
        var x = 0;
        AllocationAssert.AtMost("BUD-test-zero", 0, () => x += 3);
        Assert.NotEqual(0, x);
    }

    [Fact]
    public void AtMost_AllocatingOperation_FailsAndNamesTheAllocatingType()
    {
        var ex = Assert.ThrowsAny<XunitException>(() =>
            AllocationAssert.AtMost("BUD-test-alloc", 0, () => _sink = new int[512]));
        Assert.Contains("BUD-test-alloc", ex.Message);
        Assert.Contains("System.Int32[]", ex.Message);
    }

    [Fact]
    public void AtMost_AllocationWithinBudget_Passes()
    {
        AllocationAssert.AtMost("BUD-test-bounded", 4096, () => _sink = new byte[1024]);
    }

    [Fact]
    public void Measure_ReportsBytesPerOperation()
    {
        var bytes = AllocationAssert.Measure(() => _sink = new byte[1000], iterations: 100);
        Assert.InRange(bytes, 1000, 1100);
    }
}
