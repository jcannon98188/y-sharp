using System;
using System.Buffers;
using System.Linq;
using System.Text;
using Xunit;
using YSharp.TestKit.Traceability;
using static YSharp.Tests.DocFeatures.Ds;
using YSharp.TestKit.Assertions;

namespace YSharp.Tests.DocFeatures;

/// <summary>
/// Relative positions (RP-01..RP-09, docs/research/doc-features.md section 5) and the ports of the upstream <c>relativePositions.tests.js</c>
/// tests. Wire bytes, JSON and resolved indexes come from running the same operations on the pinned Yjs; the oracle fixtures under
/// <c>fixtures/relpos</c> replay the same behaviour end to end.
/// </summary>
public sealed class RelativePositionTests
{
    private static string EncodeHex(RelativePosition p)
    {
        var w = new ArrayBufferWriter<byte>();
        p.Encode(w);
        return Hex.Of(w.WrittenSpan);
    }

    private static RelativePosition Decode(string hex)
    {
        Assert.True(RelativePosition.TryDecode(Hex.Bytes(hex), out var p).Ok, $"decoding {hex}");
        return p;
    }

    private static string JsonOf(RelativePosition p) => Json(p.ToJson());

    private static AbsolutePosition Resolve(RelativePosition p, Doc doc, bool follow = true) =>
        p.Resolve(doc, follow) ?? throw new Xunit.Sdk.XunitException("the position did not resolve");

    private static Doc TextDoc(string content, ulong client = 1)
    {
        var doc = Net.Mk(client);
        doc.GetText("t").Insert(0, content);
        return doc;
    }

    // ------------------------------------------------------------------ the upstream round-trip check

    /// <summary>JS <c>checkRelativePositions</c>: every index and association survives encoding and resolves back to itself.</summary>
    private static void CheckRelativePositions(YText ytext)
    {
        var doc = ytext.Doc!;
        for (var i = 0; i < ytext.Length; i++)
        {
            for (var assoc = -1; assoc < 2; assoc++)
            {
                var rpos = ytext.CreateRelativePosition(i, assoc);
                var w = new ArrayBufferWriter<byte>();
                rpos.Encode(w);
                Assert.True(RelativePosition.TryDecode(w.WrittenSpan, out var decoded).Ok);
                var abs = Resolve(decoded, doc);
                Assert.Equal(i, abs.Index);
                Assert.Equal(assoc, abs.Assoc);
            }
        }
    }

    private static YText Seq(params (int At, string Value)[] inserts)
    {
        var ydoc = new Doc();
        var ytext = ydoc.GetText();
        foreach (var (at, value) in inserts) ytext.Insert(at, value);
        return ytext;
    }

    [Fact]
    [PortOf("relativePositions.tests.js:testRelativePositionCase1", "RP-02", "RP-06")]
    public void RelativePosition_FiveInsertsAtZero_ResolveAtEveryIndex() => Impl.Run(() => CheckRelativePositions(Seq((0, "1"), (0, "abc"), (0, "z"), (0, "y"), (0, "x"))));

    [Fact]
    [PortOf("relativePositions.tests.js:testRelativePositionCase2", "RP-02", "RP-06")]
    public void RelativePosition_SingleInsert_ResolveAtEveryIndex() => Impl.Run(() => CheckRelativePositions(Seq((0, "abc"))));

    [Fact]
    [PortOf("relativePositions.tests.js:testRelativePositionCase3", "RP-02", "RP-06")]
    public void RelativePosition_ThreeInsertsAtZero_ResolveAtEveryIndex() => Impl.Run(() => CheckRelativePositions(Seq((0, "abc"), (0, "1"), (0, "xyz"))));

    [Fact]
    [PortOf("relativePositions.tests.js:testRelativePositionCase4", "RP-02", "RP-06")]
    public void RelativePosition_OneCharacter_ResolveAtEveryIndex() => Impl.Run(() => CheckRelativePositions(Seq((0, "1"))));

    [Fact]
    [PortOf("relativePositions.tests.js:testRelativePositionCase5", "RP-02", "RP-06")]
    public void RelativePosition_TwoInsertsAtZero_ResolveAtEveryIndex() => Impl.Run(() => CheckRelativePositions(Seq((0, "2"), (0, "1"))));

    [Fact]
    [PortOf("relativePositions.tests.js:testRelativePositionCase6", "RP-02", "RP-06")]
    public void RelativePosition_EmptyText_ResolveAtIndexZero() => Impl.Run(() => CheckRelativePositions(Seq()));

    [Fact]
    [PortOf("relativePositions.tests.js:testRelativePositionCase7", "RP-06", "RP-08")]
    public void RelativePosition_FollowAndNoFollow_ResolveToTheSameIndex() => Impl.Run(() =>
    {
        var docA = new Doc();
        var textA = docA.GetText("text");
        textA.Insert(0, "abcde");
        // Create a relative position at index 2 in 'textA'
        var relativePosition = textA.CreateRelativePosition(2);
        // Verify that the absolute positions on 'docA' are the same
        var withFollow = relativePosition.Resolve(docA, true);
        var withoutFollow = relativePosition.Resolve(docA, false);
        Assert.Equal(2, withFollow?.Index);
        Assert.Equal(2, withoutFollow?.Index);
    });

    [Fact]
    [PortOf("relativePositions.tests.js:testRelativePositionAssociationDifference", "RP-02", "RP-06", "RP-09")]
    public void RelativePosition_InsertAtTheAnchor_RespectsTheAssociationSide() => Impl.Run(() =>
    {
        var ydoc = new Doc();
        var ytext = ydoc.GetText();
        ytext.Insert(0, "2");
        ytext.Insert(0, "1");
        var rposRight = ytext.CreateRelativePosition(1, 0);
        var rposLeft = ytext.CreateRelativePosition(1, -1);
        ytext.Insert(1, "x");
        var posRight = rposRight.Resolve(ydoc);
        var posLeft = rposLeft.Resolve(ydoc);
        Assert.NotNull(posRight);
        Assert.Equal(2, posRight!.Value.Index);
        Assert.NotNull(posLeft);
        Assert.Equal(1, posLeft!.Value.Index);
    });

    [Fact]
    [PortOf("relativePositions.tests.js:testRelativePositionWithUndo", "RP-06", "RP-08")]
    public void RelativePosition_DeleteThenUndo_FollowsTheRestoredContent() => Impl.Run(() =>
    {
        var ydoc = new Doc();
        var ytext = ydoc.GetText();
        ytext.Insert(0, "hello world");
        var rpos = ytext.CreateRelativePosition(1);
        var um = Um(ytext);
        ytext.Delete(0, 6);
        Assert.Equal(0, rpos.Resolve(ydoc)?.Index);
        um.Undo();
        Assert.Equal(1, rpos.Resolve(ydoc)?.Index);
        Assert.Equal(6, rpos.Resolve(ydoc, false)?.Index);
        var ydocClone = new Doc();
        ydocClone.ApplyUpdate(ydoc.EncodeStateAsUpdate());
        Assert.Equal(6, rpos.Resolve(ydocClone)?.Index);
        Assert.Equal(6, rpos.Resolve(ydocClone, false)?.Index);
    });

    // ------------------------------------------------------------------ RP-01, RP-02

    [Fact]
    [Covers("RP-01")]
    public void Shape_DecodedPosition_HasExactlyTheAnchorItsTagNames() => Impl.Run(() =>
    {
        var item = Decode("00010100");
        Assert.Equal(new ItemId(1, 1), item.Item);
        Assert.Null(item.TypeName);
        Assert.Null(item.Type);
        Assert.Equal(0, item.Assoc);

        var name = Decode("01017441");
        Assert.Equal("t", name.TypeName);
        Assert.Null(name.Item);
        Assert.Null(name.Type);
        Assert.Equal(-1, name.Assoc);

        var type = Decode("02010301");
        Assert.Equal(new ItemId(1, 3), type.Type);
        Assert.Null(type.Item);
        Assert.Null(type.TypeName);
        Assert.Equal(1, type.Assoc);
    });

    [Theory]
    [InlineData(1, 0, "00010100", 1)]
    [InlineData(1, 1, "00010101", 1)]
    [InlineData(1, -1, "00010041", 1)]
    [InlineData(3, 0, "01017400", 3)]
    [InlineData(3, 1, "01017401", 3)]
    [InlineData(3, -1, "00010241", 3)]
    [InlineData(0, 0, "00010000", 0)]
    [InlineData(0, 1, "00010001", 0)]
    [InlineData(0, -1, "01017441", 0)]
    [InlineData(5, 0, "01017400", 3)]
    [InlineData(5, -1, "00010241", 3)]
    [Covers("RP-02", "RP-04")]
    public void CreateFromIndex_AnchorsOnTheItemRightOfTheIndexOrTheOneToItsLeft(int index, int assoc, string expectedHex, int resolvedIndex) => Impl.Run(() =>
    {
        var doc = TextDoc("abc");
        var rpos = doc.GetText("t").CreateRelativePosition(index, assoc);
        Assert.Equal(expectedHex, EncodeHex(rpos));
        var abs = Resolve(rpos, doc);
        Assert.Equal(resolvedIndex, abs.Index);
        Assert.Equal(assoc, abs.Assoc);
        Assert.Same(doc.GetText("t"), abs.Type);
    });

    [Fact]
    [Covers("RP-02")]
    public void CreateFromIndex_DeletedItemsAndFormattingMarkers_AreSkipped() => Impl.Run(() =>
    {
        var doc = TextDoc("abcd");
        var t = doc.GetText("t");
        t.Format(1, 2, Attrs(("bold", true)));                          // marker items sit between the characters but are not counted
        var rpos = t.CreateRelativePosition(3, 0);
        Assert.Equal(new ItemId(1, 3), rpos.Item);                       // the character "d", not a marker
        Assert.Equal(3, Resolve(rpos, doc).Index);
        t.Delete(3, 1);
        var end = t.CreateRelativePosition(3, -1);
        Assert.Equal(new ItemId(1, 2), end.Item);                        // "c": the deleted tail is skipped
        Assert.Equal(3, Resolve(end, doc).Index);
    });

    // ------------------------------------------------------------------ RP-03

    [Fact]
    [Covers("RP-03")]
    public void RootTypes_AreAnchoredByName_AndNestedTypesByTheirItemId() => Impl.Run(() =>
    {
        var doc = TextDoc("abc");
        var arr = doc.GetArray("arr");
        var nested = new YText();
        arr.Insert(0, new YEntry[] { nested });
        nested.Insert(0, "xyz");

        Assert.Equal("t", doc.GetText("t").CreateRelativePosition(3, 0).TypeName);
        var inside = nested.CreateRelativePosition(1, 0);
        Assert.Equal("00010500", EncodeHex(inside));
        Assert.Equal(new ItemId(1, 3), inside.Type);
        Assert.Null(inside.TypeName);
        Assert.Equal("02010300", EncodeHex(nested.CreateRelativePosition(3, 0)));   // end of the nested type: type id only
        Assert.Equal(1, Resolve(inside, doc).Index);
        Assert.Same(nested, Resolve(inside, doc).Type);
        Assert.Equal(3, Resolve(nested.CreateRelativePosition(3, 0), doc).Index);
    });

    // ------------------------------------------------------------------ RP-04 binary encoding

    [Theory]
    [InlineData("00010100")]
    [InlineData("00010041")]
    [InlineData("01017400")]
    [InlineData("01017441")]
    [InlineData("02010300")]
    [InlineData("02010301")]
    [Covers("RP-04")]
    public void Encoding_EveryTagWithAndWithoutNegativeAssociation_RoundTrips(string hex) => Impl.Run(() =>
    {
        Assert.Equal(hex, EncodeHex(Decode(hex)));
    });

    [Fact]
    [Covers("RP-04")]
    public void Decoding_MissingAssociation_MeansZero() => Impl.Run(() =>
    {
        var item = Decode("000100");
        Assert.Equal(new ItemId(1, 0), item.Item);
        Assert.Equal(0, item.Assoc);
        var name = Decode("010174");
        Assert.Equal("t", name.TypeName);
        Assert.Equal(0, name.Assoc);
    });

    [Fact]
    [Covers("RP-04")]
    public void Decoding_AnUnknownTagOrTruncatedBytes_NeverYieldsAnEncodablePosition() => Impl.Run(() =>
    {
        foreach (var hex in new[] { "0501", "00", "0001", "01", "02" })
        {
            var status = RelativePosition.TryDecode(Hex.Bytes(hex), out var position);
            if (!status.Ok) continue;
            // JS decodes an unknown tag to an all-null position that then throws on re-encode
            Assert.ThrowsAny<Exception>(() => position.Encode(new ArrayBufferWriter<byte>()));
        }
    });

    // ------------------------------------------------------------------ RP-05 JSON

    [Fact]
    [Covers("RP-05")]
    public void Json_NamesOnlyThePresentAnchors_AndAlwaysTheAssociation() => Impl.Run(() =>
    {
        var doc = TextDoc("abc");
        var t = doc.GetText("t");
        Assert.Equal(Canon("{\"tname\":\"t\",\"item\":{\"client\":1,\"clock\":1},\"assoc\":0}"), JsonOf(t.CreateRelativePosition(1, 0)));
        Assert.Equal(Canon("{\"tname\":\"t\",\"item\":{\"client\":1,\"clock\":0},\"assoc\":-1}"), JsonOf(t.CreateRelativePosition(1, -1)));
        Assert.Equal(Canon("{\"tname\":\"t\",\"assoc\":1}"), JsonOf(t.CreateRelativePosition(3, 1)));
        var nested = new YText();
        doc.GetArray("arr").Insert(0, new YEntry[] { nested });
        nested.Insert(0, "xyz");
        Assert.Equal(Canon("{\"type\":{\"client\":1,\"clock\":3},\"item\":{\"client\":1,\"clock\":5},\"assoc\":0}"), JsonOf(nested.CreateRelativePosition(1, 0)));
    });

    [Fact]
    [Covers("RP-05")]
    public void Json_AnyPosition_RoundTripsThroughFromJson() => Impl.Run(() =>
    {
        var doc = TextDoc("abc");
        var t = doc.GetText("t");
        foreach (var (index, assoc) in new[] { (1, 0), (1, -1), (3, 1), (0, -1) })
        {
            var rpos = t.CreateRelativePosition(index, assoc);
            Assert.True(YValue.TryParseJson(Encoding.UTF8.GetBytes(JsonOf(rpos)), null, out var parsed).Ok);
            Assert.True(RelativePosition.TryFromJson(parsed, out var back).Ok);
            Assert.Equal(Resolve(rpos, doc).Index, Resolve(back, doc).Index);
            Assert.Equal(assoc, back.Assoc);
        }
    });

    [Fact]
    [Covers("RP-05")]
    public void Json_AbsentAssociation_DefaultsToZero() => Impl.Run(() =>
    {
        Assert.True(YValue.TryParseJson(Encoding.UTF8.GetBytes("{\"tname\":\"n\"}"), null, out var parsed).Ok);
        Assert.True(RelativePosition.TryFromJson(parsed, out var back).Ok);
        Assert.Equal("n", back.TypeName);
        Assert.Equal(0, back.Assoc);
    });

    [Fact]
    [Covers("RP-05")]
    public void Json_TheDefaultRootNameIsFalsyAndDroppedAsInJs_LeavingAPositionThatCannotBeUsed() => Impl.Run(() =>
    {
        var doc = new Doc();
        var rpos = doc.GetText().CreateRelativePosition(0);              // root named "": tname is "" and falsy in JS
        Assert.Equal("{\"assoc\":0}", JsonOf(rpos));
        Assert.Equal("010000", EncodeHex(rpos));                         // the binary form keeps the empty name
        Assert.True(YValue.TryParseJson(Encoding.UTF8.GetBytes("{\"assoc\":0}"), null, out var parsed).Ok);
        var status = RelativePosition.TryFromJson(parsed, out var rebuilt);
        if (status.Ok)
        {
            Assert.ThrowsAny<Exception>(() => rebuilt.Encode(new ArrayBufferWriter<byte>()));   // JS: Unexpected case
            Assert.ThrowsAny<Exception>(() => rebuilt.Resolve(doc));
        }
    });

    // ------------------------------------------------------------------ RP-06 resolution

    [Fact]
    [Covers("RP-06")]
    public void Resolve_AnItemThatIsUnknownToTheDoc_IsNull() => Impl.Run(() =>
    {
        var a = TextDoc("abc");
        var rpos = a.GetText("t").CreateRelativePosition(1);
        var fresh = Net.Mk(2);
        Assert.Null(rpos.Resolve(fresh));
        Net.Sync(a, fresh);
        Assert.Equal(1, rpos.Resolve(fresh)?.Index);
    });

    [Fact]
    [Covers("RP-06")]
    public void Resolve_ADeletedAnchor_GivesTheIndexOfTheNextLiveContent() => Impl.Run(() =>
    {
        var doc = TextDoc("abcdef");
        var t = doc.GetText("t");
        var onD = t.CreateRelativePosition(3, 0);
        var afterC = t.CreateRelativePosition(3, -1);
        t.Delete(3, 1);
        Assert.Equal((3, 0), (Resolve(onD, doc).Index, Resolve(onD, doc).Assoc));
        Assert.Equal((3, -1), (Resolve(afterC, doc).Index, Resolve(afterC, doc).Assoc));
        t.Delete(0, 6);
        Assert.Equal(0, Resolve(onD, doc).Index);
        Assert.Equal(0, Resolve(afterC, doc).Index);
        t.Insert(0, "XY");
        Assert.Equal(0, Resolve(onD, doc).Index);
        Assert.Equal(0, Resolve(afterC, doc).Index);
    });

    [Fact]
    [Covers("RP-06")]
    public void Resolve_ACollectedRegion_IsNull() => Impl.Run(() =>
    {
        var a = TextDoc("abc");
        var arr = a.GetArray("arr");
        var nested = new YText();
        arr.Insert(0, new YEntry[] { nested });
        nested.Insert(0, "xyz");
        var inside = nested.CreateRelativePosition(1, 0);
        var atEnd = nested.CreateRelativePosition(3, 0);
        Assert.Equal(3, Resolve(atEnd, a).Index);
        arr.Delete(0, 1);                                                // the nested type's children become GC structs
        Assert.Null(inside.Resolve(a));
        Assert.Null(atEnd.Resolve(a));
        var b = Net.Mk(3);
        Net.Sync(a, b);
        Assert.Null(inside.Resolve(b));
    });

    [Fact]
    [Covers("RP-06")]
    public void Resolve_BoundaryPositions_FollowTheAssociation() => Impl.Run(() =>
    {
        var doc = TextDoc("abc");
        var t = doc.GetText("t");
        Assert.Equal(3, Resolve(t.CreateRelativePosition(99, 0), doc).Index);   // past the end clamps to the end
        Assert.Equal(0, Resolve(t.CreateRelativePosition(0, -1), doc).Index);   // start of type
        t.Insert(0, "X");
        Assert.Equal(4, Resolve(t.CreateRelativePosition(99, 0), doc).Index);
    });

    // ------------------------------------------------------------------ RP-07

    [Fact]
    [Covers("RP-07")]
    public void Positions_AnyPair_CompareByAnchorsAndAssociation() => Impl.Run(() =>
    {
        var doc = TextDoc("abc");
        var t = doc.GetText("t");
        var a = t.CreateRelativePosition(1, 0);
        Assert.Equal(a, t.CreateRelativePosition(1, 0));
        Assert.NotEqual(a, t.CreateRelativePosition(1, 1));
        Assert.NotEqual(a, t.CreateRelativePosition(2, 0));
        Assert.Equal(Decode(EncodeHex(a)), Decode(EncodeHex(a)));
    });

    // ------------------------------------------------------------------ RP-08, RP-09

    [Fact]
    [Covers("RP-09", "RP-08")]
    public void Positions_ConcurrentEditsAndDeletions_FollowContent() => Impl.Run(() =>
    {
        var a = Net.Mk(3);
        var b = Net.Mk(4);
        var ta = a.GetText("t");
        var tb = b.GetText("t");
        ta.Insert(0, "abcdef");
        Net.Both(a, b);
        var mid = ta.CreateRelativePosition(3, 0);
        var midLeft = ta.CreateRelativePosition(3, -1);
        var end = ta.CreateRelativePosition(6, 0);
        tb.Insert(0, "XX");
        tb.Insert(5, "YY");
        ta.Delete(2, 3);
        Net.Both(a, b);
        Assert.Equal("XXabYYf", ta.ToString());
        foreach (var doc in new[] { a, b })
        {
            Assert.Equal((6, 0), (Resolve(mid, doc, false).Index, Resolve(mid, doc, false).Assoc));
            Assert.Equal((4, -1), (Resolve(midLeft, doc, false).Index, Resolve(midLeft, doc, false).Assoc));
            Assert.Equal(7, Resolve(end, doc, false).Index);
        }
        ta.Insert(4, "!");
        Assert.Equal(7, Resolve(mid, a, false).Index);
    });

    [Fact]
    [Covers("RP-08")]
    public void FollowUndoneDeletions_MayDifferBetweenReplicas_SoComparisonsUseFalse() => Impl.Run(() =>
    {
        var a = Net.Mk(5);
        var b = Net.Mk(6);
        var t = a.GetText("t");
        t.Insert(0, "hello world");
        var um = Um(t);
        var p = t.CreateRelativePosition(1);
        t.Delete(0, 6);
        Assert.Equal(0, Resolve(p, a).Index);
        um.Undo();
        Assert.Equal(1, Resolve(p, a).Index);                            // follows the redone item
        Assert.Equal(6, Resolve(p, a, false).Index);                     // the original (deleted) item is skipped over
        Net.Both(a, b);
        Assert.Equal(6, Resolve(p, b).Index);                            // the replica never saw the redo chain
        Assert.Equal(6, Resolve(p, b, false).Index);
    });
}
