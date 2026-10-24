using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using YSharp.TestKit.Values;
using YSharp.TestKit.Stubs;

namespace YSharp.Tests.TestKit;

public class TaggedValueTests
{
    private static TaggedValue Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return TaggedValue.Read(doc.RootElement);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("1.5")]
    [InlineData("42")]
    [InlineData("\"plain\"")]
    [InlineData("[1,\"a\",null,[true]]")]
    [InlineData("{\"$\":\"undefined\"}")]
    [InlineData("{\"$\":\"nan\"}")]
    [InlineData("{\"$\":\"inf\"}")]
    [InlineData("{\"$\":\"-inf\"}")]
    [InlineData("{\"$\":\"-0\"}")]
    [InlineData("{\"$\":\"bigint\",\"v\":\"-9223372036854775808\"}")]
    [InlineData("{\"$\":\"bytes\",\"hex\":\"00ff10\"}")]
    [InlineData("{\"$\":\"str\",\"units\":[97,55357,98]}")]
    [InlineData("{\"$\":\"obj\",\"pairs\":[[\"b\",1],[\"a\",{\"$\":\"undefined\"}],[\"1\",[]]]}")]
    public void Write_AfterRead_RoundTripsCanonicalText(string json)
    {
        Assert.Equal(json, Read(json).ToJson());
    }

    [Fact]
    public void Read_SpecialNumbers_ProduceDistinctKinds()
    {
        Assert.Equal(TaggedKind.Number, Read("{\"$\":\"nan\"}").Kind);
        Assert.True(double.IsNaN(Read("{\"$\":\"nan\"}").Number));
        Assert.True(double.IsNegative(Read("{\"$\":\"-0\"}").Number));
        Assert.Equal(double.PositiveInfinity, Read("{\"$\":\"inf\"}").Number);
        Assert.Equal(TaggedKind.BigInt, Read("{\"$\":\"bigint\",\"v\":\"7\"}").Kind);
    }

    [Fact]
    public void Read_LoneSurrogateString_KeepsCodeUnits()
    {
        var v = Read("{\"$\":\"str\",\"units\":[55357]}");
        Assert.Equal("\ud83d", v.String);
    }

    [Fact]
    public void FromString_WithLoneSurrogate_WritesUnitList()
    {
        Assert.Equal("{\"$\":\"str\",\"units\":[55357,97]}", TaggedValue.FromString("\ud83da").ToJson());
        var paired = TaggedValue.FromString("😀");
        Assert.True(paired.WireEquals(TaggedValue.Parse(paired.ToJson())));
        Assert.DoesNotContain("units", paired.ToJson());
    }

    [Fact]
    public void FromNumber_NegativeZeroAndNonFinite_AreTagged()
    {
        Assert.Equal("{\"$\":\"-0\"}", TaggedValue.FromNumber(-0.0).ToJson());
        Assert.Equal("{\"$\":\"nan\"}", TaggedValue.FromNumber(double.NaN).ToJson());
        Assert.Equal("0", TaggedValue.FromNumber(0.0).ToJson());
    }

    [Fact]
    public void WireEquals_NanAndNegativeZero_FollowWireRules()
    {
        Assert.True(Read("{\"$\":\"nan\"}").WireEquals(TaggedValue.FromNumber(double.NaN)));
        Assert.False(Read("{\"$\":\"-0\"}").WireEquals(TaggedValue.FromNumber(0.0)));
        Assert.False(TaggedValue.FromNumber(1).WireEquals(TaggedValue.BigInt(1)));
        Assert.True(Read("[1,{\"$\":\"bytes\",\"hex\":\"01\"}]").WireEquals(Read("[1,{\"$\":\"bytes\",\"hex\":\"01\"}]")));
    }

    [Fact]
    public void Object_PairOrder_IsPreservedAndSignificant()
    {
        var a = Read("{\"$\":\"obj\",\"pairs\":[[\"2\",1],[\"1\",2]]}");
        Assert.Equal(new[] { "2", "1" }, new List<string>(a.Pairs.ConvertAll(p => p.Key)));
        Assert.False(a.WireEquals(Read("{\"$\":\"obj\",\"pairs\":[[\"1\",2],[\"2\",1]]}")));
    }

    [Theory]
    [InlineData("{\"a\":1}")]
    [InlineData("{\"$\":\"mystery\"}")]
    [InlineData("{\"$\":\"bytes\",\"hex\":\"abc\"}")]
    [InlineData("{\"$\":\"bytes\",\"hex\":\"zz\"}")]
    [InlineData("{\"$\":\"bigint\",\"v\":\"1.5\"}")]
    [InlineData("{\"$\":\"nan\",\"extra\":1}")]
    [InlineData("{\"$\":\"str\",\"units\":[70000]}")]
    [InlineData("{\"$\":\"obj\",\"pairs\":[[\"a\"]]}")]
    public void Read_Malformed_Throws(string json)
    {
        Assert.Throws<FormatException>(() => Read(json));
    }

    [Fact]
    public void ToYValue_AgainstStub_ConvertsOrSkips()
    {
        try
        {
            var v = Read("[1,\"a\",null]").ToYValue();
            Assert.Equal(YValueKind.Array, v.Kind);
        }
        catch (NotImplementedException) when (ApiStub.IsStub)
        {
            Assert.Skip(ApiStub.Reason);
        }
    }
}
