using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace YSharp.TestKit.Assertions;

/// <summary>Byte comparison that names the first differing offset and shows hex around it (ADR 0012 reporting).</summary>
public static class HexDiff
{
    private const int Window = 8;

    /// <summary>Null when equal, otherwise a description of the first difference.</summary>
    public static string? Describe(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        var common = Math.Min(expected.Length, actual.Length);
        var at = expected[..common].CommonPrefixLength(actual[..common]);
        if (at == common && expected.Length == actual.Length) return null;

        var sb = new StringBuilder();
        if (expected.Length != actual.Length)
            sb.Append($"length differs: expected {expected.Length} bytes, actual {actual.Length}. ");
        sb.Append($"First difference at offset {at}: expected {ByteAt(expected, at)}, actual {ByteAt(actual, at)}.");
        sb.AppendLine();
        sb.AppendLine($"  expected [{Dump(expected, at)}]");
        sb.Append($"  actual   [{Dump(actual, at)}]");
        return sb.ToString();
    }

    private static string ByteAt(ReadOnlySpan<byte> s, int i) => i < s.Length ? s[i].ToString("x2") : "<end>";

    private static string Dump(ReadOnlySpan<byte> s, int at)
    {
        var from = Math.Max(0, at - Window);
        var to = Math.Min(s.Length, at + Window + 1);
        var sb = new StringBuilder();
        if (from > 0) sb.Append("... ");
        for (var i = from; i < to; i++)
        {
            if (i == at) sb.Append('<');
            sb.Append(s[i].ToString("x2"));
            if (i == at) sb.Append('>');
            if (i < to - 1) sb.Append(' ');
        }
        if (to < s.Length) sb.Append(" ...");
        return sb.ToString();
    }
}

public static class HexAssert
{
    public static void Equal(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string? label = null)
    {
        var d = HexDiff.Describe(expected, actual);
        if (d != null) Assert.Fail((label == null ? "" : label + ": ") + d);
    }
}

/// <summary>
/// Structural comparison of two JSON trees (fixture observations, doc and structs dumps). Object member order is ignored,
/// array order and number sign of zero are not. Returns the first difference as a path from <c>$</c>.
/// </summary>
public static class StructuralDiff
{
    public static string? Find(string expectedJson, string actualJson)
    {
        using var e = JsonDocument.Parse(expectedJson);
        using var a = JsonDocument.Parse(actualJson);
        return Find(e.RootElement, a.RootElement);
    }

    public static string? Find(JsonElement expected, JsonElement actual) => Walk(expected, actual, "$");

    private static string? Walk(JsonElement e, JsonElement a, string path)
    {
        if (e.ValueKind != a.ValueKind)
            return $"{path}: kind differs: expected {e.ValueKind}, actual {a.ValueKind}";
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var ea = e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                var aa = a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                foreach (var name in ea.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    var child = $"{path}.{name}";
                    if (!aa.TryGetValue(name, out var av)) return $"{child}: missing in actual";
                    var d = Walk(ea[name], av, child);
                    if (d != null) return d;
                }
                foreach (var name in aa.Keys.OrderBy(k => k, StringComparer.Ordinal))
                    if (!ea.ContainsKey(name)) return $"{path}.{name}: unexpected in actual";
                return null;
            case JsonValueKind.Array:
                var el = e.EnumerateArray().ToList();
                var al = a.EnumerateArray().ToList();
                for (var i = 0; i < Math.Min(el.Count, al.Count); i++)
                {
                    var d = Walk(el[i], al[i], $"{path}[{i}]");
                    if (d != null) return d;
                }
                if (el.Count > al.Count) return $"{path}[{al.Count}]: missing in actual (expected {el.Count} items, actual {al.Count})";
                if (al.Count > el.Count) return $"{path}[{el.Count}]: unexpected in actual (expected {el.Count} items, actual {al.Count})";
                return null;
            case JsonValueKind.String:
                return e.GetString() == a.GetString() ? null : $"{path}: expected \"{e.GetString()}\", actual \"{a.GetString()}\"";
            case JsonValueKind.Number:
                var x = e.GetDouble();
                var y = a.GetDouble();
                return BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y)
                    ? null
                    : $"{path}: expected {e.GetRawText()}, actual {a.GetRawText()}";
            default:
                return null; // true, false, null: kind equality is value equality
        }
    }

}

public static class StructuralAssert
{
    public static void Equal(string expectedJson, string actualJson, string? label = null)
    {
        var d = StructuralDiff.Find(expectedJson, actualJson);
        if (d != null) Assert.Fail((label == null ? "" : label + ": ") + d);
    }

    public static void Equal(JsonElement expected, JsonElement actual, string? label = null)
    {
        var d = StructuralDiff.Find(expected, actual);
        if (d != null) Assert.Fail((label == null ? "" : label + ": ") + d);
    }
}
