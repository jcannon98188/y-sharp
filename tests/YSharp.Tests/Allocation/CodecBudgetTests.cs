using System;
using System.Text;
using Xunit;
using YSharp.Lib0;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Allocation;

/// <summary>
/// BUD-06 (ADR 0009) on a document built from an update alone, so it measures the codec before the shared types land: the same content as
/// <c>AllocDocs.Populated</c> (200 characters of text, 20 array values, 20 map entries) applied as one V1 update. Same serial collection as
/// <see cref="HotPathBudgetTests"/>, Release only.
/// </summary>
[Requires(Feature.V1, Feature.Gc)]
[Collection("Allocation")]
[Tier(Tier.Gating)]
public sealed class CodecBudgetTests
{
    [Fact, Covers("BUD-06")]
    public void EncodeStateAsUpdate_OfAPopulatedDoc_IntoAPreSizedWriter_AllocatesNothing()
    {
        var doc = new Doc(new DocOptions { ClientId = 9 });
        doc.ApplyUpdate(PopulatedUpdate());
        var writer = Writers.PreSized();
        AllocationBudgets.Check("BUD-06", "", () =>
        {
            writer.Clear();
            doc.EncodeStateAsUpdate(writer);
        });
        Assert.True(writer.WrittenCount > 300);
    }

    /// <summary>Client 1: "a" x 200 in root "t", [0..19] in root "a", and "k0".."k19" = i in root "m".</summary>
    private static byte[] PopulatedUpdate()
    {
        using var w = new Lib0Writer();
        w.WriteVarUint(1);
        w.WriteVarUint(22);
        w.WriteVarUint(1);
        w.WriteVarUint(0);
        w.WriteUint8(0x04); w.WriteVarUint(1); w.WriteVarString("t"); w.WriteVarString(new string('a', 200));
        w.WriteUint8(0x08); w.WriteVarUint(1); w.WriteVarString("a"); w.WriteVarUint(20);
        for (var i = 0; i < 20; i++) { w.WriteUint8(0x7d); w.WriteVarInt(i); }
        for (var i = 0; i < 20; i++)
        {
            w.WriteUint8(0x28); w.WriteVarUint(1); w.WriteVarString("m"); w.WriteVarString("k" + i);
            w.WriteVarUint(1); w.WriteUint8(0x7d); w.WriteVarInt(i);
        }

        w.WriteVarUint(0);
        return w.ToArray();
    }
}
