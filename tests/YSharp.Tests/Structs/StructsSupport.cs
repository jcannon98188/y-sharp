using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;
using YSharp.TestKit.Stubs;

namespace YSharp.Tests.Structs;

/// <summary>
/// Spec 6 tests assert against y-sharp, which is still the API stub (ADR 0019). Every body goes through
/// <see cref="Impl.Run"/>: while the stub throws <see cref="NotImplementedException"/> the test is skipped visibly, and
/// the moment the implementation exists the same assertions run for real.
/// </summary>
internal static class Impl
{
    public static void Run(Action body)
    {
        ApiStub.Run(body);
    }

    /// <summary>For tests whose first calls would work on the stub (plain properties): fails over to a skip until the implementation exists.</summary>
    public static void RequireImplementation() => ApiStub.SkipIfStub();
}

/// <summary>
/// Reads the struct store through the internal seam of Spec 6 (ADR 0015, 0016): <c>internal string Doc.DumpStructs()</c> returns the
/// struct store as JSON, exactly the shape of the fixture <c>structs</c> observation: an array of
/// <c>[client, [struct, ...]]</c> with clients in descending id order and structs in clock order, each struct
/// <c>{id:[client,clock], len, kind:"item|gc|skip", origin?, rightOrigin?, parent, parentSub?, content:{ref,value}, deleted, keep}</c>.
/// <c>content.value</c> is the tagged value of ADR 0011 (a string for ContentString, a tagged array for ContentAny/JSON,
/// <c>[key,value]</c> for ContentFormat, the type name for ContentType, <c>[guid,opts]</c> for ContentDoc, null for ContentDeleted).
/// The members are called directly: this project sees the library's internals.
/// </summary>
internal static class StructsProbe
{
    public static string DumpJson(Doc doc) => doc.DumpStructs();

    /// <summary><c>internal bool Doc.HasPending</c>: true while pending structs or a pending delete set are held (JS <c>store.pendingStructs !== null || store.pendingDs !== null</c>).</summary>
    public static bool HasPending(Doc doc) => doc.HasPending;

    public static List<ClientStructs> Dump(Doc doc)
    {
        using var json = JsonDocument.Parse(DumpJson(doc));
        return json.RootElement.EnumerateArray().Select(c => new ClientStructs(
            c[0].GetUInt32(),
            c[1].EnumerateArray().Select(s => StructInfo.From(c[0].GetUInt64(), s)).ToList())).ToList();
    }

    public static IReadOnlyList<StructInfo> Of(Doc doc, ulong client) =>
        Dump(doc).FirstOrDefault(c => c.Client == client)?.Structs ?? new List<StructInfo>();

    public static IReadOnlyList<StructInfo> All(Doc doc) => Dump(doc).SelectMany(c => c.Structs).ToList();

    /// <summary>The delete set derived from the store (JS <c>createDeleteSetFromStructStore</c>): merged, sorted by client.</summary>
    public static string DeleteSetJson(IEnumerable<ClientStructs> dump)
    {
        var parts = new List<string>();
        foreach (var c in dump.OrderBy(c => c.Client))
        {
            var ranges = new List<(long Clock, long Len)>();
            foreach (var s in c.Structs.Where(s => s.Deleted))
            {
                if (ranges.Count > 0 && ranges[^1].Clock + ranges[^1].Len >= s.Clock)
                    ranges[^1] = (ranges[^1].Clock, Math.Max(ranges[^1].Len, s.Clock + s.Len - ranges[^1].Clock));
                else ranges.Add((s.Clock, s.Len));
            }
            if (ranges.Count > 0) parts.Add($"[{c.Client},[{string.Join(",", ranges.Select(r => $"[{r.Clock},{r.Len}]"))}]]");
        }
        return "[" + string.Join(",", parts) + "]";
    }
}

/// <summary>The <see cref="YSharp.TestKit.Sim.ISimInternals"/> checks of the simulation harness, implemented over the structs probe.</summary>
internal sealed class StructsSimInternals : YSharp.TestKit.Sim.ISimInternals
{
    public void AssertNoPending(Doc doc) => Assert.False(StructsProbe.HasPending(doc), "pending structs or delete set remain after a full sync");

    public void AssertStoresEqual(Doc a, Doc b)
    {
        var diff = YSharp.TestKit.Assertions.StructuralDiff.Find(StructsProbe.DumpJson(a), StructsProbe.DumpJson(b));
        Assert.True(diff == null, $"struct stores differ at {diff}");
        Assert.Equal(StructsProbe.DeleteSetJson(StructsProbe.Dump(a)), StructsProbe.DeleteSetJson(StructsProbe.Dump(b)));
    }
}

internal sealed record ClientStructs(ulong Client, List<StructInfo> Structs);

/// <summary>One struct of the dump; GC and Skip report <see cref="Deleted"/> true.</summary>
internal sealed record StructInfo(
    ulong Client, long Clock, long Len, string Kind, (ulong Client, long Clock)? Origin, (ulong Client, long Clock)? RightOrigin,
    string? ParentName, (ulong Client, long Clock)? ParentId, string? ParentSub, int Ref, JsonElement? Value, bool Deleted, bool Keep)
{
    public bool IsItem => Kind == "item";
    public bool IsGc => Kind == "gc";
    public long End => Clock + Len;
    public string? Text => Ref == 4 ? Value?.GetString() : null;

    public static StructInfo From(ulong client, JsonElement s)
    {
        var kind = s.GetProperty("kind").GetString()!;
        var id = s.GetProperty("id");
        (ulong, long)? Pair(string name) => s.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array ? (p[0].GetUInt64(), p[1].GetInt64()) : null;
        string? parentName = null;
        (ulong, long)? parentId = null;
        if (s.TryGetProperty("parent", out var parent))
        {
            if (parent.ValueKind == JsonValueKind.String) parentName = parent.GetString();
            else if (parent.ValueKind == JsonValueKind.Array) parentId = (parent[0].GetUInt32(), parent[1].GetInt64());
        }
        var hasContent = s.TryGetProperty("content", out var content);
        return new StructInfo(
            client, id[1].GetInt64(), s.GetProperty("len").GetInt64(), kind, Pair("origin"), Pair("rightOrigin"),
            parentName, parentId, s.TryGetProperty("parentSub", out var ps) ? ps.GetString() : null,
            hasContent ? content.GetProperty("ref").GetInt32() : kind == "gc" ? 0 : 10,
            hasContent && content.TryGetProperty("value", out var v) ? v.Clone() : null,
            kind != "item" || s.GetProperty("deleted").GetBoolean(),
            kind == "item" && s.GetProperty("keep").GetBoolean());
    }
}

/// <summary>Small helpers for building replicas and moving updates between them.</summary>
internal static class Net
{
    public static Doc Mk(ulong client, bool gc = true) => new(new DocOptions { ClientId = client, Gc = gc });

    public static byte[] StateVector(Doc d)
    {
        var w = new ArrayBufferWriter<byte>();
        d.EncodeStateVector(w);
        return w.WrittenSpan.ToArray();
    }

    /// <summary>Applies what <paramref name="to"/> is missing from <paramref name="from"/> (the <c>sync</c> step of ADR 0016).</summary>
    public static void Sync(Doc from, Doc to) => to.ApplyUpdate(from.EncodeStateAsUpdate(StateVector(to)), "sync");

    public static void Both(Doc a, Doc b) { Sync(a, b); Sync(b, a); }

    public static List<byte[]> Capture(Doc d)
    {
        var list = new List<byte[]>();
        d.Update += (u, _, _, _) => list.Add(u.ToArray());
        return list;
    }

}
