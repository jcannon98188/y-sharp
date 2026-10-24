using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Values;

namespace YSharp.Tests.Protocols;

/// <summary>Malformed awareness batches (AW-51), the upstream <c>testAwareness</c> port and the structural-equality case table.</summary>
public sealed class AwarenessErrorTests
{
    private static string[] StatesOf(AwRig rig) => rig.Awareness.GetStates().Keys.OrderBy(k => k).Select(k => k.ToString()).ToArray();

    [Fact, Diverges("AW-51", adr: "0005")]
    public void ABadBatch_AppliesNothing_AndEmitsNothing() => Stub.Run(() =>
    {
        // JS applies the entries before the failure and emits no event for them; y-sharp rejects the whole batch (ADR 0005).
        var good = AwBytes.Update((11, 1, """{"a":1}"""), (12, 1, """{"a":2}"""), (13, 1, """{"a":3}"""));
        var cases = new (string Name, byte[] Bytes)[]
        {
            ("cut inside the third entry", good.Take(good.Length - 3).ToArray()),
            ("count larger than the entries present", new byte[] { 5 }.Concat(good.Skip(1)).ToArray()),
            ("invalid JSON text", AwBytes.Update((11, 1, """{"a":1}"""), (12, 1, "{oops"))),
            ("empty input", Array.Empty<byte>()),
        };
        foreach (var (name, bytes) in cases)
        {
            using var rig = new AwRig(9);
            var result = rig.Apply(bytes);
            Assert.False(result.Ok, name);
            Assert.Equal(new[] { "9" }, StatesOf(rig));
            Assert.Empty(rig.Events);
            Assert.False(rig.Awareness.Meta.ContainsKey(11), name);
        }
    });

    [Fact]
    [Covers("AW-51")]
    public void TheOracleFixture_RecordsThatTheBatchThrew_AndThatItsLeadingEntriesStuck() => Stub.Run(() =>
    {
        var fixture = FixtureReplay.Load("awareness/apply-trailing-and-truncated.json");
        var results = fixture.Observations.GetProperty("results").GetProperty("value");
        foreach (var name in new[] { "truncated", "count-too-large", "invalid-json-cut", "invalid-json-text" })
            Assert.True(results.GetProperty(name).GetProperty("threw").GetBoolean(), name);
        Assert.False(results.GetProperty("trailing").GetProperty("threw").GetBoolean());
        var partial = fixture.Observations.GetProperty("awareness").GetProperty("value").GetProperty("partial").GetProperty("states");
        Assert.True(partial.GetArrayLength() > 1, "the oracle keeps the entries that were applied before the failure");
        // y-sharp replays the same fixture and fails every bad batch (ProtocolFixtureTests compares threw), keeping nothing of them.
        var ctx = ProtocolFixtureTests.Replay(fixture, out _);
        var aw = ProtocolSteps.Of(ctx).Awarenesses["partial"];
        Assert.Equal(new[] { 8UL }, aw.GetStates().Keys.OrderBy(k => k).ToArray());
    });

    [Fact]
    [PortOf("awareness.test.js:testAwareness", "AW-6", "AW-8", "AW-9", "AW-30", "AW-42", "AW-43", "AW-48", "AW-50")]
    public void Awareness_TwoInstances_PipeUpdates() => Stub.Run(() =>
    {
        using var a1 = new AwRig(0);
        using var a2 = new AwRig(1);
        AwEvent? local = null, remote = null;
        a1.Awareness.Update += (c, o) =>
        {
            var ids = c.Added.Concat(c.Updated).Concat(c.Removed).ToArray();
            Assert.True(a2.Apply(AwBytes.Encode(a1.Awareness, ids), "custom").Ok);
        };
        a1.Awareness.Change += (c, _) => local = new AwEvent("change", 0, c.Added.ToArray(), c.Updated.ToArray(), c.Removed.ToArray(), null);
        a2.Awareness.Change += (c, _) => remote = new AwEvent("change", 0, c.Added.ToArray(), c.Updated.ToArray(), c.Removed.ToArray(), null);

        a1.Awareness.SetLocalState(Js.Obj(("x", 3)));
        Assert.Equal("""{"x":3}""", Js.StateJson(a2.Awareness, 0));
        Assert.Equal(1, a2.ClockOf(0));
        Assert.Equal(new[] { 0UL }, remote!.Added);
        Assert.Equal(new[] { 0UL }, local!.Updated);

        remote = local = null;
        a1.Awareness.SetLocalState(Js.Obj(("x", 4)));
        Assert.Equal("""{"x":4}""", Js.StateJson(a2.Awareness, 0));
        Assert.Equal(local!.Updated, remote!.Updated);

        remote = local = null;
        a1.Awareness.SetLocalState(Js.Obj(("x", 4)));
        Assert.Null(remote);
        Assert.Null(local);
        Assert.Equal(3, a2.ClockOf(0));

        remote = local = null;
        a1.Awareness.SetLocalState(YValue.Null);
        Assert.Single(remote!.Removed);
        Assert.False(a1.Awareness.GetStates().ContainsKey(0));
        Assert.Equal(local!.Removed, remote.Removed);
    });
}

/// <summary>
/// The structural-equality case table (ADR 0014): <c>change.updated</c> uses lib0 <c>equalityDeep</c> semantics for JSON-shaped
/// values. The cases live in tools/fixture-gen/scenarios/equality-cases.json, which also drives the Node scenario that generates
/// fixtures/awareness/structural-equality.json, so the table, the oracle's event log and this test cannot drift apart.
/// </summary>
public sealed class StructuralEqualityTests
{
    private sealed record Case(string Name, TaggedValue A, TaggedValue B, bool Equal);

    private static Case[] Load()
    {
        var path = Path.Combine(RepoRoot.Find(), "tools", "fixture-gen", "scenarios", "equality-cases.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateArray()
            .Select(e => new Case(e.GetProperty("name").GetString()!, TaggedValue.Read(e.GetProperty("a")), TaggedValue.Read(e.GetProperty("b")), e.GetProperty("equal").GetBoolean()))
            .ToArray();
    }

    public static System.Collections.Generic.IEnumerable<object[]> Names() => Load().Select(c => new object[] { c.Name });

    [Fact, Covers("AW-9")]
    public void TheCaseTable_Exists_AndCoversEqualAndUnequalPairs()
    {
        var cases = Load();
        Assert.True(cases.Length >= 15);
        Assert.Contains(cases, c => c.Equal);
        Assert.Contains(cases, c => !c.Equal);
        Assert.Contains(cases, c => c.Name.Contains("nan", StringComparison.Ordinal));
        Assert.Contains(cases, c => c.Name.Contains("zeros", StringComparison.Ordinal));
        Assert.Contains(cases, c => c.Name.Contains("key-order", StringComparison.Ordinal));
    }

    [Theory, MemberData(nameof(Names)), Covers("AW-9", "AW-50")]
    public void ChangeUpdated_StructurallyEqualStates_UsesDeepEquality(string name) => Stub.Run(() =>
    {
        var c = Load().Single(x => x.Name == name);
        using var rig = new AwRig(1);
        rig.Awareness.SetLocalState(c.A.ToYValue());
        var n = rig.Events.Count;
        rig.Awareness.SetLocalState(c.B.ToYValue());
        var expected = c.Equal
            ? new[] { "update@0 +[] ~[1] -[] origin=local" }
            : new[] { "change@0 +[] ~[1] -[] origin=local", "update@0 +[] ~[1] -[] origin=local" };
        Assert.Equal(expected, rig.Log(n));
    });
}
