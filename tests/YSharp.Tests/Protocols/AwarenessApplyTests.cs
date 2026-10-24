using System;
using System.Linq;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Protocols;

/// <summary>applyAwarenessUpdate (AW-41..52): the clock rules, the local-client rules, event classification and error behaviour.</summary>
public sealed class AwarenessApplyTests
{
    private static byte[] U(uint id, long clock, string json) => AwBytes.Update((id, clock, json));

    [Fact, Covers("AW-41")]
    public void Apply_DecodesIdClockAndJsonState_AndAnUnknownClientCountsAsClockZero() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        Assert.True(rig.Apply(U(5, 1, """{"a":[1,{"b":"c"}]}""")).Ok);
        Assert.Equal("""{"a":[1,{"b":"c"}]}""", Js.StateJson(rig.Awareness, 5));
        Assert.Equal(1, rig.ClockOf(5));
    });

    [Fact, Covers("AW-42")]
    public void Acceptance_NeedsAHigherClock_OrAnEqualClockNullForAPresentClient() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        rig.Apply(U(5, 2, """{"a":1}"""));
        var n = rig.Events.Count;
        rig.Apply(U(5, 1, """{"a":9}"""));
        rig.Apply(U(5, 2, """{"a":9}"""));
        Assert.Equal(n, rig.Events.Count);
        Assert.Equal("""{"a":1}""", Js.StateJson(rig.Awareness, 5));
        rig.Apply(U(5, 2, "null"));
        Assert.Equal("absent", Js.StateJson(rig.Awareness, 5));
    });

    [Fact, Covers("AW-43")]
    public void FirstMessageFromAnUnknownClient_WithClockZero_IsIgnored() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        Assert.True(rig.Apply(U(5, 0, """{"a":1}""")).Ok);
        Assert.Empty(rig.Events);
        Assert.False(rig.Awareness.Meta.ContainsKey(5));
        rig.Apply(U(5, 1, """{"a":1}"""));
        Assert.Equal("""{"a":1}""", Js.StateJson(rig.Awareness, 5));
    });

    [Fact, Covers("AW-44")]
    public void EqualClock_NonNull_IsIgnored_EqualClockNull_RemovesOnlyAPresentClient() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        rig.Apply(U(5, 3, """{"a":1}"""));
        rig.Apply(U(5, 3, """{"a":2}"""));
        Assert.Equal("""{"a":1}""", Js.StateJson(rig.Awareness, 5));
        rig.Apply(U(5, 3, "null"));
        Assert.Equal("absent", Js.StateJson(rig.Awareness, 5));
        var n = rig.Events.Count;
        rig.Apply(U(5, 3, "null"));
        Assert.Equal(n, rig.Events.Count);
    });

    [Fact, Covers("AW-45")]
    public void RemovingTheLocalClient_IsRefused_TheStateIsKept_AndTheClockIsBumped() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        rig.Awareness.SetLocalState(Js.Obj(("me", 1)));
        var n = rig.Events.Count;
        rig.Apply(U(9, 5, "null"), "r");
        Assert.Equal("""{"me":1}""", Js.StateJson(rig.Awareness, 9));
        Assert.Equal(6, rig.ClockOf(9));
        Assert.Equal(new[] { "change@0 +[] ~[] -[9] origin=r", "update@0 +[] ~[] -[9] origin=r" }, rig.Log(n));
    });

    [Fact, Covers("AW-46")]
    public void ARemoteUpdate_MayOverwriteTheLocalState() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        rig.Awareness.SetLocalState(Js.Obj(("me", 1)));
        rig.Apply(U(9, 7, """{"overwritten":true}"""));
        Assert.Equal("""{"overwritten":true}""", Js.StateJson(rig.Awareness, 9));
        Assert.Equal(7, rig.ClockOf(9));
    });

    [Fact, Covers("AW-47")]
    public void Meta_IsSetOnAcceptance_AndTheTimestampIsSharedByTheWholeBatch() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        rig.Apply(U(1, 1, "{}"));
        rig.Tick(40);
        rig.Apply(AwBytes.Update((1, 1, """{"ignored":true}"""), (2, 1, "{}"), (3, 1, "{}")));
        Assert.Equal(0, rig.LastUpdatedMs(1));
        Assert.Equal(40, rig.LastUpdatedMs(2));
        Assert.Equal(40, rig.LastUpdatedMs(3));
        Assert.Equal(1, rig.ClockOf(2));
    });

    [Fact, Covers("AW-48")]
    public void Classification_FollowsTheDocumentedOrder() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        // unknown + state: added; unknown + null: nowhere
        rig.Apply(AwBytes.Update((1, 1, """{"a":1}"""), (2, 1, "null")), "o");
        Assert.Equal(new[] { "change@0 +[1] ~[] -[] origin=o", "update@0 +[1] ~[] -[] origin=o" }, rig.Log());
        var n = rig.Events.Count;
        // known + state: updated, and in change only when the content differs
        rig.Apply(AwBytes.Update((1, 2, """{"a":1}""")), "same");
        rig.Apply(AwBytes.Update((1, 3, """{"a":2}""")), "diff");
        // known + null: removed
        rig.Apply(AwBytes.Update((1, 4, "null")), "gone");
        Assert.Equal(
            new[]
            {
                "update@0 +[] ~[1] -[] origin=same",
                "change@0 +[] ~[1] -[] origin=diff", "update@0 +[] ~[1] -[] origin=diff",
                "change@0 +[] ~[] -[1] origin=gone", "update@0 +[] ~[] -[1] origin=gone",
            },
            rig.Log(n));
    });

    [Fact, Covers("AW-49")]
    public void Quirks_RevivalIsUpdated_NullForAnAbsentClientIsRemoved_TombstonesRaiseTheBar() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        rig.Apply(U(5, 1, """{"a":1}"""));
        rig.Apply(U(5, 2, "null"));
        var n = rig.Events.Count;
        rig.Apply(U(5, 3, "null"));          // already absent, higher clock: still reported as removed
        Assert.Equal(new[] { "change@0 +[] ~[] -[5] origin=null", "update@0 +[] ~[] -[5] origin=null" }, rig.Log(n));
        n = rig.Events.Count;
        rig.Apply(U(5, 4, """{"a":4}"""));    // revival: updated, not added, and in change.updated
        Assert.Equal(new[] { "change@0 +[] ~[5] -[] origin=null", "update@0 +[] ~[5] -[] origin=null" }, rig.Log(n));
        n = rig.Events.Count;
        rig.Apply(U(77, 3, "null"));          // never seen: tombstone, no event
        Assert.Equal(n, rig.Events.Count);
        Assert.Equal(3, rig.ClockOf(77));
        rig.Apply(U(77, 3, """{"z":1}"""));
        Assert.Equal("absent", Js.StateJson(rig.Awareness, 77));
        rig.Apply(U(77, 4, """{"z":1}"""));
        Assert.Equal("""{"z":1}""", Js.StateJson(rig.Awareness, 77));
    });

    [Fact, Covers("AW-50")]
    public void Events_ChangeFiresBeforeUpdate_HeartbeatsAreUpdateOnly_StaleOrEmptyBatchesAreSilent() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        rig.Apply(U(5, 1, """{"a":1}"""), "x");
        var n = rig.Events.Count;
        rig.Apply(U(5, 2, """{"a":1}"""), "x");
        Assert.Equal(new[] { "update@0 +[] ~[5] -[] origin=x" }, rig.Log(n));
        n = rig.Events.Count;
        rig.Apply(U(5, 1, """{"a":3}"""), "x");
        rig.Apply(AwBytes.Update(), "x");
        Assert.Equal(n, rig.Events.Count);
    });

    [Fact, Covers("AW-52")]
    public void Apply_IsIdempotent_DeliveryOrderAcrossClientsDoesNotMatter_HighestClockWins() => Stub.Run(() =>
    {
        var updates = new[] { U(1, 1, """{"v":1}"""), U(2, 4, """{"v":"b"}"""), U(1, 3, """{"v":3}"""), U(2, 2, """{"v":"old"}"""), U(1, 2, """{"v":2}""") };
        string Final(byte[][] order, int repeat)
        {
            using var rig = new AwRig(9);
            for (var r = 0; r < repeat; r++)
                foreach (var u in order) rig.Apply(u);
            return Js.StateJson(rig.Awareness, 1) + Js.StateJson(rig.Awareness, 2);
        }
        var expected = """{"v":3}{"v":"b"}""";
        Assert.Equal(expected, Final(updates, 1));
        Assert.Equal(expected, Final(updates, 3));
        Assert.Equal(expected, Final(updates.Reverse().ToArray(), 1));
    });
}
