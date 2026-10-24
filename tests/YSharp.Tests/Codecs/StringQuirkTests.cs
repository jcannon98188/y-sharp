using Xunit;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Codecs;

/// <summary>
/// UTF-16 text semantics and the UTF-8 wire conversion (Q16 to Q19). A .NET string is UTF-16 like a JS string, so indexes and
/// clocks agree; the wire conversion needs a replacing encoder and a strict decoder (encoding.md section 9).
/// </summary>
[Requires(Feature.Values)]
public sealed class StringQuirkTests
{
    private const string Astral = "a😀b";      // a, U+1F600 (two code units), b

    private static Doc Client1(string text)
    {
        var doc = Wire.NewDoc();
        doc.GetText("t").Insert(0, text);
        return doc;
    }

    [Fact, Covers("Q16")]
    [Requires(Feature.Text)]
    public void Q16_TextLengthAndIndexes_AreUtf16CodeUnits()
    {
        var doc = Client1(Astral);
        var text = doc.GetText("t");
        Assert.Equal(4, text.Length);                 // not 3 scalar values and not 6 UTF-8 bytes
        text.Insert(3, "X");                          // index 3 is after the whole emoji
        Assert.Equal("a😀Xb", text.ToString());
        text.Delete(1, 2);                            // removes exactly the two code units of the emoji
        Assert.Equal("aXb", text.ToString());
        text.Insert(100, "!");                        // JS clamps the index
        Assert.Equal("aXb!", text.ToString());
        Assert.Equal(4, text.Length);
    }

    [Fact, Covers("Q17")]
    [Requires(Feature.Text)]
    public void Q17_DeletingOneHalfOfAPair_ReplacesBothHalvesWithReplacementCharacters()
    {
        var doc = Client1(Astral);
        var text = doc.GetText("t");
        text.Delete(2, 1);                            // the low surrogate only
        Assert.Equal(3, text.Length);
        Assert.Equal("a�b", text.ToString());    // the surviving high half is now U+FFFD
        Assert.Equal("01030100040101740461efbfbd8101010184010201620101010201", Hex.Of(Wire.Encode(doc)));
    }

    [Fact, Covers("Q17")]
    [Requires(Feature.Text)]
    public void Q17_InsertingBetweenTheHalvesOfAPair_SplitsItIntoTwoReplacementCharacters()
    {
        var doc = Client1(Astral);
        var text = doc.GetText("t");
        text.Insert(2, "X");
        Assert.Equal("a�X�b", text.ToString());
        Assert.Equal(5, text.Length);
        // a peer sees the same content: the replacement happens when the item is split, not at encode time
        var peer = new Doc();
        peer.ApplyUpdate(Wire.Encode(doc));
        Assert.Equal("a�X�b", peer.GetText("t").ToString());
    }

    [Fact, Covers("Q18")]
    [Requires(Feature.Text)]
    public void Q18_LoneSurrogates_AreReplacedByU_FFFD_WhenWrittenAsUtf8()
    {
        var doc = Client1("\ud800");
        Assert.Equal("\ud800", doc.GetText("t").ToString());         // the local string keeps the lone surrogate
        Assert.Equal("010101000401017403efbfbd00", Hex.Of(Wire.Encode(doc)));
        var peer = new Doc();
        peer.ApplyUpdate(Wire.Encode(doc));
        Assert.Equal("�", peer.GetText("t").ToString());          // the peer cannot receive it
        Assert.Equal(1, peer.GetText("t").Length);

        var reversed = Client1("\udc00\ud800");
        Assert.Equal("010101000401017406efbfbdefbfbd00", Hex.Of(Wire.Encode(reversed)));
        Assert.Equal("010102", Hex.Of(Wire.StateVector(reversed)));    // the clock still counts two code units
    }

    [Fact, Covers("Q18")]
    [Requires(Feature.Text)]
    public void Q18_TheUtf8Decoder_IsStrictAndKeepsTheByteOrderMark()
    {
        Malformed.AssertRejected("01 01 01 00 04 01 01 74 01 ff 00");
        Malformed.AssertRejected("01 01 01 00 04 01 01 74 03 ed a0 80 00");   // a surrogate encoded as three bytes
        var bom = Malformed.AssertAccepted("01 01 01 00 04 01 01 74 04 ef bb bf 61 00");
        Assert.Equal("﻿a", bom.GetText("t").ToString());                // JS keeps U+FEFF, so length is 2
    }

    [Fact, Covers("Q19", "Q17")]
    [Requires(Feature.Text, Feature.V2)]
    public void Q19_V2StringColumn_JoinsTheStrings_AndRecordsLengthsInCodeUnits()
    {
        var doc = Client1(Astral);
        var cols = V2Columns.Split(Wire.Encode(doc, UpdateFormat.V2));
        // varstring of the joined UTF-8 bytes ("t" + "a" + emoji + "b" = 7 bytes), then the lengths 1 and 4 in UTF-16 units
        Assert.Equal("077461f09f9880620104", Hex.Of(cols.String));

        var split = Client1(Astral);
        split.GetText("t").Delete(2, 1);
        var splitCols = V2Columns.Split(Wire.Encode(split, UpdateFormat.V2));
        Assert.Contains("efbfbd", Hex.Of(splitCols.String));                   // the replacement characters are in the joined text

        var peer = new Doc();
        peer.ApplyUpdate(Wire.Encode(doc, UpdateFormat.V2), null, UpdateFormat.V2);
        Assert.Equal(Astral, peer.GetText("t").ToString());
    }
}
