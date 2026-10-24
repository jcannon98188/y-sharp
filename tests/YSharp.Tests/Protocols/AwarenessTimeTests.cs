using System;
using System.Linq;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.TestKit.Time;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Protocols;

/// <summary>The check timer, local renewal and remote expiry (AW-14..21) on a virtual <see cref="TimeProvider"/>; no test waits on a real clock.</summary>
public sealed class AwarenessTimeTests
{
    private static byte[] U(uint id, long clock, string json) => AwBytes.Update((id, clock, json));

    [Fact, Covers("AW-14")]
    public void CheckTimer_Default_IsOneTimerWithAPeriodOf3000Milliseconds() => Stub.Run(() =>
    {
        var clock = new VirtualTimeProvider();
        var counting = new CountingTimeProvider(clock);
        using var rig = new AwRig(1, clock, counting);
        var (due, period) = Assert.Single(counting.Created);
        Assert.Equal(TimeSpan.FromMilliseconds(3000), period);
        Assert.Equal(TimeSpan.FromMilliseconds(3000), due);
    });

    [Fact, Diverges("AW-21", adr: "0014")]
    public void CheckTimer_IsCreatedThroughTheTimeProvider_AndReleasedOnDispose() => Stub.Run(() =>
    {
        // JS setInterval keeps a Node process alive until destroy(). The port owns one ITimer from the injected TimeProvider and
        // disposes it with the awareness, so nothing outlives Dispose.
        var clock = new VirtualTimeProvider();
        var counting = new CountingTimeProvider(clock);
        var rig = new AwRig(1, clock, counting);
        Assert.Equal(0, counting.Disposed);
        rig.Dispose();
        Assert.Equal(1, counting.Disposed);
        rig.Dispose();
        Assert.Single(counting.Created);
        Assert.Equal(1, counting.Disposed);
    });

    [Fact, Covers("AW-15", "AW-9")]
    public void LocalState_IsRenewedAsAHeartbeat_OnlyOnceItIs15000MillisecondsOld() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        rig.Tick(1);
        rig.Awareness.SetLocalState(Js.Obj(("me", 1)));
        var n = rig.Events.Count;
        var clock = rig.ClockOf(9);
        rig.Tick(14999);                       // t = 15000: the timer fires, the state is 14999 ms old
        Assert.Equal(n, rig.Events.Count);
        rig.Tick(3000);                        // t = 18000: renewed
        Assert.Equal(new[] { "update@18000 +[] ~[9] -[] origin=local" }, rig.Log(n));
        Assert.Equal(clock + 1, rig.ClockOf(9));
        Assert.Equal(18000, rig.LastUpdatedMs(9));
    });

    [Fact, Covers("AW-15", "AW-17")]
    public void LocalRenewal_BoundaryIsInclusive_ExactlyFifteenSecondsRenews() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        rig.Awareness.SetLocalState(Js.Obj(("me", 1)));
        var n = rig.Events.Count;
        rig.Tick(14999);
        Assert.Equal(n, rig.Events.Count);
        rig.Tick(1);
        Assert.Equal(new[] { "update@15000 +[] ~[9] -[] origin=local" }, rig.Log(n));
    });

    [Fact, Covers("AW-16", "AW-17")]
    public void RemoteClients_AreRemovedAtThirtySecondsInclusive_OnThe3000MillisecondGrid() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        rig.Apply(U(5, 1, """{"who":"five"}"""));
        rig.Tick(1);
        rig.Apply(U(6, 1, """{"who":"six"}"""));
        rig.Tick(26999);
        Assert.Equal(new[] { "5", "6", "9" }, Js.Keys(rig.Awareness));
        rig.Tick(3000);                        // t = 30000: client 5 is exactly 30000 old, client 6 is 29999 old
        Assert.Equal(new[] { "6", "9" }, Js.Keys(rig.Awareness));
        rig.Tick(2999);
        Assert.Equal(new[] { "6", "9" }, Js.Keys(rig.Awareness));
        rig.Tick(1);                           // t = 33000
        Assert.Equal(new[] { "9" }, Js.Keys(rig.Awareness));
        var removals = rig.Events.Where(e => e.Removed.Length > 0).ToList();
        Assert.Equal(new[] { "change", "update", "change", "update" }, removals.Select(e => e.Name));
        Assert.All(removals, e => Assert.Equal("timeout", e.Origin));
        Assert.Equal(new long[] { 30000, 30000, 33000, 33000 }, removals.Select(e => e.AtMs));
    });

    [Fact, Covers("AW-16")]
    public void ClientsExpiringTogether_OnTheSameTick_AreRemovedInOneBatch() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        rig.Apply(AwBytes.Update((5, 1, "{}"), (6, 1, "{}")));
        rig.Tick(30000);
        var removal = rig.Events.Where(e => e.Name == "change" && e.Removed.Length > 0).ToList();
        Assert.Equal(new[] { 5u, 6u }, Assert.Single(removal).Removed);
    });

    [Fact, Covers("AW-18", "AW-49")]
    public void ARemovedRemoteClient_IsNotRemovedAgain_AndKeepsItsTombstoneClock() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        rig.Apply(U(5, 1, """{"who":"five"}"""));
        rig.Tick(30000);
        var n = rig.Events.Count;
        rig.Tick(90000);
        Assert.DoesNotContain(rig.Events.Skip(n), e => e.Removed.Length > 0);
        Assert.Equal(1, rig.ClockOf(5));
        rig.Apply(U(5, 1, """{"who":"stale"}"""));
        Assert.Equal("absent", Js.StateJson(rig.Awareness, 5));
        rig.Apply(U(5, 2, """{"who":"back"}"""));
        Assert.Equal("""{"who":"back"}""", Js.StateJson(rig.Awareness, 5));
        Assert.Equal(new[] { 5u }, rig.Events[^1].Updated);
    });

    [Fact, Covers("AW-19")]
    public void TheLocalClient_IsNeverExpired_OnlyRenewed() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        rig.Awareness.SetLocalState(Js.Obj(("me", 1)));
        rig.Tick(300000);
        Assert.Equal("""{"me":1}""", Js.StateJson(rig.Awareness, 9));
        Assert.DoesNotContain(rig.Events, e => e.Removed.Length > 0);
        Assert.True(rig.ClockOf(9) > 10);
    });

    [Fact, Covers("AW-20")]
    public void ANonNullLocalState_AlwaysHasMeta_SoTheTimerCannotFail() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        Assert.True(rig.Awareness.Meta.ContainsKey(9));
        rig.Awareness.SetLocalState(YValue.Null);
        rig.Awareness.SetLocalState(Js.Obj(("a", 1)));
        rig.Apply(U(9, 50, """{"b":1}"""));
        rig.Awareness.RemoveStates(new uint[] { 9 }, null);
        Assert.True(rig.Awareness.Meta.ContainsKey(9));
        rig.Tick(120000);
    });
}
