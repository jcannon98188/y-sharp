using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace YSharp.TestKit.Values;

public enum TaggedKind { Undefined, Null, Bool, Number, BigInt, String, Bytes, Array, Object }

/// <summary>
/// A value of the ADR 0011 tagged JSON: what plain JSON cannot carry (<c>-0</c>, NaN, infinities, undefined, BigInt,
/// bytes, strings with lone surrogates, ordered objects). The nine kinds map one to one onto the nine <c>YValue</c> cases.
/// <para>
/// Wire format. Plain: <c>null</c>, booleans, finite numbers other than <c>-0</c>, strings without lone surrogates, arrays.
/// Tagged objects, always with a <c>"$"</c> member: <c>{"$":"undefined"}</c>, <c>{"$":"nan"}</c>, <c>{"$":"inf"}</c>,
/// <c>{"$":"-inf"}</c>, <c>{"$":"-0"}</c>, <c>{"$":"bigint","v":"decimal"}</c>, <c>{"$":"bytes","hex":"..."}</c>,
/// <c>{"$":"str","units":[utf16 code units]}</c>, and objects as ordered pairs <c>{"$":"obj","pairs":[[key,value],...]}</c>.
/// A JSON object without <c>"$"</c> is rejected.
/// </para>
/// </summary>
public sealed class TaggedValue
{
    private readonly object? _payload;
    private readonly double _number;

    private TaggedValue(TaggedKind kind, double number = 0, object? payload = null)
    {
        Kind = kind;
        _number = number;
        _payload = payload;
    }

    public TaggedKind Kind { get; }

    public static TaggedValue Undefined { get; } = new(TaggedKind.Undefined);
    public static TaggedValue Null { get; } = new(TaggedKind.Null);
    public static TaggedValue True { get; } = new(TaggedKind.Bool, 1);
    public static TaggedValue False { get; } = new(TaggedKind.Bool, 0);

    public static TaggedValue FromBool(bool v) => v ? True : False;
    public static TaggedValue FromNumber(double v) => new(TaggedKind.Number, v);
    public static TaggedValue BigInt(long v) => new(TaggedKind.BigInt, 0, v);
    public static TaggedValue FromString(string v) => new(TaggedKind.String, 0, v);
    public static TaggedValue FromBytes(byte[] v) => new(TaggedKind.Bytes, 0, v);
    public static TaggedValue FromArray(IEnumerable<TaggedValue> items) => new(TaggedKind.Array, 0, items.ToList());
    public static TaggedValue FromObject(IEnumerable<KeyValuePair<string, TaggedValue>> pairs) => new(TaggedKind.Object, 0, pairs.ToList());

    public bool Bool => Kind == TaggedKind.Bool ? _number != 0 : throw Wrong(TaggedKind.Bool);
    public double Number => Kind == TaggedKind.Number ? _number : throw Wrong(TaggedKind.Number);
    public long BigIntValue => Kind == TaggedKind.BigInt ? (long)_payload! : throw Wrong(TaggedKind.BigInt);
    public string String => Kind == TaggedKind.String ? (string)_payload! : throw Wrong(TaggedKind.String);
    public byte[] Bytes => Kind == TaggedKind.Bytes ? (byte[])_payload! : throw Wrong(TaggedKind.Bytes);
    public List<TaggedValue> Items => Kind == TaggedKind.Array ? (List<TaggedValue>)_payload! : throw Wrong(TaggedKind.Array);
    public List<KeyValuePair<string, TaggedValue>> Pairs => Kind == TaggedKind.Object ? (List<KeyValuePair<string, TaggedValue>>)_payload! : throw Wrong(TaggedKind.Object);

    private InvalidOperationException Wrong(TaggedKind wanted) => new($"Tagged value is {Kind}, not {wanted}.");

    // ------------------------------------------------------------------ reading

    public static TaggedValue Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Read(doc.RootElement);
    }

    /// <exception cref="FormatException">The element is not valid tagged JSON.</exception>
    public static TaggedValue Read(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Null: return Null;
            case JsonValueKind.True: return True;
            case JsonValueKind.False: return False;
            case JsonValueKind.String: return FromString(e.GetString()!);
            case JsonValueKind.Number:
                var d = e.GetDouble();
                if (d == 0 && double.IsNegative(d)) throw new FormatException("Plain -0 is not allowed; use {\"$\":\"-0\"}.");
                return FromNumber(d);
            case JsonValueKind.Array:
                return FromArray(e.EnumerateArray().Select(Read));
            case JsonValueKind.Object:
                return ReadTagged(e);
            default:
                throw new FormatException($"Unexpected JSON {e.ValueKind}.");
        }
    }

    private static TaggedValue ReadTagged(JsonElement e)
    {
        if (!e.TryGetProperty("$", out var tagEl) || tagEl.ValueKind != JsonValueKind.String)
            throw new FormatException("Objects must be tagged with \"$\"; plain objects are ordered pair lists.");
        var tag = tagEl.GetString();
        var members = e.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        void Only(params string[] allowed)
        {
            members.Remove("$");
            foreach (var m in members)
                if (Array.IndexOf(allowed, m) < 0) throw new FormatException($"Unknown member '{m}' on tag '{tag}'.");
            foreach (var a in allowed)
                if (!members.Contains(a)) throw new FormatException($"Tag '{tag}' needs member '{a}'.");
        }

        switch (tag)
        {
            case "undefined": Only(); return Undefined;
            case "nan": Only(); return FromNumber(double.NaN);
            case "inf": Only(); return FromNumber(double.PositiveInfinity);
            case "-inf": Only(); return FromNumber(double.NegativeInfinity);
            case "-0": Only(); return FromNumber(-0.0);
            case "bigint":
                Only("v");
                if (e.GetProperty("v").ValueKind != JsonValueKind.String
                    || !long.TryParse(e.GetProperty("v").GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var big))
                    throw new FormatException("bigint 'v' must be a decimal string that fits in 64 bits.");
                return BigInt(big);
            case "bytes":
                Only("hex");
                try { return FromBytes(Convert.FromHexString(e.GetProperty("hex").GetString()!)); }
                catch (Exception ex) when (ex is FormatException or InvalidOperationException) { throw new FormatException("bytes 'hex' is not valid hex.", ex); }
            case "str":
                Only("units");
                var sb = new StringBuilder();
                foreach (var u in e.GetProperty("units").EnumerateArray())
                {
                    if (!u.TryGetInt32(out var unit) || unit < 0 || unit > 0xFFFF) throw new FormatException("str 'units' must be UTF-16 code units.");
                    sb.Append((char)unit);
                }
                return FromString(sb.ToString());
            case "obj":
                Only("pairs");
                var pairs = new List<KeyValuePair<string, TaggedValue>>();
                foreach (var p in e.GetProperty("pairs").EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() != 2 || p[0].ValueKind != JsonValueKind.String)
                        throw new FormatException("obj 'pairs' entries must be [key, value].");
                    pairs.Add(new(p[0].GetString()!, Read(p[1])));
                }
                return FromObject(pairs);
            default:
                throw new FormatException($"Unknown tag '{tag}'.");
        }
    }

    // ------------------------------------------------------------------ writing

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            Write(w);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public void Write(Utf8JsonWriter w)
    {
        switch (Kind)
        {
            case TaggedKind.Undefined: Tag(w, "undefined"); break;
            case TaggedKind.Null: w.WriteNullValue(); break;
            case TaggedKind.Bool: w.WriteBooleanValue(_number != 0); break;
            case TaggedKind.Number:
                if (double.IsNaN(_number)) Tag(w, "nan");
                else if (double.IsPositiveInfinity(_number)) Tag(w, "inf");
                else if (double.IsNegativeInfinity(_number)) Tag(w, "-inf");
                else if (_number == 0 && double.IsNegative(_number)) Tag(w, "-0");
                else w.WriteNumberValue(_number);
                break;
            case TaggedKind.BigInt:
                w.WriteStartObject();
                w.WriteString("$", "bigint");
                w.WriteString("v", ((long)_payload!).ToString(CultureInfo.InvariantCulture));
                w.WriteEndObject();
                break;
            case TaggedKind.String:
                var s = (string)_payload!;
                if (HasLoneSurrogate(s))
                {
                    w.WriteStartObject();
                    w.WriteString("$", "str");
                    w.WriteStartArray("units");
                    foreach (var c in s) w.WriteNumberValue((int)c);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                else w.WriteStringValue(s);
                break;
            case TaggedKind.Bytes:
                w.WriteStartObject();
                w.WriteString("$", "bytes");
                w.WriteString("hex", Convert.ToHexString((byte[])_payload!).ToLowerInvariant());
                w.WriteEndObject();
                break;
            case TaggedKind.Array:
                w.WriteStartArray();
                foreach (var i in Items) i.Write(w);
                w.WriteEndArray();
                break;
            case TaggedKind.Object:
                w.WriteStartObject();
                w.WriteString("$", "obj");
                w.WriteStartArray("pairs");
                foreach (var p in Pairs)
                {
                    w.WriteStartArray();
                    w.WriteStringValue(p.Key);
                    p.Value.Write(w);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                break;
        }
    }

    private static void Tag(Utf8JsonWriter w, string tag)
    {
        w.WriteStartObject();
        w.WriteString("$", tag);
        w.WriteEndObject();
    }

    private static bool HasLoneSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
                return true;
            }
            if (char.IsLowSurrogate(s[i])) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ comparison and conversion

    /// <summary>Wire equality (ADR 0007): NaN equals NaN, <c>+0</c> differs from <c>-0</c>, object pair order matters.</summary>
    public bool WireEquals(TaggedValue other)
    {
        if (Kind != other.Kind) return false;
        switch (Kind)
        {
            case TaggedKind.Undefined:
            case TaggedKind.Null: return true;
            case TaggedKind.Bool: return _number == other._number;
            case TaggedKind.Number: return BitConverter.DoubleToInt64Bits(_number) == BitConverter.DoubleToInt64Bits(other._number)
                                           || (double.IsNaN(_number) && double.IsNaN(other._number));
            case TaggedKind.BigInt: return (long)_payload! == (long)other._payload!;
            case TaggedKind.String: return string.Equals((string)_payload!, (string)other._payload!, StringComparison.Ordinal);
            case TaggedKind.Bytes: return ((byte[])_payload!).AsSpan().SequenceEqual((byte[])other._payload!);
            case TaggedKind.Array:
                return Items.Count == other.Items.Count && Items.Zip(other.Items, (a, b) => a.WireEquals(b)).All(x => x);
            default:
                return Pairs.Count == other.Pairs.Count
                       && Pairs.Zip(other.Pairs, (a, b) => a.Key == b.Key && a.Value.WireEquals(b.Value)).All(x => x);
        }
    }

    /// <summary>Converts to the library value. Fails with the stub's exception until <c>YValue</c> is implemented.</summary>
    public YValue ToYValue()
    {
        switch (Kind)
        {
            case TaggedKind.Undefined: return YValue.Undefined;
            case TaggedKind.Null: return YValue.Null;
            case TaggedKind.Bool: return _number != 0;
            case TaggedKind.Number: return _number;
            case TaggedKind.BigInt: return YValue.BigInt((long)_payload!);
            case TaggedKind.String: return (string)_payload!;
            case TaggedKind.Bytes: return YValue.Bytes((byte[])_payload!);
            case TaggedKind.Array: return YValue.Array(Items.Select(i => i.ToYValue()).ToArray());
            default: return YValue.Object(Pairs.Select(p => new KeyValuePair<string, YValue>(p.Key, p.Value.ToYValue())).ToArray());
        }
    }
}
