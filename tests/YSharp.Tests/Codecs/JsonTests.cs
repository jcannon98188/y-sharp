using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Codecs;

/// <summary>
/// The JS-compatible JSON writer and reader (ADR 0007, docs/research/json.md; Q11, Q28, Q30). The expected texts are what V8's
/// <c>JSON.stringify</c> and <c>JSON.parse</c> do, recorded in the research note from the pinned Node run.
/// </summary>
[Requires(Feature.Json)]
public sealed class JsonTests
{
    private static YStatus TryWrite(in YValue v, out string json)
    {
        var w = new ArrayBufferWriter<byte>();
        var status = v.TryWriteJson(w);
        json = Encoding.UTF8.GetString(w.WrittenSpan);
        return status;
    }

    private static string Json(YValue v)
    {
        var status = TryWrite(v, out var json);
        Assert.True(status.Ok, $"TryWriteJson failed with {status.Error}");
        return json;
    }

    private static YStatus TryParse(string text, out YValue value) =>
        YValue.TryParseJson(Encoding.UTF8.GetBytes(text), null, out value);

    private static YValue Parse(string text)
    {
        var status = TryParse(text, out var v);
        Assert.True(status.Ok, $"'{text}' was rejected with {status.Error}");
        return v;
    }

    private static YValue Obj(params (string Key, YValue Value)[] members) =>
        YValue.Object(members.Select(m => new KeyValuePair<string, YValue>(m.Key, m.Value)).ToArray());

    public static IEnumerable<object[]> NumberText() => new[]
    {
        new object[] { 1e20, "100000000000000000000" }, new object[] { 1e21, "1e+21" }, new object[] { 1.5e21, "1.5e+21" },
        new object[] { 123456789012345680000.0, "123456789012345680000" }, new object[] { 1e-6, "0.000001" },
        new object[] { 1.5e-6, "0.0000015" }, new object[] { 1e-7, "1e-7" }, new object[] { 1.234e-7, "1.234e-7" },
        new object[] { 5e-324, "5e-324" }, new object[] { 1.7976931348623157e308, "1.7976931348623157e+308" },
        new object[] { 9007199254740992.0, "9007199254740992" }, new object[] { 9007199254740994.0, "9007199254740994" },
        new object[] { 0.1 + 0.2, "0.30000000000000004" }, new object[] { 1.0 / 3, "0.3333333333333333" },
        new object[] { 4.35, "4.35" }, new object[] { 1e23, "1e+23" }, new object[] { 8.41e21, "8.41e+21" },
        new object[] { -1.5, "-1.5" }, new object[] { 0.0, "0" }, new object[] { 100.0, "100" },
    };

    [Theory, Covers("Q28", "Q30")]
    [MemberData(nameof(NumberText))]
    public void Q28_NumberToString_FollowsTheEcmaLayout(double value, string expected) =>
        Assert.Equal(expected, Json(value));

    [Fact, Covers("Q28", "Q30")]
    public void Q28_NegativeZero_NaN_AndInfinity_WriteAsZeroAndNull()
    {
        Assert.Equal("0", Json(-0.0));
        Assert.Equal("null", Json(double.NaN));
        Assert.Equal("null", Json(double.PositiveInfinity));
        Assert.Equal("null", Json(double.NegativeInfinity));
        Assert.Equal("[null,0,null]", Json(YValue.Array(new YValue[] { double.NaN, -0.0, double.PositiveInfinity })));
    }

    [Fact, Covers("Q28")]
    [Requires(Feature.Xml)]
    public void Q28_XmlAttributeText_UsesTheSameNumberFormatter()
    {
        var doc = Wire.NewDoc();
        var fragment = doc.GetXmlFragment("x");
        var p = new YXmlElement("p");
        fragment.Push(new YType[] { p });
        foreach (var (name, value) in new (string, YValue)[]
        {
            ("a", 0.1 + 0.2), ("b", 1e21), ("c", 1e-7), ("d", -0.0), ("g", YValue.Null), ("h", YValue.Undefined),
            ("i", double.NaN), ("j", true), ("l", 123456789012345680000.0), ("m", 5e-324), ("n", 9007199254740994.0), ("r", 1),
        })
            p.SetAttribute(name, value);
        Assert.Equal(
            "<p a=\"0.30000000000000004\" b=\"1e+21\" c=\"1e-7\" d=\"0\" g=\"null\" h=\"undefined\" i=\"NaN\" j=\"true\" l=\"123456789012345680000\" m=\"5e-324\" n=\"9007199254740994\" r=\"1\"></p>",
            p.ToString());
    }

    [Theory, Covers("Q30")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a\\b", "\"a\\\\b\"")]
    [InlineData("\b\t\n\f\r", "\"\\b\\t\\n\\f\\r\"")]
    [InlineData("\u0000\u000b\u001f", "\"\\u0000\\u000b\\u001f\"")]
    [InlineData("/<>&'", "\"/<>&'\"")]
    [InlineData("\u007f\u0080\u009f\u00a0", "\"\u007f\u0080\u009f\u00a0\"")]
    [InlineData("\u2028\u2029\ufeff", "\"\u2028\u2029\ufeff\"")]
    [InlineData("\u00e9\u20ac\ud83d\ude00", "\"\u00e9\u20ac\ud83d\ude00\"")]
    [InlineData("\ud800", "\"\\ud800\"")]
    [InlineData("\udc00\ud800", "\"\\udc00\\ud800\"")]
    [InlineData("\ud800\ud83d\ude00", "\"\\ud800\ud83d\ude00\"")]
    [InlineData("\udfffa", "\"\\udfffa\"")]
    public void Q30_StringEscaping_MatchesQuoteJsonString(string value, string expected) =>
        Assert.Equal(expected, Json(value));

    [Fact, Covers("Q30", "Q08")]
    public void Q30_ObjectsWriteArrayIndexKeysFirst_AndDuplicatesCollapse()
    {
        Assert.Equal("{\"1\":4,\"2\":2,\"b\":1,\"a\":3}", Json(Obj(("b", 1), ("2", 2), ("a", 3), ("1", 4))));
        Assert.Equal("{\"1000\":1,\"4294967294\":1,\"a\":1,\"4294967295\":1,\"01\":1,\"-1\":1,\"1.5\":1,\"1e3\":1}",
            Json(Obj(("a", 1), ("4294967294", 1), ("4294967295", 1), ("01", 1), ("-1", 1), ("1.5", 1), ("1e3", 1), ("1000", 1))));
        Assert.Equal("{\"9\":1,\"10\":1,\"z\":1,\"\":1,\" \":1}", Json(Obj(("z", 1), ("10", 1), ("9", 1), ("", 1), (" ", 1))));
        Assert.Equal("{\"a\":3,\"b\":2}", Json(Obj(("a", 1), ("b", 2), ("a", 3))));   // last value at the first position
        Assert.Equal("{\"a\\\"b\\n\\ud800\":1}", Json(Obj(("a\"b\n\ud800", 1))));
    }

    [Fact, Covers("Q30", "Q11")]
    public void Q30_UndefinedAndBigInt_FollowStringifyRules()
    {
        // top-level Undefined has no JSON text and BigInt throws at any depth, so both are errors (ADR 0007)
        Assert.False(TryWrite(YValue.Undefined, out _).Ok);
        Assert.False(TryWrite(YValue.BigInt(1), out _).Ok);
        Assert.False(TryWrite(Obj(("b", YValue.BigInt(1))), out _).Ok);
        Assert.False(TryWrite(YValue.Array(new[] { YValue.BigInt(1) }), out _).Ok);
        // nested Undefined: an object member vanishes (without a stray comma) and an array element becomes null
        Assert.Equal("{\"k\":1}", Json(Obj(("u", YValue.Undefined), ("k", 1))));
        Assert.Equal("{\"a\":[null]}", Json(Obj(("u", YValue.Undefined), ("a", YValue.Array(new[] { YValue.Undefined })))));
        Assert.Equal("[1,\"x\",null,false,{},[]]", Json(YValue.Array(new YValue[]
        {
            1, "x", YValue.Null, false, Obj(), YValue.Array(Array.Empty<YValue>()),
        })));
    }

    [Fact, Covers("Q30")]
    public void Q30_Bytes_WriteAsAnIndexKeyedObject()=> Assert.Equal("{\"0\":1,\"1\":2}", Json(YValue.Bytes(new byte[] { 1, 2 })));

    [Theory, Covers("Q30")]
    [InlineData("null"), InlineData("true"), InlineData("\"a\""), InlineData("1"), InlineData("[]"), InlineData("{}")]
    [InlineData(" \t\n\r1 "), InlineData("\"\\/\""), InlineData("\"\\ud800\""), InlineData("\"\\udc00\\ud800\"")]
    [InlineData("-0"), InlineData("0e0"), InlineData("1E5"), InlineData("1.0e+5"), InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"__proto__\":5}"), InlineData("\"\u2028\u007f\u00a0\"")]
    public void Q30_Parse_AcceptsTheJsonGrammar(string text)=> Assert.True(TryParse(text, out _).Ok);

    [Theory, Covers("Q30")]
    [InlineData("01"), InlineData("00"), InlineData("-01"), InlineData("+1"), InlineData(".5"), InlineData("-.5"), InlineData("1."), InlineData("1e")]
    [InlineData("1e+"), InlineData("-"), InlineData("--1"), InlineData("0x10"), InlineData("NaN"), InlineData("Infinity"), InlineData("-Infinity")]
    [InlineData("undefined"), InlineData("NULL"), InlineData("True"), InlineData("nul"), InlineData("tru"), InlineData("'a'")]
    [InlineData("[1,]"), InlineData("{\"a\":1,}"), InlineData("[,1]"), InlineData("{,}"), InlineData("{a:1}"), InlineData("{1:2}")]
    [InlineData("{\"a\"}"), InlineData("{\"a\":}"), InlineData("[1 2]"), InlineData("{\"a\":1 \"b\":2}"), InlineData("[1,2"), InlineData("\"abc")]
    [InlineData("[1]x"), InlineData("1 2"), InlineData("\"a\" \"b\""), InlineData("{\"a\":1}}"), InlineData(""), InlineData("   ")]
    [InlineData("// c\n1"), InlineData("/*c*/1"), InlineData("\u00a01"), InlineData("\ufeff1"), InlineData("\u000b1"), InlineData("\u20281")]
    [InlineData("\"\t\""), InlineData("\"\n\""), InlineData("\"\\x41\""), InlineData("\"\\a\""), InlineData("\"\\U0041\""), InlineData("\"\\u00\""), InlineData("\"\\u004g\"")]
    public void Q30_Parse_RejectsWhatJsonParseRejects(string text)
    {
        var status = TryParse(text, out _);
        Assert.False(status.Ok);
        Assert.Equal(YError.Malformed, status.Error);
    }

    [Fact, Covers("Q30", "Q28")]
    public void Q30_ParseNumbers_AreCorrectlyRoundedDoubles()
    {
        Assert.Equal(double.PositiveInfinity, Parse("1e400").AsNumber());
        Assert.Equal(double.NegativeInfinity, Parse("-1e400").AsNumber());
        Assert.Equal(0.0, Parse("1e-400").AsNumber());
        Assert.Equal(5e-324, Parse("2.4703282292062328e-324").AsNumber());
        Assert.Equal(0.0, Parse("2.4703282292062327e-324").AsNumber());
        Assert.Equal(double.PositiveInfinity, Parse("1.7976931348623159e308").AsNumber());
        Assert.Equal(9007199254740992.0, Parse("9007199254740993").AsNumber());   // ties to even
        Assert.Equal(1.2345678901234568e+29, Parse("123456789012345678901234567890").AsNumber());
        Assert.True(double.IsNegative(Parse("-0").AsNumber()));                   // parse keeps -0, stringify drops it
        Assert.True(double.IsNegative(Parse("-0e0").AsNumber()));
        Assert.Equal(1.0, Parse("1e0").AsNumber());
        Assert.Equal(100.0, Parse("1E+2").AsNumber());
        Assert.Equal(100000.0, Parse("1.0e+5").AsNumber());
    }

    [Fact, Covers("Q30")]
    public void Q30_ParseStrings_KeepLoneSurrogates_AndObjectsKeepFirstPositionLastValue()
    {
        Assert.Equal("\ud800", Parse("\"\\ud800\"").AsString());
        Assert.Equal("\udc00\ud800", Parse("\"\\udc00\\ud800\"").AsString());
        Assert.Equal("/", Parse("\"\\/\"").AsString());
        Assert.Equal("\ud800\ud83d\ude00", Parse("\"\\ud800\\ud83d\\ude00\"").AsString());
        Assert.True(Parse("{\"a\":1,\"b\":2,\"a\":3}").TryGetObject(out var o));
        Assert.Equal(2, o.Count);
        Assert.Equal("a", o[0].Key);
        Assert.Equal(3.0, o[0].Value.AsNumber());
        // JSON.parse creates __proto__ as an own property, so the key stays
        Assert.True(Parse("{\"__proto__\":{\"x\":1},\"y\":2}").TryGetObject(out var p));
        Assert.True(p.TryGetValue("__proto__", out _));
        Assert.Equal(2, p.Count);
    }

    [Fact, Covers("Q30")]
    public void Q30_ParseDepth_IsLimitedByYLimitsAndNeverOverflowsTheStack()
    {
        var limits = new YLimits { MaxAnyDepth = 4 };
        var ok = YValue.TryParseJson(Encoding.UTF8.GetBytes("[[[[]]]]"), limits, out _);
        var tooDeep = YValue.TryParseJson(Encoding.UTF8.GetBytes("[[[[[]]]]]"), limits, out _);
        Assert.True(ok.Ok);
        Assert.Equal(YError.LimitExceeded, tooDeep.Error);
        var huge = new string('[', 1_000_000) + new string(']', 1_000_000);
        Assert.False(YValue.TryParseJson(Encoding.UTF8.GetBytes(huge), null, out _).Ok);   // default cap, no recursion
    }

    [Fact, Covers("Q30")]
    public void Q30_WriteThenParse_RoundTripsExceptTheLossyCases()
    {
        var value = Obj(("s", "x\u00e9\ud83d\ude00"), ("n", YValue.Array(new YValue[] { 1, 2.5, YValue.Null, true })), ("o", Obj(("k", "v"))));
        Assert.Equal(value, Parse(Json(value)));
        Assert.Equal((YValue)0.0, Parse(Json(-0.0)));   // -0 is written as 0 and returns as +0
    }

    [Fact, Covers("Q11", "Q30", "ENC-12", "ENC-75")]
    [Requires(Feature.TextFormat, Feature.Text, Feature.V2)]
    public void Q11_V1Embeds_AreJsonText_AndV2Embeds_AreAny()
    {
        var embed = Obj(("n", double.NaN), ("i", double.NegativeInfinity), ("z", -0.0));
        var doc = Wire.NewDoc();
        doc.GetText("t").Insert(0, "ab");
        doc.GetText("t").InsertEmbed(1, embed);
        var v1 = Hex.Of(Wire.Encode(doc));
        var v2 = Hex.Of(Wire.Encode(doc, UpdateFormat.V2));
        Assert.Contains("7b226e223a6e756c6c2c2269223a6e756c6c2c227a223a307d", v1);   // {"n":null,"i":null,"z":0}
        Assert.Contains("7b7ff8000000000000", v2);                                   // V2 keeps NaN, -Infinity and -0 through Any
        Assert.Contains("7d40", v2);

        var peer = new Doc();
        peer.ApplyUpdate(Wire.Encode(doc));                                          // the V1 round trip is lossy: null, null, 0
        Assert.Equal("{\"n\":null,\"i\":null,\"z\":0}", Json(peer.GetText("t").ToDelta().Single(d => d.Embed.Value.Kind == YValueKind.Object).Embed.Value));
    }

    [Fact, Covers("Q11", "Q30")]
    [Requires(Feature.TextFormat, Feature.Text, Feature.V2)]
    public void Q11_V1Formats_NaNWritesNull_AndLoneSurrogatesSurviveInV1Only()
    {
        var doc = Wire.NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "ab");
        text.Format(0, 2, Obj(("gone", double.NaN), ("s", "\ud800")));
        var v1 = Hex.Of(Wire.Encode(doc));
        Assert.Contains("6e756c6c", v1);                    // null
        Assert.Contains("5c7564383030", v1);                // the escape text \ud800, not a replacement character
        var v2 = Hex.Of(Wire.Encode(doc, UpdateFormat.V2));
        Assert.Contains("efbfbd", v2);                      // V2 goes through UTF-8, so the lone surrogate is replaced (Q18)
    }

}
