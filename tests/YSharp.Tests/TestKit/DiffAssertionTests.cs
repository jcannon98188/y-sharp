using System;
using Xunit;
using Xunit.Sdk;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.TestKit;

[LibraryIndependent]
public class DiffAssertionTests
{
    [Fact]
    public void HexDiff_EqualBytes_ReturnsNull()
    {
        Assert.Null(HexDiff.Describe(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void HexDiff_DifferentByte_ReportsOffsetAndWindow()
    {
        var expected = new byte[20];
        var actual = new byte[20];
        actual[12] = 0xAB;
        var msg = HexDiff.Describe(expected, actual)!;
        Assert.Contains("offset 12", msg);
        Assert.Contains("expected 00", msg);
        Assert.Contains("actual ab", msg);
    }

    [Fact]
    public void HexDiff_PrefixLengthMismatch_ReportsLengths()
    {
        var msg = HexDiff.Describe(new byte[] { 1, 2 }, new byte[] { 1, 2, 3 })!;
        Assert.Contains("length", msg);
        Assert.Contains("offset 2", msg);
    }

    [Fact]
    public void HexAssert_Mismatch_ThrowsWithLabel()
    {
        var ex = Assert.ThrowsAny<XunitException>(() => HexAssert.Equal(new byte[] { 1 }, new byte[] { 2 }, "update[0]"));
        Assert.Contains("update[0]", ex.Message);
    }

    [Fact]
    public void HexAssert_Equal_DoesNotThrow()
    {
        HexAssert.Equal(Convert.FromHexString("0a0b"), Convert.FromHexString("0A0B"));
    }

    [Fact]
    public void StructuralDiff_ObjectKeyOrder_IsIgnored()
    {
        Assert.Null(StructuralDiff.Find("{\"a\":1,\"b\":[1,2]}", "{\"b\":[1,2],\"a\":1}"));
    }

    [Fact]
    public void StructuralDiff_ArrayElementDiffers_ReportsPath()
    {
        var msg = StructuralDiff.Find("{\"doc\":{\"t\":{\"delta\":[{\"insert\":\"a\"},{\"insert\":\"b\"}]}}}",
                                      "{\"doc\":{\"t\":{\"delta\":[{\"insert\":\"a\"},{\"insert\":\"c\"}]}}}")!;
        Assert.StartsWith("$.doc.t.delta[1].insert", msg);
    }

    [Fact]
    public void StructuralDiff_MissingAndExtraMembers_AreReported()
    {
        Assert.Contains("$.x", StructuralDiff.Find("{\"x\":1}", "{}")!);
        Assert.Contains("$.y", StructuralDiff.Find("{}", "{\"y\":1}")!);
    }

    [Fact]
    public void StructuralDiff_ArrayLengthMismatch_ReportsPath()
    {
        Assert.Contains("$[2]", StructuralDiff.Find("[1,2,3]", "[1,2]")!);
    }

    [Fact]
    public void StructuralDiff_NegativeZeroAndZero_Differ()
    {
        Assert.NotNull(StructuralDiff.Find("[0]", "[-0]"));
    }

    [Fact]
    public void StructuralAssert_Mismatch_Throws()
    {
        Assert.ThrowsAny<XunitException>(() => StructuralAssert.Equal("[1]", "[2]", "doc dump"));
        StructuralAssert.Equal("{\"a\":null}", "{\"a\":null}");
    }
}
