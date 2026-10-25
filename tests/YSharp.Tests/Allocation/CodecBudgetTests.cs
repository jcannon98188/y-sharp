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
/// <see cref="HotPathBudgetTests"/>, Release only. BUD-08 likewise, on updates built by hand.
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

    /// <summary>
    /// BUD-08 on the codec alone: the same stream as <see cref="HotPathBudgetTests"/> (one-character text inserts arriving one update at a time),
    /// built as V1 updates and converted to V2 with <see cref="YUpdate.TryConvert"/>, so it runs before the shared types land.
    /// </summary>
    [Theory, Covers("BUD-08")]
    [InlineData(UpdateFormat.V1)]
    [InlineData(UpdateFormat.V2)]
    [Requires(Feature.V2, Feature.V2Functions)]
    public void TryApplyUpdate_OfAOneCharacterInsertStream_StaysWithinItsBudget(UpdateFormat format)
    {
        var updates = InsertStream(200 + 50 + 8, format);
        var target = new Doc();
        var next = 0;
        AllocationBudgets.Check("BUD-08", format.ToString(), () =>
        {
            var result = target.TryApplyUpdate(updates[next++ % updates.Length], null, format);
            if (!result.Ok) throw new InvalidOperationException("the update was rejected: " + result.Error);
        });
    }

    /// <summary>Client 1 typing "x" at the end of root text "t": update i holds the one struct at clock i, its origin the previous character.</summary>
    private static byte[][] InsertStream(int count, UpdateFormat format)
    {
        var updates = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            using var w = new Lib0Writer();
            w.WriteVarUint(1);
            w.WriteVarUint(1);
            w.WriteVarUint(1);
            w.WriteVarUint((ulong)i);
            if (i == 0)
            {
                w.WriteUint8(0x04); w.WriteVarUint(1); w.WriteVarString("t");
            }
            else
            {
                w.WriteUint8(0x84); w.WriteVarUint(1); w.WriteVarUint((ulong)(i - 1));
            }

            w.WriteVarString("x");
            w.WriteVarUint(0);
            updates[i] = w.ToArray();
            if (format == UpdateFormat.V2)
            {
                var v2 = new System.Buffers.ArrayBufferWriter<byte>();
                Assert.True(YUpdate.TryConvert(updates[i], UpdateFormat.V1, UpdateFormat.V2, v2).Ok);
                updates[i] = v2.WrittenSpan.ToArray();
            }
        }

        return updates;
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
