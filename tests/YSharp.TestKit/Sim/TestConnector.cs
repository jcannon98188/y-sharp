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
    private readonly List<(TestYInstance Sender, Queue<byte[]> Messages)> _receiving = new();

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

    private void Publish(ReadOnlySpan<byte> update, object? origin, byte[] message)
    {
        if (!ReferenceEquals(origin, Tc)) Tc.Broadcast(this, message);
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
        Tc.Broadcast(this, SimFraming.Step1(Tc.Format, Doc));
        foreach (var remote in Tc.OnlineConns)
        {
            if (!ReferenceEquals(remote, this)) Receive(SimFraming.Step1(Tc.Format, remote.Doc), remote);
        }
    }

    /// <summary>Queues a message; the connector decides when this client reads it.</summary>
    public void Receive(byte[] message, TestYInstance remote)
    {
        var i = _receiving.FindIndex(e => ReferenceEquals(e.Sender, remote));
        if (i < 0)
        {
            _receiving.Add((remote, new Queue<byte[]>()));
            i = _receiving.Count - 1;
        }
        _receiving[i].Messages.Enqueue(message);
    }

    internal IReadOnlyList<(TestYInstance Sender, Queue<byte[]> Messages)> Pending => _receiving;

    internal byte[] Dequeue(TestYInstance sender)
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
    public TestConnector(IPrng gen, UpdateFormat format = UpdateFormat.V1)
    {
        Gen = gen;
        Format = format;
    }

    public IPrng Gen { get; }

    /// <summary>The encoding this whole run uses (SIM-04: the init coin flip really selects it).</summary>
    public UpdateFormat Format { get; }

    public OrderedSet<TestYInstance> AllConns { get; } = new();
    public OrderedSet<TestYInstance> OnlineConns { get; } = new();
    public PayloadCounters Counters { get; } = new();

    public TestYInstance CreateY(int clientId) => new(this, clientId);

    /// <summary>Publish a message created by <paramref name="from"/> to all other online clients.</summary>
    internal void Broadcast(TestYInstance from, byte[] message)
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
        var reply = SimFraming.Read(Format, message, receiver.Doc, this);
        if (reply is { Length: > 0 }) sender.Receive(reply, receiver);
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
