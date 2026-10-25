using System;
using Xunit;
using YSharp.Lib0;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Structs;

/// <summary>
/// Garbage collection and the commit merge (TASK-4.15) through updates alone, no shared type: content is applied, a delete set deletes it,
/// and the encoded state shows what the commit did. Every expected byte string is the pinned oracle's output (Yjs 13.6.33, a fresh Doc with
/// clientID 77 applying the same updates, then <c>encodeStateAsUpdate</c>).
/// </summary>
[Requires(Feature.V1, Feature.Gc)]
public sealed class GcV1Tests
{
    private static Doc Apply(DocOptions options, params string[] updates)
    {
        var doc = new Doc(options with { ClientId = 77 });
        foreach (var u in updates) doc.ApplyUpdate(Hex.Bytes(u));
        return doc;
    }

    [Fact, Covers("GC-01", "GC-06", "ENC-19")]
    public void DeletedText_IsCollectedIntoDeletedContent_OnlyWhenGcIsOn()
    {
        var on = Apply(new DocOptions(), "01 01 28 00 04 01 01 74 05 68 65 6c 6c 6f 00", "00 01 28 01 00 05");
        Assert.Equal("0101280001010174050128010005", Hex.Of(on.EncodeStateAsUpdate()));
        var off = Apply(new DocOptions { Gc = false }, "01 01 29 00 04 01 01 74 05 68 65 6c 6c 6f 00", "00 01 29 01 00 05");
        Assert.Equal("01012900040101740568656c6c6f0129010005", Hex.Of(off.EncodeStateAsUpdate()));
    }

    [Fact, Covers("GC-01")]
    public void AGcFilterReturningFalse_KeepsTheContent()
    {
        var doc = Apply(new DocOptions { GcFilter = _ => false }, "01 01 28 00 04 01 01 74 05 68 65 6c 6c 6f 00", "00 01 28 01 00 05");
        Assert.Equal("01012800040101740568656c6c6f0128010005", Hex.Of(doc.EncodeStateAsUpdate()));
    }

    [Fact, Covers("GC-03", "GC-08", "STR-65")]
    public void ADeleteInsideAnItem_SplitsIt_AndCollectsOnlyTheMiddle()
    {
        var doc = Apply(new DocOptions(), "01 01 01 00 04 01 01 74 05 61 62 63 64 65 00", "00 01 01 01 01 02");
        Assert.Equal("01030100040101740161810100028401020264650101010102", Hex.Of(doc.EncodeStateAsUpdate()));
    }

    [Fact, Covers("GC-04", "GC-08")]
    public void ADeletedNestedType_TurnsEverythingInsideIntoOneMergedGcStruct()
    {
        var chain = Chain(5);
        Assert.Equal("01020100010101640100040101010005", Hex.Of(Apply(new DocOptions(), chain, "00 01 01 01 00 01").EncodeStateAsUpdate()));
        Assert.Equal("01050100070101640007000100000700010100070001020007000103000101010005",
            Hex.Of(Apply(new DocOptions { Gc = false }, chain, "00 01 01 01 00 01").EncodeStateAsUpdate()));

        // a map type with a live entry and an overwritten one: both values and the key chain are collected
        var map = Apply(new DocOptions(), "01 03 01 00 07 01 01 6d 01 28 00 01 00 01 6b 01 77 01 61 a8 01 01 01 77 01 62 00", "00 01 01 01 00 01");
        Assert.Equal("010201000101016d0100020101010003", Hex.Of(map.EncodeStateAsUpdate()));
    }

    [Fact, Covers("GC-04", "HOS-DEEP")]
    public void CollectingA100kDeepTypeChain_IsIterative()
    {
        const int depth = 100_000;
        var doc = new Doc(new DocOptions { ClientId = 77 });
        doc.ApplyUpdate(Hex.Bytes(Chain(depth)));
        doc.ApplyUpdate(Hex.Bytes("00 01 01 01 00 01"));
        using var w = new Lib0Writer();
        w.WriteVarUint(1); w.WriteVarUint(2); w.WriteVarUint(1); w.WriteVarUint(0);
        w.WriteUint8(0x01); w.WriteVarUint(1); w.WriteVarString("d"); w.WriteVarUint(1);   // the type item, now deleted content
        w.WriteUint8(0x00); w.WriteVarUint(depth - 1);                                      // everything inside: one GC struct
        w.WriteVarUint(1); w.WriteVarUint(1); w.WriteVarUint(1); w.WriteVarUint(0); w.WriteVarUint(depth);
        Assert.Equal(Hex.Of(w.ToArray()), Hex.Of(doc.EncodeStateAsUpdate()));
    }

    /// <summary>Nested arrays of client 1: (1,0) in root "d", each (1,i) inside (1,i-1).</summary>
    private static string Chain(int depth)
    {
        using var w = new Lib0Writer();
        w.WriteVarUint(1); w.WriteVarUint((ulong)depth); w.WriteVarUint(1); w.WriteVarUint(0);
        w.WriteUint8(0x07); w.WriteVarUint(1); w.WriteVarString("d"); w.WriteVarUint(0);
        for (var i = 1; i < depth; i++)
        {
            w.WriteUint8(0x07); w.WriteVarUint(0); w.WriteVarUint(1); w.WriteVarUint((ulong)(i - 1)); w.WriteVarUint(0);
        }

        w.WriteVarUint(0);
        return Hex.Of(w.ToArray());
    }
}
