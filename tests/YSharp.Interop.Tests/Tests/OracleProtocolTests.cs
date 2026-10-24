using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;
using YSharp.TestKit.Node;
using YSharp.TestKit.Tiers;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Interop.Tests;

/// <summary>The rule for when the live tests run, fail or skip (ADR 0012 "Availability"). No Node needed.</summary>
[LibraryIndependent]
public sealed class AvailabilityTests
{
    [Fact, Covers("DIFF-08", "DIFF-11")]
    public void GatingTier_NeverRunsTheLiveTests_EvenWithNode()
    {
        var d = Availability.Decide(Tier.Gating, null, requireNode: true);
        Assert.Equal(AvailabilityAction.Skip, d.Action);
        Assert.Contains("Smoke", d.Reason);
    }

    [Theory, Covers("DIFF-08")]
    [InlineData(Tier.Smoke)]
    [InlineData(Tier.Nightly)]
    public void Node_UsableInstall_Runs(Tier tier) => Assert.Equal(AvailabilityAction.Run, Availability.Decide(tier, null, false).Action);

    [Theory, Covers("DIFF-08")]
    [InlineData(Tier.Smoke)]
    [InlineData(Tier.Nightly)]
    public void MissingOrWrongNode_SkipsLocally_WithAVisibleReason(Tier tier)
    {
        var d = Availability.Decide(tier, "node 18.0.0 does not match .nvmrc (24)", requireNode: false);
        Assert.Equal(AvailabilityAction.Skip, d.Action);
        Assert.Contains(".nvmrc", d.Reason);
    }

    [Theory, Covers("DIFF-08")]
    [InlineData(Tier.Smoke)]
    [InlineData(Tier.Nightly)]
    public void Node_MissingOrWrong_FailsInTheDifferentialCiJobs(Tier tier)
    {
        var d = Availability.Decide(tier, "node is not available (x)", requireNode: true);
        Assert.Equal(AvailabilityAction.Fail, d.Action);
        Assert.Contains("Node is required", d.Reason);
    }

    [Fact, Covers("DIFF-08")]
    public void Node_DoesNotExist_IsReportedWithAReason()
    {
        var reason = NodeAvailability.Check(YSharp.TestKit.Fixtures.RepoRoot.Find(), "definitely-not-a-node-binary");
        Assert.NotNull(reason);
        Assert.Contains("not available", reason);
    }

    [Fact, Covers("DIFF-08")]
    public void Node_AnotherMajor_IsReportedAsAMismatch() =>
        Assert.False(NodeAvailability.MajorMatches("24", "v22.11.0"));

    [Fact, Covers("DIFF-09")]
    public void Catalogue_AnyServer_NamesTheOpsItAnswers()
    {
        Assert.Equal(new[] { "ping", "run", "shutdown" }, Protocol.Ops);
        Assert.All(Protocol.FaultOps, op => Assert.StartsWith("fault.", op));
        Assert.Equal(1, Protocol.Schema);
        Assert.Equal(NodeClient.ProtocolSchema, Protocol.Schema);
    }
}

/// <summary>The live Node child: handshake, message catalogue, result statuses, virtual time (ADR 0012 "Process", "Protocol", "Time"). Smoke tier.</summary>
[LibraryIndependent]
[NeedsNode]
[Tier(Tier.Smoke)]
public sealed class OracleProtocolTests(OracleHost host)
{
    private const string TwoReplicasScript = """
        {"schema":1,"replicas":[{"name":"a","clientId":1},{"name":"b","clientId":2}],"steps":[
          {"step":"text.insert","doc":"a","target":{"root":"t","kind":"text"},"at":0,"value":"hi"},
          {"step":"sync","from":"a","to":"b"}]}
        """;

    private OracleHost Ready()
    {
        host.RequireOrSkip();
        return host;
    }

    [Fact, Covers("DIFF-09", "DIFF-08")]
    public async Task Handshake_OnConnect_AdvertisesTheSchemaVersionsAndTheCatalogue()
    {
        using var hello = JsonDocument.Parse(await Ready().HelloAsync());
        var root = hello.RootElement;
        Assert.True(root.GetProperty("hello").GetBoolean());
        Assert.Equal(Protocol.Schema, root.GetProperty("schema").GetInt32());
        Assert.Equal(Protocol.Ops, root.GetProperty("ops").EnumerateArray().Select(o => o.GetString()!).ToList());
        var v = root.GetProperty("versions");
        Assert.Equal(YSharp.TestKit.Fixtures.PinnedVersions.Read(host.RepoRoot).Yjs, v.GetProperty("yjs").GetString());
    }

    [Fact, Covers("DIFF-09")]
    public async Task Ping_AnyRequest_AnswersWithTheRequestCount()
    {
        var reply = await Ready().RunRawAsync(Protocol.Ping, JsonDocument.Parse("{}").RootElement);
        Assert.Equal(OracleStatus.Ok, reply.Status);
        Assert.True(reply.Observations!["pong"]!.GetValue<bool>());
    }

    [Fact, Covers("DIFF-01", "DIFF-09")]
    public async Task Run_FreshReplay_ReturnsTheNamedObservations()
    {
        var reply = await Ready().RunAsync(TwoReplicasScript);
        Assert.True(reply.Status == OracleStatus.Ok, reply.Status + ": " + reply.Message);
        var obs = reply.Observations!;
        foreach (var name in new[] { "updates", "state", "sv", "doc" }) Assert.True(obs.ContainsKey(name), name);
        Assert.Equal("hi", obs["doc"]!["value"]!["b"]![0]![1]!["delta"]![0]!["insert"]!.GetValue<string>());
        // stateless: the same request again starts from fresh docs, so it answers identically
        var again = await host.RunAsync(TwoReplicasScript);
        Assert.Equal(obs.ToJsonString(), again.Observations!.ToJsonString());
    }

    [Fact, Covers("DIFF-09")]
    public async Task Run_ExtraObservations_AreOptIn()
    {
        var plain = await Ready().RunAsync(TwoReplicasScript);
        Assert.False(plain.Observations!.ContainsKey("structs"));
        var withExtras = await host.RunAsync(TwoReplicasScript, new[] { "structs", "ds" });
        Assert.True(withExtras.Observations!.ContainsKey("structs"));
        Assert.True(withExtras.Observations.ContainsKey("ds"));
    }

    [Theory, Covers("DIFF-09", "DIFF-07")]
    [InlineData("""{"schema":1,"replicas":[{"name":"a","clientId":1}],"steps":[{"step":"text.insert","doc":"nobody","target":{"root":"t","kind":"text"},"at":0,"value":"x"}]}""")]
    [InlineData("""{"schema":1,"steps":[{"step":"no.such.step"}]}""")]
    [InlineData("""{"schema":1,"replicas":[{"name":"a","clientId":1}],"steps":[{"step":"text.insert","doc":"a","target":{"root":"t","kind":"text"},"at":0,"value":"x","bogus":1}]}""")]
    public async Task InvalidScripts_AreScriptErrors_NotOracleMisbehaviour(string script)
    {
        var before = Ready().Misbehaved;
        var reply = await host.RunAsync(script);
        Assert.Equal(OracleStatus.ScriptError, reply.Status);
        Assert.False(string.IsNullOrEmpty(reply.Message));
        Assert.Equal(before, host.Misbehaved);
    }

    [Fact, Covers("DIFF-07", "DIFF-02")]
    public async Task AnOracleThatThrowsOnAValidScript_IsOracleMisbehaved_AndTheChildSurvives()
    {
        // an unnamed applyForeign of an empty update makes Yjs throw ("Unexpected end of array"): valid script, oracle error
        const string script = """{"schema":1,"replicas":[{"name":"a","clientId":1}],"steps":[{"step":"applyForeign","doc":"a","bytes":{"$":"bytes","hex":""}}]}""";
        var before = Ready().Misbehaved;
        var reply = await host.RunAsync(script);
        Assert.Equal(OracleStatus.OracleError, reply.Status);
        Assert.True(reply.Misbehaved);
        Assert.Equal(before + 1, host.Misbehaved);
        Assert.Equal(OracleStatus.Ok, (await host.RunAsync(TwoReplicasScript)).Status);
    }

    [Fact, Covers("DIFF-10")]
    public async Task VirtualClock_Tick_DrivesTimersAndRealTimeDoesNotMatter()
    {
        // awareness renews its local state every 15 s of *virtual* time; the response must not depend on how long the request really takes
        const string script = """
            {"schema":1,"replicas":[{"name":"a","clientId":1}],"steps":[
              {"step":"awareness.new","awareness":"aw","doc":"a"},
              {"step":"awareness.setLocal","awareness":"aw","state":{"$":"obj","pairs":[["x",1]]}},
              {"step":"tick","ms":16000}]}
            """;
        var first = await Ready().RunAsync(script);
        await Task.Delay(120, TestContext.Current.CancellationToken);
        var second = await host.RunAsync(script);
        Assert.Equal(OracleStatus.Ok, first.Status);
        Assert.Equal(first.Observations!["awareness"]!.ToJsonString(), second.Observations!["awareness"]!.ToJsonString());
        var meta = first.Observations["awareness"]!["value"]!["aw"]!["meta"]!.AsArray();
        Assert.True(meta[0]![1]!["clock"]!.GetValue<int>() >= 2, "sixteen virtual seconds must have renewed the state");
    }

    [Fact, Covers("DIFF-09")]
    public async Task OracleConsoleOutput_GoesToStderr_NotTheProtocolChannel()
    {
        // a script that makes yjs log (a second client with a clientId already used: "Changed the client-id ...") must not break the framing
        const string script = """
            {"schema":1,"replicas":[{"name":"a","clientId":1},{"name":"b","clientId":1}],"steps":[
              {"step":"text.insert","doc":"a","target":{"root":"t","kind":"text"},"at":0,"value":"x"},
              {"step":"sync","from":"a","to":"b"}]}
            """;
        var reply = await Ready().RunAsync(script);
        Assert.Equal(OracleStatus.Ok, reply.Status);
        Assert.Equal(OracleStatus.Ok, (await host.RunAsync(TwoReplicasScript)).Status);
    }
}

/// <summary>The hang and crash handling of the Node child (ADR 0012 "Hangs"), with the fault ops enabled. Smoke tier.</summary>
[LibraryIndependent]
[NeedsNode]
[Tier(Tier.Smoke)]
public sealed class OracleFaultTests
{
    private static async Task<OracleHost> StartAsync(TimeSpan timeout)
    {
        var host = new OracleHost(new OracleOptions { Faults = true, RequestTimeout = timeout });
        host.RequireOrSkip();
        await host.HelloAsync();
        return host;
    }

    private static JsonElement Empty => JsonDocument.Parse("{}").RootElement;

    [Fact, Covers("DIFF-07")]
    public async Task ARequestTimeout_KillsAndRestartsTheChild_AndIsOracleMisbehaved()
    {
        await using var host = await StartAsync(TimeSpan.FromMilliseconds(800));
        var reply = await host.RunRawAsync(Protocol.FaultHang, Empty);
        Assert.Equal(OracleStatus.Timeout, reply.Status);
        Assert.True(reply.Misbehaved);
        Assert.Equal(1, host.Restarts);
        Assert.Equal(1, host.Misbehaved);
        // the restarted child answers
        Assert.Equal(OracleStatus.Ok, (await host.RunRawAsync(Protocol.Ping, Empty)).Status);
    }

    [Fact, Covers("DIFF-07")]
    public async Task ACrashedChild_IsReplaced_AndIsOracleMisbehaved()
    {
        await using var host = await StartAsync(TimeSpan.FromSeconds(10));
        var reply = await host.RunRawAsync(Protocol.FaultCrash, Empty);
        Assert.Equal(OracleStatus.Crashed, reply.Status);
        Assert.Equal(1, host.Misbehaved);
        Assert.Equal(OracleStatus.Ok, (await host.RunRawAsync(Protocol.Ping, Empty)).Status);
    }

    [Fact, Covers("DIFF-07")]
    public async Task ABusyOracle_PastTheTimeout_IsATimeout_NotAFailureOfTheRequestAfterIt()
    {
        await using var host = await StartAsync(TimeSpan.FromMilliseconds(800));
        var reply = await host.RunRawAsync(Protocol.FaultSpin, JsonDocument.Parse("{\"ms\":5000}").RootElement);
        Assert.Equal(OracleStatus.Timeout, reply.Status);
        Assert.Equal(OracleStatus.Ok, (await host.RunRawAsync(Protocol.Ping, Empty)).Status);
    }

    [Fact, Covers("DIFF-09")]
    public async Task FaultOps_WhenNotEnabled_AreAbsent()
    {
        await using var plain = new OracleHost();
        plain.RequireOrSkip();
        using var hello = JsonDocument.Parse(await plain.HelloAsync());
        Assert.DoesNotContain(hello.RootElement.GetProperty("ops").EnumerateArray(), o => o.GetString()!.StartsWith("fault.", StringComparison.Ordinal));
        await Assert.ThrowsAnyAsync<Exception>(() => plain.RunRawAsync(Protocol.FaultHang, Empty));
    }
}
