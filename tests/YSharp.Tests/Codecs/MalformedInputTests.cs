using System;
using System.Buffers;
using System.Linq;
using Xunit;
using YSharp.TestKit.Traceability;
using static YSharp.Tests.Codecs.Malformed;
using YSharp.TestKit.Assertions;

namespace YSharp.Tests.Codecs;

/// <summary>
/// Hand-written malformed inputs of docs/research/encoding.md section 8 (ENC-29 to ENC-69) against the strict decoding contract of
/// ADR 0005: EOF is always an error, a rejection is a typed result and changes nothing, trailing bytes stay accepted. Where the oracle
/// is lenient or crashes the test carries <c>[Diverges]</c>. The oracle's recorded outcome for each of these inputs is replayed
/// from <c>fixtures/hostile</c> by <see cref="WireFixtureTests"/>.
/// </summary>
public sealed class MalformedInputTests
{
    private const string HiV1 = "010101000401017402686900";
    private const string HiV2 = "0000010100000104060374686901020101000001010000";

    // a ContentString/JSON/Any/Binary struct of client 1 at clock 0 into root text "t", empty delete set
    private static string Struct(string info, string content) => $"01 01 01 00 {info} 01 01 74 {content} 00";

    [Fact, Covers("ENC-29")]
    public void Enc29_EmptyBytes_AreRejected() => Stub.Run(() => AssertRejected(""));

    [Fact, Covers("ENC-30")]
    public void Enc30_TwoZeroBytes_AreAnEmptyUpdate() => Stub.Run(() =>
    {
        var doc = AssertAccepted("0000");
        Assert.True(Wire.IsEmpty(StateVector.Of(doc)));
    });

    [Fact, Covers("ENC-31")]
    public void Enc31_TrailingBytes_AfterAValidUpdate_AreIgnored() => Stub.Run(() =>
        Assert.Equal("hi", AssertAccepted(HiV1 + "ffff").GetText("t").ToString()));

    [Fact, Covers("ENC-32")]
    public void Enc32_NumStructs2Pow32_IsRejectedWithoutAllocating() => Stub.Run(() => AssertRejected("01 80 80 80 80 10 01 00"));

    [Fact, Covers("ENC-33")]
    public void Enc33_NumStructs2Pow32Minus1_WithAShortBody_IsRejectedFast() => Stub.Run(() => AssertRejected("01 ff ff ff ff 0f 01 00"));

    [Fact, Covers("ENC-34")]
    public void Enc34_NumClients2Pow32Minus1_IsRejected() => Stub.Run(() => AssertRejected("ff ff ff ff 0f"));

    [Fact, Covers("ENC-35")]
    public void Enc35_UnterminatedVarUint_IsRejected() => Stub.Run(() => AssertRejected("80 80 80 80 80 80 80 80 80 80 00"));

    [Fact, Covers("ENC-36")]
    public void Enc36_ClockAt2Pow53Minus1_IsAcceptedAndWaitsForItsPredecessors() => Stub.Run(() =>
    {
        var doc = AssertAccepted("01 01 01 ff ff ff ff ff ff ff 0f 04 01 01 74 01 61 00");
        Assert.True(Wire.IsEmpty(StateVector.Of(doc)));
        Assert.Equal("", doc.GetText("t").ToString());
    });

    [Fact, Covers("ENC-37", "Q12"), Diverges("ENC-37", adr: "0003")]
    public void Enc37_ClientId2Pow63_IsRejected_WhereJsRoundsItToTheNearestDouble() => Stub.Run(() =>
        AssertRejected("01 01 80 80 80 80 80 80 80 80 80 01 00 04 01 01 74 02 68 69 00"));

    [Fact, Covers("ENC-37", "Q12")]
    public void Enc37_ClientIdUpTo2Pow53Minus1_RoundTripsThroughTheDocumentAndTheStateVector() => Stub.Run(() =>
    {
        const ulong max = (1UL << 53) - 1;
        var doc = new Doc(new DocOptions { ClientId = max });
        doc.GetText("t").Insert(0, "x");
        Assert.Equal(max, doc.ClientId);
        Assert.Equal("01ffffffffffffff0f01", Hex.Of(Wire.StateVector(doc)));
        Assert.Equal("0101ffffffffffffff0f0004010174017800", Hex.Of(doc.EncodeStateAsUpdate()));
        var peer = AssertAccepted("01 01 ff ff ff ff ff ff ff 0f 00 04 01 01 74 01 78 00");
        Assert.Equal("x", peer.GetText("t").ToString());
        Assert.Equal(1L, peer.GetStateVector()[max]);
        Assert.Equal(new[] { max }, peer.GetStateVector().Clients.Select(c => c.Key).ToArray());
        Assert.Equal(1L, StateVector.Decode(Hex.Bytes("01 ff ff ff ff ff ff ff 0f 01"))[max]);
    });

    [Fact, Covers("ENC-38")]
    public void Enc38_ContentRef11_IsRejected() => Stub.Run(() => AssertRejected(Struct("0b", "")));

    [Fact, Covers("ENC-39")]
    public void Enc39_UnknownTypeRef9_IsRejected() => Stub.Run(() => AssertRejected(Struct("07", "09")));

    [Fact, Covers("ENC-40")]
    public void Enc40_InvalidUtf8_InAStringOrKey_IsRejected() => Stub.Run(() =>
    {
        AssertRejected(Struct("04", "01 ff"));
        AssertRejected("01 01 01 00 04 01 01 ff 01 61 00");          // invalid byte in the root key
        AssertRejected(Struct("04", "02 c3 28"));                     // bad continuation
        AssertRejected(Struct("04", "02 c0 80"));                     // overlong
    });

    [Fact, Covers("ENC-41")]
    public void Enc41_CesuSurrogateBytes_AreRejected() => Stub.Run(() => AssertRejected(Struct("04", "03 ed a0 80")));

    [Fact, Covers("ENC-42")]
    public void Enc42_ABom_InsideAString_IsKept() => Stub.Run(() =>
    {
        var doc = AssertAccepted(Struct("04", "04 ef bb bf 61"));
        Assert.Equal("﻿a", doc.GetText("t").ToString());
        Assert.Equal(2, doc.GetText("t").Length);
    });

    [Fact, Covers("ENC-43")]
    public void Enc43_JsonContentThatIsNotJson_IsRejected() => Stub.Run(() => AssertRejected(Struct("02", "01 02 7b 7b")));

    [Fact, Covers("ENC-44")]
    public void Enc44_JsonContentUndefined_IsAnUndefinedElement() => Stub.Run(() =>
    {
        var doc = AssertAccepted("01 01 01 00 02 01 01 61 01 09 75 6e 64 65 66 69 6e 65 64 00");
        var array = doc.GetArray("a");
        Assert.Equal(1, array.Length);
        Assert.Equal(YValueKind.Undefined, array[0].Value.Kind);
        AssertRejected("01 01 01 00 02 01 01 61 01 0a 75 6e 64 65 66 69 6e 65 64 20 00");   // "undefined " is not special-cased
    });

    [Theory, Covers("ENC-45")]
    [InlineData("10")] [InlineData("73")] [InlineData("80")] [InlineData("00")] [InlineData("ff")]
    public void Enc45_AnyTagsOutsideTheTable_AreRejected(string tag) => Stub.Run(() => AssertRejected(Struct("08", $"01 {tag}")));

    [Fact, Diverges("ENC-46", adr: "0005"), Diverges("ENC-62", adr: "0005")]
    public void Enc46_AnyIntTagAtTheEnd_IsRejectedAtomically_WhereJsReadsZeroAndKeepsTheStruct() => Stub.Run(() =>
        AssertRejected("01 01 01 00 08 01 01 74 01 7d"));

    [Fact, Covers("ENC-47", "ENC-69")]
    public void Enc47_TruncatedFloats_AreRejected() => Stub.Run(() =>
    {
        AssertRejected(Struct("08", "01 7c 3f c0"));
        AssertRejected(Struct("08", "01 7b 3f f0 00"));
    });

    [Fact, Covers("ENC-48", "ENC-69")]
    public void Enc48_BinaryLengthBeyondTheBuffer_IsRejected() => Stub.Run(() => AssertRejected(Struct("03", "05 01 02")));

    [Theory, Covers("ENC-49")]
    [InlineData("02")] [InlineData("05")]
    public void Enc49_ParentInfoOtherThanOne_IsReadAsAParentId(string parentInfo) => Stub.Run(() =>
        AssertRejected($"01 01 01 00 04 {parentInfo} 01 00 01 61 00"));   // parent id (1,0) is the struct itself and never resolves

    [Fact, Covers("ENC-50")]
    public void Enc50_ADeleteSetClientWithNoRanges_IsAccepted() => Stub.Run(() => AssertAccepted("00 01 05 00"));

    [Fact, Covers("ENC-51"), Diverges("ENC-51", adr: "0003")]
    public void Enc51_ADeleteRangeOfLengthZero_IsIgnored_AndConvertsWhereJsThrowsUnexpectedCase() => Stub.Run(() =>
    {
        var update = Hex.Bytes("00 01 05 01 00 00");
        AssertAccepted(update);
        var w = new ArrayBufferWriter<byte>();
        Assert.True(YUpdate.TryConvert(update, UpdateFormat.V1, UpdateFormat.V2, w).Ok);
    });

    [Fact, Covers("ENC-52")]
    public void Enc52_ADeleteRangeBeyondKnownState_IsAccepted() => Stub.Run(() => AssertAccepted("00 01 05 01 00 01"));

    [Fact, Covers("ENC-53")]
    public void Enc53_ATruncatedDeleteSet_IsRejected() => Stub.Run(() =>
    {
        AssertRejected("00 01 05 02 00 01");
        AssertRejected(HiV1[..^2] + "01 01 01 00");                  // valid structs, delete set cut mid-range: nothing may apply
    });

    [Fact, Covers("ENC-54")]
    public void Enc54_V2BytesThroughTheV1Reader_AreReadAsAnEmptyUpdate_AndV1ThroughV2IsRejected() => Stub.Run(() =>
    {
        var doc = AssertAccepted(HiV2);                                   // 00 00 ...: zero clients, then an empty delete set, junk ignored
        Assert.True(Wire.IsEmpty(StateVector.Of(doc)));
        AssertRejected(HiV1, UpdateFormat.V2);
    });

    [Fact, Covers("ENC-55")]
    public void Enc55_ANonZeroV2FeatureFlag_IsIgnored() => Stub.Run(() =>
    {
        var bytes = Hex.Bytes(HiV2);
        bytes[0] = 1;
        Assert.Equal("hi", AssertAccepted(bytes, UpdateFormat.V2).GetText("t").ToString());
    });

    [Fact, Covers("ENC-56")]
    public void Enc56_AV2ColumnLongerThanTheRest_IsRejected() => Stub.Run(() =>
    {
        AssertRejected("00 ff 01", UpdateFormat.V2);
        AssertRejected("00 00 00 00 00 ff 01", UpdateFormat.V2);
    });

    [Fact, Covers("ENC-60"), Diverges("ENC-60", adr: "0005")]
    public void Enc60_AnInfoByteMissingAtEof_IsRejected() => Stub.Run(() => AssertRejected("01 01 01 00"));

    [Fact, Covers("ENC-61")]
    public void Enc61_AVarUintAtEof_IsRejected() => Stub.Run(() =>
    {
        AssertRejected("01");
        AssertRejected("01 01");
    });

    [Fact, Covers("ENC-63")]
    public void Enc63_AVarIntContinuationAtEof_IsRejected() => Stub.Run(() => AssertRejected("01 01 01 00 08 01 01 74 01 7d 80"));

    [Fact, Covers("ENC-64"), Diverges("ENC-64", adr: "0005")]
    public void Enc64_AnEmptyUintOptRleColumn_DoesNotYieldZerosForever() => Stub.Run(() =>
        // the string-length column is empty: JS reads length 0 for every string and accepts an item with an empty root key
        AssertRejected(V2("01 01 00 00", info: "04", parentInfo: "01"), UpdateFormat.V2));

    [Fact, Covers("ENC-65"), Diverges("ENC-65", adr: "0005")]
    public void Enc65_AnEmptyIntDiffOptRleColumn_DoesNotYieldZerosForever() => Stub.Run(() =>
        // info 0x84 reads an origin id from the (empty) client and left-clock columns; JS reads (0, 0)
        AssertRejected(V2("01 01 00 00", info: "84"), UpdateFormat.V2));

    [Fact, Covers("ENC-66")]
    public void Enc66_AnEmptyRleColumn_IsRejected() => Stub.Run(() => AssertRejected(V2("01 01 00 00"), UpdateFormat.V2));

    [Fact, Covers("ENC-67"), Diverges("ENC-67", adr: "0005")]
    public void Enc67_ARleColumnRunningOut_DoesNotRepeatItsLastValue() => Stub.Run(() =>
        // two structs but one info run and one parentInfo run: JS repeats the last value for the second struct
        AssertRejected(V2("01 02 00 00", info: "04", parentInfo: "01"), UpdateFormat.V2));

    [Fact, Covers("ENC-68"), Diverges("ENC-68", adr: "0005")]
    public void Enc68_AStringLengthBeyondTheJoinedString_IsRejected() => Stub.Run(() =>
        AssertRejected(V2("01 01 00 00", info: "04", parentInfo: "01", str: "01 61 05"), UpdateFormat.V2));
}
