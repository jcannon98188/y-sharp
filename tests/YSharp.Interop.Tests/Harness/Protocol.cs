using System.Collections.Generic;

namespace YSharp.Interop;

/// <summary>
/// The message catalogue of the differential protocol (ADR 0012, schema 1). The Node half is <c>tools/fixture-gen/lib/server.mjs</c>, whose
/// header comment is the same table; a test starts the real server and checks that its handshake advertises exactly these ops.
/// </summary>
public static class Protocol
{
    public const int Schema = 1;

    public const string Ping = "ping";
    public const string Run = "run";
    public const string Shutdown = "shutdown";

    /// <summary>Only when the server was started with <c>YSHARP_DIFF_FAULTS=1</c>; used to test hang and crash handling.</summary>
    public const string FaultHang = "fault.hang";
    public const string FaultCrash = "fault.crash";
    public const string FaultSpin = "fault.spin";

    /// <summary>Statuses of a <c>run</c> result.</summary>
    public const string StatusOk = "ok";
    public const string StatusScriptError = "script-error";
    public const string StatusOracleError = "oracle-error";

    /// <summary>The ops every server offers, in handshake order.</summary>
    public static readonly IReadOnlyList<string> Ops = new[] { Ping, Run, Shutdown };

    public static readonly IReadOnlyList<string> FaultOps = new[] { FaultHang, FaultCrash, FaultSpin };

    /// <summary>Opt-in observations a <c>run</c> may request beside the always-present updates, state, sv and doc.</summary>
    public static readonly IReadOnlyList<string> Extras = new[] { "structs", "ds" };
}

/// <summary>How a request to the oracle ended.</summary>
public enum OracleStatus
{
    /// <summary>The script ran; <see cref="OracleReply.Observations"/> holds the named observations.</summary>
    Ok,

    /// <summary>The script is invalid (bad vocabulary, an unresolved path). Never a y-sharp failure; the case is discarded.</summary>
    ScriptError,

    /// <summary>The oracle threw on a script it accepted as valid. <c>oracle-misbehaved</c> (ADR 0012): counted, saved, surfaced, not a failure.</summary>
    OracleError,

    /// <summary>No answer within the per-request timeout; the child was killed and restarted. <c>oracle-misbehaved</c>.</summary>
    Timeout,

    /// <summary>The child exited without answering; it was restarted. <c>oracle-misbehaved</c>.</summary>
    Crashed,
}

/// <summary>An oracle answer. <see cref="Observations"/> is a cloned JSON object (the <c>observations</c> member of the fixture format).</summary>
public sealed record OracleReply(OracleStatus Status, System.Text.Json.Nodes.JsonObject? Observations, string? Message)
{
    public bool Misbehaved => Status is OracleStatus.OracleError or OracleStatus.Timeout or OracleStatus.Crashed;
}
