using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Codecs;

/// <summary>
/// The byte layout of V1 and V2 updates, state vectors and delete sets (docs/research/encoding.md sections 2 to 4). Every golden
/// below was produced by Yjs 13.6.33 / lib0 0.2.99 on the pinned Node for the same edits made by client 1 (or the clients named);
/// <see cref="WireFixtureTests"/> replays the larger generated families.
/// </summary>
public sealed class UpdateLayoutTests
{
    private const string HiV1 = "010101000401017402686900";
    private const string HiV2 = "0000010100000104060374686901020101000001010000";

    private static Doc ClientText(uint client, string text, bool gc = true)
    {
        var doc = Wire.NewDoc(client, gc);
        doc.GetText("t").Insert(0, text);
        return doc;
    }

    private static string V1(Doc d) => Hex.Of(Wire.Encode(d));

    private static string V2(Doc d) => Hex.Of(Wire.Encode(d, UpdateFormat.V2));

    private static YValue Obj(params (string Key, YValue Value)[] m) =>
        YValue.Object(m.Select(x => new KeyValuePair<string, YValue>(x.Key, x.Value)).ToArray());

    // ------------------------------------------------------------------ buffers and primitives

    [Fact, Covers("ENC-01")]
    public void Enc01_EncoderBuffer_GrowsPastItsInitialSize_WithoutCorruptingTheUpdate() => Stub.Run(() =>
    {
        var doc = Wire.NewDoc();
        var text = doc.GetText("t");
        var big = new string('x', 300_000);
        text.Insert(0, big);
        for (var i = 0; i < 2_000; i++) text.Insert(i * 2, "y");     // many structs, so the encoder grows in several steps
        var update = Wire.Encode(doc);
        Assert.True(update.Length > 300_000);
        var peer = new Doc();
        peer.ApplyUpdate(update);
        Assert.Equal(text.ToString(), peer.GetText("t").ToString());
    });

    [Theory, Covers("ENC-02", "ENC-71", "ENC-70")]
    [InlineData(127u, 3, "017f03")]
    [InlineData(128u, 130, "0180018201")]
    [InlineData(4294967295u, 16384, "01ffffffff0f808001")]
    public void Enc02_VarUint_ClientsAndClocksAtTheByteBoundaries(uint client, int length, string expectedSv) => Stub.Run(() =>
    {
        var doc = ClientText(client, new string('x', length));
        Assert.Equal(expectedSv, Hex.Of(Wire.StateVector(doc)));
        var decoded = StateVector.Decode(Hex.Bytes(expectedSv));
        Assert.Equal((long)length, decoded[client]);
    });

    [Fact, Covers("ENC-02", "Q12"), Diverges("Q12", adr: "0003")]
    public void Enc02_VarUint_AcceptsOverlongForms_AndRejectsValuesAbove2Pow53Minus1() => Stub.Run(() =>
    {
        Assert.Equal(1L, StateVector.Decode(Hex.Bytes("01 01 81 00"))[1]);                      // overlong 1, accepted as in JS
        Assert.Equal(0L, StateVector.Decode(Hex.Bytes("01 01 80 00"))[1]);
        Assert.Equal((1L << 53) - 1, StateVector.Decode(Hex.Bytes("01 01 ff ff ff ff ff ff ff 0f"))[1]);
        // JS rounds 2^53 and above silently; y-sharp rejects them so a corrupt clock cannot enter a document
        Assert.Throws<YDecodeException>(() => StateVector.Decode(Hex.Bytes("01 01 80 80 80 80 80 80 80 10")));
        Assert.Throws<YDecodeException>(() => StateVector.Decode(Hex.Bytes("01 01 ff ff ff ff ff ff ff ff 7f")));
        Assert.Throws<YDecodeException>(() => StateVector.Decode(Hex.Bytes("01 01 80 80 80 80 80 80 80 80 80 80 00")));   // 11 groups
    });

    [Fact, Covers("ENC-09", "ENC-74")]
    public void Enc09_StateVector_ListsClientsInDescendingOrder_AndIsTheSameForBothFormats() => Stub.Run(() =>
    {
        var a = ClientText(5, "ab");
        var b = Wire.NewDoc(300);
        b.ApplyUpdate(Wire.Encode(a));
        b.GetText("t").Insert(2, "xyz");
        a.ApplyUpdate(Wire.Encode(b));
        Assert.Equal("02ac02030502", Hex.Of(Wire.StateVector(a)));
        Assert.Equal("00", Hex.Of(Wire.StateVector(Wire.NewDoc())));
        var decoded = StateVector.Decode(Hex.Bytes("02ac02030502"));
        Assert.Equal(new[] { (300u, 3L), (5u, 2L) }, decoded.Clients.Select(c => (c.Key, c.Value)).ToArray());
        Assert.Equal(0L, decoded[999]);                                                          // unknown clients are at clock 0
        // the state vector of an update is the same bytes whichever format the update has
        Assert.Equal(Hex.Of(Wire.StateVector(a)), Hex.Of(Wire.StateVectorOfUpdate(Wire.Encode(a))));
        Assert.Equal(Hex.Of(Wire.StateVector(a)), Hex.Of(Wire.StateVectorOfUpdate(Wire.Encode(a, UpdateFormat.V2), UpdateFormat.V2)));
        var encoded = new System.Buffers.ArrayBufferWriter<byte>();
        decoded.Encode(encoded);
        Assert.Equal("02ac02030502", Hex.Of(encoded.WrittenSpan));
    });

    // ------------------------------------------------------------------ update skeleton and struct layout

    [Fact, Covers("ENC-10", "ENC-11", "ENC-15", "ENC-77", "ENC-79")]
    public void Enc10_WorkedExample_TextHi_IsTheDocumentedV1AndV2Bytes() => Stub.Run(() =>
    {
        var doc = ClientText(1, "hi");
        Assert.Equal(HiV1, V1(doc));
        Assert.Equal(HiV2, V2(doc));
        var cols = V2Columns.Split(Wire.Encode(doc, UpdateFormat.V2));
        Assert.Equal("04", Hex.Of(cols.Info));                    // ENC-77: one info run, the final count is omitted
        Assert.Equal("01010000", Hex.Of(cols.Rest));              // ENC-79: numClients, numStructs, startClock and the empty delete set are raw
    });

    [Fact, Covers("ENC-10", "ENC-14")]
    public void Enc10_EmptyDocument_IsTwoZeroBytesInV1_AndThirteenInV2() => Stub.Run(() =>
    {
        var doc = Wire.NewDoc();
        Assert.Equal("0000", V1(doc));
        Assert.Equal("00000000000001000000000000", V2(doc));
    });

    [Fact, Covers("ENC-11", "ENC-15", "ENC-14")]
    public void Enc11_WorkedExample_MapWithOverwrite_UsesParentSubAndOriginFlags() => Stub.Run(() =>
    {
        var doc = Wire.NewDoc();
        var map = doc.GetMap("m");
        map.Set("k", Obj(("a", 1)));
        map.Set("s", "v");
        map.Set("k", 2);
        Assert.Equal("010301002101016d016b012801016d017301770176a80100017d020101010001", V1(doc));
        Assert.Equal("00000241000100000521002800a807046d6b6d7341020101000241010103007701767d020101010000", V2(doc));
    });

    [Fact, Covers("ENC-11", "ENC-12", "ENC-13")]
    public void Enc12_ContentKinds_String_Format_Embed_Binary_Type_Any_Deleted() => Stub.Run(() =>
    {
        var doc = Wire.NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "hello");
        text.Format(0, 2, Obj(("bold", true)));
        text.InsertEmbed(5, Obj(("a", 1)));
        Assert.Equal("0105010004010174026865840101036c6c6f46010004626f6c640474727565c60101010204626f6c64046e756c6c850104077b2261223a317d00", V1(doc));

        var bin = Wire.NewDoc();
        bin.GetMap("m").Set("b", YValue.Bytes(new byte[] { 1, 2 }));
        Assert.Equal("010101002301016d016202010200", V1(bin));                    // ContentBinary (3), not Any

        var nested = Wire.NewDoc();
        var map = nested.GetMap("m");
        map.Set("child", new YMap()).Set("x", 1);
        map.Set("arr", new YArray());
        Assert.Equal("010301002701016d056368696c6401280001000178017d012701016d036172720000", V1(nested));   // ContentType (7), Any (8)

        var overwritten = Wire.NewDoc();
        overwritten.GetMap("m").Set("k", "x");
        overwritten.GetMap("m").Set("k", "y");
        Assert.Equal("010201002101016d016b01a80100017701790101010001", V1(overwritten));                    // ContentDeleted (1) after gc

        var kept = Wire.NewDoc(gc: false);
        kept.GetMap("m").Set("k", "x");
        kept.GetMap("m").Set("k", "y");
        Assert.Equal("010201002801016d016b01770178a80100017701790101010001", V1(kept));                     // gc off keeps the old Any

        var allGone = ClientText(1, "abc");
        allGone.GetText("t").Delete(0, 3);
        Assert.Equal("0101010001010174030101010003", V1(allGone));
    });

    [Fact, Covers("ENC-12", "ENC-22")]
    public void Enc12_LegacyContentJson_DecodesAndIsWrittenBackAsContentJson() => Stub.Run(() =>
    {
        const string legacy = "0101010002010161020131013200";           // ContentJSON (ref 2) ["1","2"] in root array "a"
        var doc = new Doc();
        doc.ApplyUpdate(Hex.Bytes(legacy));
        Assert.Equal(YValue.Array(new YValue[] { 1, 2 }), doc.GetArray("a").ToJson());
        Assert.Equal(legacy, Hex.Of(doc.EncodeStateAsUpdate()));
    });

    [Fact, Covers("ENC-13", "ENC-83")]
    public void Enc13_DiffInsideAStruct_RewritesOriginAndKeepsTheTail() => Stub.Run(() =>
    {
        var doc = ClientText(1, "abc");
        Assert.Equal("0101010184010002626300", Hex.Of(Wire.Encode(doc, UpdateFormat.V1, Hex.Bytes("010101"))));
        Assert.Equal("00000241000100000184040262630200000001010100", Hex.Of(Wire.Encode(doc, UpdateFormat.V2, Hex.Bytes("010101"))));
        var astral = ClientText(1, "a😀b");
        // the state vector falls between the surrogate halves: the low half cannot be written, so U+FFFD stands in for it
        Assert.Equal("0101010284010104efbfbd6200", Hex.Of(Wire.Encode(astral, UpdateFormat.V1, Hex.Bytes("010102"))));
    });

    [Fact, Covers("ENC-27")]
    public void Enc27_UpdateEvent_CarriesTheSameBytesAsEncodingTheState() => Stub.Run(() =>
    {
        var doc = Wire.NewDoc();
        var events = new List<string>();
        doc.Update += (u, _, _, _) => events.Add(Hex.Of(u));
        doc.GetText("t").Insert(0, "hi");
        Assert.Equal(new[] { HiV1 }, events);
        Assert.Equal(HiV1, V1(doc));
        events.Clear();
        doc.GetText("t").Insert(2, "!");
        Assert.Single(events);
        Assert.NotEqual(V1(doc), events[0]);                      // later events carry only the new structs
    });

    // ------------------------------------------------------------------ V2 columns

    [Fact, Covers("ENC-08", "Q13")]
    public void Q13_UintOptRle_MarksARunWithNegativeZero_SoZeroRunsAreDistinguishable() => Stub.Run(() =>
    {
        YArray[] Arrays(int n) => Enumerable.Range(0, n).Select(_ => new YArray()).ToArray();
        byte[] TypeRefColumn(int n)
        {
            var doc = Wire.NewDoc();
            doc.GetArray("a").Insert(0, Arrays(n).Select(a => (YEntry)a).ToArray());
            return V2Columns.Split(Wire.Encode(doc, UpdateFormat.V2)).TypeRef;
        }
        Assert.Equal("00", Hex.Of(TypeRefColumn(1)));             // a single 0 is plain varint 0
        Assert.Equal("4000", Hex.Of(TypeRefColumn(2)));           // -0 (sign bit, no magnitude) then count - 2
        Assert.Equal("4001", Hex.Of(TypeRefColumn(3)));
        var one = Wire.NewDoc();
        one.GetArray("a").Insert(0, new YEntry[] { new YArray() });
        Assert.Equal("000001010000010703016101010101000001010000", V2(one));
    });

    [Fact, Covers("ENC-08", "Q14")]
    public void Q14_IntDiffOptRle_ColumnsHoldDiffsAndRunFlags() => Stub.Run(() =>
    {
        var a = ClientText(5, "ab");
        var b = Wire.NewDoc(300);
        b.ApplyUpdate(Wire.Encode(a));
        b.GetText("t").Insert(2, "xyz");
        a.ApplyUpdate(Wire.Encode(b));
        Assert.Equal("000004ac044500010200038400040a0678797a74616203010201010000020100010000", V2(a));
        var cols = V2Columns.Split(Wire.Encode(a, UpdateFormat.V2));
        Assert.Equal("ac044500", Hex.Of(cols.Client));
        Assert.Equal("02", Hex.Of(cols.LeftClock));              // origin clock 1 (diff 1, doubled, no run flag)
    });

    [Fact, Covers("ENC-76")]
    public void Enc76_V2Keys_NeverReuseAClock() => Stub.Run(() =>
    {
        var doc = ClientText(1, "ab");
        doc.GetText("t").Format(0, 1, Obj(("bold", true)));
        doc.GetText("t").Format(1, 1, Obj(("bold", true)));
        var cols = V2Columns.Split(Wire.Encode(doc, UpdateFormat.V2));
        Assert.Equal("0002", Hex.Of(cols.KeyClock));              // clocks 0 and 1; a cache hit would repeat 0
        Assert.Equal("0b746162626f6c64626f6c6441014400", Hex.Of(cols.String));   // "bold" is written both times
    });

    [Fact, Covers("ENC-78", "ENC-81", "ENC-23")]
    public void Enc78_SkipLengths_AreRawVarUintInTheRestSection_AndMergeCoalescesSkipsNotItems() => Stub.Run(() =>
    {
        var u0 = Hex.Bytes("0101010004010174016100");
        var u1 = Hex.Bytes("01010101840100016200");
        var u2 = Hex.Bytes("01010102840101016300");
        // Items stay separate structs after a merge (the live encoder would join them)
        Assert.Equal("01020100040101740161840100016200", Hex.Of(Wire.Merge(UpdateFormat.V1, u0, u1)));
        Assert.Equal("010301000401017401618401000162840101016300", Hex.Of(Wire.Merge(UpdateFormat.V1, u0, u1, u2)));
        // a gap becomes a Skip struct (info 0a) with a raw length
        Assert.Equal("010301000401017401610a01840101016300", Hex.Of(Wire.Merge(UpdateFormat.V1, u0, u2)));

        var d = Wire.NewDoc();
        var v2 = new List<byte[]>();
        d.UpdateV2 += (u, _, _, _) => v2.Add(u.ToArray());
        var t = d.GetText("t");
        t.Insert(0, "a"); t.Insert(1, "b"); t.Insert(2, "c");
        var merged = Wire.Merge(UpdateFormat.V2, v2[0], v2[2]);
        Assert.Equal("00000241000102000504000a008406037461634101010100000103000100", Hex.Of(merged));
        Assert.Equal("0103000100", Hex.Of(V2Columns.Split(merged).Rest));
    });

    [Fact, Covers("ENC-82")]
    public void Enc82_MergeOfOneUpdate_ReturnsItsBytes_EvenWhenTheyAreNotValid() => Stub.Run(() =>
    {
        var one = Hex.Bytes("01010101840100016200");
        Assert.Equal(Hex.Of(one), Hex.Of(Wire.Merge(UpdateFormat.V1, one)));
        var skipWithFlags = Hex.Bytes("010101008a0200");
        Assert.Equal(Hex.Of(skipWithFlags), Hex.Of(Wire.Merge(UpdateFormat.V1, skipWithFlags)));
    });

    // ------------------------------------------------------------------ delete sets

    private static Doc Eight() { var d = ClientText(1, "abcdefgh"); d.GetText("t").Delete(1, 2); d.GetText("t").Delete(3, 1); return d; }

    [Fact, Covers("ENC-16", "ENC-17", "ENC-18", "ENC-80")]
    public void Enc16_DeleteSet_IsSortedRangesPerClient_DeltaCodedInV2() => Stub.Run(() =>
    {
        var doc = Eight();
        Assert.Equal("01050100040101740161810100028401020264658101040184010502676801010201020501", V1(doc));
        Assert.Equal("0000024103040005000200090400810084008100840b067461646567684100420001010002020101050001010201010200", V2(doc));
        var cols = V2Columns.Split(Wire.Encode(doc, UpdateFormat.V2));
        // numClients 1, client 1, two ranges: (clock 1, len 2) then (clock 5 as a delta from the end of the last range, len 1);
        // V2 writes len - 1 and resets the running clock for each client
        Assert.Equal("01050001010201010200", Hex.Of(cols.Rest));
    });

    [Fact, Covers("ENC-19")]
    public void Enc19_UpdateEvents_CarryOnlyTheDeletesOfTheirTransaction_StateCarriesAll() => Stub.Run(() =>
    {
        var doc = Wire.NewDoc();
        var events = new List<string>();
        doc.Update += (u, _, _, _) => events.Add(Hex.Of(u));
        var t = doc.GetText("t");
        t.Insert(0, "abc");
        t.Delete(1, 1);
        Assert.Equal(new[] { "01010100040101740361626300", "000101010101" }, events);          // the second event is a delete set only
        Assert.Equal("010301000401017401618101000184010101630101010101", V1(doc));            // the state holds the structs and every delete
    });

    [Fact, Covers("ENC-20", "ENC-52")]
    public void Enc20_DeleteSetRangesBeyondKnownState_WaitInPendingDs_UntilTheStructsArrive() => Stub.Run(() =>
    {
        var author = Wire.NewDoc();
        var events = new List<byte[]>();
        author.Update += (u, _, _, _) => events.Add(u.ToArray());
        author.GetText("t").Insert(0, "abc");
        author.GetText("t").Delete(1, 1);

        var peer = new Doc();
        var first = peer.TryApplyUpdate(events[1]);                                             // delete first, struct unknown
        Assert.True(first.Ok);
        Assert.Equal("00", Hex.Of(Wire.StateVector(peer)));
        peer.ApplyUpdate(events[0]);                                                            // the insert arrives: the parked delete applies
        Assert.Equal("ac", peer.GetText("t").ToString());
        Assert.Equal("010301000401017401618101000184010101630101010101", V1(peer));
    });

    // ------------------------------------------------------------------ applying updates

    [Fact, Covers("ENC-21", "ENC-84")]
    public void Enc21_OutOfOrderUpdates_ArePending_AndEncodeThroughMerge() => Stub.Run(() =>
    {
        var u0 = Hex.Bytes("0101010004010174016100");
        var u1 = Hex.Bytes("01010101840100016200");
        var u2 = Hex.Bytes("01010102840101016300");
        var peer = new Doc();
        peer.ApplyUpdate(u2);                                                                   // clock 2 with nothing before it
        Assert.Equal("00", Hex.Of(Wire.StateVector(peer)));
        Assert.Equal("", peer.GetText("t").ToString());
        Assert.Equal("01010102840101016300", Hex.Of(peer.EncodeStateAsUpdate()));               // the pending struct is re-emitted as it came in
        peer.ApplyUpdate(u0);
        Assert.Equal("a", peer.GetText("t").ToString());
        peer.ApplyUpdate(u1);
        Assert.Equal("abc", peer.GetText("t").ToString());
        Assert.Equal("010103", Hex.Of(Wire.StateVector(peer)));
        peer.ApplyUpdate(u1);                                                                   // applying again changes nothing
        Assert.Equal("abc", peer.GetText("t").ToString());
    });

    [Theory, Covers("ENC-22", "ENC-38", "ENC-39")]
    [InlineData(0, "")] [InlineData(1, "")] [InlineData(2, "")] [InlineData(3, "01 78")] [InlineData(4, "")] [InlineData(5, "01 78")] [InlineData(6, "")]
    public void Enc22_TypeRefs0To6_AreAccepted(int typeRef, string name) => Stub.Run(() =>
    {
        var update = Hex.Bytes($"01 01 01 00 07 01 01 61 {typeRef:x2} {name} 00");
        var (result, doc) = Wire.TryApply(update);
        Assert.True(result.Ok);
        Assert.Equal("010101", Hex.Of(Wire.StateVector(doc)));
    });

    [Theory, Covers("ENC-22", "ENC-39")]
    [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(255)]
    public void Enc22_UnknownTypeRefs_AreRejected(int typeRef) => Stub.Run(() =>
        AssertRejected(Hex.Bytes($"01 01 01 00 07 01 01 61 {typeRef:x2} 00")));

    [Theory, Covers("ENC-22", "ENC-38", "ENC-85")]
    [InlineData(11)] [InlineData(12)] [InlineData(31)]
    public void Enc22_UnknownContentRefs_AreRejected(int contentRef) => Stub.Run(() =>
        AssertRejected(Hex.Bytes($"01 01 01 00 {contentRef:x2} 01 01 74 00")));

    [Fact, Covers("ENC-85")]
    public void Enc85_SkipWithFlagBits_IsASkipWhenApplied_ButFailsInMerge() => Stub.Run(() =>
    {
        var skip = Hex.Bytes("01 01 01 00 8a 02 00");            // ref 10 with the origin flag bit set
        Assert.True(Wire.TryApply(skip).Result.Ok);
        var w = new System.Buffers.ArrayBufferWriter<byte>();
        var u0 = Hex.Bytes("0101010004010174016100");
        Assert.False(YUpdate.TryMerge(new[] { (ReadOnlyMemory<byte>)skip, u0 }, w).Ok);
    });

    private static void AssertRejected(byte[] update, UpdateFormat format = UpdateFormat.V1)
    {
        var (result, doc) = Wire.TryApply(update, format);
        Assert.False(result.Ok);
        Assert.True(result.Error is YError.Malformed or YError.LimitExceeded, $"error was {result.Error}");
        Assert.True(Wire.IsEmpty(StateVector.Of(doc)), "a rejected update must leave the document untouched");
        Assert.Throws<YDecodeException>(() => new Doc().ApplyUpdate(update, null, format));
    }

    // ------------------------------------------------------------------ truncation

    [Theory, Covers("ENC-28")]
    [InlineData(UpdateFormat.V1, HiV1)]
    [InlineData(UpdateFormat.V2, HiV2)]
    public void Enc28_EveryProperPrefix_IsRejectedAndChangesNothing(UpdateFormat format, string full) => Stub.Run(() =>
    {
        var bytes = Hex.Bytes(full);
        for (var cut = 0; cut < bytes.Length; cut++) AssertRejected(bytes[..cut], format);
        Assert.True(Wire.TryApply(bytes, format).Result.Ok);
    });

    // ------------------------------------------------------------------ strings and clocks

    [Fact, Covers("ENC-73", "Q16", "Q19")]
    public void Enc73_Strings_ClocksCountUtf16Units_WhileTheWirePrefixCountsUtf8Bytes() => Stub.Run(() =>
    {
        var doc = ClientText(1, "a😀b");
        Assert.Equal(4, doc.GetText("t").Length);                                                // 'a', high, low, 'b'
        Assert.Equal("01010100040101740661f09f98806200", V1(doc));                               // content length 6 = UTF-8 bytes
        Assert.Equal("010104", Hex.Of(Wire.StateVector(doc)));                                   // the clock advanced by 4
        Assert.Equal("00000101000001040a077461f09f98806201040101000001010000", V2(doc));
        var cols = V2Columns.Split(Wire.Encode(doc, UpdateFormat.V2));
        Assert.Equal("077461f09f9880620104", Hex.Of(cols.String));                               // joined "ta<emoji>b", lengths 1 and 4 in code units
    });

    [Fact, Covers("ENC-71")]
    public void Enc71_LengthsAndClocks_Reach2Pow53Minus1_AndNoFurther() => Stub.Run(() =>
    {
        // a struct that starts at the largest clock is accepted and waits for its predecessors
        var atMax = Hex.Bytes("01 01 01 ff ff ff ff ff ff ff 0f 04 01 01 74 01 61 00");
        var (result, doc) = Wire.TryApply(atMax);
        Assert.True(result.Ok);
        Assert.Equal("00", Hex.Of(Wire.StateVector(doc)));
        // clock + length beyond 2^53-1 would round in JS; y-sharp checks the sum
        var overflow = Hex.Bytes("01 01 01 ff ff ff ff ff ff ff 0f 04 01 01 74 02 61 62 00");
        Assert.False(Wire.TryApply(overflow).Result.Ok);
    });
}
