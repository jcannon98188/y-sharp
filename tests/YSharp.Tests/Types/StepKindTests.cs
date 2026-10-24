using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Traceability;
using static YSharp.Tests.Types.TypeTestExtensions;

namespace YSharp.Tests.Types;

/// <summary>
/// The step kinds ADR 0016 assigns to the shared types, on the C# interpreter: <c>text.insertEmbed</c>, <c>text.applyDelta</c>,
/// <c>array.insertType</c>, <c>map.insertType</c>, <c>xml.insert</c>, <c>xml.delete</c>, <c>xml.setAttribute</c>,
/// <c>xml.removeAttribute</c> and <c>text.*</c> on an XML text. Each kind has a schema entry (tools/fixture-gen/schema/ops.schema.json)
/// and a Node implementation (tools/fixture-gen/lib/steps.mjs); the fixtures in fixtures/{text,array,map,xml,events} replay them.
/// </summary>
public sealed class StepKindTests
{
    private static OpsContext Run(string stepsJson)
    {
        using var json = JsonDocument.Parse($$"""{"schema":1,"replicas":[{"name":"a","clientId":1}],"steps":{{stepsJson}}}""");
        var registry = TypeSteps.CreateRegistry();
        var script = OpsScript.Parse(json.RootElement.Clone(), registry);
        return new OpsInterpreter(registry).Run(script);
    }

    private const string Text = """{"root":"t","kind":"text"}""";
    private const string Arr = """{"root":"arr","kind":"array"}""";
    private const string Map = """{"root":"m","kind":"map"}""";
    private const string Xml = """{"root":"x","kind":"xmlFragment"}""";

    [Fact]
    public void TheCSharpRegistry_AndTheSchemaDeclareTheSameStepKinds()
    {
        var path = Path.Combine(RepoRoot.Find(), "tools", "fixture-gen", "schema", "ops.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(path));
        var fromSchema = schema.RootElement.GetProperty("$defs").GetProperty("step").GetProperty("oneOf").EnumerateArray()
            .ToDictionary(o => o.GetProperty("properties").GetProperty("step").GetProperty("const").GetString()!, o => o, StringComparer.Ordinal);
        var registry = TypeSteps.CreateRegistry();
        // Kinds other areas add (Spec 9 protocol, Spec 10 simulation) are in the schema and in their own registries, not in this one.
        Assert.All(registry.Kinds, kind => Assert.True(fromSchema.ContainsKey(kind), $"{kind} is registered but not in the schema"));
        foreach (var kind in registry.Kinds)
        {
            registry.TryGet(kind, out var def);
            var o = fromSchema[kind];
            var fields = o.GetProperty("properties").EnumerateObject().Select(p => p.Name).Where(n => n is not ("step" or "steps")).OrderBy(n => n, StringComparer.Ordinal);
            Assert.Equal(fields, def.Required.Concat(def.Optional).OrderBy(n => n, StringComparer.Ordinal));
            var required = o.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).Where(n => n is not ("step" or "steps")).OrderBy(n => n, StringComparer.Ordinal);
            Assert.Equal(required, def.Required.OrderBy(n => n, StringComparer.Ordinal));
        }
    }

    [Fact, Covers("E-1")]
    public void TextInsertEmbed_InsertsAnEmbedWithAttributes() => Stub.Run(() =>
    {
        var ctx = Run($$$"""
            [{"step":"text.insert","doc":"a","target":{{{Text}}},"at":0,"value":"ab"},
             {"step":"text.insertEmbed","doc":"a","target":{{{Text}}},"at":1,"value":{"$":"obj","pairs":[["image","x.png"]]},"attributes":{"$":"obj","pairs":[["width",100]]}}]
            """);
        Assert.Equal("""[{"insert":"a"},{"insert":{"image":"x.png"},"attributes":{"width":100}},{"insert":"b"}]""", ctx.Doc("a").GetText("t").DeltaJson());
    });

    [Fact, Covers("T-6", "E-4")]
    public void TextApplyDelta_AppliesInsertRetainDeleteAndAttributes() => Stub.Run(() =>
    {
        var ctx = Run($$$"""
            [{"step":"text.applyDelta","doc":"a","target":{{{Text}}},"delta":[{"insert":"hello"},{"insert":{"$":"obj","pairs":[["linebreak","s"]]}}]},
             {"step":"text.applyDelta","doc":"a","target":{{{Text}}},"delta":[{"retain":1},{"delete":1},{"retain":2,"attributes":{"$":"obj","pairs":[["bold",true]]}}]}]
            """);
        Assert.Equal(
            """[{"insert":"h"},{"insert":"ll","attributes":{"bold":true}},{"insert":"o"},{"insert":{"linebreak":"s"}}]""",
            ctx.Doc("a").GetText("t").DeltaJson());
    });

    [Fact, Covers("A-9", "T-0.8")]
    public void ArrayInsertType_InsertsANestedTypeReachableByPath() => Stub.Run(() =>
    {
        var ctx = Run($$$"""
            [{"step":"array.insertType","doc":"a","target":{{{Arr}}},"at":0,"kind":"map"},
             {"step":"array.insertType","doc":"a","target":{{{Arr}}},"at":1,"kind":"text","init":"inner"},
             {"step":"array.insertType","doc":"a","target":{{{Arr}}},"at":2,"kind":"array","init":[1,2]},
             {"step":"map.set","doc":"a","target":[{{{Arr}}},0],"key":"n","value":7},
             {"step":"text.insert","doc":"a","target":[{{{Arr}}},1],"at":5,"value":"!"}]
            """);
        Assert.Equal("""[{"n":7},"inner!",[1,2]]""", ctx.Doc("a").GetArray("arr").Json());
    });

    [Fact, Covers("M-12", "T-0.8")]
    public void MapInsertType_SetsANestedTypeReachableByPath() => Stub.Run(() =>
    {
        var ctx = Run($$$"""
            [{"step":"map.insertType","doc":"a","target":{{{Map}}},"key":"list","kind":"array"},
             {"step":"array.push","doc":"a","target":[{{{Map}}},"list"],"values":["deep"]},
             {"step":"map.insertType","doc":"a","target":{{{Map}}},"key":"cfg","kind":"map","init":{"$":"obj","pairs":[["k",1]]}}]
            """);
        Assert.Equal("""{"list":["deep"],"cfg":{"k":1}}""", ctx.Doc("a").GetMap("m").Json());
    });

    [Fact, Covers("X-E4", "X-E1")]
    public void XmlInsertAndDelete_BuildAndTrimATree() => Stub.Run(() =>
    {
        var ctx = Run($$$"""
            [{"step":"xml.insert","doc":"a","target":{{{Xml}}},"at":0,"kind":"element","name":"p"},
             {"step":"xml.insert","doc":"a","target":{{{Xml}}},"at":1,"kind":"element","name":"img"},
             {"step":"xml.insert","doc":"a","target":{{{Xml}}},"at":0,"kind":"text","init":"lead"},
             {"step":"xml.insert","doc":"a","target":[{{{Xml}}},1],"at":0,"kind":"element","name":"span"},
             {"step":"xml.delete","doc":"a","target":{{{Xml}}},"at":2}]
            """);
        var root = ctx.Doc("a").GetXmlFragment("x");
        Assert.Equal(2, root.Length);
        Assert.Equal("lead<p><span></span></p>", root.ToString());
    });

    [Fact, Covers("X-E2")]
    public void XmlSetAndRemoveAttribute_WorkOnElementsAndXmlText() => Stub.Run(() =>
    {
        var ctx = Run($$$"""
            [{"step":"xml.insert","doc":"a","target":{{{Xml}}},"at":0,"kind":"element","name":"div"},
             {"step":"xml.insert","doc":"a","target":{{{Xml}}},"at":1,"kind":"text"},
             {"step":"xml.setAttribute","doc":"a","target":[{{{Xml}}},0],"name":"class","value":"c1"},
             {"step":"xml.setAttribute","doc":"a","target":[{{{Xml}}},0],"name":"gone","value":1},
             {"step":"xml.removeAttribute","doc":"a","target":[{{{Xml}}},0],"name":"gone"},
             {"step":"xml.setAttribute","doc":"a","target":[{{{Xml}}},1],"name":"align","value":"left"}]
            """);
        var root = ctx.Doc("a").GetXmlFragment("x");
        var div = (YXmlElement)root[0];
        Assert.True(div.TryGetAttribute("class", out var c));
        Assert.Equal("c1", c.AsString());
        Assert.False(div.HasAttribute("gone"));
        Assert.True(((YXmlText)root[1]).TryGetAttribute("align", out var align));
        Assert.Equal("left", align.AsString());
    });

    [Fact, Covers("X-1", "F-1")]
    public void TextSteps_RunOnAnXmlTextReachedByPath() => Stub.Run(() =>
    {
        var ctx = Run($$$"""
            [{"step":"xml.insert","doc":"a","target":{{{Xml}}},"at":0,"kind":"text"},
             {"step":"text.insert","doc":"a","target":[{{{Xml}}},0],"at":0,"value":"abc","attributes":{"$":"obj","pairs":[["bold",true]]}},
             {"step":"text.format","doc":"a","target":[{{{Xml}}},0],"at":1,"len":1,"attributes":{"$":"obj","pairs":[["bold",null]]}},
             {"step":"text.delete","doc":"a","target":[{{{Xml}}},0],"at":2,"len":1}]
            """);
        var xmlText = (YXmlText)ctx.Doc("a").GetXmlFragment("x")[0];
        Assert.Equal("""[{"insert":"a","attributes":{"bold":true}},{"insert":"b"}]""", xmlText.DeltaJson());
    });

    [Fact, Covers("Y-1")]
    public void Observe_RecordsEventsInFiringOrder() => Stub.Run(() =>
    {
        var ctx = Run($$$"""
            [{"step":"observe","doc":"a","target":{{{Xml}}},"name":"x"},
             {"step":"xml.insert","doc":"a","target":{{{Xml}}},"at":0,"kind":"element","name":"p"},
             {"step":"xml.setAttribute","doc":"a","target":[{{{Xml}}},0],"name":"k","value":"v"}]
            """);
        var events = TypeSteps.Events(ctx);
        var first = Assert.Single(events);
        Assert.Equal("YXmlEvent", first["type"]!.GetValue<string>());
        Assert.True(first["childListChanged"]!.GetValue<bool>());
    });
}
