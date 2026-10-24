using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Sim;

namespace YSharp.Tests.DocFeatures;

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

/// <summary>Names the JS <c>testHelper.init</c> gives its roots, and the replicas that go with them (client ids 0..n-1, all synced).</summary>
internal sealed class Users
{
    private Users(SimResult<object?> sim) => Sim = sim;

    public SimResult<object?> Sim { get; }
    public TestConnector Connector => Sim.TestConnector;
    public Doc Doc(int i) => Sim.Users[i].Doc;
    public YArray Array(int i) => Doc(i).GetArray("array");
    public YMap Map(int i) => Doc(i).GetMap("map");
    public YText Text(int i) => Doc(i).GetText("text");
    public YXmlElement Xml(int i) => Doc(i).GetXmlElement("xml");
    public void SyncAll() => Connector.SyncAll();

    /// <summary>JS <c>init(tc, {users})</c> with a fixed PRNG seed (the seed only picks V1 or V2 framing and the delivery order).</summary>
    public static Users Init(int users, uint seed = 1)
    {
        var gen = YSharp.TestKit.Rng.Prng.Create(seed);
        return new Users(SimHarness.Init(gen, users));
    }
}
