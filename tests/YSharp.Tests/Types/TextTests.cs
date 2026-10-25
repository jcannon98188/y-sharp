using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Traceability;
using static YSharp.Tests.Types.TypeTestExtensions;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Types;

/// <summary>Behaviour of <see cref="YText"/>: plain text, formats, embeds, deltas (T-*, F-*, E-*, D-*, X-1, X-2) and the y-text.tests.js ports.</summary>
[Requires(Feature.Text)]
public sealed class TextTests
{
    private static Doc NewDoc(ulong client = 1) => new(new DocOptions { ClientId = client });

    private static YValue Bold => Obj(("bold", true));

    // ------------------------------------------------------------------ plain text

    [Fact, Covers("T-1")]
    [Requires(Feature.Events)]
    public void Insert_UsesUtf16Offsets_ClampsBeyondTheEnd_AndIgnoresEmptyStrings()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        var updates = 0;
        doc.Update += (_, _, _, _) => updates++;
        text.Insert(0, "ac");
        text.Insert(1, "b");
        text.Insert(100, "d");
        Assert.Equal("abcd", text.ToString());
        Assert.Equal(4, updates);
        text.Insert(2, "");
        Assert.Equal(4, updates);
        text.Insert(0, "\U0001F600");
        Assert.Equal(6, text.Length);
        text.Insert(2, "|");
        Assert.Equal("\U0001F600|abcd", text.ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => text.Insert(-1, "x"));
    }

    [Fact, Covers("T-2")]
    [Requires(Feature.Events)]
    public void Delete_ZeroIsANoOp_AndOverflowDeletesWhatExistsWithoutThrowing()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        var updates = 0;
        text.Insert(0, "abcdef");
        doc.Update += (_, _, _, _) => updates++;
        text.Delete(1, 0);
        text.Delete(0, 0);
        Assert.Equal(0, updates);
        text.Delete(1, 2);
        Assert.Equal("adef", text.ToString());
        text.Delete(2, 100);
        Assert.Equal("ad", text.ToString());
        text.Delete(5, 3);
        Assert.Equal("ad", text.ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => text.Delete(-1, 1));
    }

    [Fact, Covers("T-3", "E-1")]
    [Requires(Feature.TextFormat, Feature.Map)]
    public void ToString_SkipsEmbeds_AndLengthCountsThem()
    {
        var text = NewDoc().GetText("t");
        text.Insert(0, "ab");
        text.InsertEmbed(1, Obj(("i", 1)));
        text.InsertEmbed(3, new YMap());
        Assert.Equal("ab", text.ToString());
        Assert.Equal(4, text.Length);
        Assert.Equal("ab", text.ToJson().AsString());
        var buffer = new char[2];
        text.CopyTo(buffer);
        Assert.Equal("ab", new string(buffer));
    }

    [Fact, Covers("T-4")]
    [Requires(Feature.Map)]
    public void NewText_WithInitialContent_IsEmptyUntilAttached()
    {
        var doc = NewDoc();
        var text = new YText("x");
        Assert.Equal(0, text.Length);
        Assert.Equal("", text.ToString());
        doc.GetMap("m").Set("t", text);
        Assert.Equal("x", text.ToString());
        Assert.Equal(1, text.Length);
        text.Insert(1, "yz");
        Assert.Equal("xyz", text.ToString());
    }

    [Fact, PortOf("y-text.tests.js:testSplitSurrogateCharacter", "T-5")]
    [Requires(Feature.TextFormat, Feature.Sync)]
    public void Text_SplitSurrogatePair_SurvivesDisconnectAndSync()
    {
        {
            var net = Net.Init(2);
            var text0 = net.Peers[0].Text;
            net.Users[1].Disconnect(); // disconnecting forces the user to encode the split surrogate
            text0.Insert(0, "\U0001F47E");
            Assert.Equal(2, text0.Length);
            text0.Insert(1, "hi!"); // split surrogate, which should not lead to an encoding error
            // both halves of the split pair become U+FFFD
            Assert.Equal("�hi!�", text0.ToString());
            net.Compare();
        }
        {
            var net = Net.Init(2);
            var text0 = net.Peers[0].Text;
            net.Users[1].Disconnect();
            text0.Insert(0, "\U0001F47E\U0001F47E");
            text0.Delete(1, 2); // partially delete surrogate
            Assert.Equal("��", text0.ToString());
            net.Compare();
        }
        {
            var net = Net.Init(2);
            var text0 = net.Peers[0].Text;
            net.Users[1].Disconnect();
            text0.Insert(0, "\U0001F47E\U0001F47E");
            text0.Format(1, 2, Bold); // formatting will also split surrogates
            net.Compare();
        }
    }

    [Fact, Covers("T-6")]
    [Requires(Feature.TextFormat, Feature.Events)]
    public void ApplyDelta_AcceptsInsertRetainAndDelete_InOneTransaction()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        var events = 0;
        text.Observe((_, _) => events++);
        text.ApplyDelta(new[] { Insert("hello"), Insert(" world", Bold) });
        Assert.Equal(1, events);
        text.ApplyDelta(new[] { Retain(2), Delete(3), Insert("p!") });
        Assert.Equal("hep! world", text.ToString());
        Assert.Equal(2, events);
        Assert.Equal("""[{"insert":"hep!"},{"insert":" world","attributes":{"bold":true}}]""", text.DeltaJson());
        // sanitize: a trailing newline of the last insert stays by default and is dropped with sanitize: false
        var kept = NewDoc(2).GetText("t");
        kept.ApplyDelta(new[] { Insert("a\n") });
        Assert.Equal("a\n", kept.ToString());
        var dropped = NewDoc(3).GetText("t");
        dropped.ApplyDelta(new[] { Insert("a\n") }, sanitize: false);
        Assert.Equal("a", dropped.ToString());
    }

    [Fact, Covers("T-7")]
    [Requires(Feature.TextFormat, Feature.Map)]
    public void ApplyDelta_BeforeAttachment_IsQueued_AndTheSanitizeOptionIsLost()
    {
        var doc = NewDoc();
        var text = new YText();
        text.ApplyDelta(new[] { Insert("a\n") }, sanitize: false);
        doc.GetMap("m").Set("t", text);
        // JS re-calls applyDelta(delta) with default options when it replays the queue
        Assert.Equal("a\n", text.ToString());
    }

    [Fact, Covers("T-8")]
    [Requires(Feature.TextFormat, Feature.Events)]
    public void ApplyDelta_EmptyInserts_AreSkipped()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        var updates = 0;
        doc.Update += (_, _, _, _) => updates++;
        text.ApplyDelta(new[] { Insert(""), Insert("", Obj(("x", 1))) });
        Assert.Equal(0, updates);
        Assert.Equal("[]", text.DeltaJson());
        text.ApplyDelta(new[] { Insert("a"), Insert(""), Insert("b") });
        Assert.Equal("ab", text.ToString());
    }

    [Fact, Covers("T-9")]
    [Requires(Feature.TextFormat)]
    public void ApplyDelta_RetainBeyondTheEnd_AppendsNewlines_AndDeleteBeyondTheEndStops()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "ab");
        text.ApplyDelta(new[] { Retain(3) });
        Assert.Equal("ab\n", text.ToString());
        var attributed = doc.GetText("u");
        attributed.ApplyDelta(new[] { Retain(2, Bold) });
        Assert.Equal("""[{"insert":"\n\n","attributes":{"bold":true}}]""", attributed.DeltaJson());
        var deleted = doc.GetText("v");
        deleted.Insert(0, "abc");
        deleted.ApplyDelta(new[] { Delete(10) });
        Assert.Equal("[]", deleted.DeltaJson());
    }

    // ------------------------------------------------------------------ formats

    [Fact, PortOf("y-text.tests.js:testBasicFormat", "F-1", "F-3", "F-2")]
    [Requires(Feature.TextFormat, Feature.Events)]
    public void Text_FormatRanges_ReportDeltasForEachChange()
    {
        var net = Net.Init(2);
        var text0 = net.Peers[0].Text;
        string delta = "";
        text0.Observe((e, _) => delta = DeltaJson(((YTextEvent)e).Delta.ToArray()));
        text0.Insert(0, "abc", Bold);
        Assert.Equal("abc", text0.ToString());
        Assert.Equal("""[{"insert":"abc","attributes":{"bold":true}}]""", text0.DeltaJson());
        Assert.Equal("""[{"insert":"abc","attributes":{"bold":true}}]""", delta);
        text0.Delete(0, 1);
        Assert.Equal("bc", text0.ToString());
        Assert.Equal("""[{"insert":"bc","attributes":{"bold":true}}]""", text0.DeltaJson());
        Assert.Equal("""[{"delete":1}]""", delta);
        text0.Delete(1, 1);
        Assert.Equal("b", text0.ToString());
        Assert.Equal("""[{"insert":"b","attributes":{"bold":true}}]""", text0.DeltaJson());
        Assert.Equal("""[{"retain":1},{"delete":1}]""", delta);
        text0.Insert(0, "z", Bold);
        Assert.Equal("zb", text0.ToString());
        Assert.Equal("""[{"insert":"zb","attributes":{"bold":true}}]""", text0.DeltaJson());
        Assert.Equal("""[{"insert":"z","attributes":{"bold":true}}]""", delta);
        // JS asserts that no duplicate attribute marker was inserted by walking the item list (text0._start.right.right.right).
        // The item list is not public API; the observable consequence is that the state vector advanced by exactly the inserted length.
        text0.Insert(0, "y");
        Assert.Equal("yzb", text0.ToString());
        Assert.Equal("""[{"insert":"y"},{"insert":"zb","attributes":{"bold":true}}]""", text0.DeltaJson());
        Assert.Equal("""[{"insert":"y"}]""", delta);
        text0.Format(0, 2, Obj(("bold", YValue.Null)));
        Assert.Equal("yzb", text0.ToString());
        Assert.Equal("""[{"insert":"yz"},{"insert":"b","attributes":{"bold":true}}]""", text0.DeltaJson());
        Assert.Equal("""[{"retain":1},{"retain":1,"attributes":{"bold":null}}]""", delta);
        net.Compare();
    }

    [Fact, Covers("F-2")]
    public void Insert_WithoutAttributes_InheritsTheFormatting_ButAnExplicitObjectDoesNot()
    {
        var text = NewDoc().GetText("t");
        text.Insert(0, "ab", Bold);
        text.Insert(1, "X");
        Assert.Equal("""[{"insert":"aXb","attributes":{"bold":true}}]""", text.DeltaJson());
        text.Insert(1, "Y", Obj());
        Assert.Equal("""[{"insert":"a","attributes":{"bold":true}},{"insert":"Y"},{"insert":"Xb","attributes":{"bold":true}}]""", text.DeltaJson());
        text.Insert(1, "Z", Obj(("bold", YValue.Null)));
        Assert.Equal("""[{"insert":"a","attributes":{"bold":true}},{"insert":"ZY"},{"insert":"Xb","attributes":{"bold":true}}]""", text.DeltaJson());
    }

    [Fact, PortOf("y-text.tests.js:testFalsyFormats", "F-4")]
    [Requires(Feature.TextFormat, Feature.Events)]
    public void Text_FalsyFormatValues_AreKeptInTheDelta()
    {
        var net = Net.Init(2);
        var text0 = net.Peers[0].Text;
        string delta = "";
        text0.Observe((e, _) => delta = DeltaJson(((YTextEvent)e).Delta.ToArray()));
        text0.Insert(0, "abcde", Obj(("falsy", false)));
        Assert.Equal("""[{"insert":"abcde","attributes":{"falsy":false}}]""", text0.DeltaJson());
        Assert.Equal("""[{"insert":"abcde","attributes":{"falsy":false}}]""", delta);
        text0.Format(1, 3, Obj(("falsy", true)));
        Assert.Equal("""[{"insert":"a","attributes":{"falsy":false}},{"insert":"bcd","attributes":{"falsy":true}},{"insert":"e","attributes":{"falsy":false}}]""", text0.DeltaJson());
        Assert.Equal("""[{"retain":1},{"retain":3,"attributes":{"falsy":true}}]""", delta);
        text0.Format(2, 1, Obj(("falsy", false)));
        Assert.Equal("""[{"insert":"a","attributes":{"falsy":false}},{"insert":"b","attributes":{"falsy":true}},{"insert":"c","attributes":{"falsy":false}},{"insert":"d","attributes":{"falsy":true}},{"insert":"e","attributes":{"falsy":false}}]""", text0.DeltaJson());
        Assert.Equal("""[{"retain":2},{"retain":1,"attributes":{"falsy":false}}]""", delta);
        net.Compare();
    }

    [Fact, PortOf("y-xml.tests.js:testFormattingBug", "F-5", "Q27")]
    [Requires(Feature.TextFormat, Feature.Xml)]
    public void XmlText_FormattingRegression_RendersTheExpectedDelta()
    {
        var ydoc = NewDoc();
        var yxml = ydoc.GetXmlText("");   // upstream: ydoc.get('', Y.XmlText)
        var delta = new[]
        {
            Insert("A", Obj(("em", Obj()), ("strong", Obj()))),
            Insert("B", Obj(("em", Obj()))),
            Insert("C", Obj(("em", Obj()), ("strong", Obj()))),
        };
        yxml.ApplyDelta(delta);
        Assert.Equal(DeltaJson(delta), yxml.DeltaJson());
    }

    [Fact, Covers("F-5", "Q27")]
    [Requires(Feature.TextFormat, Feature.Sync)]
    public void AttributeEquality_IsShallow_SoFlatObjectsAreEqualAndNestedOnesAreNot()
    {
        var net = Net.Init(1);
        var doc = net.Peers[0].Doc;
        var text = net.Peers[0].Text;
        text.Insert(0, "abcdef");
        text.Format(0, 6, Obj(("em", Obj(("x", 1)))));
        var before = doc.GetStateVector()[doc.ClientId];
        // an equal flat object, applied to part of the range: no new format items are needed
        text.Format(1, 2, Obj(("em", Obj(("x", 1)))));
        Assert.Equal(before, doc.GetStateVector()[doc.ClientId]);
        // nested objects compare by reference in equalFlat, so this is a change
        text.Format(3, 1, Obj(("em", Obj(("x", Obj(("y", 1)))))));
        var afterNested = doc.GetStateVector()[doc.ClientId];
        Assert.True(afterNested > before);
        text.Format(3, 1, Obj(("em", Obj(("x", Obj(("y", 1)))))));
        Assert.True(doc.GetStateVector()[doc.ClientId] > afterNested);
        // YValue.AttributeEquals is the rule itself: NaN never equals NaN, +0 equals -0, objects compare one level deep
        Assert.False(((YValue)double.NaN).AttributeEquals((YValue)double.NaN));
        Assert.True(((YValue)0.0).AttributeEquals((YValue)(-0.0)));
        Assert.True(Obj(("a", 1)).AttributeEquals(Obj(("a", 1))));
        Assert.False(Obj(("a", Obj(("b", 1)))).AttributeEquals(Obj(("a", Obj(("b", 1))))));
        Assert.False(((YValue)1).AttributeEquals(YValue.Null));
        Assert.False(YValue.Null.AttributeEquals(default));
        net.Compare();
    }

    [Fact, Covers("F-6")]
    [Requires(Feature.TextFormat, Feature.Events)]
    public void Format_ZeroLengthAndPastTheEnd_AreNoOps_AndRangesSplitStrings()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "abcdef");
        var updates = 0;
        doc.Update += (_, _, _, _) => updates++;
        text.Format(2, 0, Bold);
        text.Format(6, 3, Bold);
        text.Format(100, 3, Bold);
        Assert.Equal(0, updates);
        text.Format(2, 2, Bold);
        Assert.Equal("""[{"insert":"ab"},{"insert":"cd","attributes":{"bold":true}},{"insert":"ef"}]""", text.DeltaJson());
        text.Format(2, 2, Bold);
        // formatting a range that already has the format produces no new items
        Assert.Equal(1, updates);
        // a range that crosses an embed formats the embed too
        text.InsertEmbed(4, Obj(("image", "x.png")));
        text.Format(3, 3, Obj(("color", "red")));
        Assert.Equal(
            """[{"insert":"ab"},{"insert":"c","attributes":{"bold":true}},{"insert":"d","attributes":{"bold":true,"color":"red"}},{"insert":{"image":"x.png"},"attributes":{"color":"red"}},{"insert":"e","attributes":{"color":"red"}},{"insert":"f"}]""",
            text.DeltaJson());
    }

    [Fact, PortOf("y-text.tests.js:testFormattingRemoved", "F-7")]
    [Requires(Feature.Sync)]
    public void Text_FormattedTextDeleted_LeavesNothingBehind()
    {
        var text0 = Net.Init(1).Peers[0].Text;
        text0.Insert(0, "ab", Bold);
        text0.Delete(0, 2);
        // JS: Y.getTypeChildren(text0).length === 1 (a test-only walk of the item list, not public API). Observable here: nothing is left.
        Assert.Equal("[]", text0.DeltaJson());
        Assert.Equal(0, text0.Length);
    }

    [Fact, PortOf("y-text.tests.js:testFormattingRemovedInMidText", "F-7")]
    [Requires(Feature.Sync)]
    public void Text_FormattedMiddleDeleted_LeavesNoFormattingMarkers()
    {
        var text0 = Net.Init(1).Peers[0].Text;
        text0.Insert(0, "1234");
        text0.Insert(2, "ab", Bold);
        text0.Delete(2, 2);
        // JS: getTypeChildren(text0).length === 3 (the two string items and the surviving format-free gap)
        Assert.Equal("""[{"insert":"1234"}]""", text0.DeltaJson());
    }

    [Fact, PortOf("y-text.tests.js:testFormattingBug", "F-8", "F-7")]
    [Requires(Feature.TextFormat)]
    public void Text_FormattingOnNewlines_ConvergesAcrossTwoDocs()
    {
        var ydoc1 = NewDoc(1);
        var ydoc2 = NewDoc(2);
        var text1 = ydoc1.GetText();
        text1.Insert(0, "\n\n\n");
        text1.Format(0, 3, Obj(("url", "http://example.com")));
        ydoc1.GetText().Format(1, 1, Obj(("url", "http://docs.yjs.dev")));
        ydoc2.GetText().Format(1, 1, Obj(("url", "http://docs.yjs.dev")));
        ydoc2.ApplyUpdate(ydoc1.EncodeStateAsUpdate());
        var text2 = ydoc2.GetText();
        const string expected =
            """[{"insert":"\n","attributes":{"url":"http://example.com"}},{"insert":"\n","attributes":{"url":"http://docs.yjs.dev"}},{"insert":"\n","attributes":{"url":"http://example.com"}}]""";
        Assert.Equal(expected, text1.DeltaJson());
        Assert.Equal(text1.DeltaJson(), text2.DeltaJson());
    }

    [Fact, PortOf("y-text.tests.js:testDeleteFormatting", "F-7", "F-8")]
    [Requires(Feature.TextFormat)]
    public void Text_DeletedFormattedRange_SyncsToThePeer()
    {
        var doc = NewDoc(1);
        var text = doc.GetText();
        text.Insert(0, "Attack ships on fire off the shoulder of Orion.");
        var doc2 = NewDoc(2);
        var text2 = doc2.GetText();
        doc2.ApplyUpdate(doc.EncodeStateAsUpdate());
        text.Format(13, 7, Bold);
        doc2.ApplyUpdate(doc.EncodeStateAsUpdate());
        text.Format(16, 4, Obj(("bold", YValue.Null)));
        doc2.ApplyUpdate(doc.EncodeStateAsUpdate());
        const string expected =
            """[{"insert":"Attack ships "},{"insert":"on ","attributes":{"bold":true}},{"insert":"fire off the shoulder of Orion."}]""";
        Assert.Equal(expected, text.DeltaJson());
        Assert.Equal(expected, text2.DeltaJson());
    }

    [Fact, Covers("F-8")]
    [Requires(Feature.TextFormat)]
    public void RemoteChange_SameConcurrentFormat_DeletesTheDuplicateFormatItems()
    {
        // Yjs cleanupYTextAfterTransaction runs in a follow-up transaction of each doc that applies the other's format; the state, delete
        // set included, is the oracle's (yjs 13.6.33)
        var a = NewDoc(1);
        var b = NewDoc(2);
        a.GetText("text").Insert(0, "hello world");
        Exchange(a, b);
        a.GetText("text").Format(0, 5, Bold);
        b.GetText("text").Format(0, 5, Bold);
        Exchange(a, b);
        Exchange(a, b);
        const string expected =
            "0202020046010004626f6c640474727565c60104010504626f6c64046e756c6c040100040104746578740568656c6c6f8401040620776f726c6441010001" +
            "c60104010504626f6c64046e756c6c0101010b01";
        Assert.Equal(expected, Convert.ToHexString(a.EncodeStateAsUpdate()).ToLowerInvariant());
        Assert.Equal(expected, Convert.ToHexString(b.EncodeStateAsUpdate()).ToLowerInvariant());
    }

    [Fact, Covers("F-8")]
    [Requires(Feature.TextFormat, Feature.Map)]
    public void RemoteChange_ToANestedTextNobodyRead_StillCleansUpItsFormatting()
    {
        // the relay never reads the map or the text, so no shared type object wraps them; Yjs has one for every type and still cleans up
        var a = NewDoc(1);
        var c = NewDoc(3);
        var relay = NewDoc(2);
        a.GetMap("m").Set("t", new YText("abcdefgh"));
        Exchange(a, c);
        Nested(a).Format(1, 4, Obj(("b", 1)));
        Nested(c).Delete(1, 4);
        relay.ApplyUpdate(a.EncodeStateAsUpdate());
        relay.ApplyUpdate(c.EncodeStateAsUpdate());
        Assert.Equal("010601002701016d0174020400010001618101010484010503666768c10101010201c6010501060162046e756c6c01010202040901",
            Convert.ToHexString(relay.EncodeStateAsUpdate()).ToLowerInvariant());

        static YText Nested(Doc doc)
        {
            Assert.True(doc.GetMap("m").TryGet("t", out var entry));
            return (YText)entry.Type!;
        }
    }

    private static void Exchange(Doc a, Doc b)
    {
        var fromA = a.EncodeStateAsUpdate();
        var fromB = b.EncodeStateAsUpdate();
        b.ApplyUpdate(fromA);
        a.ApplyUpdate(fromB);
    }

    [Fact, PortOf("y-text.tests.js:testDeltaAfterConcurrentFormatting", "F-9", "Y-6")]
    [Requires(Feature.TextFormat, Feature.Events)]
    public void Text_ConcurrentFormatting_ProducesTheExpectedDelta()
    {
        var net = Net.Init(2);
        var (text0, text1) = (net.Peers[0].Text, net.Peers[1].Text);
        text0.Insert(0, "abcde");
        net.Flush();
        text0.Format(0, 3, Bold);
        text1.Format(2, 2, Bold);
        var deltas = new List<string>();
        text1.Observe((e, _) =>
        {
            var te = (YTextEvent)e;
            if (te.Delta.Length > 0) deltas.Add(DeltaJson(te.Delta.ToArray()));
        });
        net.Flush();
        Assert.Equal(new[] { """[{"retain":3,"attributes":{"bold":true}},{"retain":2,"attributes":{"bold":null}}]""" }, deltas);
    }

    [Fact, PortOf("y-text.tests.js:testFormattingDeltaUnnecessaryAttributeChange", "Y-6", "F-9")]
    [Requires(Feature.TextFormat, Feature.Events)]
    public void Text_ReappliedSameFormat_ProducesNoUnnecessaryAttributeChange()
    {
        var net = Net.Init(2);
        var (text0, text1) = (net.Peers[0].Text, net.Peers[1].Text);
        text0.Insert(0, "\n", Obj(("PARAGRAPH_STYLES", "normal"), ("LIST_STYLES", "bullet")));
        text0.Insert(1, "abc", Obj(("PARAGRAPH_STYLES", "normal")));
        net.Flush();
        var deltas = new List<string>();
        text0.Observe((e, _) => deltas.Add(DeltaJson(((YTextEvent)e).Delta.ToArray())));
        text1.Observe((e, _) => deltas.Add(DeltaJson(((YTextEvent)e).Delta.ToArray())));
        text1.Format(0, 1, Obj(("LIST_STYLES", "number")));
        net.Flush();
        var filtered = deltas.Where(d => d != "[]").ToList();
        Assert.Equal(2, filtered.Count);
        Assert.Equal("""[{"retain":1,"attributes":{"LIST_STYLES":"number"}}]""", filtered[0]);
        Assert.Equal(filtered[0], filtered[1]);
    }

    // ------------------------------------------------------------------ embeds

    [Fact, Covers("E-1", "E-2")]
    [Requires(Feature.TextFormat, Feature.Map)]
    public void InsertEmbed_OccupiesOneUnit_AndTakesAnyValueOrAType()
    {
        var text = NewDoc().GetText("t");
        text.Insert(0, "ab");
        text.InsertEmbed(1, Obj(("image", "src.png")), Obj(("width", 100)));
        text.InsertEmbed(0, 5);
        var map = new YMap();
        text.InsertEmbed(text.Length, map);
        map.Set("k", "v");
        Assert.Equal(5, text.Length);
        Assert.Equal("ab", text.ToString());
        var delta = text.ToDelta();
        Assert.Equal(5, delta.Length);
        Assert.Equal(5, delta[0].Embed.Value.AsNumber());
        Assert.Equal(YEntryKind.Type, delta[4].Embed.Kind);
        Assert.Same(map, delta[4].Embed.Type);
        Assert.Equal("""{"k":"v"}""", map.Json());
    }

    [Fact, PortOf("y-text.tests.js:testToDeltaEmbedAttributes", "E-3", "D-1", "D-2")]
    [Requires(Feature.TextFormat, Feature.Sync)]
    public void ToDelta_EmbedWithAttributes_IncludesTheAttributes()
    {
        var text0 = Net.Init(1).Peers[0].Text;
        text0.Insert(0, "ab", Bold);
        text0.InsertEmbed(1, Obj(("image", "imageSrc.png")), Obj(("width", 100)));
        Assert.Equal(
            """[{"insert":"a","attributes":{"bold":true}},{"insert":{"image":"imageSrc.png"},"attributes":{"width":100}},{"insert":"b","attributes":{"bold":true}}]""",
            text0.DeltaJson());
    }

    [Fact, PortOf("y-text.tests.js:testToDeltaEmbedNoAttributes", "E-3", "D-2", "E-6")]
    [Requires(Feature.TextFormat, Feature.Sync)]
    public void ToDelta_EmbedWithoutAttributes_OmitsTheAttributesKey()
    {
        var text0 = Net.Init(1).Peers[0].Text;
        text0.Insert(0, "ab", Bold);
        text0.InsertEmbed(1, Obj(("image", "imageSrc.png")));
        // toDelta does not set the attributes key when no attributes are present on an embed
        Assert.Equal(
            """[{"insert":"a","attributes":{"bold":true}},{"insert":{"image":"imageSrc.png"}},{"insert":"b","attributes":{"bold":true}}]""",
            text0.DeltaJson());
    }

    [Fact, PortOf("y-text.tests.js:testGetDeltaWithEmbeds", "E-4")]
    [Requires(Feature.TextFormat, Feature.Sync)]
    public void ToDelta_ApplyDeltaEmbed_ReturnsTheEmbed()
    {
        var text0 = Net.Init(1).Peers[0].Text;
        text0.ApplyDelta(new[] { new TextDeltaOp(DeltaKind.Insert, 1, null, (YEntry)Obj(("linebreak", "s"))) });
        Assert.Equal("""[{"insert":{"linebreak":"s"}}]""", text0.DeltaJson());
    }

    [Fact, PortOf("y-text.tests.js:testTypesAsEmbed", "E-4", "E-3")]
    [Requires(Feature.TextFormat, Feature.Map, Feature.Events)]
    public void Text_TypeInsertedAsEmbed_SyncsAndReadsBack()
    {
        var net = Net.Init(2);
        var (text0, text1) = (net.Peers[0].Text, net.Peers[1].Text);
        text0.ApplyDelta(new[] { new TextDeltaOp(DeltaKind.Insert, 1, null, (YEntry)new YMap(new[] { new KeyValuePair<string, YEntry>("key", "val") })) });
        Assert.Equal("""{"key":"val"}""", text0.ToDelta()[0].Embed.Type!.Json());
        var firedEvent = false;
        text1.Observe((e, _) =>
        {
            var d = ((YTextEvent)e).Delta.ToArray();
            Assert.Single(d);
            Assert.Equal("""{"key":"val"}""", d[0].Embed.Type!.Json());
            firedEvent = true;
        });
        net.Flush();
        var delta = text1.ToDelta();
        Assert.Single(delta);
        Assert.Equal("""{"key":"val"}""", delta[0].Embed.Type!.Json());
        Assert.True(firedEvent, "fired the event observer containing a Type-Embed");
    }

    [Fact, Covers("E-5")]
    [Requires(Feature.TextFormat, Feature.Array, Feature.Map)]
    public void EmbeddedType_Deleted_DeletesItsSubtree()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "ab");
        var map = new YMap(new[] { new KeyValuePair<string, YEntry>("k", new YArray()) });
        text.InsertEmbed(1, map);
        var inner = map.ArrayOf("k");
        inner.Push(new YEntry[] { 1, 2 });
        text.Delete(0, 3);
        Assert.Equal(0, map.Count);
        Assert.Equal(0, inner.Length);
        Assert.Equal("[]", text.DeltaJson());
        Assert.Equal(0, text.Length);
    }

    [Fact, Covers("E-6")]
    [Requires(Feature.TextFormat)]
    public void Embeds_NeighbouringStrings_AreNotMerged()
    {
        var text = NewDoc().GetText("t");
        text.Insert(0, "abcd");
        text.InsertEmbed(2, Obj(("e", 1)));
        text.InsertEmbed(3, Obj(("e", 2)));
        Assert.Equal("""[{"insert":"ab"},{"insert":{"e":1}},{"insert":{"e":2}},{"insert":"cd"}]""", text.DeltaJson());
    }

    // ------------------------------------------------------------------ toDelta

    [Fact, Covers("D-1", "D-2", "D-4")]
    [Requires(Feature.TextFormat)]
    public void ToDelta_MergesAdjacentRunsWithEqualAttributes_AndOmitsEmptyAttributes()
    {
        var text = NewDoc().GetText("t");
        Assert.Equal("[]", text.DeltaJson());
        text.Insert(0, "a", Obj(("b", true)));
        text.Insert(1, "b", Obj(("b", true)));
        Assert.Equal("""[{"insert":"ab","attributes":{"b":true}}]""", text.DeltaJson());
        text.Insert(2, "c", Obj());
        Assert.Equal("""[{"insert":"ab","attributes":{"b":true}},{"insert":"c"}]""", text.DeltaJson());
        Assert.All(text.ToDelta(), op => Assert.Equal(DeltaKind.Insert, op.Kind));
    }

    [Fact, PortOf("y-text.tests.js:testSnapshot", "D-3")]
    [Requires(Feature.TextFormat, Feature.Snapshots)]
    public void Text_SnapshotsOfEdits_ProduceTheDeltaOfEachVersion()
    {
        var net = Net.Init(1);
        var text0 = net.Peers[0].Text;
        var doc0 = text0.Doc!;
        doc0.Gc = false;
        text0.ApplyDelta(new[] { Insert("abcd") });
        var snapshot1 = YSnapshot.Create(doc0);
        text0.ApplyDelta(new[] { Retain(1), Insert("x"), Delete(1) });
        var snapshot2 = YSnapshot.Create(doc0);
        text0.ApplyDelta(new[] { Retain(2), Delete(3), Insert("x"), Delete(1) });
        Assert.Equal("""[{"insert":"abcd"}]""", DeltaJson(text0.ToDelta(snapshot1)));
        Assert.Equal("""[{"insert":"axcd"}]""", DeltaJson(text0.ToDelta(snapshot2)));
        // the ychange attribute carries the user in JS (removed by the test); the callback here returns only the type
        var diff = text0.ToDelta(snapshot2, snapshot1, (type, _) => Obj(("type", type)));
        Assert.Equal(
            """[{"insert":"a"},{"insert":"x","attributes":{"ychange":{"type":"added"}}},{"insert":"b","attributes":{"ychange":{"type":"removed"}}},{"insert":"cd"}]""",
            DeltaJson(diff));
    }

    [Fact, PortOf("y-text.tests.js:testSnapshotDeleteAfter", "D-3")]
    [Requires(Feature.TextFormat, Feature.Snapshots)]
    public void Text_DeleteAfterSnapshot_SnapshotStillShowsTheDeletedText()
    {
        var text0 = Net.Init(1).Peers[0].Text;
        var doc0 = text0.Doc!;
        doc0.Gc = false;
        text0.ApplyDelta(new[] { Insert("abcd") });
        var snapshot1 = YSnapshot.Create(doc0);
        text0.ApplyDelta(new[] { Retain(4), Insert("e") });
        Assert.Equal("""[{"insert":"abcd"}]""", DeltaJson(text0.ToDelta(snapshot1)));
    }

    // ------------------------------------------------------------------ attributes (map side)

    [Fact, Covers("X-1", "X-2")]
    [Requires(Feature.TextFormat)]
    public void Attributes_UseTheMapMachinery_AndListOnlyLiveOnes()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "abc", Bold);
        text.SetAttribute("align", "left");
        text.SetAttribute("level", 2);
        text.SetAttribute("gone", true);
        text.RemoveAttribute("gone");
        text.RemoveAttribute("never-set");
        Assert.True(text.TryGetAttribute("align", out var align));
        Assert.Equal("left", align.AsString());
        Assert.False(text.TryGetAttribute("gone", out _));
        var attrs = text.GetAttributes();
        Assert.Equal(new[] { "align", "level" }, attrs.Keys.OrderBy(k => k, StringComparer.Ordinal));
        // attributes are separate from rich-text formats and from the text itself
        Assert.Equal("abc", text.ToString());
        Assert.Equal("""[{"insert":"abc","attributes":{"bold":true}}]""", text.DeltaJson());
        text.SetAttribute("align", "right");
        Assert.True(text.TryGetAttribute("align", out align));
        Assert.Equal("right", align.AsString());
    }

    // ------------------------------------------------------------------ remaining ports

    [Fact, PortOf("y-text.tests.js:testBasicInsertAndDelete", "Y-2", "Y-5", "A-E4", "TX-5", "T-1", "T-2")]
    [Requires(Feature.Events)]
    public void Text_InsertAndDelete_ReportDeltasForEachChange()
    {
        var net = Net.Init(2);
        var text0 = net.Peers[0].Text;
        string delta = "";
        text0.Observe((e, _) => delta = DeltaJson(((YTextEvent)e).Delta.ToArray()));

        text0.Delete(0, 0); // does not throw when deleting zero elements with position 0

        text0.Insert(0, "abc");
        Assert.Equal("abc", text0.ToString());
        Assert.Equal("""[{"insert":"abc"}]""", delta);

        text0.Delete(0, 1);
        Assert.Equal("bc", text0.ToString());
        Assert.Equal("""[{"delete":1}]""", delta);

        text0.Delete(1, 1);
        Assert.Equal("b", text0.ToString());
        Assert.Equal("""[{"retain":1},{"delete":1}]""", delta);

        net.Users[0].Doc.Transact(_ =>
        {
            text0.Insert(0, "1");
            text0.Delete(0, 1);
        });
        Assert.Equal("[]", delta);

        net.Compare();
    }

    [Fact, PortOf("y-text.tests.js:testMultilineFormat", "F-6", "T-6")]
    [Requires(Feature.TextFormat)]
    public void Text_FormatAcrossLines_KeepsTheLineBreaksUnformatted()
    {
        var testText = NewDoc().GetText("test");
        testText.Insert(0, "Test\nMulti-line\nFormatting");
        testText.ApplyDelta(new[] { Retain(4, Bold), Retain(1), Retain(10, Bold), Retain(1), Retain(10, Bold) });
        Assert.Equal(
            """[{"insert":"Test","attributes":{"bold":true}},{"insert":"\n"},{"insert":"Multi-line","attributes":{"bold":true}},{"insert":"\n"},{"insert":"Formatting","attributes":{"bold":true}}]""",
            testText.DeltaJson());
    }

    [Fact, PortOf("y-text.tests.js:testNotMergeEmptyLinesFormat", "D-1", "T-6")]
    [Requires(Feature.TextFormat)]
    public void Text_EmptyLinesWithTheSameFormat_AreNotMerged()
    {
        var testText = NewDoc().GetText("test");
        var title = Obj(("title", true));
        var delta = new[] { Insert("Text"), Insert("\n", title), Insert("\nText"), Insert("\n", title) };
        testText.ApplyDelta(delta);
        Assert.Equal(DeltaJson(delta), testText.DeltaJson());
    }

    [Fact, PortOf("y-text.tests.js:testPreserveAttributesThroughDelete", "F-7", "T-6")]
    [Requires(Feature.TextFormat)]
    public void Text_DeleteBetweenFormattedLines_PreservesTheAttributes()
    {
        var testText = NewDoc().GetText("test");
        var title = Obj(("title", true));
        testText.ApplyDelta(new[] { Insert("Text"), Insert("\n", title), Insert("\n") });
        testText.ApplyDelta(new[] { Retain(4), Delete(1), Retain(1, title) });
        Assert.Equal(DeltaJson(new[] { Insert("Text"), Insert("\n", title) }), testText.DeltaJson());
    }

    [Fact, PortOf("y-text.tests.js:testToJson", "T-3")]
    [Requires(Feature.Sync)]
    public void ToJson_FormattedText_ReturnsTheUnformattedText()
    {
        var text0 = Net.Init(1).Peers[0].Text;
        text0.Insert(0, "abc", Bold);
        Assert.Equal("abc", text0.ToJson().AsString()); // toJSON returns the unformatted text
    }

    [Fact, PortOf("y-text.tests.js:testAppendChars", "T-1")]
    [Requires(Feature.Sync)]
    public void Text_TenThousandAppends_KeepTheLengthCorrect()
    {
        const int n = 10000;
        var text0 = Net.Init(1).Peers[0].Text;
        for (var i = 0; i < n; i++) text0.Insert(text0.Length, "a");
        Assert.Equal(n, text0.Length);
    }

    [Fact, PortOf("y-text.tests.js:testSearchMarkerBug1", "A-14")]
    [Requires(Feature.Events)]
    public void Text_SearchMarkerRegression_ConvergesAcrossPeers()
    {
        var net = Net.Init(2);
        var (text0, text1) = (net.Peers[0].Text, net.Peers[1].Text);
        var (doc0, doc1) = (net.Users[0].Doc, net.Users[1].Doc);
        doc0.Update += (update, _, _, _) =>
        {
            var bytes = update.ToArray();
            doc0.Transact(_ => doc0.ApplyUpdate(bytes));
        };
        doc0.Update += (update, _, _, _) =>
        {
            var bytes = update.ToArray();
            doc1.Transact(_ => doc1.ApplyUpdate(bytes));
        };

        text0.Insert(0, "a_a");
        net.Flush();
        text0.Insert(2, "s");
        net.Flush();
        text1.Insert(3, "d");
        net.Flush();
        text0.Delete(0, 5);
        net.Flush();
        text0.Insert(0, "a_a");
        net.Flush();
        text0.Insert(2, "s");
        net.Flush();
        text1.Insert(3, "d");
        net.Flush();
        Assert.Equal(text0.ToString(), text1.ToString());
        Assert.Equal("a_sda", text0.ToString());
        net.Compare();
    }

    [Fact, PortOf("y-text.tests.js:testDeltaBug", "D-1", "F-2")]
    [Requires(Feature.TextFormat)]
    public void Text_DeltaRegression_AppliesTheStoredDeltaCleanly()
    {
        const string file = "y-text.tests.js";
        string Literal(string name) => JsSource.ConstAsJson(file, "testDeltaBug", name);
        var ydoc1 = NewDoc();
        var ytext = ydoc1.GetText();
        ytext.ApplyDelta(JsSource.DeltaOps(Literal("initialDelta")));
        ytext.ApplyDelta(JsSource.DeltaOps(Literal("addingDash")));
        ytext.ApplyDelta(JsSource.DeltaOps(Literal("addingSpace")));
        ytext.ApplyDelta(JsSource.DeltaOps(Literal("addingList")));
        StructuralAssert.Equal(Literal("expectedResult"), ytext.DeltaJson());
    }

    [Fact, PortOf("y-text.tests.js:testDeltaBug2", "D-1", "F-2")]
    [Requires(Feature.TextFormat, Feature.Array)]
    public void Text_SecondDeltaRegression_AppliesTheStoredDeltaCleanly()
    {
        const string file = "y-text.tests.js";
        string Literal(string name) => JsSource.ConstAsJson(file, "testDeltaBug2", name);
        var ydoc = NewDoc();
        var ytext = ydoc.GetText("id");
        ytext.ApplyDelta(JsSource.DeltaOps(Literal("initialContent")));
        ytext.ApplyDelta(JsSource.DeltaOps(Literal("changeEvent")));
        var delta = Parse(ytext.DeltaJson());
        Assert.True(delta.TryGetArray(out var items));
        StructuralAssert.Equal(
            """{"insert":"\n","attributes":{"block-id":"block-9d6566a1-be55-4e20-999a-b990bc15e143"}}""",
            items[41].Json());
    }
}
