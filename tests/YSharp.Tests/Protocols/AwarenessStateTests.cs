using System;
using System.Linq;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Protocols;

/// <summary>Local state, events, destroy and removal of awareness (AW-1..13, AW-22..29). Bodies skip while y-sharp is the API stub (ADR 0019).</summary>
public sealed class AwarenessStateTests
{
    private static YValue Obj(string k, YValue v) => Js.Obj((k, v));

    [Fact, Covers("AW-1")]
    public void OutdatedTimeout_Default_Is30000Milliseconds() => Stub.Run(() =>
        Assert.Equal(TimeSpan.FromMilliseconds(30000), Awareness.OutdatedTimeout));

    [Fact, Covers("AW-2")]
    public void Events_SubscribeThenUnsubscribe_StopsDelivery() => Stub.Run(() =>
    {
        using var rig = new AwRig();
        var calls = 0;
        AwarenessHandler h = (_, _) => calls++;
        rig.Awareness.Change += h;
        rig.Awareness.SetLocalState(Obj("x", 1));
        Assert.Equal(1, calls);
        rig.Awareness.Change -= h;
        rig.Awareness.SetLocalState(Obj("x", 2));
        Assert.Equal(1, calls);
    });

    [Fact, Covers("AW-3")]
    public void ClientId_IsCopiedAtConstruction_AndNotFollowed() => Stub.Run(() =>
    {
        using var rig = new AwRig(7);
        Assert.Equal(7u, rig.Awareness.ClientId);
        rig.Doc.ClientId = 99;
        Assert.Equal(7u, rig.Awareness.ClientId);
        rig.Awareness.SetLocalState(Obj("x", 1));
        Assert.Equal(new[] { "7" }, Js.Keys(rig.Awareness));
    });

    [Fact, Covers("AW-4")]
    public void States_HoldOnlyLiveClients_WhileMetaKeepsTombstones() => Stub.Run(() =>
    {
        using var rig = new AwRig(1);
        rig.Apply(AwBytes.Update((5, 3, """{"a":1}""")));
        rig.Apply(AwBytes.Update((5, 4, "null")));
        Assert.Equal(new[] { "1" }, Js.Keys(rig.Awareness));
        Assert.Equal(4, rig.ClockOf(5));
    });

    [Fact, Covers("AW-5")]
    public void StateValues_SeenByPeers_AreTheirJsonRoundTrip() => Stub.Run(() =>
    {
        using var a = new AwRig(1);
        using var b = new AwRig(2);
        a.Awareness.SetLocalState(Js.Obj(("n", double.NaN), ("z", -0.0), ("s", "hé"), ("arr", YValue.Array(new YValue[] { 1, YValue.Null }))));
        Assert.True(b.Apply(AwBytes.Encode(a.Awareness, 1)).Ok);
        Assert.Equal("{\"n\":null,\"z\":0,\"s\":\"hé\",\"arr\":[1,null]}", Js.StateJson(b.Awareness, 1));
    });

    [Fact, Covers("AW-6")]
    public void Constructor_SetsEmptyLocalState_WithClockZero_AndFirstObservableSetIsClockOne() => Stub.Run(() =>
    {
        using var rig = new AwRig(3);
        Assert.Equal("{}", Js.StateJson(rig.Awareness, 3));
        Assert.Equal(0, rig.ClockOf(3));
        Assert.Empty(rig.Events);
        rig.Awareness.SetLocalState(Obj("x", 1));
        Assert.Equal(1, rig.ClockOf(3));
    });

    [Fact, Covers("AW-7")]
    public void LocalState_AfterRemoval_IsNull() => Stub.Run(() =>
    {
        using var rig = new AwRig();
        Assert.Equal("{}", Js.Json(rig.Awareness.LocalState));
        rig.Awareness.SetLocalState(YValue.Null);
        Assert.Equal(YValueKind.Null, rig.Awareness.LocalState.Kind);
    });

    [Fact, Covers("AW-8")]
    public void SetLocalState_BumpsTheClockEveryCall_AndReportsAddedUpdatedRemoved() => Stub.Run(() =>
    {
        using var rig = new AwRig(1);
        rig.Tick(250);
        rig.Awareness.SetLocalState(Obj("x", 1));
        rig.Awareness.SetLocalState(Obj("x", 1));
        rig.Awareness.SetLocalState(YValue.Null);
        rig.Awareness.SetLocalState(Obj("x", 2));
        Assert.Equal(4, rig.ClockOf(1));
        Assert.Equal(250, rig.LastUpdatedMs(1));
        Assert.Equal(
            new[]
            {
                "change@250 +[] ~[1] -[] origin=local", "update@250 +[] ~[1] -[] origin=local",
                "update@250 +[] ~[1] -[] origin=local",
                "change@250 +[] ~[] -[1] origin=local", "update@250 +[] ~[] -[1] origin=local",
                "change@250 +[1] ~[] -[] origin=local", "update@250 +[1] ~[] -[] origin=local",
            },
            rig.Log());
    });

    [Fact, Covers("AW-9")]
    public void IdenticalState_EmitsUpdateOnly_ChangedState_EmitsChangeThenUpdate() => Stub.Run(() =>
    {
        using var rig = new AwRig(1);
        rig.Awareness.SetLocalState(Obj("x", 4));
        var before = rig.Events.Count;
        rig.Awareness.SetLocalState(Obj("x", 4));
        Assert.Equal(new[] { "update@0 +[] ~[1] -[] origin=local" }, rig.Log(before));
        before = rig.Events.Count;
        rig.Awareness.SetLocalState(Obj("x", 5));
        Assert.Equal(new[] { "change@0 +[] ~[1] -[] origin=local", "update@0 +[] ~[1] -[] origin=local" }, rig.Log(before));
    });

    [Fact, Covers("AW-10")]
    public void SetLocalStateNull_WhenAlreadyNull_StillReportsRemovalAndBumpsTheClock() => Stub.Run(() =>
    {
        using var rig = new AwRig(1);
        rig.Awareness.SetLocalState(YValue.Null);
        var clock = rig.ClockOf(1);
        var before = rig.Events.Count;
        rig.Awareness.SetLocalState(YValue.Null);
        Assert.Equal(clock + 1, rig.ClockOf(1));
        Assert.Equal(new[] { "change@0 +[] ~[] -[1] origin=local", "update@0 +[] ~[] -[1] origin=local" }, rig.Log(before));
    });

    [Fact, Diverges("AW-11", adr: "0007")]
    public void StateObjects_AreImmutableValues_SoTheyCannotBeAliased() => Stub.Run(() =>
    {
        // JS stores the caller's object by reference, so mutating it in place and setting it again looks unchanged. YValue is
        // immutable and copies its input, so the stored state never changes behind the awareness's back.
        using var rig = new AwRig(1);
        var items = new YValue[] { 1, 2 };
        rig.Awareness.SetLocalState(Obj("list", YValue.Array(items)));
        items[0] = 99;
        Assert.Equal("""{"list":[1,2]}""", Js.StateJson(rig.Awareness, 1));
        var before = rig.Events.Count;
        rig.Awareness.SetLocalState(Obj("list", YValue.Array(items)));
        Assert.Contains("change@0", rig.Log(before)[0]);
    });

    [Fact, Covers("AW-12")]
    public void SetLocalStateField_IsNoOpWhenNull_ElseShallowMergesAndBumpsTheClock() => Stub.Run(() =>
    {
        using var rig = new AwRig(1);
        rig.Awareness.SetLocalState(YValue.Null);
        var before = rig.Events.Count;
        var clock = rig.ClockOf(1);
        rig.Awareness.SetLocalStateField("a", 1);
        Assert.Equal(before, rig.Events.Count);
        Assert.Equal(clock, rig.ClockOf(1));

        rig.Awareness.SetLocalState(Js.Obj(("a", 1), ("b", 2)));
        clock = rig.ClockOf(1);
        rig.Awareness.SetLocalStateField("b", 3);
        rig.Awareness.SetLocalStateField("c", 4);
        Assert.Equal("""{"a":1,"b":3,"c":4}""", Js.StateJson(rig.Awareness, 1));
        Assert.Equal(clock + 2, rig.ClockOf(1));
    });

    [Fact, Covers("AW-13")]
    public void GetStates_CalledRepeatedly_ReturnsTheSameContent() => Stub.Run(() =>
    {
        using var rig = new AwRig(1);
        rig.Apply(AwBytes.Update((5, 1, """{"a":1}""")));
        var first = rig.Awareness.GetStates().OrderBy(k => k.Key).Select(k => k.Key + Js.Json(k.Value)).ToArray();
        var second = rig.Awareness.GetStates().OrderBy(k => k.Key).Select(k => k.Key + Js.Json(k.Value)).ToArray();
        Assert.Equal(first, second);
        Assert.Equal(2, first.Length);
    });

    // ------------------------------------------------------------------ destroy

    [Fact, Covers("AW-22")]
    public void Dispose_EmitsDestroy_ThenTheLocalRemoval_ThenStopsTheTimer() => Stub.Run(() =>
    {
        using var rig = new AwRig(4);
        rig.Awareness.SetLocalState(Obj("here", true));
        var before = rig.Events.Count;
        rig.Awareness.Dispose();
        Assert.Equal(
            new[] { "destroy@0 +[] ~[] -[] origin=null", "change@0 +[] ~[] -[4] origin=local", "update@0 +[] ~[] -[4] origin=local" },
            rig.Log(before));
        before = rig.Events.Count;
        rig.Tick(60000);
        Assert.Equal(before, rig.Events.Count);
    });

    [Fact, Covers("AW-23")]
    public void Doc_Destroyed_DestroysTheAwareness() => Stub.Run(() =>
    {
        using var rig = new AwRig(4);
        rig.Awareness.SetLocalState(Obj("here", true));
        var before = rig.Events.Count;
        rig.Doc.Destroy();
        Assert.Equal("destroy@0 +[] ~[] -[] origin=null", rig.Log(before)[0]);
        Assert.Contains(rig.Log(before), e => e.StartsWith("change@0 +[] ~[] -[4]", StringComparison.Ordinal));
    });

    [Fact, Diverges("AW-24", adr: "0014")]
    public void Dispose_CalledTwice_IsIdempotent() => Stub.Run(() =>
    {
        // JS destroy() run twice emits the removal path again and bumps the clock (see AW-10). Dispose does nothing the second time.
        using var rig = new AwRig(4);
        rig.Awareness.Dispose();
        var clock = rig.ClockOf(4);
        var before = rig.Events.Count;
        rig.Awareness.Dispose();
        Assert.Equal(before, rig.Events.Count);
        Assert.Equal(clock, rig.ClockOf(4));
    });

    // ------------------------------------------------------------------ RemoveStates

    [Fact, Covers("AW-25")]
    public void RemoveStates_RemovesPresentClients_AndSkipsUnknownOnes() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        rig.Apply(AwBytes.Update((1, 1, """{"a":1}"""), (2, 4, """{"a":2}""")));
        rig.Awareness.RemoveStates(new uint[] { 99, 2 }, "o");
        Assert.Equal(new[] { "1", "9" }, Js.Keys(rig.Awareness));
        Assert.Equal(4, rig.ClockOf(2));
        Assert.Equal(new[] { 2u }, rig.Events[^1].Removed);
    });

    [Fact, Covers("AW-26")]
    public void RemoveStates_BumpsOnlyTheLocalClock_AndLeavesRemoteMetaUntouched() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        rig.Apply(AwBytes.Update((1, 5, """{"a":1}""")));
        rig.Tick(100);
        var local = rig.ClockOf(9);
        rig.Awareness.RemoveStates(new uint[] { 9, 1 }, null);
        Assert.Equal(local + 1, rig.ClockOf(9));
        Assert.Equal(100, rig.LastUpdatedMs(9));
        Assert.Equal(5, rig.ClockOf(1));
        Assert.Equal(0, rig.LastUpdatedMs(1));
    });

    [Fact, Covers("AW-27")]
    public void RemoveStates_EmitsChangeThenUpdateWithTheOrigin_ButNothingWhenNothingWasRemoved() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        rig.Apply(AwBytes.Update((1, 1, "{}")));
        var before = rig.Events.Count;
        rig.Awareness.RemoveStates(new uint[] { 1 }, "bye");
        Assert.Equal(new[] { "change@0 +[] ~[] -[1] origin=bye", "update@0 +[] ~[] -[1] origin=bye" }, rig.Log(before));
        before = rig.Events.Count;
        rig.Awareness.RemoveStates(new uint[] { 1, 77 }, "again");
        rig.Awareness.RemoveStates(ReadOnlySpan<uint>.Empty, "none");
        Assert.Equal(before, rig.Events.Count);
    });

    [Fact, Covers("AW-28")]
    public void RemoveStates_DuplicatesAreSkipped_SoTheLocalClockIsBumpedOnce() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        var clock = rig.ClockOf(9);
        rig.Awareness.RemoveStates(new uint[] { 9, 9 }, null);
        Assert.Equal(clock + 1, rig.ClockOf(9));
        Assert.Equal(new[] { 9u }, rig.Events[^1].Removed);
    });

    [Fact, Covers("AW-29")]
    public void RemoveStates_AnyClients_ProducesNoDocumentUpdate() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        var updates = 0;
        rig.Doc.Update += (_, _, _, _) => updates++;
        rig.Awareness.RemoveStates(new uint[] { 9 }, "x");
        Assert.Equal(0, updates);
    });
}
