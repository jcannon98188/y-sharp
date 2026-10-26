using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using YSharp.Tests.Types;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Values;

namespace YSharp.Interop;

/// <summary>The first place two observation sets disagree: which observation, where in it, and what differs (ADR 0012 "Reporting").</summary>
public sealed record Difference(string Observation, string Path, string Detail)
{
    public override string ToString() => $"{Observation} {Path}: {Detail}";
}

/// <summary>
/// Equivalence of observations (ADR 0012): bytes exact (reported as the first differing byte offset with hex around it), state structural
/// (reported as a path into the <c>doc</c> dump). Only observations both sides produced are compared, in a fixed order, so the first
/// difference is deterministic.
/// </summary>
public static class ObservationDiff
{
    /// <summary>
    /// The observations both sides render, in comparison order. <c>events</c> is present when a script observes something that fired;
    /// <c>structs</c> and <c>ds</c> when the run asked for them (ADR 0012 <c>extras</c>).
    /// </summary>
    public static readonly string[] Compared = { "updates", "state", "sv", "doc", "results", "events", "awareness", "structs", "ds" };

    public static Difference? Find(JsonObject oracle, JsonObject sharp, IEnumerable<string>? only = null)
    {
        foreach (var name in Compared)
        {
            if (only != null && !only.Contains(name)) continue;
            var hasO = oracle.TryGetPropertyValue(name, out var o);
            var hasS = sharp.TryGetPropertyValue(name, out var s);
            if (!hasO && !hasS) continue;
            if (hasO != hasS) return new Difference(name, "$", hasO ? "missing in y-sharp's observations" : "unexpected in y-sharp's observations");
            var ev = Strip(o!["value"]!, name);
            var av = Strip(s!["value"]!, name);
            var d = Walk(ev, av, "$");
            if (d != null) return new Difference(name, d.Value.Path, d.Value.Detail);
        }
        return null;
    }

    /// <summary>
    /// Compares two values of the same observation shape, e.g. the dumps of two replicas. With <paramref name="isDoc"/> the unordered parts
    /// of a <c>doc</c> dump are canonicalised first, including the pair order of maps, which legitimately depends on arrival order across
    /// replicas (it is compared exactly only for the same replica on both sides, by <see cref="Find"/>).
    /// </summary>
    public static Difference? CompareValues(JsonNode? expected, JsonNode? actual, string path, bool isDoc = false)
    {
        var e = expected?.DeepClone();
        var a = actual?.DeepClone();
        if (isDoc)
        {
            e = e == null ? null : Canonical(e);
            a = a == null ? null : Canonical(a);
        }
        var d = Walk(e, a, path);
        return d == null ? null : new Difference("value", d.Value.Path, d.Value.Detail);
    }

    private static JsonNode Canonical(JsonNode n)
    {
        TypeDump.Normalize(n);
        SortMapPairs(n);
        return n;
    }

    private static void SortMapPairs(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o:
                if (o["kind"]?.GetValue<string>() == "map" && o["pairs"] is JsonArray pairs)
                {
                    var sorted = pairs.Select(p => p!.DeepClone()).OrderBy(p => p[0]!.GetValue<string>(), StringComparer.Ordinal).ToArray();
                    o["pairs"] = new JsonArray(sorted);
                }
                foreach (var child in o.Select(kv => kv.Value).ToList()) SortMapPairs(child);
                break;
            case JsonArray a:
                foreach (var child in a) SortMapPairs(child);
                break;
        }
    }

    /// <summary>Drops the API labels (which differ by implementation by design) and normalises the unordered parts of a <c>doc</c> dump.</summary>
    private static JsonNode Strip(JsonNode value, string observation)
    {
        var clone = value.DeepClone();
        if (observation is "doc" or "events") TypeDump.Normalize(clone);
        if (observation == "results" && clone is JsonObject results)
            foreach (var entry in results.Select(kv => kv.Value).OfType<JsonObject>())
            {
                entry.Remove("api");
                // As the protocol fixture replay compares them: a read or encode that threw has no defined reply bytes or read position
                // (the Node writer may have begun its reply before throwing), and one whose error went to the handler no defined position.
                if (entry["threw"]?.GetValue<bool>() == true) { entry.Remove("bytes"); entry.Remove("consumed"); }
                if (entry["handlerCalled"]?.GetValue<bool>() == true) entry.Remove("consumed");
            }
        return clone;
    }

    private static bool IsBytes(JsonNode? n) => n is JsonObject o && o["$"]?.GetValue<string>() == "bytes" && o["hex"] != null;

    private static (string Path, string Detail)? Walk(JsonNode? expected, JsonNode? actual, string path)
    {
        if (IsBytes(expected) && IsBytes(actual))
        {
            var d = HexDiff.Describe(Convert.FromHexString(expected!["hex"]!.GetValue<string>()), Convert.FromHexString(actual!["hex"]!.GetValue<string>()));
            return d == null ? null : (path, "bytes differ: " + d);
        }
        switch (expected, actual)
        {
            case (JsonObject eo, JsonObject ao):
            {
                foreach (var name in eo.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal))
                {
                    var child = $"{path}.{name}";
                    if (!ao.TryGetPropertyValue(name, out var av)) return (child, "missing in y-sharp's observations");
                    var d = Walk(eo[name], av, child);
                    if (d != null) return d;
                }
                foreach (var name in ao.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal))
                    if (!eo.ContainsKey(name)) return ($"{path}.{name}", "unexpected in y-sharp's observations");
                return null;
            }
            case (JsonArray ea, JsonArray aa):
            {
                for (var i = 0; i < Math.Min(ea.Count, aa.Count); i++)
                {
                    var d = Walk(ea[i], aa[i], $"{path}[{i}]");
                    if (d != null) return d;
                }
                if (ea.Count != aa.Count) return (path, $"expected {ea.Count} items, y-sharp has {aa.Count}");
                return null;
            }
            default:
            {
                var e = expected?.ToJsonString() ?? "null";
                var a = actual?.ToJsonString() ?? "null";
                using var ed = JsonDocument.Parse(e);
                using var ad = JsonDocument.Parse(a);
                var d = StructuralDiff.Find(ed.RootElement, ad.RootElement);
                return d == null ? null : (path, d.Substring(d.IndexOf(':') + 1).Trim() is { Length: > 0 } t ? t : d);
            }
        }
    }
}
