using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Structs;

/// <summary>Client ids in the struct store (quirks Q20, Q21): where they come from and how they compare at the 32-bit extremes.</summary>
[Requires(Feature.V1)]
public sealed class ClientIdTests
{
    private sealed class ScriptedIds : IClientIdGenerator
    {
        private readonly Queue<ulong> _ids;
        public ScriptedIds(params ulong[] ids) => _ids = new Queue<ulong>(ids);
        public int Calls { get; private set; }
        public ulong Next() { Calls++; return _ids.Dequeue(); }
    }

    [Fact]
    [Covers("Q20")]
    [Requires(Feature.Text)]
    public void ClientIds_AreDrawnFromAGenerator_AndAreRegeneratedFromItOnACollision()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => new Doc().ClientId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());           // random 32-bit values: 50 draws do not collide

        var generator = new ScriptedIds(1, 99);
        var b = new Doc(new DocOptions { ClientIdGenerator = generator });
        Assert.Equal(1UL, b.ClientId);
        var a = Net.Mk(1);
        a.GetText("t").Insert(0, "abc");
        Net.Sync(a, b);                                            // a's structs carry b's id: b picks the next generated id
        Assert.Equal(99UL, b.ClientId);
        Assert.Equal(2, generator.Calls);
    }

    [Fact]
    [Covers("Q21", "STR-02")]
    [Requires(Feature.Text)]
    public void ClientIds_AtTheExtremesOf32Bits_OrderAndCompareAsUnsignedNumbers()
    {
        var a = Net.Mk(4294967295);
        var b = Net.Mk(2147483648);
        var c = Net.Mk(1);
        a.GetText("t").Insert(0, "A");
        b.GetText("t").Insert(0, "B");
        c.GetText("t").Insert(0, "C");
        var all = new[] { (a, b), (b, a), (a, c), (c, a), (b, c), (c, b) };
        foreach (var (x, y) in all) Net.Sync(x, y);
        a.GetText("t").Insert(1, "x");
        b.GetText("t").Insert(1, "y");
        c.GetText("t").Insert(1, "z");
        foreach (var (x, y) in all) Net.Sync(x, y);

        foreach (var d in new[] { a, b, c })
        {
            Assert.Equal("CzyxBA", d.GetText("t").ToString());
            Assert.Equal(new ulong[] { 4294967295, 2147483648, 1 }, StructsProbe.Dump(d).Select(s => s.Client));   // descending, unsigned
            Assert.Equal(2L, d.GetStateVector()[4294967295]);
        }
    }
}
