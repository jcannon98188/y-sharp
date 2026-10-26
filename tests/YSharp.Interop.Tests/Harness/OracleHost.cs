using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Node;
using YSharp.TestKit.Tiers;

namespace YSharp.Interop;

public sealed class OracleOptions
{
    /// <summary>Per-request timeout (ADR 0012 "Hangs"). <c>YSHARP_ORACLE_TIMEOUT_MS</c> overrides the default.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMilliseconds(EnvMs("YSHARP_ORACLE_TIMEOUT_MS", 20_000));

    /// <summary>Start the server with its fault ops (<c>fault.hang</c>, ...) enabled, to test the harness itself.</summary>
    public bool Faults { get; init; }

    internal static int EnvMs(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
}

/// <summary>
/// The one long-lived Node child of a test run (ADR 0012), started lazily by the first test that needs it and shared as an xUnit v3
/// assembly fixture. Requests are serialised: the protocol carries ids so a pool can be added later, but a timeout kills the child, which
/// would fail unrelated in-flight requests. The host verifies the oracle versions against the submodules at startup, restarts the child
/// after a timeout or a crash, and keeps the <c>oracle-misbehaved</c> tally.
/// </summary>
public sealed class OracleHost : IAsyncDisposable
{
    private static readonly object EnvLock = new();
    private readonly OracleOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<string> _stderr = new();
    private NodeClient? _client;
    private string? _unavailable;
    private readonly object _probeLock = new();
    private bool _probed;

    public OracleHost() : this(new OracleOptions()) { }

    internal OracleHost(OracleOptions options) => _options = options;

    public string RepoRoot { get; } = YSharp.TestKit.Fixtures.RepoRoot.Find();

    public string ServerScript => Path.Combine(RepoRoot, "tools", "fixture-gen", "serve.mjs");

    /// <summary>Requests answered, and the ones that ended <c>oracle-misbehaved</c> (ADR 0012).</summary>
    public int Requests { get; private set; }

    public int Misbehaved { get; private set; }

    public int Restarts { get; private set; }

    public string? NodeVersion => _client?.NodeVersion;

    public IReadOnlyList<string> Stderr => _stderr;

    /// <summary>Why the oracle cannot be used (Node missing or not the pinned major, or the generator's packages are not installed); null when usable.</summary>
    public string? UnavailableReason
    {
        get
        {
            lock (_probeLock)
            {
                if (_probed) return _unavailable;
                var reason = NodeAvailability.Check(RepoRoot);
                if (reason == null && !Directory.Exists(Path.Combine(RepoRoot, "tools", "fixture-gen", "node_modules", "ajv")))
                    reason = "tools/fixture-gen has no node_modules (run `npm ci` there)";
                if (reason == null && !File.Exists(Path.Combine(RepoRoot, "yjs", "src", "index.js")))
                    reason = "the yjs submodule is not checked out (git submodule update --init)";
                _unavailable = reason;
                _probed = true;
                return _unavailable;
            }
        }
    }

    /// <summary>
    /// The availability rule of ADR 0012: the live tests run in the Smoke and Nightly tiers (the gating suite stays fixtures only). A
    /// missing or wrong Node skips with a visible reason, unless <c>YSHARP_REQUIRE_NODE=1</c> (the differential CI jobs), which fails.
    /// </summary>
    public void RequireOrSkip(bool ignoreTier = false)
    {
        var decision = Availability.Decide(ignoreTier ? Tier.Smoke : YSharpEnv.CurrentTier, UnavailableReason, Environment.GetEnvironmentVariable("YSHARP_REQUIRE_NODE") == "1");
        switch (decision.Action)
        {
            case AvailabilityAction.Skip: Assert.Skip(decision.Reason!); break;
            case AvailabilityAction.Fail: Assert.Fail(decision.Reason!); break;
        }
    }

    private async Task EnsureStartedAsync()
    {
        if (_client != null) return;
        var client = new NodeClient(new NodeClientOptions
        {
            ScriptPath = ServerScript,
            WorkingDirectory = Path.GetDirectoryName(ServerScript),
            RequestTimeout = _options.RequestTimeout,
            HandshakeTimeout = TimeSpan.FromSeconds(60),
            OnStderr = line => { lock (_stderr) _stderr.Add(line); },
        });
        lock (EnvLock)
        {
            var previous = Environment.GetEnvironmentVariable("YSHARP_DIFF_FAULTS");
            Environment.SetEnvironmentVariable("YSHARP_DIFF_FAULTS", _options.Faults ? "1" : null);
            try { client.StartAsync().GetAwaiter().GetResult(); }
            finally { Environment.SetEnvironmentVariable("YSHARP_DIFF_FAULTS", previous); }
        }
        VerifyHandshake(client);
        _client = client;
    }

    /// <summary>Node and the submodule versions are verified at startup against <c>.nvmrc</c> and the fixture headers (ADR 0012).</summary>
    private void VerifyHandshake(NodeClient client)
    {
        using var hello = JsonDocument.Parse(client.HelloLine!);
        var root = hello.RootElement;
        var pinned = PinnedVersions.Read(RepoRoot);
        var v = root.GetProperty("versions");
        var problems = new List<string>();
        if (v.GetProperty("yjs").GetString() != pinned.Yjs) problems.Add($"yjs {v.GetProperty("yjs").GetString()} != pinned {pinned.Yjs}");
        if (v.GetProperty("lib0").GetString() != pinned.Lib0) problems.Add($"lib0 {v.GetProperty("lib0").GetString()} != pinned {pinned.Lib0}");
        if (v.GetProperty("y-protocols").GetString() != pinned.YProtocols) problems.Add($"y-protocols {v.GetProperty("y-protocols").GetString()} != pinned {pinned.YProtocols}");
        var ops = root.GetProperty("ops").EnumerateArray().Select(o => o.GetString()!).ToList();
        var expected = Protocol.Ops.Concat(_options.Faults ? Protocol.FaultOps : Array.Empty<string>()).ToList();
        if (!ops.SequenceEqual(expected)) problems.Add($"ops [{string.Join(",", ops)}] != catalogue [{string.Join(",", expected)}]");
        if (problems.Count > 0) throw new InvalidOperationException("The oracle server does not match the repository: " + string.Join("; ", problems));
    }

    /// <summary>The handshake line of the running child (for tests of the catalogue).</summary>
    public async Task<string> HelloAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureStartedAsync();
            return _client!.HelloLine!;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Replays <paramref name="scriptJson"/> (an ops script, <c>schema: 1</c>) on fresh docs in the oracle.</summary>
    public async Task<OracleReply> RunAsync(string scriptJson, IEnumerable<string>? extras = null)
    {
        // one request is one line: a script written with line breaks must be compacted before it is framed
        var payload = new StringBuilder("{\"script\":").Append(JsonNode.Parse(scriptJson)!.ToJsonString());
        if (extras != null) payload.Append(",\"extras\":").Append(JsonSerializer.Serialize(extras));
        payload.Append('}');
        using var doc = JsonDocument.Parse(payload.ToString());
        return await RunRawAsync(Protocol.Run, doc.RootElement);
    }

    /// <summary>One request; the answer is classified into an <see cref="OracleReply"/> and a dead or hung child is replaced.</summary>
    public async Task<OracleReply> RunRawAsync(string op, JsonElement payload)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureStartedAsync();
            Requests++;
            try
            {
                using var result = await _client!.RequestAsync(op, payload);
                var r = result.RootElement;
                var status = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("status", out var s) ? s.GetString() : null;
                switch (status)
                {
                    case Protocol.StatusOk:
                        return new OracleReply(OracleStatus.Ok, JsonNode.Parse(r.GetProperty("observations").GetRawText())!.AsObject(), null);
                    case Protocol.StatusScriptError:
                        return new OracleReply(OracleStatus.ScriptError, null, r.GetProperty("message").GetString());
                    case Protocol.StatusOracleError:
                        Misbehaved++;
                        return new OracleReply(OracleStatus.OracleError, null, $"{r.GetProperty("name").GetString()}: {r.GetProperty("message").GetString()}");
                    case null when op != Protocol.Run:
                        return new OracleReply(OracleStatus.Ok, JsonNode.Parse(r.GetRawText()) as JsonObject ?? new JsonObject(), null);
                    default:
                        throw new InvalidOperationException($"Unexpected oracle result for '{op}': {r.GetRawText()}");
                }
            }
            catch (NodeTimeoutException ex)
            {
                Misbehaved++;
                Restarts++;
                return new OracleReply(OracleStatus.Timeout, null, ex.Message + StderrTail());
            }
            catch (NodeRequestException ex) when (ex.Message.StartsWith("Node exited", StringComparison.Ordinal) || ex.Message.StartsWith("Node was restarted", StringComparison.Ordinal))
            {
                Misbehaved++;
                Restarts++;
                await ReplaceClientAsync();
                return new OracleReply(OracleStatus.Crashed, null, ex.Message + StderrTail());
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>The last lines the child wrote to stderr (Node's console), to explain a hang or a crash.</summary>
    private string StderrTail()
    {
        lock (_stderr) return _stderr.Count == 0 ? "" : Environment.NewLine + "node stderr: " + string.Join(" | ", _stderr.TakeLast(5));
    }

    private async Task ReplaceClientAsync()
    {
        if (_client != null) await _client.DisposeAsync();
        _client = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null) await _client.DisposeAsync();
        _client = null;
    }
}

public enum AvailabilityAction { Run, Skip, Fail }

public sealed record AvailabilityDecision(AvailabilityAction Action, string? Reason);

/// <summary>The availability rule of ADR 0012, as a pure function so it can be tested without removing Node from the machine.</summary>
public static class Availability
{
    /// <summary>
    /// The gating tier never runs the live tests (the gating suite stays fixtures only). Otherwise a usable Node runs them; a missing or wrong one
    /// skips with a visible reason, except in the differential CI jobs (<paramref name="requireNode"/>), where it is a hard failure.
    /// </summary>
    public static AvailabilityDecision Decide(Tier tier, string? unavailableReason, bool requireNode)
    {
        if (tier == Tier.Gating)
            return new AvailabilityDecision(AvailabilityAction.Skip, "Live differential tests run in the Smoke and Nightly tiers (set YSHARP_TIER=Smoke).");
        if (unavailableReason == null) return new AvailabilityDecision(AvailabilityAction.Run, null);
        return requireNode
            ? new AvailabilityDecision(AvailabilityAction.Fail, "Node is required: " + unavailableReason)
            : new AvailabilityDecision(AvailabilityAction.Skip, unavailableReason);
    }
}
