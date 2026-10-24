using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using YSharp.TestKit.Fixtures;

namespace YSharp.Tests.Types;

/// <summary>
/// Reads the big literal inputs of the upstream tests (the Quill deltas of <c>testDeltaBug</c>) straight from the pinned
/// <c>yjs/tests/y-text.tests.js</c>, so they cannot drift from what the oracle runs. A deliberately small converter: JS object and
/// array literals with bare or quoted keys, single or double quoted strings, numbers, booleans, null and line comments.
/// </summary>
internal static class JsSource
{
    private static string TestBlock(string file, string testName)
    {
        var path = Path.Combine(RepoRoot.Find(), "yjs", "tests", file);
        var source = File.ReadAllText(path);
        var start = source.IndexOf($"export const {testName}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{testName} is not in {path}");
        var end = source.IndexOf("\nexport const ", start + 1, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    /// <summary>The literal assigned to <c>const name = ...</c> inside the test, as JSON text.</summary>
    public static string ConstAsJson(string file, string testName, string name)
    {
        var block = TestBlock(file, testName);
        var marker = $"const {name} = ";
        var at = block.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"const {name} not found in {testName}");
        return ToJson(ReadBalanced(block, at + marker.Length));
    }

    private static string ReadBalanced(string s, int start)
    {
        var depth = 0;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '\'' or '"')
            {
                for (i++; s[i] != c; i++) if (s[i] == '\\') i++;
                continue;
            }
            if (c == '/' && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            if (c is '[' or '{') depth++;
            else if (c is ']' or '}')
            {
                depth--;
                if (depth == 0) return s[start..(i + 1)];
            }
        }
        throw new InvalidOperationException("Unbalanced literal.");
    }

    private static string ToJson(string js)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < js.Length; i++)
        {
            var c = js[i];
            if (c is '\'' or '"')
            {
                sb.Append('"');
                for (i++; js[i] != c; i++)
                {
                    if (js[i] == '\\')
                    {
                        i++;
                        if (js[i] == '\'') sb.Append('\'');
                        else sb.Append('\\').Append(js[i]);
                    }
                    else if (js[i] == '"') sb.Append("\\\"");
                    else sb.Append(js[i]);
                }
                sb.Append('"');
            }
            else if (c == '/' && js[i + 1] == '/')
            {
                while (i < js.Length && js[i] != '\n') i++;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var j = i;
                while (j < js.Length && (char.IsLetterOrDigit(js[j]) || js[j] == '_')) j++;
                var word = js[i..j];
                var k = j;
                while (k < js.Length && char.IsWhiteSpace(js[k])) k++;
                if (k < js.Length && js[k] == ':' && word is not ("true" or "false" or "null")) sb.Append('"').Append(word).Append('"');
                else sb.Append(word);
                i = j - 1;
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>A Quill delta literal as the ops <see cref="YText.ApplyDelta"/> takes.</summary>
    public static TextDeltaOp[] DeltaOps(string json)
    {
        var value = TypeTestExtensions.Parse(json);
        Assert.True(value.TryGetArray(out var items));
        var ops = new List<TextDeltaOp>();
        foreach (var item in items.ToArray())
        {
            Assert.True(item.TryGetObject(out var o));
            var attributes = o.TryGetValue("attributes", out var a) ? a : default;
            if (o.TryGetValue("insert", out var insert))
            {
                if (insert.TryGetString(out var s)) ops.Add(new TextDeltaOp(DeltaKind.Insert, s.Length, s, default, attributes));
                else ops.Add(new TextDeltaOp(DeltaKind.Insert, 1, null, (YEntry)insert, attributes));
            }
            else if (o.TryGetValue("retain", out var retain)) ops.Add(new TextDeltaOp(DeltaKind.Retain, (int)retain.AsNumber(), null, default, attributes));
            else if (o.TryGetValue("delete", out var del)) ops.Add(new TextDeltaOp(DeltaKind.Delete, (int)del.AsNumber()));
            else throw new InvalidOperationException("A delta op needs insert, retain or delete.");
        }
        return ops.ToArray();
    }
}
