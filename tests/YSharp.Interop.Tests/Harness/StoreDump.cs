using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace YSharp.Interop;

/// <summary>
/// The y-sharp half of the opt-in <c>structs</c> and <c>ds</c> observations (shape of <c>dumpStructs</c> and <c>dumpDs</c> in
/// <c>tools/fixture-gen/lib/observe.mjs</c>). The struct store has no public reader and the interop project sees the public API only
/// (ADR 0015), so the dump the fixture tests use, <c>internal string Doc.DumpStructs()</c>, is read by reflection here. It is an observation
/// of the finished replica, never a step of the script, so the scripts still drive y-sharp through the public API alone.
/// </summary>
public static class StoreDump
{
    private static readonly MethodInfo DumpStructs = typeof(Doc).GetMethod("DumpStructs", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("Doc.DumpStructs() is missing: the structs observation needs it.");

    /// <summary><c>[[client, [struct, ...]], ...]</c>, clients in descending id order, structs in clock order.</summary>
    public static JsonNode Structs(Doc doc) => JsonNode.Parse((string)DumpStructs.Invoke(doc, null)!)!;

    /// <summary>
    /// JS <c>createDeleteSetFromStructStore</c> over a struct dump, as <c>dumpDs</c> renders it: clients ascending, ranges sorted and merged.
    /// A GC and a skip count as deleted, as their <c>deleted</c> getters say in Yjs.
    /// </summary>
    public static JsonNode DeleteSet(JsonNode structs)
    {
        var result = new JsonArray();
        foreach (var client in structs.AsArray().OrderBy(c => c![0]!.GetValue<ulong>()))
        {
            var ranges = new List<(long Clock, long Len)>();
            foreach (var s in client![1]!.AsArray())
            {
                var kind = s!["kind"]!.GetValue<string>();
                var deleted = kind is "gc" or "skip" || s["deleted"]!.GetValue<bool>();
                if (!deleted) continue;
                var clock = s["id"]![1]!.GetValue<long>();
                var len = s["len"]!.GetValue<long>();
                if (ranges.Count > 0 && ranges[^1].Clock + ranges[^1].Len >= clock)
                    ranges[^1] = (ranges[^1].Clock, Math.Max(ranges[^1].Len, clock + len - ranges[^1].Clock));
                else ranges.Add((clock, len));
            }
            if (ranges.Count > 0)
                result.Add(new JsonArray(client[0]!.DeepClone(), new JsonArray(ranges.Select(r => (JsonNode)new JsonArray(r.Clock, r.Len)).ToArray())));
        }
        return result;
    }
}
