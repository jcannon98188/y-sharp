using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.TestKit.Traceability;
using static YSharp.Tests.Types.TypeTestExtensions;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Types;

/// <summary>Behaviour of the XML types (X-E*) and the ports of y-xml.tests.js.</summary>
[Requires(Feature.Xml)]
public sealed class XmlTests
{
    private static Doc NewDoc(ulong client = 1) => new(new DocOptions { ClientId = client });

    private static YType[] Nodes(params YType[] nodes) => nodes;

    [Fact, Covers("X-E1")]
    [Requires(Feature.Sync)]
    public void NodeName_DefaultsToUndefined_AndKeepsAGivenName()
    {
        Assert.Equal("UNDEFINED", new YXmlElement().NodeName);
        Assert.Equal("my-Tag", new YXmlElement("my-Tag").NodeName);
        var net = Net.Init(2);
        net.Peers[0].Xml.Insert(0, Nodes(new YXmlElement("p"), new YXmlElement()));
        net.Flush();
        var remote = net.Peers[1].Xml;
        Assert.Equal("p", ((YXmlElement)remote[0]).NodeName);
        Assert.Equal("UNDEFINED", ((YXmlElement)remote[1]).NodeName);
        net.Compare();
    }

    [Fact, Covers("X-E2")]
    [Requires(Feature.TextFormat)]
    public void Attributes_AnyElement_AreAStringKeyedMapOfAnyValues()
    {
        var doc = NewDoc();
        var fragment = doc.GetXmlFragment("f");
        var el = new YXmlElement("div");
        fragment.Insert(0, Nodes(el));
        el.SetAttribute("s", "text");
        el.SetAttribute("n", 42);
        el.SetAttribute("o", Obj(("a", YValue.Array(new YValue[] { 1, 2 }))));
        el.SetAttribute("b", true);
        el.SetAttribute("gone", 1);
        el.RemoveAttribute("gone");
        Assert.False(el.HasAttribute("gone"));
        Assert.True(el.HasAttribute("s"));
        Assert.False(el.TryGetAttribute("missing", out _));
        Assert.True(el.TryGetAttribute("n", out var n));
        Assert.Equal(42, n.AsNumber());
        var all = el.GetAttributes();
        Assert.Equal(new[] { "b", "n", "o", "s" }, all.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("""{"a":[1,2]}""", all["o"].Json());
        // before attachment the attributes are buffered and applied on integration, in insertion order
        var prelim = new YXmlElement("p");
        prelim.SetAttribute("z", 1);
        prelim.SetAttribute("a", 2);
        fragment.Insert(1, Nodes(prelim));
        Assert.Equal(new[] { "z", "a" }, prelim.GetAttributes().Keys);
    }

    [Fact, Covers("X-E3")]
    [Requires(Feature.Text)]
    public void ToString_SortsAttributes_StringifiesValuesLikeJs_AndDoesNotEscape()
    {
        var doc = NewDoc();
        var fragment = doc.GetXmlFragment("f");
        var el = new YXmlElement("Div");
        var inner = new YXmlElement("span");
        var text = new YXmlText("plain");
        fragment.Insert(0, Nodes(el));
        el.Insert(0, Nodes(inner, text));
        el.SetAttribute("n", 42);
        el.SetAttribute("b", true);
        el.SetAttribute("q", "say \"hi\" <&>");
        el.SetAttribute("nil", YValue.Null);
        el.SetAttribute("obj", Obj(("a", 1)));
        el.SetAttribute("arr", YValue.Array(new YValue[] { 1, 2 }));
        inner.SetAttribute("k", "v");
        Assert.Equal(
            "<div arr=\"1,2\" b=\"true\" n=\"42\" nil=\"null\" obj=\"[object Object]\" q=\"say \"hi\" <&>\"><span k=\"v\"></span>plain</div>",
            el.ToString());
        Assert.Equal(el.ToString(), fragment.ToString());
        Assert.Equal(fragment.ToString(), fragment.ToJson().AsString());
    }

    [Fact, PortOf("y-xml.tests.js:testSetProperty", "X-E2", "X-E5")]
    [Requires(Feature.Sync)]
    public void Xml_SetAttribute_IsReadBackLocallyAndOnThePeer()
    {
        var net = Net.Init(2);
        var (xml0, xml1) = (net.Peers[0].Xml, net.Peers[1].Xml);
        xml0.SetAttribute("height", "10");
        Assert.True(xml0.TryGetAttribute("height", out var h0) && h0.AsString() == "10");
        net.Flush();
        Assert.True(xml1.TryGetAttribute("height", out var h1) && h1.AsString() == "10");
        net.Compare();
    }

    [Fact, PortOf("y-xml.tests.js:testHasProperty", "X-E2")]
    [Requires(Feature.Sync)]
    public void Xml_HasAttribute_TrueAfterSetAndFalseAfterRemove()
    {
        var net = Net.Init(2);
        var (xml0, xml1) = (net.Peers[0].Xml, net.Peers[1].Xml);
        xml0.SetAttribute("height", "10");
        Assert.True(xml0.HasAttribute("height"));
        net.Flush();
        Assert.True(xml1.HasAttribute("height"));
        xml0.RemoveAttribute("height");
        Assert.False(xml0.HasAttribute("height"));
        net.Flush();
        Assert.False(xml1.HasAttribute("height"));
        net.Compare();
    }

    [Fact, PortOf("y-xml.tests.js:testEvents", "X-E5", "Y-1")]
    [Requires(Feature.TextFormat, Feature.Text, Feature.Events)]
    [Requires(Feature.Sync)]
    public void Xml_AttributeAndChildChanges_RaiseEventsLocallyAndRemotely()
    {
        var net = Net.Init(2);
        var (xml0, xml1) = (net.Peers[0].Xml, net.Peers[1].Xml);
        (string[] Attributes, bool ChildList) local = (System.Array.Empty<string>(), false), remote = (System.Array.Empty<string>(), false);
        xml0.Observe((e, _) => local = Capture(e));
        xml1.Observe((e, _) => remote = Capture(e));
        static (string[], bool) Capture(YEvent e) => (((YXmlEvent)e).AttributesChanged.ToArray(), ((YXmlEvent)e).ChildListChanged);

        xml0.SetAttribute("key", "value");
        Assert.Contains("key", local.Attributes);
        net.Flush();
        Assert.Contains("key", remote.Attributes);
        // attributeRemoved
        xml0.RemoveAttribute("key");
        Assert.Contains("key", local.Attributes);
        net.Flush();
        Assert.Contains("key", remote.Attributes);
        xml0.Insert(0, Nodes(new YXmlText("some text")));
        Assert.True(local.ChildList);
        net.Flush();
        Assert.True(remote.ChildList);
        // childRemoved
        xml0.Delete(0);
        Assert.True(local.ChildList);
        net.Flush();
        Assert.True(remote.ChildList);
        net.Compare();
    }

    [Fact, PortOf("y-xml.tests.js:testTreewalker", "X-E4")]
    [Requires(Feature.Text, Feature.Sync)]
    public void Xml_TreeWalker_VisitsNodesInDocumentOrder()
    {
        var net = Net.Init(3);
        var xml0 = net.Peers[0].Xml;
        var paragraph1 = new YXmlElement("p");
        var paragraph2 = new YXmlElement("p");
        var text1 = new YXmlText("init");
        var text2 = new YXmlText("text");
        paragraph1.Insert(0, Nodes(text1, text2));
        xml0.Insert(0, Nodes(paragraph1, paragraph2, new YXmlElement("img")));
        var allParagraphs = xml0.QuerySelectorAll("p");
        Assert.Equal(2, allParagraphs.Count);
        Assert.Same(paragraph1, allParagraphs[0]);
        Assert.Same(paragraph2, allParagraphs[1]);
        Assert.Same(paragraph1, xml0.QuerySelector("p"));
        // the query matches element names case-insensitively and never matches text nodes
        Assert.Same(paragraph1, xml0.QuerySelector("P"));
        Assert.Null(xml0.QuerySelector("nothing"));
        xml0.Delete(0);
        Assert.Same(paragraph2, xml0.QuerySelector("p"));
        net.Compare();
    }

    [Fact, PortOf("y-xml.tests.js:testSiblings", "X-E4")]
    [Requires(Feature.Text)]
    public void Xml_Siblings_LinkNeighboursParentAndFirstChild()
    {
        var ydoc = NewDoc();
        var yxml = ydoc.GetXmlFragment();
        var first = new YXmlText();
        var second = new YXmlElement("p");
        yxml.Insert(0, Nodes(first, second));
        Assert.Same(second, first.NextSibling);
        Assert.Same(first, second.PrevSibling);
        Assert.Same(yxml, first.Parent);
        Assert.Null(yxml.Parent);
        Assert.Same(first, yxml.FirstChild);
        Assert.Null(first.PrevSibling);
        Assert.Null(second.NextSibling);
        // deleted neighbours are skipped, and an unattached node has no siblings
        var third = new YXmlElement("q");
        yxml.Push(Nodes(third));
        yxml.Delete(1);
        Assert.Same(third, first.NextSibling);
        Assert.Same(first, third.PrevSibling);
        Assert.Null(new YXmlElement("x").NextSibling);
    }

    [Fact, PortOf("y-xml.tests.js:testInsertafter", "X-E4")]
    [Requires(Feature.Text)]
    public void Xml_InsertAfterNestedNodes_KeepsTheOrderAndRejectsInvalidUse()
    {
        var ydoc = NewDoc();
        var yxml = ydoc.GetXmlFragment();
        var first = new YXmlText();
        var second = new YXmlElement("p");
        var third = new YXmlElement("p");

        var deepsecond1 = new YXmlElement("span");
        var deepsecond2 = new YXmlText();
        second.InsertAfter(null, Nodes(deepsecond1));
        second.InsertAfter(deepsecond1, Nodes(deepsecond2));

        yxml.InsertAfter(null, Nodes(first, second));
        yxml.InsertAfter(second, Nodes(third));

        Assert.Equal(3, yxml.Length);
        Assert.Same(deepsecond1, second[0]);
        Assert.Same(deepsecond2, second[1]);
        Assert.Same(first, yxml[0]);
        Assert.Same(second, yxml[1]);
        Assert.Same(third, yxml[2]);

        // a reference that is not in the prelim content of this element throws
        Assert.ThrowsAny<Exception>(() =>
        {
            var el = new YXmlElement("p");
            el.InsertAfter(deepsecond1, Nodes(new YXmlText()));
        });
    }

    [Fact, PortOf("y-xml.tests.js:testClone", "X-E6")]
    [Requires(Feature.Text, Feature.Array)]
    public void Xml_CloneOfAFragment_HasTheSameJsonAndLength()
    {
        var ydoc = NewDoc();
        var yxml = ydoc.GetXmlFragment();
        var first = new YXmlText("text");
        var second = new YXmlElement("p");
        var third = new YXmlElement("p");
        yxml.Push(Nodes(first, second, third));
        Assert.Same(first, yxml[0]);
        Assert.Same(third, yxml[2]);
        var cloneYxml = (YXmlFragment)yxml.Clone();
        ydoc.GetArray("copyarr").Insert(0, new YEntry[] { cloneYxml });
        Assert.Equal(3, cloneYxml.Length);
        Assert.Equal(yxml.ToJson().Json(), cloneYxml.ToJson().Json());
    }

    [Fact, PortOf("y-xml.tests.js:testElement", "X-E8")]
    [Requires(Feature.Text)]
    public void Xml_ElementWithTwoTextChildren_KeepsTheirOrder()
    {
        var ydoc = NewDoc();
        var yxmlel = ydoc.GetXmlElement();
        var text1 = new YXmlText("text1");
        var text2 = new YXmlText("text2");
        yxmlel.Insert(0, Nodes(text1, text2));
        Assert.Equal(2, yxmlel.Length);
        Assert.Same(text1, yxmlel[0]);
        Assert.Same(text2, yxmlel[1]);
    }

    [Fact, Covers("X-E8")]
    public void RootElement_NoNodeName_HasTheDefaultNodeName()
    {
        var doc = NewDoc();
        var root = doc.GetXmlElement("e");
        Assert.Equal("UNDEFINED", root.NodeName);
        Assert.Equal("<undefined></undefined>", root.ToString());
        Assert.Same(root, doc.GetXmlElement("e"));
    }

    [Fact, Covers("X-E7", "Y-7")]
    [Requires(Feature.Events)]
    public void AttributeEvents_AnyChange_UseMapSemantics()
    {
        var doc = NewDoc();
        var el = new YXmlElement("p");
        doc.GetXmlFragment("f").Insert(0, Nodes(el));
        var seen = new List<string>();
        el.Observe((e, _) => seen.Add(string.Join(";", e.Keys.OrderBy(k => k.Key, StringComparer.Ordinal)
            .Select(k => $"{k.Key}:{k.Value.Action}:{(k.Value.OldValue is { } old ? old.Value.Json() : "-")}"))));
        el.SetAttribute("a", 1);
        el.SetAttribute("a", 2);
        doc.Transact(_ =>
        {
            el.SetAttribute("a", 3);
            el.SetAttribute("a", 4);
            el.SetAttribute("b", 1);
            el.SetAttribute("b", 2);
            el.SetAttribute("c", 1);
            el.RemoveAttribute("c");
        });
        el.RemoveAttribute("a");
        Assert.Equal(new[] { "a:Add:-", "a:Update:1", "a:Update:2;b:Add:-", "a:Delete:4" }, seen);
    }

    [Fact, Covers("X-E4")]
    public void FirstChild_DeletedChildren_AreSkipped()
    {
        var fragment = NewDoc().GetXmlFragment("f");
        var a = new YXmlElement("a");
        var b = new YXmlElement("b");
        fragment.Push(Nodes(a, b));
        fragment.Delete(0);
        Assert.Same(b, fragment.FirstChild);
        fragment.Delete(0);
        Assert.Null(fragment.FirstChild);
        Assert.Equal(0, fragment.Length);
    }
    // ------------------------------------------------------------------ oracle regressions (expected values from yjs 13.6.33)

    private static string Hex(Doc doc) => Convert.ToHexString(doc.EncodeStateAsUpdate()).ToLowerInvariant();

    private static YValue Arr(params YValue[] items) => YValue.Array(items);

    [Fact]
    public void Push_AfterDeletedTrailingChildren_InsertsAfterTheLastLiveChild()
    {
        // JS push is insert(length): the new child's origin is the last undeleted child, not the deleted ones after it
        var doc = NewDoc();
        var fragment = doc.GetXmlFragment("f");
        fragment.Insert(0, Nodes(new YXmlElement("a"), new YXmlElement("b"), new YXmlElement("c")));
        fragment.Delete(1, 2);
        fragment.Push(Nodes(new YXmlElement("d")));
        Assert.Equal("<a></a><d></d>", fragment.ToString());
        Assert.Equal("010301000701016603016181010002c7010001010301640101010102", Hex(doc));
    }

    [Fact]
    [Requires(Feature.Text)]
    public void InsertAfter_IntegratedReferenceOrNull_InsertsAfterItOrAtTheStart()
    {
        var doc = NewDoc();
        var fragment = doc.GetXmlFragment("f");
        var a = new YXmlElement("a");
        fragment.Insert(0, Nodes(a, new YXmlElement("b")));
        fragment.InsertAfter(a, Nodes(new YXmlText("x")));
        fragment.InsertAfter(null, Nodes(new YXmlElement("z")));
        Assert.Equal("<z></z><a></a>x<b></b>", fragment.ToString());
        Assert.Equal("0105010007010166030161870100030162c7010001010604000102017847010003017a00", Hex(doc));
        // a node that is not a child of this fragment is not a reference
        Assert.Throws<ArgumentException>(() => fragment.InsertAfter(new YXmlElement("q"), Nodes(new YXmlElement("r"))));
    }

    [Fact]
    [Requires(Feature.Text, Feature.TextFormat)]
    public void XmlText_ToString_RendersEachFormatAsASortedNestedTag()
    {
        var doc = NewDoc();
        var fragment = doc.GetXmlFragment("f");
        var text = new YXmlText();
        fragment.Insert(0, Nodes(text));
        text.Insert(0, "plain");
        text.Insert(5, "bold", Obj(("bold", true)));
        text.Insert(9, "link", Obj(("link", Obj(("href", "x"), ("a", 1))), ("em", Obj())));
        text.Insert(13, "str", Obj(("s", "ab")));
        text.Insert(16, "arr", Obj(("z", Arr(1, YValue.Null, 2))));
        text.InsertEmbed(19, Obj(("x", 1)));
        text.InsertEmbed(20, Arr(1, 2), Obj(("b", true)));
        // a format's tag takes the members JS for...in finds: none for true, an object's keys, a string's or an array's indexes
        const string expected = "plain<bold>bold</bold><em><link a=\"1\" href=\"x\">link</link></em><s 0=\"a\" 1=\"b\">str</s>"
            + "<z 0=\"1\" 1=\"null\" 2=\"2\">arr</z>[object Object]<b>1,2</b>";
        Assert.Equal(expected, text.ToString());
        Assert.Equal(expected, text.ToJson().AsString());
        Assert.Equal(expected, fragment.ToString());
    }

    [Fact, Covers("X-E3")]
    public void ToString_AttributeValues_AreConvertedAsJsStringDoes()
    {
        var doc = NewDoc();
        var el = doc.GetXmlElement("e");
        el.SetAttribute("a", 1e21);
        el.SetAttribute("b", 0.1);
        el.SetAttribute("c", -0.0);
        el.SetAttribute("d", 123456789012.0);
        el.SetAttribute("e", Arr(Arr(1, 2), Arr(3)));
        el.SetAttribute("f", YValue.Bytes(new byte[] { 1, 2 }));
        el.SetAttribute("B", "upper");
        el.SetAttribute("_", "u");
        Assert.Equal("<undefined B=\"upper\" _=\"u\" a=\"1e+21\" b=\"0.1\" c=\"0\" d=\"123456789012\" e=\"1,2,3\" f=\"1,2\"></undefined>", el.ToString());
    }

    [Fact, Covers("X-E2", "X-E6")]
    [Requires(Feature.Text)]
    public void Clone_OfAnElement_IntegratesItsChildrenBeforeItsAttributes()
    {
        var doc = NewDoc();
        var fragment = doc.GetXmlFragment("f");
        var p = new YXmlElement("p");
        p.SetAttribute("z", 1);
        p.SetAttribute("a", 2);
        p.Insert(0, Nodes(new YXmlText("t")));
        fragment.Insert(0, Nodes(p));
        fragment.Insert(1, Nodes(p.Clone()));
        Assert.Equal("<p a=\"2\" z=\"1\">t</p><p a=\"2\" z=\"1\">t</p>", fragment.ToString());
        Assert.Equal(new[] { "z", "a" }, ((YXmlElement)fragment[1]).GetAttributes().Keys);
        Assert.Equal(
            "010a010007010166030170070001000604000101017428000100017a017d01280001000161017d02870100030170070001050604000106017428000105017a017d01280001050161017d0200",
            Hex(doc));
    }

    [Fact]
    [Requires(Feature.Map)]
    public void Hook_InAFragment_PrintsAsAnObject_AndItsCloneKeepsTheName()
    {
        var doc = NewDoc();
        var fragment = doc.GetXmlFragment("f");
        var hook = new YXmlHook("hk");
        hook.Set("k", 1);
        fragment.Insert(0, Nodes(hook, new YXmlElement("a")));
        var clone = Assert.IsType<YXmlHook>(hook.Clone());
        fragment.Insert(2, Nodes(clone));
        Assert.Equal("hk", clone.HookName);
        Assert.Equal("[object Object]<a></a>[object Object]", fragment.ToString());
        Assert.Equal("01050100070101660502686b28000100016b017d018701000301618701020502686b28000103016b017d0100", Hex(doc));
    }

    [Fact, Covers("X-E4")]
    [Requires(Feature.Text)]
    public void QuerySelectorAll_NestedElements_MatchesInDocumentOrderIgnoringCaseAndDeletedNodes()
    {
        var fragment = NewDoc().GetXmlFragment("f");
        var a = new YXmlElement("div");
        var b = new YXmlElement("DIV");
        var c = new YXmlElement("div");
        var x = new YXmlElement("x");
        fragment.Insert(0, Nodes(a, x));
        a.Insert(0, Nodes(b, new YXmlText("q")));
        b.Insert(0, Nodes(c));
        x.Insert(0, Nodes(new YXmlElement("div")));
        x.Delete(0);
        Assert.Equal(new[] { a, b, c }, fragment.QuerySelectorAll("div"));
        Assert.Equal("div,DIV,div,text,x", string.Join(",", fragment.TreeWalk().Select(n => n is YXmlElement el ? el.NodeName : "text")));
        Assert.Same(a, fragment.QuerySelector("Div"));
    }
}
