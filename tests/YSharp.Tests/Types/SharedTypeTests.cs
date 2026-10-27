using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.TestKit.Traceability;
using static YSharp.Tests.Types.TypeTestExtensions;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Types;

/// <summary>Structure shared by every type: event classes, parents, root placeholders, prelim content and what a list or map accepts (T-0.*, Q10, Q29).</summary>
[Requires(Feature.TypesCore)]
public sealed class SharedTypeTests
{
    [Fact, Covers("T-0.1", "X-F8", "X-T4", "X-H1")]
    [Requires(Feature.Text, Feature.Array, Feature.Map, Feature.Xml, Feature.Events)]
    public void Events_EachType_RaisesItsOwnEventClassAndXmlTextEventsAreTextEvents()
    {
        var doc = new Doc(new DocOptions { ClientId = 1 });
        var array = doc.GetArray("a");
        var map = doc.GetMap("m");
        var text = doc.GetText("t");
        var fragment = doc.GetXmlFragment("f");
        var kinds = new List<string>();
        array.Observe((e, _) => kinds.Add("array:" + e.GetType().Name));
        map.Observe((e, _) => kinds.Add("map:" + e.GetType().Name));
        text.Observe((e, _) => kinds.Add("text:" + e.GetType().Name));
        fragment.Observe((e, _) => kinds.Add("fragment:" + e.GetType().Name));
        var xmlText = new YXmlText("x");
        var element = new YXmlElement("p");
        var hook = new YXmlHook("h");
        fragment.Insert(0, new YType[] { xmlText, element });
        map.Set("hook", hook);
        xmlText.Observe((e, _) => kinds.Add("xmlText:" + e.GetType().Name));
        element.Observe((e, _) => kinds.Add("element:" + e.GetType().Name));
        hook.Observe((e, _) => kinds.Add("hook:" + e.GetType().Name));
        array.Push(new YEntry[] { 1 });
        text.Insert(0, "a");
        doc.Transact(_ =>
        {
            xmlText.Insert(0, "b");
            element.SetAttribute("k", "v");
            hook.Set("k", 1);
        });
        Assert.Contains("array:YArrayEvent", kinds);
        Assert.Contains("map:YMapEvent", kinds);
        Assert.Contains("text:YTextEvent", kinds);
        Assert.Contains("fragment:YXmlEvent", kinds);
        Assert.Contains("xmlText:YTextEvent", kinds);
        Assert.Contains("element:YXmlEvent", kinds);
        Assert.Contains("hook:YMapEvent", kinds);
        Assert.DoesNotContain("xmlText:YXmlEvent", kinds);
    }

    [Fact, Covers("T-0.2")]
    [Requires(Feature.Text, Feature.Array, Feature.Map, Feature.Xml)]
    public void Parent_RootAndNestedTypes_IsNullForRootsAndTheContainerForNested()
    {
        var doc = new Doc(new DocOptions { ClientId = 1 });
        var array = doc.GetArray("a");
        var map = doc.GetMap("m");
        Assert.Null(array.Parent);
        Assert.Null(map.Parent);
        var inner = array.Insert(new YMap());
        Assert.Same(array, inner.Parent);
        var deep = inner.Set("t", new YText("x"));
        Assert.Same(inner, deep.Parent);
        var fragment = doc.GetXmlFragment("f");
        var child = new YXmlElement("p");
        fragment.Insert(0, new YType[] { child });
        Assert.Same(fragment, child.Parent);
        Assert.Null(fragment.Parent);
        Assert.Null(new YArray().Parent);
    }

    [Fact, PortOf("y-xml.tests.js:testYtextAttributes", "T-0.3", "X-1")]
    [Requires(Feature.TextFormat, Feature.Text, Feature.Array, Feature.Map, Feature.Xml, Feature.Events)]
    public void RootPlaceholder_IsUpgradedInPlace_AndConflictingConstructorsThrow()
    {
        // JS: ydoc.get('', Y.XmlText) creates an XmlText root (Doc.GetXmlText); the placeholder part of the test uses the untyped Get.
        var source = new Doc(new DocOptions { ClientId = 1 });
        source.GetText("t").Insert(0, "hello");
        source.GetMap("m").Set("k", 1);
        var doc = new Doc(new DocOptions { ClientId = 2 });
        doc.ApplyUpdate(source.EncodeStateAsUpdate());
        var placeholder = doc.Get("t");
        Assert.IsType<YUntyped>(placeholder);
        var upgraded = doc.GetText("t");
        Assert.Equal("hello", upgraded.ToString());
        Assert.Same(upgraded, doc.GetText("t"));
        Assert.Throws<YTypeConflictException>(() => doc.GetArray("t"));
        Assert.Throws<YTypeConflictException>(() => doc.GetMap("t"));
        var untypedMap = doc.Get("m");
        Assert.IsType<YUntyped>(untypedMap);
        Assert.Equal(1, doc.GetMap("m").Get("k").AsNumber());
        // XmlElement and XmlFragment are different constructors, so asking for the other one fails (also for a fresh root).
        doc.GetXmlFragment("f");
        Assert.Throws<YTypeConflictException>(() => doc.GetXmlElement("f"));
        doc.GetXmlElement("e");
        Assert.Throws<YTypeConflictException>(() => doc.GetXmlFragment("e"));

        var attributes = new Doc(new DocOptions { ClientId = 3 });
        var xmlText = attributes.GetXmlText("");
        Assert.Same(xmlText, attributes.GetXmlText(""));
        Assert.Throws<YTypeConflictException>(() => attributes.GetText(""));   // YText and YXmlText are different constructors
        var changes = new List<KeyChange>();
        xmlText.Observe((e, _) => changes.Add(e.Keys["test"]));
        xmlText.SetAttribute("test", 42);
        Assert.True(xmlText.TryGetAttribute("test", out var value));
        Assert.Equal(42, value.AsNumber());
        Assert.Equal("""{"test":42}""", YValue.Object(xmlText.GetAttributes().ToArray()).Json());
        Assert.Equal(KeyChangeAction.Add, Assert.Single(changes).Action);
        Assert.Null(changes[0].OldValue);
    }

    [Fact, Covers("T-0.4")]
    [Requires(Feature.TextFormat, Feature.Text, Feature.Array, Feature.Map, Feature.Xml)]
    public void UnattachedTypes_ReadAsEmpty_WithoutThrowing()
    {
        var array = new YArray();
        array.Push(new YEntry[] { 1, 2 });
        Assert.Equal(0, array.Length);
        Assert.Empty(array.ToArray());
        Assert.Equal("[]", array.Json());
        var map = new YMap();
        map.Set("k", 1);
        Assert.Equal(0, map.Count);
        Assert.False(map.Has("k"));
        Assert.Equal("{}", map.Json());
        var text = new YText("x");
        Assert.Equal(0, text.Length);
        Assert.Equal("", text.ToString());
        Assert.Empty(text.ToDelta());
        // the exception: an unattached fragment reports its prelim length (X-F6)
        var fragment = new YXmlFragment();
        fragment.Insert(0, new YType[] { new YXmlElement("p") });
        Assert.Equal(1, fragment.Length);
        Assert.False(array.IsIntegrated);
        Assert.Null(array.Doc);
    }

    [Fact, Covers("T-0.5")]
    [Requires(Feature.TextFormat, Feature.Text, Feature.Array, Feature.Map, Feature.Xml)]
    public void Writes_BeforeAttachment_AreBufferedAndAppearAfterIt()
    {
        var doc = new Doc(new DocOptions { ClientId = 1 });
        var host = doc.GetArray("host");
        var array = new YArray();
        array.Insert(0, new YEntry[] { 1, 2 });
        array.Push(new YEntry[] { 3 });
        var map = new YMap();
        map.Set("a", 1);
        map.Set("b", "two");
        map.Delete("a");
        var text = new YText();
        text.Insert(0, "hello");
        text.Format(0, 2, Obj(("bold", true)));
        var element = new YXmlElement("p");
        element.SetAttribute("k", "v");
        element.Insert(0, new YType[] { new YXmlText("child") });
        host.Push(new YEntry[] { array, map, text });
        var fragment = doc.GetXmlFragment("f");
        fragment.Insert(0, new YType[] { element });
        Assert.Equal("[1,2,3]", array.Json());
        Assert.Equal("""{"b":"two"}""", map.Json());
        Assert.Equal("""[{"insert":"he","attributes":{"bold":true}},{"insert":"llo"}]""", text.DeltaJson());
        Assert.True(element.TryGetAttribute("k", out var k));
        Assert.Equal("v", k.AsString());
        Assert.Equal(1, element.Length);
        Assert.Equal("""[[1,2,3],{"b":"two"},"hello"]""", host.Json());
    }

    [Fact, Covers("T-0.6")]
    [Requires(Feature.Text, Feature.Array, Feature.Map, Feature.Events)]
    public void Attaching_ReplaysBufferedContent_InsideTheTransactionThatIntegratesTheParent()
    {
        var doc = new Doc(new DocOptions { ClientId = 1 });
        var host = doc.GetMap("host");
        var events = 0;
        var updates = new List<byte[]>();
        host.Observe((_, _) => events++);
        doc.Update += (u, _, _, _) => updates.Add(u.ToArray());
        var array = new YArray();
        array.Push(new YEntry[] { 1, 2, 3 });
        var text = new YText("abc");
        text.Insert(3, "def");
        var order = new List<string>();
        array.Observe((_, _) => order.Add("array"));
        doc.Transact(_ =>
        {
            host.Set("array", array);
            host.Set("text", text);
        });
        Assert.Equal(1, events);
        Assert.Single(updates);
        Assert.Equal("abcdef", text.ToString());
        Assert.Equal("[1,2,3]", array.Json());
        // a type created inside the transaction that inserts it never gets its own event (A-E8)
        Assert.Empty(order);
    }

    [Fact, PortOf("y-map.tests.js:testMapHavingIterableAsConstructorParamTests", "T-0.7", "M-10")]
    [Requires(Feature.Text, Feature.Array, Feature.Map, Feature.Xml, Feature.Sync)]
    public void ConstructorSugar_InitialContent_IsBuffered()
    {
        var net = Net.Init(1);
        var map0 = net.Peers[0].Map;
        var m1 = new YMap(new KeyValuePair<string, YEntry>[] { new("number", 1), new("string", "hello") });
        map0.Set("m1", m1);
        Assert.Equal(1, m1.Get("number").AsNumber());
        Assert.Equal("hello", m1.Get("string").AsString());
        var m2 = new YMap(new KeyValuePair<string, YEntry>[]
        {
            new("object", Obj(("x", 1))),
            new("boolean", true),
        });
        map0.Set("m2", m2);
        Assert.True(m2.Get("object").TryGetObject(out var obj) && obj.TryGetValue("x", out var x) && x.AsNumber() == 1);
        Assert.True(m2.Get("boolean").AsBool());
        var m3 = new YMap(TypeDump.Enumerate(m1).Concat(TypeDump.Enumerate(m2)));
        map0.Set("m3", m3);
        Assert.Equal(1, m3.Get("number").AsNumber());
        Assert.Equal("hello", m3.Get("string").AsString());
        Assert.True(m3.Get("boolean").AsBool());
        Assert.Equal(4, m3.Count);

        var text = new YText("abc");
        var array = YArray.From(new YEntry[] { 0, 1, 2 });
        var element = new YXmlElement();
        var xmlText = new YXmlText("init");
        var host = net.Peers[0].Array;
        host.Push(new YEntry[] { text, array });
        net.Peers[0].Xml.Insert(0, new YType[] { element, xmlText });
        Assert.Equal("abc", text.ToString());
        Assert.Equal("[0,1,2]", array.Json());
        Assert.Equal("UNDEFINED", element.NodeName);
        Assert.Equal("init", xmlText.ToString().Replace("<", "").Replace(">", ""));
        // duplicates in the initial entries: the last one wins
        var dup = new YMap(new KeyValuePair<string, YEntry>[] { new("k", 1), new("k", 2) });
        map0.Set("dup", dup);
        Assert.Equal(2, dup.Get("k").AsNumber());
        Assert.Equal(1, dup.Count);
        var empty = new YMap();
        map0.Set("empty", empty);
        Assert.Equal(0, empty.Count);
    }

    [Fact, PortOf("y-array.tests.js:testArrayFrom", "T-0.7")]
    [Requires(Feature.Array, Feature.Map)]
    public void ArrayFrom_Items_AreBuffered()
    {
        var doc1 = new Doc(new DocOptions { ClientId = 1 });
        var db1 = doc1.GetMap("root");
        var nestedArray1 = YArray.From(new YEntry[] { 0, 1, 2 });
        db1.Set("array", nestedArray1);
        Assert.Equal("[0,1,2]", nestedArray1.Json());
    }

    [Fact, Covers("T-0.8")]
    [Requires(Feature.Text, Feature.Array, Feature.Map, Feature.Events)]
    public void NestedPrelimTypes_AreIntegratedRecursively_AsOneChange()
    {
        var doc = new Doc(new DocOptions { ClientId = 1 });
        var root = doc.GetArray("root");
        var events = 0;
        root.Observe((_, _) => events++);
        var inner = new YArray();
        inner.Push(new YEntry[] { 1, new YMap(new[] { new KeyValuePair<string, YEntry>("deep", new YText("t")) }) });
        var outer = new YMap();
        outer.Set("inner", inner);
        outer.Set("n", 1);
        root.Push(new YEntry[] { outer });
        Assert.Equal(1, events);
        Assert.Equal("""[{"inner":[1,{"deep":"t"}],"n":1}]""", root.Json());
        Assert.True(inner.IsIntegrated);
        Assert.Same(doc, inner.Doc);
        Assert.Same(outer, inner.Parent);
    }

    [Fact, Covers("T-0.9", "X-E6")]
    [Requires(Feature.Text, Feature.Array, Feature.Map, Feature.Xml)]
    public void Clone_IsAnUnattachedCopy_ThatIsReadableAfterAttachment()
    {
        var doc = new Doc(new DocOptions { ClientId = 1 });
        var array = doc.GetArray("a");
        array.Push(new YEntry[] { 1, new YMap(new[] { new KeyValuePair<string, YEntry>("k", "v") }) });
        var copy = (YArray)array.Clone();
        Assert.False(copy.IsIntegrated);
        Assert.Equal(0, copy.Length);
        doc.GetArray("copies").Push(new YEntry[] { copy });
        Assert.Equal("""[1,{"k":"v"}]""", copy.Json());
        copy.Push(new YEntry[] { 2 });
        Assert.Equal(2, array.Length);
        Assert.Equal(3, copy.Length);

        var map = doc.GetMap("m");
        map.Set("n", new YArray());
        map.ArrayOf("n").Push(new YEntry[] { 1 });
        var mapCopy = (YMap)map.Clone();
        doc.GetArray("copies").Push(new YEntry[] { mapCopy });
        mapCopy.ArrayOf("n").Push(new YEntry[] { 2 });
        Assert.Equal("""{"n":[1,2]}""", mapCopy.Json());
        Assert.Equal("""{"n":[1]}""", map.Json());

        var text = doc.GetText("t");
        text.Insert(0, "ab", Obj(("bold", true)));
        var textCopy = (YText)text.Clone();
        doc.GetArray("copies").Push(new YEntry[] { textCopy });
        Assert.Equal("""[{"insert":"ab","attributes":{"bold":true}}]""", textCopy.DeltaJson());

        var fragment = doc.GetXmlFragment("f");
        var el = new YXmlElement("p");
        fragment.Insert(0, new YType[] { el });
        el.SetAttribute("class", "c");
        el.Insert(0, new YType[] { new YXmlText("child") });
        var elCopy = (YXmlElement)el.Clone();
        Assert.False(elCopy.IsIntegrated);
        doc.GetXmlFragment("g").Insert(0, new YType[] { elCopy });
        Assert.Equal(el.ToString(), elCopy.ToString());
        Assert.True(elCopy.TryGetAttribute("class", out var c));
        Assert.Equal("c", c.AsString());
        Assert.NotSame(el[0], elCopy[0]);
    }

    [Fact, Covers("T-0.10")]
    [Requires(Feature.Array, Feature.Map)]
    public void ListInsert_AcceptsAnyBinaryAndTypes_AsSeparateElements()
    {
        var doc = new Doc(new DocOptions { ClientId = 1 });
        var array = doc.GetArray("a");
        array.Insert(0, new YEntry[] { Null, 1.5, true, "s", Obj(("o", 1)), YValue.Array(new YValue[] { 1, 2 }) });
        Assert.Equal(6, array.Length);
        array.Push(new YEntry[] { 1, new YMap(), 3 });
        Assert.Equal(9, array.Length);
        Assert.Equal(YEntryKind.Type, array[7].Kind);
        Assert.Equal(YEntryKind.Any, array[6].Kind);
        var bytes = new byte[] { 1, 2, 3 };
        array.Push(new YEntry[] { YValue.Bytes(bytes) });
        bytes[0] = 99;
        // JS copies binary on insert; the entry reads back as ContentBinary with the original bytes
        Assert.Equal(YEntryKind.Binary, array[9].Kind);
        Assert.True(array[9].Value.TryGetBytes(out var read));
        Assert.True(read.SequenceEqual(new byte[] { 1, 2, 3 }));
        // a list rejects undefined (JS: "Unexpected content type in insert operation")
        Assert.ThrowsAny<ArgumentException>(() => array.Push(new YEntry[] { default(YValue) }));
        Assert.Equal(10, array.Length);
    }

    [Fact, Covers("T-0.11", "M-3", "M-8")]
    [Requires(Feature.Map)]
    public void MapSet_AcceptsUndefinedAndNull_AndHasStaysTrue()
    {
        var doc = new Doc(new DocOptions { ClientId = 1 });
        var map = doc.GetMap("m");
        map.Set("u", default(YValue));
        map.Set("n", Null);
        Assert.True(map.Has("u"));
        Assert.True(map.Has("n"));
        Assert.True(map.TryGet("u", out var u));
        Assert.Equal(YValueKind.Undefined, u.Value.Kind);
        Assert.Equal(YValueKind.Null, map.Get("n").Kind);
        Assert.False(map.TryGet("missing", out _));
        Assert.Equal(2, map.Count);
        Assert.Equal("""{"n":null}""", map.Json());
        map.Set("big", YValue.BigInt(1L << 62));
        Assert.Equal(YValueKind.BigInt, map.Get("big").Kind);
        map.Set("bytes", YValue.Bytes(new byte[] { 1 }));
        Assert.Equal(YEntryKind.Binary, map.TryGet("bytes", out var b) ? b.Kind : default);
    }

    [Fact, PortOf("y-map.tests.js:testYmapSetsYmap", "T-0.12")]
    [Requires(Feature.Map, Feature.Sync)]
    public void Set_ReturnsTheLiveInstance_Map()
    {
        var net = Net.Init(2);
        var map0 = net.Peers[0].Map;
        var map = map0.Set("Map", new YMap());
        Assert.Same(map, map0.TryGet("Map", out var e) ? e.Type : null);
        map.Set("one", 1);
        Assert.Equal(1, map.Get("one").AsNumber());
        net.Compare();
    }

    [Fact, PortOf("y-map.tests.js:testYmapSetsYarray", "T-0.12")]
    [Requires(Feature.Array, Feature.Map, Feature.Sync)]
    public void Set_ReturnsTheLiveInstance_Array()
    {
        var net = Net.Init(2);
        var map0 = net.Peers[0].Map;
        var array = map0.Set("Array", new YArray());
        Assert.Same(array, map0.TryGet("Array", out var e) ? e.Type : null);
        array.Insert(0, new YEntry[] { 1, 2, 3 });
        Assert.Equal("""{"Array":[1,2,3]}""", map0.Json());
        net.Compare();
    }

    [Fact, Covers("T-0.13")]
    [Requires(Feature.Array, Feature.Map)]
    public void Insert_AnAlreadyIntegratedType_IsRejected()
    {
        var doc = new Doc(new DocOptions { ClientId = 1 });
        var a = doc.GetArray("a");
        var b = doc.GetArray("b");
        var map = a.Insert(new YMap());
        // JS has no guard (undefined behaviour); y-sharp throws (ADR 0005)
        Assert.ThrowsAny<Exception>(() => b.Push(new YEntry[] { map }));
        Assert.Equal(0, b.Length);
        Assert.Equal(1, a.Length);
        Assert.ThrowsAny<Exception>(() => doc.GetMap("m").Set("x", map));
    }

    [Fact, Diverges("Q10", "0003")]
    [Requires(Feature.Array, Feature.Map)]
    public void Q10_InsertionAcceptsOnlyTheEntryKinds_ByConstruction()
    {
        // JS throws "Unexpected content type" at run time for a Date, a function or a class instance. y-sharp cannot express them:
        // YEntry is Any, Binary, Type or Doc, so these inputs do not compile; undefined is the one runtime case.
        Assert.Equal(new[] { "Any", "Binary", "Type", "Doc" }, Enum.GetNames<YEntryKind>());
        var doc = new Doc(new DocOptions { ClientId = 1 });
        var array = doc.GetArray("a");
        Assert.ThrowsAny<ArgumentException>(() => array.Insert(0, new YEntry[] { default(YValue) }));
        // maps take undefined, as in JS
        doc.GetMap("m").Set("k", default(YValue));
        Assert.True(doc.GetMap("m").Has("k"));
        // a top-level byte string is Binary, nested inside an object it stays Any
        array.Push(new YEntry[] { YValue.Bytes(new byte[] { 1 }), Obj(("b", YValue.Bytes(new byte[] { 2 }))) });
        Assert.Equal(YEntryKind.Binary, array[0].Kind);
        Assert.Equal(YEntryKind.Any, array[1].Kind);
    }

    [Fact, Covers("Q29", "X-E3")]
    [Requires(Feature.Xml)]
    public void XmlToString_AnyCulture_UsesInvariantLowercasingAndOrdinalAttributeOrder()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var doc = new Doc(new DocOptions { ClientId = 1 });
            var fragment = doc.GetXmlFragment("f");
            var el = new YXmlElement("DIV");
            var upperI = new YXmlElement("I");
            fragment.Insert(0, new YType[] { el, upperI });
            el.SetAttribute("b", 3);
            el.SetAttribute("B", 1);
            el.SetAttribute("é", "4");
            el.SetAttribute("a", "x\"y<&");
            Assert.Equal("<div B=\"1\" a=\"x\"y<&\" b=\"3\" é=\"4\"></div><i></i>", fragment.ToString());
            Assert.Equal("<div B=\"1\" a=\"x\"y<&\" b=\"3\" é=\"4\"></div><i></i>", fragment.ToJson().AsString());
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}

internal static class SharedTypeHelpers
{
    /// <summary>Inserts one nested type at the end of a list and returns it as the live instance.</summary>
    public static T Insert<T>(this YArray array, T type) where T : YType
    {
        array.Push(new YEntry[] { type });
        return type;
    }
}
