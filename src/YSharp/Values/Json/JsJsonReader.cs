#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace YSharp;

/// <summary>
/// <c>JSON.parse(text)</c> over UTF-8 (docs/research/json.md section 5): strict ECMA-404, whitespace only U+0020/09/0A/0D, numbers as correctly
/// rounded doubles, lone surrogate escapes kept, duplicate keys last value at the first position, <c>__proto__</c> an ordinary key. Iterative,
/// capped by <see cref="YLimits.MaxAnyDepth"/> (LimitExceeded) and <see cref="YLimits.MaxStringBytes"/>; anything else wrong is Malformed.
/// </summary>
internal static class JsJsonReader
{
    private static readonly UTF8Encoding Strict = new(false, true);

    private sealed class Frame
    {
        public bool IsObject;
        public List<YValue>? Items;
        public List<KeyValuePair<string, YValue>>? Members;
        public string? Key;
    }

    public static YStatus Read(ReadOnlySpan<byte> s, YLimits? limits, out YValue value)
    {
        value = default;
        limits ??= YLimits.Default;
        var p = 0;
        var stack = new List<Frame>();
        while (true)
        {
            // ---- a value is expected at p
            SkipWs(s, ref p);
            if (p >= s.Length) return Bad();
            YValue v;
            var c = s[p];
            if (c == '[' || c == '{')
            {
                if (stack.Count >= limits.MaxAnyDepth) return new YStatus(YError.LimitExceeded);
                p++;
                var isObject = c == '{';
                SkipWs(s, ref p);
                if (p >= s.Length) return Bad();
                if (s[p] == (isObject ? '}' : ']'))
                {
                    p++;
                    v = isObject ? YValue.FromObjectOwned(YObjectData.Dedupe(Array.Empty<KeyValuePair<string, YValue>>())) : YValue.FromArrayOwned(Array.Empty<YValue>());
                }
                else
                {
                    var frame = new Frame { IsObject = isObject };
                    if (isObject)
                    {
                        frame.Members = new List<KeyValuePair<string, YValue>>();
                        var st = ReadKey(s, ref p, limits, out frame.Key);
                        if (!st.Ok) return st;
                    }
                    else frame.Items = new List<YValue>();
                    stack.Add(frame);
                    continue;
                }
            }
            else
            {
                var st = ReadScalar(s, ref p, limits, out v);
                if (!st.Ok) return st;
            }

            // ---- v is complete: attach it, and close every container it finishes
            while (true)
            {
                if (stack.Count == 0)
                {
                    SkipWs(s, ref p);
                    if (p != s.Length) return Bad();
                    value = v;
                    return default;
                }
                var f = stack[^1];
                if (f.IsObject) f.Members!.Add(new KeyValuePair<string, YValue>(f.Key!, v));
                else f.Items!.Add(v);
                SkipWs(s, ref p);
                if (p >= s.Length) return Bad();
                var d = s[p++];
                if (d == ',')
                {
                    if (f.IsObject)
                    {
                        var st = ReadKey(s, ref p, limits, out f.Key);
                        if (!st.Ok) return st;
                    }
                    break;   // read the next element
                }
                if (d != (f.IsObject ? '}' : ']')) return Bad();
                stack.RemoveAt(stack.Count - 1);
                v = f.IsObject
                    ? YValue.FromObjectOwned(YObjectData.Dedupe(f.Members!.ToArray()))
                    : YValue.FromArrayOwned(f.Items!.ToArray());
            }
        }
    }

    private static YStatus Bad() => new(YError.Malformed);

    private static void SkipWs(ReadOnlySpan<byte> s, ref int p)
    {
        while (p < s.Length && (s[p] == ' ' || s[p] == '\t' || s[p] == '\n' || s[p] == '\r')) p++;
    }

    /// <summary>Reads <c>"key" :</c>, leaving p at the start of the value.</summary>
    private static YStatus ReadKey(ReadOnlySpan<byte> s, ref int p, YLimits limits, out string? key)
    {
        key = null;
        SkipWs(s, ref p);
        if (p >= s.Length || s[p] != '"') return Bad();
        var st = ReadString(s, ref p, limits, out var k);
        if (!st.Ok) return st;
        SkipWs(s, ref p);
        if (p >= s.Length || s[p] != ':') return Bad();
        p++;
        key = k;
        return default;
    }

    private static YStatus ReadScalar(ReadOnlySpan<byte> s, ref int p, YLimits limits, out YValue v)
    {
        v = default;
        var c = s[p];
        if (c == '"')
        {
            var st = ReadString(s, ref p, limits, out var str);
            if (st.Ok) v = str;
            return st;
        }
        if (c == 't') return Literal(s, ref p, "true", YValue.FromBool(true), out v);
        if (c == 'f') return Literal(s, ref p, "false", YValue.FromBool(false), out v);
        if (c == 'n') return Literal(s, ref p, "null", YValue.Null, out v);
        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber(s, ref p, out v);
        return Bad();
    }

    private static YStatus Literal(ReadOnlySpan<byte> s, ref int p, string word, YValue result, out YValue v)
    {
        v = default;
        if (s.Length - p < word.Length) return Bad();
        for (var i = 0; i < word.Length; i++)
            if (s[p + i] != word[i]) return Bad();
        p += word.Length;
        v = result;
        return default;
    }

    private static YStatus ReadNumber(ReadOnlySpan<byte> s, ref int p, out YValue v)
    {
        v = default;
        var start = p;
        if (s[p] == '-') p++;
        if (p >= s.Length) return Bad();
        if (s[p] == '0') p++;
        else if (s[p] >= '1' && s[p] <= '9') { while (p < s.Length && s[p] >= '0' && s[p] <= '9') p++; }
        else return Bad();
        if (p < s.Length && s[p] == '.')
        {
            p++;
            var digits = p;
            while (p < s.Length && s[p] >= '0' && s[p] <= '9') p++;
            if (p == digits) return Bad();
        }
        if (p < s.Length && (s[p] == 'e' || s[p] == 'E'))
        {
            p++;
            if (p < s.Length && (s[p] == '+' || s[p] == '-')) p++;
            var digits = p;
            while (p < s.Length && s[p] >= '0' && s[p] <= '9') p++;
            if (p == digits) return Bad();
        }
        if (!double.TryParse(s[start..p], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return Bad();
        v = YValue.FromNumber(d);
        return default;
    }

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    /// <summary>Reads the string whose opening quote is at p. Raw control characters, bad escapes and invalid UTF-8 are Malformed.</summary>
    private static YStatus ReadString(ReadOnlySpan<byte> s, ref int p, YLimits limits, out string str)
    {
        str = "";
        var i = p + 1;
        var runStart = i;
        StringBuilder? sb = null;
        var total = 0;
        while (true)
        {
            if (i >= s.Length) return Bad();
            var b = s[i];
            if (b == '"') break;
            if (b < 0x20) return Bad();
            if (b != '\\') { i++; continue; }

            // an escape: flush the plain run first
            if (!Flush(s, runStart, i, ref sb, ref total, limits, out var st)) return st;
            sb ??= new StringBuilder();
            if (++i >= s.Length) return Bad();
            switch (s[i])
            {
                case (byte)'"': sb.Append('"'); break;
                case (byte)'\\': sb.Append('\\'); break;
                case (byte)'/': sb.Append('/'); break;
                case (byte)'b': sb.Append('\b'); break;
                case (byte)'f': sb.Append('\f'); break;
                case (byte)'n': sb.Append('\n'); break;
                case (byte)'r': sb.Append('\r'); break;
                case (byte)'t': sb.Append('\t'); break;
                case (byte)'u':
                {
                    if (s.Length - i < 5) return Bad();
                    var code = 0;
                    for (var k = 1; k <= 4; k++)
                    {
                        var h = HexValue(s[i + k]);
                        if (h < 0) return Bad();
                        code = code * 16 + h;
                    }
                    sb.Append((char)code);
                    i += 4;
                    break;
                }
                default: return Bad();
            }
            total += 1;
            if (total > limits.MaxStringBytes) return new YStatus(YError.LimitExceeded);
            i++;
            runStart = i;
        }
        if (!Flush(s, runStart, i, ref sb, ref total, limits, out var fin)) return fin;
        str = sb is null ? "" : sb.ToString();
        p = i + 1;
        return default;
    }

    /// <summary>Appends the plain run s[from..to] (strictly decoded UTF-8) to the builder, or starts it. When there are no escapes the run becomes the string.</summary>
    private static bool Flush(ReadOnlySpan<byte> s, int from, int to, ref StringBuilder? sb, ref int total, YLimits limits, out YStatus status)
    {
        status = default;
        var run = s[from..to];
        total += run.Length;
        if (total > limits.MaxStringBytes) { status = new YStatus(YError.LimitExceeded); return false; }
        if (run.IsEmpty) return true;
        string text;
        try { text = Strict.GetString(run); }
        catch (ArgumentException) { status = Bad(); return false; }
        (sb ??= new StringBuilder()).Append(text);
        return true;
    }
}
