using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Codecs;

/// <summary>
/// YValue and the lib0 Any encoding (ADR 0002, 0007; quirks Q01-Q09, Q31). Expected bytes come from lib0 0.2.99 run under the
/// pinned Node; the whole-fixture replays are in <see cref="WireFixtureTests"/>. Each test builds a client-1 document with one
/// <c>m.k</c> value and compares the Any payload inside the V1 update, or decodes a hand-built payload.
/// </summary>
[Requires(Feature.Values, Feature.V1, Feature.Map)]
public sealed class ValueQuirkTests
{
    private static string AnyHex(YValue v) => Hex.Of(Wire.AnyPayloadOfMapSet(v));

    private static string Reencode(string anyHex) => Hex.Of(Wire.ReencodeAny(Hex.Bytes(anyHex)));

    public static IEnumerable<object[]> NumberTagTable() => new[]
    {
        new object[] { 0.0, "7d00" }, new object[] { 1.0, "7d01" }, new object[] { -1.0, "7d41" },
        new object[] { 1.5, "7c3fc00000" }, new object[] { 1.1, "7b3ff199999999999a" }, new object[] { 0.1, "7b3fb999999999999a" },
        new object[] { 16777217.0, "7d81808010" }, new object[] { 2147483648.0, "7c4f000000" },
        new object[] { 2147483649.0, "7b41e0000000200000" }, new object[] { 9007199254740992.0, "7c5a000000" },
        new object[] { 1e21, "7b444b1ae4d6e2ef50" }, new object[] { 1.7976931348623157e308, "7b7fefffffffffffff" },
    };

    [Theory, Covers("Q01", "ENC-06", "ENC-72")]
    [MemberData(nameof(NumberTagTable))]
    public void Q01_NumberTag_IsChosenFromTheValue(double value, string expected) =>
        Assert.Equal(expected, AnyHex(value));

    [Fact, Covers("Q01")]
    public void Q01_IntAndDoubleOfTheSameValue_EncodeIdentically() =>
        Assert.Equal(AnyHex(1), AnyHex(1.0));

    [Fact, Covers("Q01")]
    public void Q01_ForeignTags_AreCanonicalisedOnReencode()
    {
        // float64 1.0 and float32 5.0 as another encoder may write them; re-encoding picks the tag from the value.
        Assert.Equal("7d01", Reencode("7b3ff0000000000000"));
        Assert.Equal("7d05", Reencode("7c40a00000"));
        Assert.Equal("7d01", Reencode("7d01"));
    }

    [Theory, Covers("Q02", "ENC-72")]
    [InlineData(2147483647.0, "7dbfffffff0f")]
    [InlineData(-2147483647.0, "7dffffffff0f")]
    [InlineData(-2147483648.0, "7ccf000000")]
    [InlineData(2147483648.0, "7c4f000000")]
    public void Q02_IntegerCutoff_Is2Pow31Minus1_SoInt32MinimumIsFloat32(double value, string expected) =>
        Assert.Equal(expected, AnyHex(value));

    [Fact, Covers("Q03", "ENC-03")]
    public void Q03_NegativeZero_IsPreserved_AndPositiveZeroIsNot()
    {
        Assert.Equal("7d40", AnyHex(-0.0));
        Assert.Equal("7d00", AnyHex(0.0));
        var back = Wire.ValueOfMapKey(Wire.DocWithAny(Hex.Bytes("7d40")));
        Assert.True(back.TryGetNumber(out var d));
        Assert.True(double.IsNegative(d) && d == 0);
        Assert.NotEqual((YValue)0.0, (YValue)(-0.0));   // wire equality tells +0 from -0
    }

    [Fact, Covers("Q04", "ENC-06")]
    public void Q04_NaN_IsNormalisedToTheV8Pattern_AndInfinitiesAreFloat32()
    {
        Assert.Equal("7b7ff8000000000000", AnyHex(double.NaN));
        Assert.Equal("7b7ff8000000000000", AnyHex(BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000000UL))));
        Assert.Equal("7c7f800000", AnyHex(double.PositiveInfinity));
        Assert.Equal("7cff800000", AnyHex(double.NegativeInfinity));
        Assert.Equal("7b7ff8000000000000", Reencode("7bfff8000000000001"));   // any NaN decodes, the canonical one is written
        Assert.Equal((YValue)double.NaN, (YValue)double.NaN);               // wire equality: NaN equals NaN
    }

    [Fact, Covers("Q05", "ENC-06")]
    public void Q05_BigInt_HasItsOwnTag_AndNeverMeetsNumber()
    {
        Assert.Equal("7a7fffffffffffffff", AnyHex(YValue.BigInt(long.MaxValue)));
        Assert.Equal("7a8000000000000000", AnyHex(YValue.BigInt(long.MinValue)));
        Assert.Equal("7a0000000000000005", AnyHex(YValue.BigInt(5)));
        Assert.Equal("7d05", AnyHex(5));
        Assert.NotEqual(YValue.BigInt(5), (YValue)5);
        var decoded = Wire.ValueOfMapKey(Wire.DocWithAny(Hex.Bytes("7a0000000000000005")));
        Assert.Equal(YValueKind.BigInt, decoded.Kind);
        Assert.True(decoded.TryGetBigInt(out var l) && l == 5);
    }

    [Fact, Covers("Q06")]
    public void Q06_Undefined_IsADistinctCaseFromNull_AndIsStoredInAMap()
    {
        Assert.Equal("7f", AnyHex(YValue.Undefined));
        Assert.Equal("7e", AnyHex(YValue.Null));
        Assert.Equal(YValueKind.Undefined, default(YValue).Kind);
        Assert.NotEqual(YValue.Null, YValue.Undefined);
        var doc = Wire.NewDoc();
        var map = doc.GetMap("m");
        map.Set("u", YValue.Undefined);
        Assert.True(map.Has("u"));                                 // live although it reads back as undefined (T-0.11)
        Assert.True(map.TryGet("u", out var e) && e.Value.Kind == YValueKind.Undefined);
        Assert.False(map.Has("absent"));
    }

    [Fact, Diverges("Q07", adr: "0003"), Covers("ENC-07")]
    public void Q07_WriteAnyFlattening_CannotBeProducedAndDecodesAsEmptyObject()
    {
        // JS: new Date(0), new Map() and an ArrayBuffer all reach the wire as the empty object 7600. y-sharp has nine value cases
        // and no constructor for them, so the flattening cannot happen; the bytes still decode.
        Assert.Equal(9, Enum.GetValues<YValueKind>().Length);
        var decoded = Wire.ValueOfMapKey(Wire.DocWithAny(Hex.Bytes("7600")));
        Assert.True(decoded.TryGetObject(out var obj));
        Assert.Equal(0, obj.Count);
        Assert.Equal("7600", AnyHex(YValue.Object(Array.Empty<KeyValuePair<string, YValue>>())));
    }

    [Fact, Covers("Q08")]
    public void Q08_ObjectKeys_ArrayIndexKeysFirstThenInsertionOrder()
    {
        var members = new[] { "b", "2", "a", "1", "01", "4294967294", "4294967295" }
            .Select((k, i) => new KeyValuePair<string, YValue>(k, i + 1)).ToArray();
        const string expected = "760701317d0401327d020a343239343936373239347d0601627d0101617d030230317d050a343239343936373239357d07";
        Assert.Equal(expected, AnyHex(YValue.Object(members)));
        // decode keeps the wire order in memory (insertion order) and the encoder sorts again
        var wireOrder = "7604" + "0162" + "7d01" + "0132" + "7d02" + "0161" + "7d03" + "0131" + "7d04";
        var view = Wire.ValueOfMapKey(Wire.DocWithAny(Hex.Bytes(wireOrder)));
        Assert.True(view.TryGetObject(out var obj));
        Assert.Equal("b", obj[0].Key);
        Assert.Equal("760401317d0401327d0201627d0101617d03", Reencode(wireOrder));
    }

    [Fact, Diverges("Q09", adr: "0003")]
    public void Q09_ProtoKey_IsAnOrdinaryKey_AndDuplicatesKeepFirstPositionLastValue()
    {
        // JS: obj["__proto__"] = {...} sets the prototype and the own key vanishes. y-sharp keeps it (no prototype in C#).
        var proto = Wire.ValueOfMapKey(Wire.DocWithAny(Hex.Bytes("7601" + "095f5f70726f746f5f5f" + "7601" + "0161" + "7d01")));
        Assert.True(proto.TryGetObject(out var p));
        Assert.Equal(1, p.Count);
        Assert.True(p.TryGetValue("__proto__", out var inner) && inner.Kind == YValueKind.Object);

        // duplicate keys: the last value wins and the key keeps its first position (this part matches JS)
        var dup = Wire.ValueOfMapKey(Wire.DocWithAny(Hex.Bytes("7603" + "0161" + "7d01" + "0162" + "7d02" + "0161" + "7d03")));
        Assert.True(dup.TryGetObject(out var d));
        Assert.Equal(2, d.Count);
        Assert.Equal("a", d[0].Key);
        Assert.Equal(3.0, d[0].Value.AsNumber());
        Assert.Equal("b", d[1].Key);
    }

    [Fact, Covers("ENC-07", "ENC-72")]
    public void Enc07_EveryAnyTag_RoundTripsThroughDecodeAndEncode()
    {
        var items = new YValue[]
        {
            YValue.Undefined, YValue.Null, 1, 1.5, 0.1, YValue.BigInt(5), true, false, "x", YValue.Bytes(new byte[] { 9 }),
            YValue.Array(Array.Empty<YValue>()), YValue.Object(Array.Empty<KeyValuePair<string, YValue>>()),
        };
        const string expected = "750c7f7e7d017c3fc000007b3fb999999999999a7a0000000000000005787977017874010975007600";
        var value = YValue.Array(items);
        Assert.Equal(expected, AnyHex(value));
        Assert.Equal(expected, Reencode(expected));
        Assert.Equal(value, Wire.ValueOfMapKey(Wire.DocWithAny(Hex.Bytes(expected))));
    }

    [Theory, Covers("ENC-03", "ENC-72")]
    [InlineData(63.0, "7d3f")]
    [InlineData(64.0, "7d8001")]
    [InlineData(-63.0, "7d7f")]
    [InlineData(-64.0, "7dc001")]
    [InlineData(8191.0, "7dbf7f")]
    [InlineData(8192.0, "7d808001")]
    [InlineData(-8192.0, "7dc08001")]
    [InlineData(1000000000.0, "7d80a8d6b907")]
    public void Enc03_VarInt_IsSignMagnitudeWithSixBitFirstGroup(double value, string expected) =>
        Assert.Equal(expected, AnyHex(value));

    [Fact, Covers("ENC-04", "ENC-06")]
    public void Enc04_FixedWidthFloats_AreBigEndian_AndForeignWidthsDecode()
    {
        Assert.Equal("7b40091eb851eb851f", AnyHex(3.14));
        Assert.Equal("7c47c35040", AnyHex(100000.5));
        Assert.Equal(1.5, Wire.ValueOfMapKey(Wire.DocWithAny(Hex.Bytes("7b3ff8000000000000"))).AsNumber());   // float64 on the wire
        Assert.Equal(1.5, Wire.ValueOfMapKey(Wire.DocWithAny(Hex.Bytes("7c3fc00000"))).AsNumber());           // float32
    }

    [Fact, Covers("ENC-05")]
    [Requires(Feature.Text)]
    public void Enc05_VarString_IsLengthPrefixedUtf8_AndBinaryIsLengthPrefixedBytes()
    {
        Assert.Equal("77026162", AnyHex("ab"));
        Assert.Equal("7700", AnyHex(""));
        Assert.Equal("7403010203", AnyHex(YValue.Bytes(new byte[] { 1, 2, 3 })));
        var doc = Wire.NewDoc();
        doc.GetText("t").Insert(0, "é");      // two UTF-8 bytes, one UTF-16 unit
        Assert.Equal("010101000401017402c3a900", Hex.Of(Wire.Encode(doc)));
    }

    [Fact, Covers("Q31"), Diverges("Q31", adr: "0003")]
    public void Q31_Buffers_AreCopiedOnConstruction_AndOnDecode()
    {
        var source = new byte[] { 1, 2, 3 };
        var value = YValue.Bytes(source);
        source[0] = 9;                                  // JS Buffers are views; y-sharp never aliases caller memory
        Assert.True(value.TryGetBytes(out var bytes));
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes.ToArray());

        var update = Wire.MapSetUpdateWithAny(Hex.Bytes("7403010203"));
        var doc = new Doc();
        doc.ApplyUpdate(update);
        Array.Clear(update);                            // decoded content must survive the caller reusing its buffer
        Assert.True(Wire.ValueOfMapKey(doc).TryGetBytes(out var decoded));
        Assert.Equal(new byte[] { 1, 2, 3 }, decoded.ToArray());

        var owned = new byte[] { 4, 5 };
        var transferred = YValue.BytesOwned(owned);     // the explicit transfer factory is the one way to share memory
        Assert.True(transferred.TryGetBytes(out var t));
        Assert.Equal(new byte[] { 4, 5 }, t.ToArray());
    }
}
