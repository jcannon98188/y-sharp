using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using YSharp.Tests.Codecs;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Protocols;

/// <summary>
/// The protocol step kinds ADR 0016 assigns to Spec 9, on the C# interpreter. Each kind has a schema entry
/// (tools/fixture-gen/schema/ops.schema.json), a Node implementation (tools/fixture-gen/lib/protocol-steps.mjs), and a test here.
/// </summary>
public sealed class ProtocolStepKindTests
{
    private static OpsContext Run(string stepsJson)
    {
        using var json = JsonDocument.Parse($$$"""{"schema":1,"replicas":[{"name":"ra","clientId":1},{"name":"rb","clientId":2}],"steps":{{{stepsJson}}}}""");
        var registry = ProtocolSteps.CreateRegistry();
        var script = OpsScript.Parse(json.RootElement.Clone(), registry);
        return new OpsInterpreter(registry).Run(script);
    }

    private const string Two = """{"step":"awareness.new","awareness":"a","doc":"ra"},{"step":"awareness.new","awareness":"b","doc":"rb"}""";

    [Fact]
    public void TheProtocolKinds_HaveTheSameFieldsInTheSchemaAndInTheRegistry()
    {
        var path = Path.Combine(RepoRoot.Find(), "tools", "fixture-gen", "schema", "ops.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(path));
        var fromSchema = schema.RootElement.GetProperty("$defs").GetProperty("step").GetProperty("oneOf").EnumerateArray()
            .ToDictionary(o => o.GetProperty("properties").GetProperty("step").GetProperty("const").GetString()!, o => o, StringComparer.Ordinal);
        var registry = ProtocolSteps.CreateRegistry();
        foreach (var kind in ProtocolSteps.Kinds)
        {
            Assert.True(fromSchema.ContainsKey(kind), $"{kind} is not in ops.schema.json");
            Assert.True(registry.TryGet(kind, out var def), $"{kind} is not registered");
            var o = fromSchema[kind];
            var fields = o.GetProperty("properties").EnumerateObject().Select(p => p.Name).Where(n => n != "step").OrderBy(n => n, StringComparer.Ordinal);
            Assert.Equal(fields, def.Required.Concat(def.Optional).OrderBy(n => n, StringComparer.Ordinal));
            var required = o.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).Where(n => n != "step").OrderBy(n => n, StringComparer.Ordinal);
            Assert.Equal(required, def.Required.OrderBy(n => n, StringComparer.Ordinal));
        }
    }

    [Fact, Covers("AW-8", "AW-12")]
    public void AwarenessSetLocalAndField_SetAndMergeTheLocalState() => Stub.Run(() =>
    {
        var ctx = Run($$$"""
            [{{{Two}}},
             {"step":"awareness.setLocal","awareness":"a","state":{"$":"obj","pairs":[["x",1]]}},
             {"step":"awareness.setLocalField","awareness":"a","field":"y","value":"two"}]
            """);
        Assert.Equal("""{"x":1,"y":"two"}""", Js.StateJson(ProtocolSteps.Of(ctx).Awarenesses["a"], 1));
    });

    [Fact, Covers("AW-30", "AW-42")]
    public void AwarenessEncodeAndApplyUpdate_CarryAStateBetweenAwarenesses() => Stub.Run(() =>
    {
        var ctx = Run($$$"""
            [{{{Two}}},
             {"step":"awareness.setLocal","awareness":"a","state":{"$":"obj","pairs":[["x",1]]}},
             {"step":"awareness.encode","name":"e","awareness":"a","clients":[1]},
             {"step":"awareness.applyUpdate","awareness":"b","from":"e","origin":"o"}]
            """);
        var st = ProtocolSteps.Of(ctx);
        Assert.Equal("""{"x":1}""", Js.StateJson(st.Awarenesses["b"], 1));
        Assert.Equal(new[] { (1u, 1L, """{"x":1}""") }, AwBytes.Parse(st.Results["e"].Bytes!));
    });

    [Fact, Covers("AW-25", "AW-27")]
    public void AwarenessRemoveStates_RemovesAndReportsTheOrigin() => Stub.Run(() =>
    {
        var ctx = Run($$$"""
            [{{{Two}}},
             {"step":"awareness.applyUpdate","awareness":"b","bytes":{"$":"bytes","hex":"01010100"}},
             {"step":"awareness.removeStates","awareness":"b","clients":[1],"origin":"bye"}]
            """);
        Assert.Contains(ProtocolSteps.Of(ctx).Events, e => e["event"]!.GetValue<string>() == "change" && e["origin"]!.GetValue<string>() == "bye");
    });

    [Fact, Covers("AW-36", "AW-22")]
    public void AwarenessModifyAndDestroy_RewriteAnUpdate_AndEmitTheDestroyEvent() => Stub.Run(() =>
    {
        var ctx = Run($$$"""
            [{{{Two}}},
             {"step":"awareness.setLocal","awareness":"a","state":{"$":"obj","pairs":[["x",1]]}},
             {"step":"awareness.encode","name":"e","awareness":"a","clients":[1]},
             {"step":"awareness.modify","name":"m","mode":"blank","from":"e"},
             {"step":"awareness.destroy","awareness":"a"}]
            """);
        var st = ProtocolSteps.Of(ctx);
        Assert.Equal(new[] { (1u, 1L, "null") }, AwBytes.Parse(st.Results["m"].Bytes!));
        Assert.Contains(st.Events, e => e["event"]!.GetValue<string>() == "destroy");
    });

    [Fact, Covers("SY-3", "SY-4", "SY-5", "SY-6")]
    public void SyncWriterSteps_RecordTheMessagesTheyWrite_AndCombine() => Stub.Run(() =>
    {
        var ctx = Run("""
            [{"step":"sync.step1","name":"s1","doc":"ra"},
             {"step":"sync.step2","name":"s2","doc":"ra"},
             {"step":"sync.update","name":"u","update":{"$":"bytes","hex":"0102"},"append":"s1"}]
            """);
        var r = ProtocolSteps.Of(ctx).Results;
        Assert.Equal(0, r["s1"].Bytes![0]);
        Assert.Equal(1, r["s2"].Bytes![0]);
        Assert.Equal(r["s1"].Bytes!.Concat(new byte[] { 2, 2, 1, 2 }), r["u"].Bytes);
    });

    [Fact, Covers("SY-11", "SY-12")]
    public void SyncRead_RecordsTheTypeTheReplyAndTheBytesConsumed() => Stub.Run(() =>
    {
        var ctx = Run("""
            [{"step":"text.insert","doc":"ra","target":{"root":"t","kind":"text"},"at":0,"value":"hi"},
             {"step":"sync.step1","name":"s1","doc":"rb"},
             {"step":"sync.read","name":"r","doc":"ra","from":"s1"}]
            """);
        var r = ProtocolSteps.Of(ctx).Results["r"];
        Assert.Equal(0, r.Type);
        Assert.NotEmpty(r.Bytes!);
        Assert.False(r.Threw);
    });

    [Fact, Covers("AU-2")]
    public void AuthPermissionDenied_RecordsTheMessage() => Stub.Run(() =>
    {
        var ctx = Run("""[{"step":"auth.permissionDenied","name":"d","reason":"no"}]""");
        Assert.Equal(new byte[] { 0, 2, (byte)'n', (byte)'o' }, ProtocolSteps.Of(ctx).Results["d"].Bytes);
    });
}
