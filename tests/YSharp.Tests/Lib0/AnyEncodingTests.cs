using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Xunit;
using YSharp.Lib0;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Lib0;

[CollectionDefinition("RentedBuffers", DisableParallelization = true)]
public sealed class RentedBuffersCollection { }

/// <summary>
/// The Any encoding at the lib0 level (ENC-07, ENC-72, Q01..Q08, Q15): a visitor-based writer and reader that do not depend on
/// YValue, which TASK-4.9 adapts. Expected bytes are the lib0 0.2.99 values already used by <c>ValueQuirkTests</c>.
/// </summary>
[Requires(Feature.Encoding)]
[Collection("RentedBuffers")]
public sealed class AnyEncodingTests
{
    // ---- a small value tree and the two adapters the tests use ----

    private sealed class Undef { public static readonly Undef Value = new(); }

    private sealed record Obj(params KeyValuePair<string, object?>[] Members);

    private static KeyValuePair<string, object?> P(string k, object? v) => new(k, v);

    private static void Write(Lib0Writer w, object? v)
    {
        switch (v)
        {
            case Undef: AnyWriter.WriteUndefined(w); break;
            case null: AnyWriter.WriteNull(w); break;
            case bool b: AnyWriter.WriteBool(w, b); break;
            case double d: AnyWriter.WriteNumber(w, d); break;
            case long l: AnyWriter.WriteBigInt(w, l); break;
            case string s: AnyWriter.WriteString(w, s); break;
            case byte[] bytes: AnyWriter.WriteBytes(w, bytes); break;
            case object?[] a:
                AnyWriter.WriteArrayHeader(w, a.Length);
                foreach (var x in a) Write(w, x);
                break;
            case Obj o:
                AnyWriter.WriteObjectHeader(w, o.Members.Length);
                var order = new int[o.Members.Length];
                JsKeyOrder.Order(o.Members.Select(m => m.Key).ToArray(), order);
                foreach (var i in order) { AnyWriter.WriteKey(w, o.Members[i].Key); Write(w, o.Members[i].Value); }
                break;
            default: throw new ArgumentException(v.GetType().Name);
        }
    }

    private static string Encode(object? v)
    {
        using var w = new Lib0Writer();
        Write(w, v);
        return Hex.Of(w.WrittenSpan);
    }

    /// <summary>Builds a compact text form: numbers as G17 (negative zero as -0), bigints with n, containers in wire order.</summary>
    private sealed class TextSink : IAnySink
    {
        public readonly StringBuilder Text = new();
        private readonly Stack<(int Left, bool Object)> open = new();
        public int MaxOpen;

        private void Sep()
        {
            if (open.Count > 0 && Text.Length > 0 && Text[^1] != '[' && Text[^1] != '{' && Text[^1] != ':') Text.Append(',');
        }

        public void Undefined() { Sep(); Text.Append("undefined"); }
        public void Null() { Sep(); Text.Append("null"); }
        public void Bool(bool v) { Sep(); Text.Append(v ? "true" : "false"); }
        public void Number(double v) { Sep(); Text.Append(double.IsNegative(v) && v == 0 ? "-0" : v.ToString("R", CultureInfo.InvariantCulture)); }
        public void BigInt(long v) { Sep(); Text.Append(v.ToString(CultureInfo.InvariantCulture)).Append('n'); }
        public void String(string v) { Sep(); Text.Append('"').Append(v).Append('"'); }
        public void Bytes(ReadOnlySpan<byte> v) { Sep(); Text.Append("bytes:").Append(Hex.Of(v)); }
        public void BeginArray(int count) { Sep(); Text.Append('['); open.Push((count, false)); MaxOpen = Math.Max(MaxOpen, open.Count); }
        public void EndArray() { open.Pop(); Text.Append(']'); }
        public void BeginObject(int count) { Sep(); Text.Append('{'); open.Push((count, true)); MaxOpen = Math.Max(MaxOpen, open.Count); }
        public void Key(string key) { Sep(); Text.Append(key).Append(':'); }
        public void EndObject() { open.Pop(); Text.Append('}'); }
    }

    private static (YError Error, string Text) Decode(string hex, int maxDepth = 128, int maxStringBytes = int.MaxValue, bool requireEnd = true)
    {
        var r = new Lib0Reader(Hex.Bytes(hex), maxStringBytes);
        var sink = new TextSink();
        var error = AnyReader.Read(ref r, ref sink, maxDepth);
        if (error == YError.None && requireEnd) Assert.Equal(0, r.Remaining);
        return (error, sink.Text.ToString());
    }

    // ---- writer: number tag selection (Q01, Q02, Q03, Q04) ----

    public static IEnumerable<object[]> NumberTable() => new[]
    {
        new object[] { 0.0, "7d00" }, new object[] { 1.0, "7d01" }, new object[] { -1.0, "7d41" },
        new object[] { 1.5, "7c3fc00000" }, new object[] { 1.1, "7b3ff199999999999a" }, new object[] { 0.1, "7b3fb999999999999a" },
        new object[] { 16777217.0, "7d81808010" }, new object[] { 2147483648.0, "7c4f000000" },
        new object[] { 2147483649.0, "7b41e0000000200000" }, new object[] { 9007199254740992.0, "7c5a000000" },
        new object[] { 1e21, "7b444b1ae4d6e2ef50" }, new object[] { 1.7976931348623157e308, "7b7fefffffffffffff" },
        new object[] { 2147483647.0, "7dbfffffff0f" }, new object[] { -2147483647.0, "7dffffffff0f" },
        new object[] { -2147483648.0, "7ccf000000" }, new object[] { -0.0, "7d40" },
        new object[] { double.NaN, "7b7ff8000000000000" }, new object[] { double.PositiveInfinity, "7c7f800000" },
        new object[] { double.NegativeInfinity, "7cff800000" }, new object[] { 3.14, "7b40091eb851eb851f" },
        new object[] { 100000.5, "7c47c35040" }, new object[] { 8192.0, "7d808001" },
    };

    [Theory, Covers("Q01", "Q02", "Q03", "Q04", "ENC-06", "ENC-72")]
    [MemberData(nameof(NumberTable))]
    public void Number_TagIsChosenFromTheValue(double value, string expected) => Assert.Equal(expected, Encode(value));

    [Fact, Covers("Q04")]
    public void Number_AnyNaNPattern_IsWrittenAsTheV8Pattern()
    {
        var weird = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000001UL));
        Assert.Equal("7b7ff8000000000000", Encode(weird));
    }

    // ---- writer: the other tags ----

    [Fact, Covers("ENC-07")]
    public void Scalars_UseTheirOwnTags()
    {
        Assert.Equal("7f", Encode(Undef.Value));
        Assert.Equal("7e", Encode(null));
        Assert.Equal("79", Encode(false));
        Assert.Equal("78", Encode(true));
        Assert.Equal("77026162", Encode("ab"));
        Assert.Equal("7700", Encode(""));
        Assert.Equal("7403010203", Encode(new byte[] { 1, 2, 3 }));
        Assert.Equal("7a7fffffffffffffff", Encode(long.MaxValue));
        Assert.Equal("7a0000000000000005", Encode(5L));
    }

    [Fact, Covers("ENC-07")]
    public void Containers_AreCountPrefixed()
    {
        Assert.Equal("7502 7d01 770161".Replace(" ", ""), Encode(new object?[] { 1.0, "a" }));
        Assert.Equal("7500", Encode(Array.Empty<object?>()));
        Assert.Equal("7602 0161 7d01 0162 7500".Replace(" ", ""), Encode(new Obj(P("a", 1.0), P("b", Array.Empty<object?>()))));
        Assert.Equal("7601017575017f", Encode(new Obj(P("u", new object?[] { Undef.Value }))));   // undefined members are kept
    }

    [Fact, Covers("Q08")]
    public void ObjectKeys_ArrayIndexKeysFirstThenInsertionOrder()
    {
        var members = new[] { "b", "2", "a", "1", "01", "4294967294", "4294967295" }
            .Select((k, i) => P(k, (double)(i + 1))).ToArray();
        const string expected = "760701317d0401327d020a343239343936373239347d0601627d0101617d030230317d050a343239343936373239357d07";
        Assert.Equal(expected, Encode(new Obj(members)));
    }

    // ---- reader ----

    [Theory, Covers("ENC-07", "Q01")]
    [InlineData("7f", "undefined")]
    [InlineData("7e", "null")]
    [InlineData("79", "false")]
    [InlineData("78", "true")]
    [InlineData("7d01", "1")]
    [InlineData("7d41", "-1")]
    [InlineData("7d40", "-0")]
    [InlineData("7c3fc00000", "1.5")]
    [InlineData("7b3ff0000000000000", "1")]                       // a foreign float64 tag for 1.0
    [InlineData("7c40a00000", "5")]                               // a foreign float32 tag for 5.0
    [InlineData("7a0000000000000005", "5n")]
    [InlineData("77026162", "\"ab\"")]
    [InlineData("7403010203", "bytes:010203")]
    [InlineData("7500", "[]")]
    [InlineData("7600", "{}")]
    [InlineData("7502 7d01 770161", "[1,\"a\"]")]
    [InlineData("7602 0161 7d01 0162 7501 7501 7e", "{a:1,b:[[null]]}")]
    public void Reader_DecodesEveryTag(string hex, string expected)
    {
        var (error, text) = Decode(hex);
        Assert.Equal(YError.None, error);
        Assert.Equal(expected, text);
    }

    [Fact, Covers("Q04")]
    public void Reader_AnyNaN_Decodes()
    {
        var r = new Lib0Reader(Hex.Bytes("7bfff8000000000001"));
        var sink = new TextSink();
        Assert.Equal(YError.None, AnyReader.Read(ref r, ref sink, 128));
        Assert.Equal("NaN", sink.Text.ToString());
    }

    [Theory, Covers("ENC-07", "Q15")]
    [InlineData("")]                      // EOF in the tag position
    [InlineData("73")]                    // tag below 116
    [InlineData("80")]                    // tag above 127
    [InlineData("7d")]                    // JS yields 0 for a VarInt at EOF; y-sharp does not
    [InlineData("7c3f c0")]
    [InlineData("7b3f f0 00")]
    [InlineData("7a00 00")]
    [InlineData("7705 6162")]
    [InlineData("7405 01")]
    [InlineData("7502 7d01")]             // array shorter than its count
    [InlineData("7602 0161 7d01")]        // object shorter than its count
    [InlineData("7601 0161")]             // key without a value
    [InlineData("7601 01ff 7d01")]        // key is invalid UTF-8
    [InlineData("77 01 ff")]
    public void Reader_RejectsTruncationAndGarbageAsMalformed(string hex)
    {
        var (error, _) = Decode(hex, requireEnd: false);
        Assert.Equal(YError.Malformed, error);
    }

    [Theory, Covers("ENC-07", "Q15")]
    [InlineData("75ffffff7f")]            // an array claiming 2^28 elements with no bytes behind it
    [InlineData("76ffffff7f")]
    [InlineData("75ffffffffffffffff0f")]  // claim beyond 2^53
    public void Reader_ChecksAContainerClaimAgainstTheBytesLeft_BeforeReservingAnything(string hex)
    {
        var (error, text) = Decode(hex, requireEnd: false);
        Assert.Equal(YError.Malformed, error);
        Assert.Equal("", text);                // the sink was never asked to begin the container
    }

    [Fact, Covers("ENC-07")]
    public void Reader_DuplicateKeysAndProtoKey_AreTheSinksBusiness_AndAllMembersAreDelivered()
    {
        var (error, text) = Decode("7603 0161 7d01 0162 7d02 0161 7d03");
        Assert.Equal(YError.None, error);
        Assert.Equal("{a:1,b:2,a:3}", text);
    }

    [Fact, Covers("ENC-07")]
    public void Reader_TrailingBytes_AreLeftForTheCaller()
    {
        var r = new Lib0Reader(Hex.Bytes("7d01 ff"));
        var sink = new TextSink();
        Assert.Equal(YError.None, AnyReader.Read(ref r, ref sink, 128));
        Assert.Equal(1, r.Remaining);
    }

    // ---- depth and string limits (ADR 0005) ----

    private static string Nested(int depth) => string.Concat(Enumerable.Repeat("7501", depth)) + "7e";

    [Fact, Covers("ENC-07")]
    public void Depth_EqualToTheLimitIsAccepted_OneMoreIsLimitExceeded()
    {
        Assert.Equal(YError.None, Decode(Nested(3), maxDepth: 3).Error);
        Assert.Equal(YError.LimitExceeded, Decode(Nested(4), maxDepth: 3, requireEnd: false).Error);
        Assert.Equal(YError.None, Decode("7e", maxDepth: 0).Error);          // a scalar leaf adds no depth
        Assert.Equal(YError.LimitExceeded, Decode("7500", maxDepth: 0, requireEnd: false).Error);
    }

    [Fact, Covers("ENC-07")]
    public void Depth_AnObjectCountsLikeAnArray()
    {
        Assert.Equal(YError.None, Decode("7601 0161 7501 7e", maxDepth: 2).Error);
        Assert.Equal(YError.LimitExceeded, Decode("7601 0161 7501 7e", maxDepth: 1, requireEnd: false).Error);
    }

    [Fact, Covers("ENC-07")]
    public void Depth_AHundredThousandNestedArrays_IsRejectedWithoutRecursing()
    {
        var hex = Nested(100_000);
        var r = new Lib0Reader(Hex.Bytes(hex));
        var sink = new TextSink();
        Assert.Equal(YError.LimitExceeded, AnyReader.Read(ref r, ref sink, 128));
        Assert.True(sink.MaxOpen <= 128);
    }

    [Fact, Covers("ENC-07")]
    public void Depth_VeryDeepInputWithAHighLimit_IsDecodedIteratively()
    {
        var hex = Nested(50_000);
        var r = new Lib0Reader(Hex.Bytes(hex));
        var sink = new TextSink();
        Assert.Equal(YError.None, AnyReader.Read(ref r, ref sink, 100_000));
        Assert.Equal(50_000, sink.MaxOpen);
    }

    [Fact, Covers("ENC-07")]
    public void StringLimit_AppliesToKeysAndValues()
    {
        Assert.Equal(YError.LimitExceeded, Decode("7705 6162636465", maxStringBytes: 4).Error);
        Assert.Equal(YError.LimitExceeded, Decode("7601 05 6162636465 7e", maxStringBytes: 4, requireEnd: false).Error);
        Assert.Equal(YError.None, Decode("7704 61626364", maxStringBytes: 4).Error);
    }

    // ---- round trips and buffers ----

    [Fact, Covers("ENC-07", "Q01")]
    public void RoundTrip_ADocumentOfEveryKind_ReEncodesToTheSameBytes()
    {
        var value = new Obj(
            P("3", 3.0), P("n", null), P("u", Undef.Value), P("t", true), P("f", false), P("s", "é😀"),
            P("big", long.MinValue), P("d", 0.1), P("z", -0.0), P("bin", new byte[] { 0, 255 }),
            P("arr", new object?[] { 1.0, new object?[] { }, new Obj() }));
        var hex = Encode(value);
        var (error, text) = Decode(hex);
        Assert.Equal(YError.None, error);
        Assert.Equal("{3:3,n:null,u:undefined,t:true,f:false,s:\"é😀\",big:-9223372036854775808n,d:0.1,z:-0,bin:bytes:00ff,arr:[1,[],{}]}", text);
    }

    [Fact, Covers("ENC-07")]
    public void Reader_ReturnsEveryRentedBufferOnEveryExitPath()
    {
        var before = BufferPool.Outstanding;
        foreach (var hex in new[] { Nested(1000), Nested(1000)[..^2], "7600", "7603 0161 7d01", "75ffffff7f", "73", Nested(300) })
        {
            var r = new Lib0Reader(Hex.Bytes(hex));
            var sink = new TextSink();
            _ = AnyReader.Read(ref r, ref sink, 200);
        }
        Assert.Equal(before, BufferPool.Outstanding);
    }

    // ---- JS key order ----

    [Theory, Covers("Q08")]
    [InlineData("0", true, 0u)]
    [InlineData("7", true, 7u)]
    [InlineData("4294967294", true, 4294967294u)]
    [InlineData("4294967295", false, 0u)]
    [InlineData("01", false, 0u)]
    [InlineData("-1", false, 0u)]
    [InlineData("1.5", false, 0u)]
    [InlineData("", false, 0u)]
    [InlineData("a1", false, 0u)]
    [InlineData("99999999999999999999", false, 0u)]
    public void JsKeyOrder_RecognisesCanonicalArrayIndices(string key, bool isIndex, uint index)
    {
        Assert.Equal(isIndex, JsKeyOrder.IsArrayIndex(key, out var got));
        if (isIndex) Assert.Equal(index, got);
    }

    [Fact, Covers("Q08")]
    public void JsKeyOrder_SortsIndexKeysAscendingThenKeepsTheRestInOrder()
    {
        var keys = new[] { "b", "10", "a", "9", "01", "c" };
        var order = new int[keys.Length];
        JsKeyOrder.Order(keys, order);
        Assert.Equal(new[] { "9", "10", "b", "a", "01", "c" }, order.Select(i => keys[i]).ToArray());
    }
}
