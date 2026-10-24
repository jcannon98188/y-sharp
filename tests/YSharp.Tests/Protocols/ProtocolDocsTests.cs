using System;
using System.Linq;
using System.Reflection;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.TestKit.Time;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Protocols;

/// <summary>
/// PROTOCOL.md versus the code (PR-1..6, flagged in docs/research/y-protocols.md section 5): y-sharp follows the code, so each
/// test pins the code's behaviour where the prose is wrong or silent. Plus Q32, time and timers, under the TimeProvider decision of ADR 0014.
/// </summary>
public sealed class ProtocolDocsTests
{
    private static string[] PublicMembers(Type t) =>
        t.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(m => m.Name).ToArray();

    [Fact, Covers("PR-1")]
    public void ThereIsNoReadAwarenessMessage_ApplyingAnUpdateIsTheEntryPoint()
    {
        Assert.DoesNotContain("ReadAwarenessMessage", PublicMembers(typeof(Awareness)));
        Assert.Contains("TryApplyUpdate", PublicMembers(typeof(Awareness)));
    }

    [Fact, Covers("PR-2")]
    public void TheEnvelopeConstants_AreNotPartOfTheProtocolTypes()
    {
        var names = PublicMembers(typeof(YSync)).Concat(PublicMembers(typeof(Awareness))).Concat(PublicMembers(typeof(YAuth)));
        Assert.DoesNotContain(names, n => n.Contains("Envelope", StringComparison.Ordinal) || n.Contains("ProtocolMessageType", StringComparison.Ordinal));
    }

    [Fact, Covers("PR-3")]
    public void TheClockRule_HasTheEqualClockNullExceptionThatTheProseOmits() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        rig.Apply(AwBytes.Update((5, 3, """{"a":1}""")));
        rig.Apply(AwBytes.Update((5, 3, "null")));
        Assert.Equal("absent", Js.StateJson(rig.Awareness, 5));
    });

    [Fact, Covers("PR-4")]
    public void ClientsExpireAtExactlyThirtySecondsOnTheCheckGrid_NotAfterMoreThanThirty() => Stub.Run(() =>
    {
        using var rig = new AwRig(9);
        rig.Apply(AwBytes.Update((5, 1, "{}")));
        rig.Tick(29999);
        Assert.Contains("5", Js.Keys(rig.Awareness));
        rig.Tick(1);
        Assert.DoesNotContain("5", Js.Keys(rig.Awareness));
    });

    [Fact, Covers("PR-5")]
    public void ReadingStep2_DoesNotTrackASyncState_ThatIsTheProvidersJob() => Stub.Run(() =>
    {
        var source = SyncSupport.DocWith(1, "hello");
        var peer = SyncSupport.DocWith(2);
        SyncSupport.Read(peer, SyncSupport.Step2(source));
        Assert.False(peer.IsSynced);
    });

    [Fact, Covers("PR-6")]
    public void SyncMessages_StartWithTheSyncTypeByte_NotAnEnvelopeByte() => Stub.Run(() =>
    {
        var doc = SyncSupport.DocWith(1, "hello");
        Assert.Equal(1, SyncSupport.Step2(doc)[0]);
        Assert.Equal(2, SyncSupport.Update(new byte[] { 1 })[0]);
    });

    [Fact, Diverges("Q32", adr: "0014")]
    public void Time_IsInjectedThroughTimeProvider_WhileTheTimeoutValuesAreMatched() => Stub.Run(() =>
    {
        // JS reads Date.now and calls setInterval; y-sharp takes a TimeProvider, so a virtual clock decides both. The semantics
        // (30000 ms timeout, 3000 ms check, 15000 ms renewal) are the same, and no real time passes in this test.
        var clock = new VirtualTimeProvider();
        var counting = new CountingTimeProvider(clock);
        using var rig = new AwRig(9, clock, counting);
        Assert.Equal(TimeSpan.FromMilliseconds(3000), counting.Created.Single().Period);
        Assert.Equal(TimeSpan.FromMilliseconds(30000), Awareness.OutdatedTimeout);
        rig.Awareness.SetLocalState(Js.Obj(("me", 1)));
        var before = rig.Events.Count;
        rig.Tick(15000);
        Assert.Equal(before + 1, rig.Events.Count);
    });
}
