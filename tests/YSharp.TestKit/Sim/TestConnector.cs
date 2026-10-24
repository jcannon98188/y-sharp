using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Rng;

namespace YSharp.TestKit.Sim;

/// <summary>
/// One simulated client (<c>TestYInstance</c> in the JS testHelper, SIM-10): a <see cref="Doc"/> plus the queues of
/// messages other clients have sent it. Per-sender queues are FIFO; the connector decides when each is read.
/// </summary>
public sealed class TestYInstance
{
    private readonly List<(TestYInstance Sender, Queue<SimMessage> Messages)> _receiving = new();

    public TestYInstance(TestConnector connector, int userId)
    {
        Tc = connector;
        UserId = userId;
        Doc = new Doc(new DocOptions { ClientId = (uint)userId });
        connector.AllConns.Add(this);
        Doc.Update += OnUpdate;
        Doc.UpdateV2 += OnUpdateV2;
        Connect();
    }

    public Doc Doc { get; }
    public int UserId { get; }
    public TestConnector Tc { get; }

    /// <summary>Every update event in order; <see cref="SimHarness.Compare"/> merges them as an independent oracle.</summary>
    public List<byte[]> Updates { get; } = new();

    public int ReceivingCount => _receiving.Count;

    private void OnUpdate(ReadOnlySpan<byte> update, object? origin, Doc doc, Transaction tx)
    {
        if (Tc.Format != UpdateFormat.V1) return;
        Publish(update, origin, SimFraming.FrameV1Update(update));
    }

    private void OnUpdateV2(ReadOnlySpan<byte> update, object? origin, Doc doc, Transaction tx)
    {
        if (Tc.Format != UpdateFormat.V2) return;
        Publish(update, origin, SimFraming.FrameV2(SyncMessageType.Update, update));
    }

    private void Publish(ReadOnlySpan<byte> update, object? origin, byte[] frame)
    {
        // JS: broadcast first, then push; the message knows its position in this client's update list
        if (!ReferenceEquals(origin, Tc)) Tc.Broadcast(this, new SimMessage(frame, SimMessageKind.Update, Updates.Count));
        Updates.Add(update.ToArray());
    }

    public void Disconnect()
    {
        _receiving.Clear();
        Tc.OnlineConns.Remove(this);
    }

    /// <summary>Joins the online set and exchanges SyncStep1 with every online client.</summary>
    public void Connect()
    {
        if (Tc.OnlineConns.Contains(this)) return;
        Tc.OnlineConns.Add(this);
        Tc.Broadcast(this, Step1Of(this));
        foreach (var remote in Tc.OnlineConns)
        {
            if (!ReferenceEquals(remote, this)) Receive(Step1Of(remote), remote);
        }
    }

    /// <summary>A fresh SyncStep1 for <paramref name="who"/>: the state vector is captured now, under its schedule name.</summary>
    private SimMessage Step1Of(TestYInstance who) =>
        new(SimFraming.Step1(Tc.Format, who.Doc), SimMessageKind.Step1, Name: Tc.Recorder?.StateVector(who.UserId));

    /// <summary>Queues a message; the connector decides when this client reads it.</summary>
    public void Receive(byte[] message, TestYInstance remote) => Receive(new SimMessage(message, KindOf(message)), remote);

    public void Receive(SimMessage message, TestYInstance remote)
    {
        var i = _receiving.FindIndex(e => ReferenceEquals(e.Sender, remote));
        if (i < 0)
        {
            _receiving.Add((remote, new Queue<SimMessage>()));
            i = _receiving.Count - 1;
        }
        _receiving[i].Messages.Enqueue(message);
    }

    /// <summary>Both framings start with the message type as a varuint (0 step 1, 1 step 2, 2 update).</summary>
    private static SimMessageKind KindOf(byte[] frame) => (SimMessageKind)frame[0];

    internal IReadOnlyList<(TestYInstance Sender, Queue<SimMessage> Messages)> Pending => _receiving;

    internal SimMessage Dequeue(TestYInstance sender)
    {
        var i = _receiving.FindIndex(e => ReferenceEquals(e.Sender, sender));
        var m = _receiving[i].Messages.Dequeue();
        if (_receiving[i].Messages.Count == 0) _receiving.RemoveAt(i);
        return m;
    }
}

/// <summary>
/// Port of the JS <c>TestConnector</c> (SIM-10): decides when queued messages are delivered and which clients are online.
/// Origin of every message it delivers is the connector itself, so delivered updates are not re-broadcast.
/// </summary>
public sealed class TestConnector
{
    public TestConnector(IPrng gen, UpdateFormat format = UpdateFormat.V1, ISimRecorder? recorder = null)
    {
        Gen = gen;
        Format = format;
        Recorder = recorder;
    }

    public IPrng Gen { get; }

    /// <summary>The encoding this whole run uses (SIM-04: the init coin flip really selects it).</summary>
    public UpdateFormat Format { get; }

    /// <summary>When set, receives the op log of the run (edits made through <see cref="Recorder"/> by the generators, and the delivery schedule).</summary>
    public ISimRecorder? Recorder { get; }

    public OrderedSet<TestYInstance> AllConns { get; } = new();
    public OrderedSet<TestYInstance> OnlineConns { get; } = new();
    public PayloadCounters Counters { get; } = new();

    public TestYInstance CreateY(int clientId) => new(this, clientId);

    /// <summary>Publish a message created by <paramref name="from"/> to all other online clients.</summary>
    internal void Broadcast(TestYInstance from, SimMessage message)
    {
        if (!OnlineConns.Contains(from)) return;
        foreach (var remote in OnlineConns)
            if (!ReferenceEquals(remote, from)) remote.Receive(message, from);
    }

    /// <summary>Choose a random online connection with pending messages and flush one random sender's oldest message.</summary>
    public bool FlushRandomMessage()
    {
        var conns = OnlineConns.Where(c => c.ReceivingCount > 0).ToList();
        if (conns.Count == 0) return false;
        var receiver = Rng.Prng.OneOf(Gen, conns);
        var (sender, _) = Rng.Prng.OneOf(Gen, receiver.Pending.ToList());
        var message = receiver.Dequeue(sender);
        string? replyName = null;
        switch (message.Kind)
        {
            case SimMessageKind.Step1: replyName = Recorder?.Reply(receiver.UserId, message.Name!); break;
            case SimMessageKind.Step2: Recorder?.ApplyReply(receiver.UserId, message.Name!); break;
            default: Recorder?.ApplyUpdate(receiver.UserId, sender.UserId, message.UpdateIndex); break;
        }
        var reply = SimFraming.Read(Format, message.Frame, receiver.Doc, this);
        if (reply is { Length: > 0 }) sender.Receive(new SimMessage(reply, SimMessageKind.Step2, Name: replyName), receiver);
        return true;
    }

    public bool FlushAllMessages()
    {
        var didSomething = false;
        while (FlushRandomMessage()) didSomething = true;
        return didSomething;
    }

    public void ReconnectAll()
    {
        foreach (var c in AllConns) c.Connect();
    }

    public void DisconnectAll()
    {
        foreach (var c in AllConns) c.Disconnect();
    }

    public void SyncAll()
    {
        ReconnectAll();
        FlushAllMessages();
    }

    public bool DisconnectRandom()
    {
        if (OnlineConns.Count == 0) return false;
        Rng.Prng.OneOf(Gen, OnlineConns.ToArray()).Disconnect();
        return true;
    }

    public bool ReconnectRandom()
    {
        var reconnectable = AllConns.Where(c => !OnlineConns.Contains(c)).ToList();
        if (reconnectable.Count == 0) return false;
        Rng.Prng.OneOf(Gen, reconnectable).Connect();
        return true;
    }
}
