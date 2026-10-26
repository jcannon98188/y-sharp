using System;
using System.Linq;
using Xunit;
using YSharp.Lib0;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Codecs;

/// <summary>
/// The V1 decode contract of ADR 0005 on hand-built updates that need no shared type: atomic decode (a failure changes nothing and
/// returns every rented buffer), limits checked on the claim before anything is allocated for it, and encoding with pending state
/// (merged as Yjs <c>encodeStateAsUpdate</c> merges it; the expected bytes are the pinned oracle's output, see each test).
/// </summary>
[Requires(Feature.V1)]
[Collection("RentedBuffers")]
public sealed class V1DecodeContractTests
{
    // two clients, a GC, strings in root "t", a map entry in root "m" and a delete set: every struct layout V1 has without nested types
    private const string Valid = "02 02 05 00 04 01 01 74 02 61 62 84 05 01 01 63 02 03 00 00 03 28 01 01 6d 01 6b 01 7d 07 01 05 01 00 01";

    private static Doc NewDoc(YLimits? limits = null) => new(new DocOptions { ClientId = 77, Limits = limits ?? YLimits.Default });

    [Fact, Covers("ERR-03", "ERR-05")]
    public void EveryProperPrefix_IsMalformed_ChangesNothing_AndReturnsEveryRentedBuffer()
    {
        var update = Hex.Bytes(Valid);
        Assert.True(NewDoc().TryApplyUpdate(update).Ok);
        var before = BufferPool.Outstanding;
        for (var cut = 0; cut < update.Length; cut++)
        {
            var doc = NewDoc();
            var result = doc.TryApplyUpdate(update.AsSpan(0, cut));
            Assert.Equal(YError.Malformed, result.Error);
            Assert.True(Wire.IsEmpty(StateVector.Of(doc)), $"cut {cut} changed the document");
            Assert.False(doc.HasPending, $"cut {cut} left pending state");
            Assert.Equal(before, BufferPool.Outstanding);
        }
    }

    [Fact, Covers("ERR-09")]
    public void Limits_AtTheLimitAreAccepted_OneOverIsLimitExceeded_AndChangesNothing()
    {
        var update = Hex.Bytes(Valid);
        AssertLimit(update, new YLimits { MaxUpdateBytes = update.Length }, new YLimits { MaxUpdateBytes = update.Length - 1 });
        // four structs over both clients: two strings, the GC and the map entry
        AssertLimit(update, new YLimits { MaxStructsPerUpdate = 4 }, new YLimits { MaxStructsPerUpdate = 3 });
        // the longest string or key is "ab": two bytes
        AssertLimit(update, new YLimits { MaxStringBytes = 2 }, new YLimits { MaxStringBytes = 1 });
        // an Any value [[1]] in root "a" is depth 2
        var nested = Hex.Bytes("01 01 01 00 08 01 01 61 01 75 01 75 01 7d 01 00");
        AssertLimit(nested, new YLimits { MaxAnyDepth = 2 }, new YLimits { MaxAnyDepth = 1 });
    }

    private static void AssertLimit(byte[] update, YLimits atLimit, YLimits overLimit)
    {
        Assert.True(NewDoc(atLimit).TryApplyUpdate(update).Ok);
        var doc = NewDoc(overLimit);
        Assert.Equal(YError.LimitExceeded, doc.TryApplyUpdate(update).Error);
        Assert.True(Wire.IsEmpty(StateVector.Of(doc)));
        Assert.Equal(YError.LimitExceeded, Assert.Throws<YDecodeException>(() => NewDoc(overLimit).ApplyUpdate(update)).Error);
    }

    [Theory, Covers("ERR-09", "ENC-32", "ENC-34")]
    [InlineData("01 80 80 80 80 10 01 00")]                               // 2^32 structs
    [InlineData("80 80 80 80 10")]                                         // 2^32 clients
    [InlineData("01 01 01 00 04 01 01 74 80 80 80 80 10")]                 // a string claiming 4 GB
    [InlineData("01 01 01 00 08 01 01 74 80 80 80 80 10")]                 // an Any content claiming 2^32 values
    [InlineData("00 01 01 80 80 80 80 10")]                                // 2^32 delete ranges
    public void HugeClaims_AreRejected_BeforeAnythingIsSizedFromThem(string hex)
    {
        var update = Hex.Bytes(hex);
        var doc = NewDoc();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var result = doc.TryApplyUpdate(update);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.False(result.Ok);
        Assert.True(allocated < 64 * 1024, $"{hex}: {allocated} bytes allocated");
    }

    [Fact, Covers("ENC-49")]
    public void AnItemThatDependsOnItsOwnClientAtOrAfterItsClock_IsMalformed()
    {
        Assert.Equal(YError.Malformed, NewDoc().TryApplyUpdate(Hex.Bytes("01 01 01 00 84 01 00 01 78 00")).Error);     // origin (1,0) is itself
        Assert.Equal(YError.Malformed, NewDoc().TryApplyUpdate(Hex.Bytes("01 01 01 00 44 01 05 01 78 00")).Error);     // right origin after itself
        Assert.True(NewDoc().TryApplyUpdate(Hex.Bytes("01 02 01 00 04 01 01 74 01 61 84 01 00 01 62 00")).Ok);         // origin (1,0), clock 1
    }

    // ------------------------------------------------------------------ pending state is part of the encoded state (STR-63, STR-64)

    [Fact, Covers("STR-63", "ENC-23")]
    public void PendingStructs_AreEncoded_AsYjsMergesThem()
    {
        // oracle: Y.applyUpdate(d, 0101010504010174017800); Y.encodeStateAsUpdate(d) is the same bytes
        var doc = NewDoc();
        Assert.True(doc.TryApplyUpdate(Hex.Bytes("0101010504010174017800")).Ok);
        Assert.True(doc.HasPending);
        Assert.Equal("0101010504010174017800", Hex.Of(doc.EncodeStateAsUpdate()));

        // oracle: two pending updates of client 1 at clocks 2 and 6 merge with a Skip filling the gap
        var two = NewDoc();
        two.ApplyUpdate(Hex.Bytes("0101010204010174017800"));
        two.ApplyUpdate(Hex.Bytes("0101010604010174017900"));
        Assert.Equal("010301020401017401780a0304010174017900", Hex.Of(two.EncodeStateAsUpdate()));
    }

    [Fact, Covers("STR-63", "STR-64", "ENC-23", "ENC-24")]
    public void StatePendingStructsAndPendingDeletes_AreMergedIntoOneUpdate_AndDiffedAgainstTheTarget()
    {
        // oracle: "ab" of client 2, a struct of client 1 waiting for client 999, a delete of unknown client 5
        var doc = NewDoc();
        doc.ApplyUpdate(Hex.Bytes("0101020004010174026162 00"));
        doc.ApplyUpdate(Hex.Bytes("01 01 01 00 84 e7 07 00 01 78 00"));
        doc.ApplyUpdate(Hex.Bytes("00 01 05 01 03 02"));
        Assert.True(doc.HasPending);
        Assert.Equal("020102000401017402616201010084e7070001780105010302", Hex.Of(doc.EncodeStateAsUpdate()));
        Assert.Equal("02010201840200016201010084e7070001780105010302", Hex.Of(doc.EncodeStateAsUpdate(Hex.Bytes("01 02 01"))));
        Assert.Equal("010202", Hex.Of(Wire.StateVector(doc)));
    }

    [Fact, Covers("ENC-16", "ENC-17")]
    public void DeleteSet_Encode_WritesClientsDescending_AndRangesByClock()
    {
        var doc = NewDoc();
        doc.ApplyUpdate(Hex.Bytes("01 01 05 00 04 01 01 74 03 61 62 63 02 05 01 00 01 07 01 01 02")); // "abc" of client 5; deletes on 5 and unknown 7
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        var ranges = new YSharp.Store.DeleteRanges();
        ranges.Add(5, 0, 1);
        ranges.Add(7, 1, 2);
        ranges.Add(5, 2, 1);
        new DeleteSet(ranges).Encode(writer);
        Assert.Equal("02070101020502000102 01".Replace(" ", ""), Hex.Of(writer.WrittenSpan.ToArray()));
        Assert.True(doc.HasPending);   // the delete on client 7 waits
    }

    [Fact, Covers("ENC-21")]
    public void EncodeStateAsUpdate_ReturnsEveryRentedBuffer()
    {
        var doc = NewDoc();
        doc.ApplyUpdate(Hex.Bytes(Valid));
        var before = BufferPool.Outstanding;
        var once = doc.EncodeStateAsUpdate();
        Assert.Equal(before, BufferPool.Outstanding);
        var again = NewDoc();
        again.ApplyUpdate(once);
        Assert.Equal(once, again.EncodeStateAsUpdate());
        Assert.Equal(before, BufferPool.Outstanding);
    }
}
