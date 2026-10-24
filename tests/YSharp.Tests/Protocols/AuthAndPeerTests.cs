using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Protocols;

/// <summary>auth.js (AU-1..7): one message type, a UTF-8 reason, a handler. Golden bytes are in fixtures/auth.</summary>
public sealed class AuthTests
{
    private static byte[] Denied(string reason)
    {
        var w = new ArrayBufferWriter<byte>();
        YAuth.WritePermissionDenied(w, reason);
        return w.WrittenSpan.ToArray();
    }

    private static YStatus Read(ref ReadOnlySpan<byte> input, Doc doc, List<(Doc, string)> calls) =>
        YAuth.TryReadMessage(ref input, doc, (d, r) => calls.Add((d, r)));

    [Fact, Covers("AU-1", "AU-2", "AU-7")]
    public void PermissionDenied_AnyReason_IsTypeZeroThenTheLengthPrefixedUtf8Reason() => Stub.Run(() =>
    {
        Assert.Equal(new byte[] { 0, 0 }, Denied(""));
        var reason = "zugriff verweigert: ä ✓ \U0001F600";
        var utf8 = Encoding.UTF8.GetBytes(reason);
        Assert.True(utf8.Length > reason.Length);
        Assert.Equal(new byte[] { 0, (byte)utf8.Length }.Concat(utf8), Denied(reason));
        Assert.Equal(new byte[] { 0, 0xc8, 0x01 }, Denied(new string('x', 200)).Take(3));
    });

    [Fact, Covers("AU-3", "AU-6", "AU-7")]
    public void ReadAuthMessage_CallsTheHandlerWithTheDocAndReason_AndRoundTrips() => Stub.Run(() =>
    {
        var doc = new Doc();
        var calls = new List<(Doc, string)>();
        ReadOnlySpan<byte> input = Denied("no ✓");
        var status = Read(ref input, doc, calls);
        Assert.True(status.Ok);
        Assert.Equal(0, input.Length);
        var (d, reason) = Assert.Single(calls);
        Assert.Same(doc, d);
        Assert.Equal("no ✓", reason);
    });

    [Fact, Covers("AU-4")]
    public void UnknownMessageTypes_AreIgnored_AndOnlyTheTypeIsConsumed() => Stub.Run(() =>
    {
        var calls = new List<(Doc, string)>();
        ReadOnlySpan<byte> input = new byte[] { 7, 1, 2, 3 };
        var status = Read(ref input, new Doc(), calls);
        Assert.True(status.Ok);
        Assert.Empty(calls);
        Assert.Equal(3, input.Length);
    });

    [Fact, Diverges("AU-5", adr: "0005")]
    public void ATruncatedReason_IsAnErrorResult_AndANullHandlerIsRefusedUpFront() => Stub.Run(() =>
    {
        // JS throws a decoder error for a truncated reason and a TypeError for a missing handler. Bad bytes are a status (ADR 0005);
        // a null handler is a programming error and throws ArgumentNullException.
        var calls = new List<(Doc, string)>();
        ReadOnlySpan<byte> input = new byte[] { 0, 5, 65 };
        Assert.False(Read(ref input, new Doc(), calls).Ok);
        Assert.Empty(calls);
        Assert.Throws<ArgumentNullException>(() =>
        {
            ReadOnlySpan<byte> bytes = Denied("x");
            YAuth.TryReadMessage(ref bytes, new Doc(), null!);
        });
    });
}

/// <summary>
/// The two-peer convergence harness (ADR 0014), ported from the Yjs <c>TestConnector</c> flow in testHelper.js for exactly two
/// peers: each side sends Step1 on connect, replies Step2, and forwards its local update events as Update messages. Messages sit in
/// queues so tests choose when each is delivered.
/// </summary>
internal sealed class TwoPeerHarness
{
    private readonly Queue<byte[]> _toA = new(), _toB = new();

    public TwoPeerHarness(uint clientA = 1, uint clientB = 2)
    {
        A = new Doc(new DocOptions { ClientId = clientA });
        B = new Doc(new DocOptions { ClientId = clientB });
        A.Update += (u, origin, _, _) => { if (!ReferenceEquals(origin, this)) _toB.Enqueue(SyncSupport.Update(u.ToArray())); };
        B.Update += (u, origin, _, _) => { if (!ReferenceEquals(origin, this)) _toA.Enqueue(SyncSupport.Update(u.ToArray())); };
    }

    public Doc A { get; }
    public Doc B { get; }
    public int Pending => _toA.Count + _toB.Count;

    /// <summary>Both peers announce themselves: each sends Step1 to the other.</summary>
    public void Connect()
    {
        _toB.Enqueue(SyncSupport.Step1(A));
        _toA.Enqueue(SyncSupport.Step1(B));
    }

    /// <summary>Delivers the oldest message to <paramref name="peer"/>; a Step1 queues the Step2 reply back. False when none is waiting.</summary>
    public bool DeliverTo(Doc peer)
    {
        var inbox = ReferenceEquals(peer, A) ? _toA : _toB;
        var outbox = ReferenceEquals(peer, A) ? _toB : _toA;
        if (inbox.Count == 0) return false;
        var read = SyncSupport.Read(peer, inbox.Dequeue(), this);
        Assert.True(read.Status.Ok);
        if (read.Reply.Length > 0) outbox.Enqueue(read.Reply);
        return true;
    }

    public void Flush()
    {
        while (DeliverTo(A) | DeliverTo(B)) { }
    }

    public void AssertConverged()
    {
        Assert.Equal(A.EncodeStateAsUpdate(), B.EncodeStateAsUpdate());
        Assert.Equal(Wire.StateVector(A), Wire.StateVector(B));
    }
}

public sealed class TwoPeerConvergenceTests
{
    [Fact, Covers("SY-13", "SY-14")]
    public void PeerToPeer_BothSendStep1_EachReplies_ThenUpdatesAreForwarded() => Stub.Run(() =>
    {
        var h = new TwoPeerHarness();
        h.A.GetText("t").Insert(0, "hello");
        h.B.GetText("t").Insert(0, "xy");
        h.Connect();
        h.Flush();
        h.AssertConverged();

        h.A.GetText("t").Insert(0, "A");
        h.B.GetText("t").Insert(0, "B");
        h.Flush();
        h.AssertConverged();
        Assert.Equal(h.A.GetText("t").ToString(), h.B.GetText("t").ToString());
    });

    [Fact, Covers("SY-14")]
    public void EditsBetweenStep1AndStep2_StillConverge_BecauseStep2IsComputedFromTheCurrentState() => Stub.Run(() =>
    {
        var h = new TwoPeerHarness();
        h.A.GetText("t").Insert(0, "base");
        h.Connect();
        h.A.GetText("t").Insert(4, "!");             // after A's Step1 was written
        h.B.GetText("t").Insert(0, "pre-");          // after B's Step1 was written
        h.Flush();
        h.AssertConverged();
        Assert.Contains("base", h.A.GetText("t").ToString());
    });

    [Fact, Covers("SY-10", "SY-12", "SY-15", Layer = CoverLayer.Port)]
    public void TheConnectorFlow_DoesNotEchoRemoteUpdates_AndOnlyStep1ProducesAReply() => Stub.Run(() =>
    {
        var h = new TwoPeerHarness();
        h.A.GetText("t").Insert(0, "hello");
        h.Connect();
        h.Flush();
        Assert.Equal(0, h.Pending);
        h.A.GetText("t").Insert(0, "1");
        Assert.Equal(1, h.Pending);
        h.Flush();
        Assert.Equal(0, h.Pending);                  // B applied it with the harness as origin, so it did not echo it back
        h.AssertConverged();
    });
}
