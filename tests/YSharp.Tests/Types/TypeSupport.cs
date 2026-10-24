using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using YSharp.TestKit.Rng;
using YSharp.TestKit.Sim;

namespace YSharp.Tests.Types;

/// <summary>One simulated client with the four root types of the upstream <c>init</c> helper (array, map, xml, text).</summary>
internal sealed class Peer
{
    public Peer(TestYInstance user)
    {
        User = user;
        Array = user.Doc.GetArray("array");
        Map = user.Doc.GetMap("map");
        Xml = user.Doc.GetXmlElement("xml");
        Text = user.Doc.GetText("text");
    }

    public TestYInstance User { get; }
    public Doc Doc => User.Doc;
    public YArray Array { get; }
    public YMap Map { get; }
    public YXmlElement Xml { get; }
    public YText Text { get; }
}

/// <summary>Port of the upstream <c>init(tc, {users})</c> and <c>compare(users)</c> helpers on the TestKit simulation (SIM-10..12).</summary>
internal sealed class Net
{
    private Net(SimResult<Peer> sim)
    {
        Connector = sim.TestConnector;
        Users = sim.Users;
        Peers = sim.TestObjects;
    }

    public TestConnector Connector { get; }
    public IReadOnlyList<TestYInstance> Users { get; }
    public IReadOnlyList<Peer> Peers { get; }

    public static Net Init(int users, uint seed = 1) => new(SimHarness.Init(Prng.Create(seed), users, u => new Peer(u)));

    /// <summary>JS <c>testConnector.flushAllMessages()</c>.</summary>
    public void Flush() => Connector.FlushAllMessages();

    /// <summary>JS <c>compare(users)</c>: every replica converges (also destroys the documents).</summary>
    public void Compare() => SimHarness.Compare(Users);
}

internal static class TypeTestExtensions
{
    public static string Json(this YValue v)
    {
        var w = new ArrayBufferWriter<byte>();
        var status = v.TryWriteJson(w);
        Assert.True(status.Ok, status.Error.ToString());
        return Encoding.UTF8.GetString(w.WrittenSpan);
    }

    public static YValue Parse(string json)
    {
        var status = YValue.TryParseJson(Encoding.UTF8.GetBytes(json), null, out var v);
        Assert.True(status.Ok, $"{status.Error}: {json}");
        return v;
    }

    /// <summary>The JSON of a type, like JS <c>JSON.stringify(type.toJSON())</c>.</summary>
    public static string Json(this YType t) => t.ToJson().Json();

    /// <summary>A map value, <see cref="YValue.Undefined"/> when the key is absent (JS <c>get</c>).</summary>
    public static YValue Get(this YMap m, string key) => m.TryGet(key, out var e) ? e.Value : default;

    public static YMap MapAt(this YArray a, int i) => (YMap)a[i].Type!;
    public static YArray ArrayAt(this YArray a, int i) => (YArray)a[i].Type!;
    public static YText TextAt(this YArray a, int i) => (YText)a[i].Type!;
    public static YMap MapOf(this YMap m, string key) => (YMap)(m.TryGet(key, out var e) ? e.Type : null)!;
    public static YArray ArrayOf(this YMap m, string key) => (YArray)(m.TryGet(key, out var e) ? e.Type : null)!;
    public static YText TextOf(this YMap m, string key) => (YText)(m.TryGet(key, out var e) ? e.Type : null)!;

    public static YEntry[] Items(params YEntry[] items) => items;

    public static YEntry Null => (YEntry)YValue.Null;

    public static List<YValue> Values(this YArray a) => a.ToArray().Select(e => e.Value).ToList();

    /// <summary>The numbers/strings of an array as the JSON JS prints for <c>toArray()</c>.</summary>
    public static string ArrayJson(this YArray a) => a.ToJson().Json();

    public static string Keys(this YMap m) => string.Join(",", m.Keys.OrderBy(k => k, StringComparer.Ordinal));

    /// <summary>A text delta as JSON: <c>[{"insert":"a","attributes":{...}},{"retain":1},{"delete":1}]</c>.</summary>
    public static string DeltaJson(IEnumerable<TextDeltaOp> ops)
    {
        var list = new List<YValue>();
        foreach (var op in ops)
        {
            var members = new List<KeyValuePair<string, YValue>>();
            switch (op.Kind)
            {
                case DeltaKind.Insert:
                    members.Add(new("insert", op.Text != null ? op.Text : EntryJson(op.Embed)));
                    break;
                case DeltaKind.Retain: members.Add(new("retain", op.Length)); break;
                default: members.Add(new("delete", op.Length)); break;
            }
            if (op.Attributes.Kind != YValueKind.Undefined) members.Add(new("attributes", op.Attributes));
            list.Add(YValue.Object(members.ToArray()));
        }
        return YValue.Array(list.ToArray()).Json();
    }

    public static string DeltaJson(this YText text) => DeltaJson(text.ToDelta());

    /// <summary>A list-event delta as JSON: inserts are arrays of the inserted entries (nested types as their JSON).</summary>
    public static string ListDeltaJson(IEnumerable<ListDeltaOp> ops)
    {
        var list = new List<YValue>();
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case DeltaKind.Insert:
                    list.Add(YValue.Object(new KeyValuePair<string, YValue>[] { new("insert", YValue.Array(op.Inserted.ToArray().Select(EntryJson).ToArray())) }));
                    break;
                case DeltaKind.Retain: list.Add(YValue.Object(new KeyValuePair<string, YValue>[] { new("retain", op.Length) })); break;
                default: list.Add(YValue.Object(new KeyValuePair<string, YValue>[] { new("delete", op.Length) })); break;
            }
        }
        return YValue.Array(list.ToArray()).Json();
    }

    public static YValue EntryJson(YEntry e) => e.Kind == YEntryKind.Type ? e.Type!.ToJson() : e.Value;

    public static YValue Obj(params (string Key, YValue Value)[] members) =>
        YValue.Object(members.Select(m => new KeyValuePair<string, YValue>(m.Key, m.Value)).ToArray());

    public static TextDeltaOp Insert(string text, YValue attributes = default) => new(DeltaKind.Insert, text.Length, text, default, attributes);
    public static TextDeltaOp Retain(int n, YValue attributes = default) => new(DeltaKind.Retain, n, null, default, attributes);
    public static TextDeltaOp Delete(int n) => new(DeltaKind.Delete, n);
}
