using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.Lib0;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Values;

/// <summary>
/// YValue against the lib0 Any codec (ADR 0002, 0007) through the internal <see cref="YValueAny"/> seam, with no document in
/// between. The bytes are the lib0 0.2.99 values ValueQuirkTests checks through Doc; these run as soon as the values land.
/// </summary>
[Requires(Feature.Values)]
[Collection("RentedBuffers")]
public sealed class YValueAnyTests
{
    private static string Encode(YValue value)
    {
        using var w = new Lib0Writer();
        Assert.Equal(YError.None, YValueAny.Write(w, value, 128));
        return Hex.Of(w.WrittenSpan);
    }

    private static YValue Decode(string hex, int maxDepth = 128)
    {
        var r = new Lib0Reader(Hex.Bytes(hex));
        Assert.Equal(YError.None, YValueAny.Read(ref r, maxDepth, out var value));
        Assert.Equal(0, r.Remaining);
        return value;
    }

    private static YError DecodeError(string hex, int maxDepth = 128)
    {
        var r = new Lib0Reader(Hex.Bytes(hex));
        return YValueAny.Read(ref r, maxDepth, out _);
    }

    private static string Reencode(string hex) => Encode(Decode(hex));

    private static YValue Obj(params (string Key, YValue Value)[] m) =>
        YValue.Object(m.Select(x => new KeyValuePair<string, YValue>(x.Key, x.Value)).ToArray());

    public static IEnumerable<object[]> NumberTagTable() => new[]
    {
        new object[] { 0.0, "7d00" }, new object[] { 1.0, "7d01" }, new object[] { -1.0, "7d41" },
        new object[] { 1.5, "7c3fc00000" }, new object[] { 1.1, "7b3ff199999999999a" }, new object[] { 0.1, "7b3fb999999999999a" },
        new object[] { 16777217.0, "7d81808010" }, new object[] { 2147483648.0, "7c4f000000" },
        new object[] { 2147483649.0, "7b41e0000000200000" }, new object[] { 9007199254740992.0, "7c5a000000" },
        new object[] { 1e21, "7b444b1ae4d6e2ef50" }, new object[] { 2147483647.0, "7dbfffffff0f" },
        new object[] { -2147483647.0, "7dffffffff0f" }, new object[] { -2147483648.0, "7ccf000000" },
        new object[] { -0.0, "7d40" }, new object[] { double.NaN, "7b7ff8000000000000" },
        new object[] { double.PositiveInfinity, "7c7f800000" }, new object[] { double.NegativeInfinity, "7cff800000" },
    };

    [Theory, Covers("Q01", "Q02", "Q03", "Q04")]
    [MemberData(nameof(NumberTagTable))]
    public void Number_TagIsChosenFromTheValue_AndSurvivesARoundTrip(double value, string expected)
    {
        Assert.Equal(expected, Encode(value));
        var back = Decode(expected);
        Assert.Equal((YValue)value, back);
    }

    [Fact, Covers("Q01")]
    public void Number_ForeignTags_AreCanonicalisedOnReencode()
    {
        Assert.Equal("7d01", Reencode("7b3ff0000000000000"));    // float64 1.0
        Assert.Equal("7d05", Reencode("7c40a00000"));            // float32 5.0
        Assert.Equal("7d01", Reencode("7d01"));
    }

    [Fact, Covers("Q03")]
    public void NegativeZero_IsPreservedFromTheIntegerTag()
    {
        var back = Decode("7d40");
        Assert.True(back.TryGetNumber(out var d) && d == 0 && double.IsNegative(d));
        Assert.NotEqual((YValue)0.0, back);
    }

    [Fact, Covers("Q04")]
    public void AnyNaNPattern_Decodes_AndTheCanonicalOneIsWritten()
    {
        Assert.Equal("7b7ff8000000000000", Reencode("7bfff8000000000001"));
        Assert.Equal("7b7ff8000000000000", Encode(BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000000UL))));
    }

    [Fact, Covers("Q05")]
    public void BigInt_HasItsOwnTag_AndNeverMeetsNumber()
    {
        Assert.Equal("7a7fffffffffffffff", Encode(YValue.BigInt(long.MaxValue)));
        Assert.Equal("7a8000000000000000", Encode(YValue.BigInt(long.MinValue)));
        Assert.Equal("7a0000000000000005", Encode(YValue.BigInt(5)));
        Assert.Equal("7d05", Encode(5));
        Assert.Equal(YValueKind.BigInt, Decode("7a0000000000000005").Kind);
        Assert.Equal(YValue.BigInt(-1), Decode("7affffffffffffffff"));
    }

    [Fact, Covers("Q06")]
    public void Undefined_IsADistinctCaseFromNull()
    {
        Assert.Equal("7f", Encode(YValue.Undefined));
        Assert.Equal("7e", Encode(YValue.Null));
        Assert.Equal(YValueKind.Undefined, Decode("7f").Kind);
        Assert.Equal(YValueKind.Null, Decode("7e").Kind);
    }

    [Fact, Covers("Q08")]
    public void ObjectKeys_ArrayIndexKeysFirst_ThenInsertionOrder()
    {
        var members = new[] { "b", "2", "a", "1", "01", "4294967294", "4294967295" }
            .Select((k, i) => new KeyValuePair<string, YValue>(k, i + 1)).ToArray();
        Assert.Equal("760701317d0401327d020a343239343936373239347d0601627d0101617d030230317d050a343239343936373239357d07",
            Encode(YValue.Object(members)));

        var wireOrder = "7604" + "0162" + "7d01" + "0132" + "7d02" + "0161" + "7d03" + "0131" + "7d04";
        var decoded = Decode(wireOrder);
        Assert.True(decoded.TryGetObject(out var view));
        Assert.Equal(new[] { "b", "2", "a", "1" }, Enumerable.Range(0, 4).Select(i => view[i].Key).ToArray());   // memory keeps the wire order
        Assert.Equal("760401317d0401327d0201627d0101617d03", Encode(decoded));                                      // the encoder sorts again
    }

    [Fact, Covers("Q09")]
    public void ProtoKey_IsOrdinary_AndDuplicateKeysKeepTheFirstPositionAndTheLastValue()
    {
        var proto = Decode("7601" + "095f5f70726f746f5f5f" + "7601" + "0161" + "7d01");
        Assert.True(proto.TryGetObject(out var p));
        Assert.Equal(1, p.Count);
        Assert.True(p.TryGetValue("__proto__", out var inner) && inner.Kind == YValueKind.Object);

        var dup = Decode("7603" + "0161" + "7d01" + "0162" + "7d02" + "0161" + "7d03");
        Assert.True(dup.TryGetObject(out var d));
        Assert.Equal(2, d.Count);
        Assert.Equal("a", d[0].Key);
        Assert.Equal(3.0, d[0].Value.AsNumber());
        Assert.Equal("b", d[1].Key);
        Assert.Equal("760201617d0301627d02", Encode(dup));
    }

    [Fact, Covers("Q31")]
    public void Bytes_AreCopiedOnDecode()
    {
        var wire = Hex.Bytes("7403010203");
        var r = new Lib0Reader(wire);
        Assert.Equal(YError.None, YValueAny.Read(ref r, 128, out var value));
        Array.Clear(wire);
        Assert.True(value.TryGetBytes(out var bytes));
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes.ToArray());
    }

    [Fact, Covers("ENC-07")]
    public void EveryAnyTag_RoundTrips()
    {
        var items = new YValue[]
        {
            YValue.Undefined, YValue.Null, 1, 1.5, 0.1, YValue.BigInt(5), true, false, "x", YValue.Bytes(new byte[] { 9 }),
            YValue.Array(Array.Empty<YValue>()), YValue.Object(Array.Empty<KeyValuePair<string, YValue>>()),
        };
        const string expected = "750c7f7e7d017c3fc000007b3fb999999999999a7a0000000000000005787977017874010975007600";
        var value = YValue.Array(items);
        Assert.Equal(expected, Encode(value));
        Assert.Equal(value, Decode(expected));
        Assert.Equal(expected, Reencode(expected));
    }

    [Fact, Covers("ENC-07")]
    public void Strings_RoundTripThroughUtf8_IncludingLoneSurrogates()
    {
        Assert.Equal("77026162", Encode("ab"));
        Assert.Equal("77 02 c3a9".Replace(" ", ""), Encode("é"));
        Assert.Equal("é", Decode("7702c3a9").AsString());
        Assert.Equal("😀", Decode(Encode("😀")).AsString());
        // a lone surrogate is written as U+FFFD, as TextEncoder does (Q18), so it does not come back
        Assert.Equal("77 03 efbfbd".Replace(" ", ""), Encode("\ud83d"));
    }

    // ------------------------------------------------------------------ limits and hostile shapes

    [Fact]
    public void Decode_DepthLimit_IsGreaterThan_SoTheLimitItselfIsAccepted()
    {
        static string Nested(int depth) => string.Concat(Enumerable.Repeat("7501", depth)) + "7f";
        Assert.Equal(YError.None, DecodeError(Nested(4), maxDepth: 4));
        Assert.Equal(YError.LimitExceeded, DecodeError(Nested(5), maxDepth: 4));
    }

    [Fact]
    public void Decode_MalformedInput_IsReportedAsData()
    {
        Assert.Equal(YError.Malformed, DecodeError(""));
        Assert.Equal(YError.Malformed, DecodeError("7d"));                  // truncated varint
        Assert.Equal(YError.Malformed, DecodeError("7b3ff000"));            // truncated float64
        Assert.Equal(YError.Malformed, DecodeError("73"));                  // unknown tag
        Assert.Equal(YError.Malformed, DecodeError("75ffffff0f"));          // an array claiming 268 million items in no bytes
        Assert.Equal(YError.Malformed, DecodeError("7603" + "0161" + "7d01"));   // object short of members
    }

    [Fact]
    public void Write_DepthLimit_ReturnsLimitExceeded_NeverRecurses()
    {
        static YValue Nest(int depth)
        {
            var v = (YValue)1;
            for (var i = 0; i < depth; i++) v = YValue.Array(new[] { v });
            return v;
        }
        using var w = new Lib0Writer();
        Assert.Equal(YError.None, YValueAny.Write(w, Nest(128), 128));
        w.Reset();
        Assert.Equal(YError.LimitExceeded, YValueAny.Write(w, Nest(129), 128));
        w.Reset();
        Assert.Equal(YError.LimitExceeded, YValueAny.Write(w, Nest(500_000), 128));   // an iterative walk, so no stack overflow
        w.Reset();
        Assert.Equal(YError.None, YValueAny.Write(w, Nest(0), 128));
    }

    [Fact]
    public void LargeCollections_RoundTrip_WithAnIterativeWalk()
    {
        var items = Enumerable.Range(0, 20_000).Select(i => (YValue)i).ToArray();
        var value = YValue.Array(items);
        var hex = Encode(value);
        Assert.Equal(value, Decode(hex));
    }

    [Fact]
    public void Decode_ARepeatedlyNestedValue_ReadsBackEqual()
    {
        var value = Obj(("a", YValue.Array(new YValue[] { 1, Obj(("b", YValue.Null)), YValue.Bytes(new byte[] { 7 }) })), ("c", "d"));
        Assert.Equal(value, Decode(Encode(value)));
    }
}
