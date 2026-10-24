using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Stubs;

namespace YSharp.Tests.TestKit;

public class OpsScriptTests
{
    private const string TwoReplicas =
        "\"replicas\":[{\"name\":\"a\",\"clientId\":1},{\"name\":\"b\",\"clientId\":2,\"gc\":false}]";

    private static string Script(string steps, string replicas = TwoReplicas) =>
        "{\"schema\":1," + replicas + ",\"steps\":[" + steps + "]}";

    private static readonly string TextInsert =
        "{\"step\":\"text.insert\",\"doc\":\"a\",\"target\":{\"root\":\"t\",\"kind\":\"text\"},\"at\":0,\"value\":\"hi\"}";

    // ------------------------------------------------------------ parsing and strictness

    [Fact]
    public void Parse_ValidScript_ProducesReplicaStepsThenSteps()
    {
        var s = OpsScript.Parse(Script(TextInsert), StandardSteps.CreateRegistry());
        Assert.Equal(new[] { "replica", "replica", "text.insert" }, s.Steps.Select(x => x.Kind));
    }

    [Fact]
    public void Parse_UnknownStepKind_Rejected()
    {
        var ex = Assert.Throws<OpsScriptException>(() => OpsScript.Parse(Script("{\"step\":\"text.explode\",\"doc\":\"a\"}"), StandardSteps.CreateRegistry()));
        Assert.Contains("text.explode", ex.Message);
    }

    [Fact]
    public void Parse_UnknownField_Rejected()
    {
        var bad = TextInsert.Replace("\"at\":0", "\"at\":0,\"bogus\":true");
        var ex = Assert.Throws<OpsScriptException>(() => OpsScript.Parse(Script(bad), StandardSteps.CreateRegistry()));
        Assert.Contains("bogus", ex.Message);
    }

    [Fact]
    public void Parse_MissingRequiredField_Rejected()
    {
        var bad = TextInsert.Replace(",\"at\":0", "");
        var ex = Assert.Throws<OpsScriptException>(() => OpsScript.Parse(Script(bad), StandardSteps.CreateRegistry()));
        Assert.Contains("at", ex.Message);
    }

    [Fact]
    public void Parse_ReplicaWithoutClientId_Rejected_NoDefaultsAreInvented()
    {
        var ex = Assert.Throws<OpsScriptException>(() =>
            OpsScript.Parse(Script("", "\"replicas\":[{\"name\":\"a\"}]"), StandardSteps.CreateRegistry()));
        Assert.Contains("clientId", ex.Message);
    }

    [Fact]
    public void Parse_UnsupportedSchema_Rejected()
    {
        Assert.Throws<OpsScriptException>(() => OpsScript.Parse("{\"schema\":2,\"steps\":[]}", StandardSteps.CreateRegistry()));
    }

    [Fact]
    public void Parse_TransactChildren_AreValidatedRecursively()
    {
        var ok = "{\"step\":\"transact\",\"doc\":\"a\",\"steps\":[" + TextInsert + "]}";
        var s = OpsScript.Parse(Script(ok), StandardSteps.CreateRegistry());
        Assert.Single(s.Steps.Last().Children!);

        var bad = "{\"step\":\"transact\",\"doc\":\"a\",\"steps\":[{\"step\":\"nope\"}]}";
        Assert.Throws<OpsScriptException>(() => OpsScript.Parse(Script(bad), StandardSteps.CreateRegistry()));
    }

    [Fact]
    public void Parse_AreaRegisteredStepKind_IsAcceptedWithoutChangingTheCore()
    {
        var reg = StandardSteps.CreateRegistry();
        reg.Register(new StepDefinition("example.area", required: new[] { "doc", "name" }, optional: Array.Empty<string>(), run: (_, _) => { }));
        var s = OpsScript.Parse(Script("{\"step\":\"example.area\",\"doc\":\"a\",\"name\":\"s1\"}"), reg);
        Assert.Equal("example.area", s.Steps.Last().Kind);
    }

    [Fact]
    public void Registry_DuplicateKind_Rejected()
    {
        var reg = StandardSteps.CreateRegistry();
        Assert.Throws<InvalidOperationException>(() => reg.Register(new StepDefinition("tick", new[] { "ms" }, Array.Empty<string>(), (_, _) => { })));
    }

    // ------------------------------------------------------------ interpretation with fake handlers

    private static (OpsRegistry reg, List<string> log) LoggingRegistry()
    {
        var log = new List<string>();
        var reg = new OpsRegistry();
        reg.Register(new StepDefinition("replica", new[] { "name", "clientId" }, new[] { "gc" }, (ctx, s) => { log.Add("replica:" + s.String("name")); ctx.DeclareReplica(s.String("name"), null!); }));
        reg.Register(new StepDefinition("note", new[] { "text" }, Array.Empty<string>(), (ctx, s) => log.Add("note:" + s.String("text"))));
        reg.Register(StandardSteps.Transact);
        return (reg, log);
    }

    [Fact]
    public void Run_Steps_ExecuteInOrder()
    {
        var (reg, log) = LoggingRegistry();
        var script = OpsScript.Parse(Script("{\"step\":\"note\",\"text\":\"1\"},{\"step\":\"note\",\"text\":\"2\"}"), reg);
        new OpsInterpreter(reg).Run(script);
        Assert.Equal(new[] { "replica:a", "replica:b", "note:1", "note:2" }, log);
    }

    [Fact]
    public void Run_Transact_WrapsChildrenInOneTransactionAndNestedJoinsOuter()
    {
        var (reg, log) = LoggingRegistry();
        var script = OpsScript.Parse(Script(
            "{\"step\":\"transact\",\"doc\":\"a\",\"origin\":\"o\",\"steps\":[" +
            "{\"step\":\"note\",\"text\":\"x\"}," +
            "{\"step\":\"transact\",\"doc\":\"a\",\"steps\":[{\"step\":\"note\",\"text\":\"y\"}]}]}"), reg);
        var interp = new OpsInterpreter(reg)
        {
            // Replace the library call with a recording runner; real docs come with the implementation.
            TransactRunner = (doc, origin, body) => { log.Add("begin:" + origin); body(); log.Add("end"); },
            DocFactory = (_, _) => null!,
        };
        interp.Run(script);
        Assert.Equal(new[] { "replica:a", "replica:b", "begin:o", "note:x", "note:y", "end" }, log);
    }

    [Fact]
    public void Run_UnknownReplica_IsAScriptErrorNotAnImplementationFailure()
    {
        var reg = StandardSteps.CreateRegistry();
        var script = OpsScript.Parse(Script("{\"step\":\"tick\",\"ms\":5},{\"step\":\"transact\",\"doc\":\"zzz\",\"steps\":[]}"), reg);
        var interp = new OpsInterpreter(reg) { DocFactory = (_, _) => null! };
        var ex = Assert.Throws<OpsScriptException>(() => interp.Run(script));
        Assert.Contains("zzz", ex.Message);
    }

    [Fact]
    public void Run_Tick_AdvancesTheVirtualClock()
    {
        var reg = StandardSteps.CreateRegistry();
        var script = OpsScript.Parse(Script("{\"step\":\"tick\",\"ms\":250},{\"step\":\"tick\",\"ms\":750}"), reg);
        var run = new OpsInterpreter(reg) { DocFactory = (_, _) => null! }.Run(script);
        Assert.Equal(TimeSpan.FromSeconds(1), run.Clock.GetUtcNow() - new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Run_TargetPathThatDoesNotResolve_IsAScriptError()
    {
        var ctx = new OpsContext(new OpsRegistry());
        using var el = JsonDocument.Parse("[{\"root\":\"t\",\"kind\":\"bogus\"}]");
        Assert.Throws<OpsScriptException>(() => ctx.ResolveTarget(null!, el.RootElement));
    }

    // ------------------------------------------------------------ end to end against the stub

    [Fact]
    public void Run_TextInsertScript_ProducesDocAndUpdates_OnceImplemented()
    {
        var reg = StandardSteps.CreateRegistry();
        var script = OpsScript.Parse(Script(TextInsert + ",{\"step\":\"sync\",\"from\":\"a\",\"to\":\"b\"}"), reg);
        try
        {
            var run = new OpsInterpreter(reg).Run(script);
            Assert.Equal("hi", run.Docs["b"].GetText("t").ToString());
            Assert.NotEmpty(run.Updates["a"]);
        }
        catch (NotImplementedException) when (ApiStub.IsStub)
        {
            Assert.Skip(ApiStub.Reason);
        }
    }
}
