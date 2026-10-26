using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using YSharp.TestKit.Values;

namespace YSharp.Interop;

/// <summary>A differential case failed. The message is the report (seed, script, both observation sets, first difference); <see cref="Persist"/> adds the saved candidate.</summary>
public sealed class DifferentialFailure : Exception
{
    public DifferentialFailure(FailureData data) : base(Reporting.Render(data, null)) => Details = data;

    public FailureData Details { get; }

    public FailureReason Reason => Details.Reason;

    public Saved? Saved { get; private set; }

    /// <summary>Writes the fixture candidate with its <c>repro</c> header and returns the full report. Called once, for the final shrunk failure.</summary>
    public string Persist(string directory)
    {
        Saved = Reporting.Save(directory, Details.ScriptJson, Details.Oracle, Details.Reason, Details.Property, Details.ReplaySeed, Details.CaseSeed, Details.Message ?? Details.First?.ToString() ?? Details.Reason.ToString());
        return Reporting.Render(Details, Saved);
    }
}

/// <summary>
/// The state and checks of one differential property: run a script on both sides, classify the outcome (ADR 0012), compare, and on a
/// problem write the fixture candidate and raise <see cref="DifferentialFailure"/>. An oracle that crashes or hangs on a generated script is
/// <c>oracle-misbehaved</c>: counted, saved and surfaced, not a failure; y-sharp hanging or crashing on it is.
/// </summary>
public sealed class DiffRun
{
    private readonly OracleHost _host;
    private readonly string _directory;

    public DiffRun(OracleHost host, string property, string? directory = null)
    {
        _host = host;
        Property = property;
        _directory = directory ?? Reporting.ArtifactsDirectory;
    }

    public string Property { get; }

    /// <summary>Replaces the y-sharp side (tests of the harness itself use a second oracle run, with injected faults, as a stand-in).</summary>
    public Func<string, IReadOnlyList<string>, SharpReply>? SharpOverride { get; set; }

    private SharpReply RunSharp(string scriptJson, IReadOnlyList<string> extras) =>
        (SharpOverride ?? ((s, x) => SharpSide.Run(s, extras: x)))(scriptJson, extras);

    /// <summary>The opt-in observations a generated plan asks both sides for: the struct store and its delete set (ADR 0012).</summary>
    public static readonly IReadOnlyList<string> PlanExtras = Protocol.Extras;

    /// <summary>The FsCheck replay seed of the chunk being run (set by the driver), repeated in every report.</summary>
    public string? ReplaySeed { get; set; }

    public long CaseSeed { get; set; }

    public string Label { get; set; } = "";

    public int Cases { get; private set; }

    public int Discarded { get; private set; }

    public string? LastDiscard { get; private set; }

    public List<Saved> MisbehavedSaved { get; } = new();

    public int Misbehaved => MisbehavedSaved.Count;

    // ------------------------------------------------------------------ one script on both sides

    /// <summary>The two replies of one script; <paramref name="extra"/> adds a check on top (convergence, a source dump) and returns a difference.</summary>
    public (OracleReply Oracle, SharpReply Sharp)? Run(string scriptJson, Func<JsonObject, JsonObject, Difference?>? extra = null, IEnumerable<string>? extras = null)
    {
        Cases++;
        var x = extras?.ToList() ?? new List<string>();
        var oracle = _host.RunAsync(scriptJson, x).GetAwaiter().GetResult();
        var sharp = RunSharp(scriptJson, x);

        if (sharp.Status == SharpStatus.Hang) throw Fail(FailureReason.SharpFault, scriptJson, oracle.Observations, null, null, "y-sharp hung: " + sharp.Message);
        if (sharp.Status == SharpStatus.Fault) throw Fail(FailureReason.SharpFault, scriptJson, oracle.Observations, null, null, "y-sharp threw " + sharp.Message + Environment.NewLine + sharp.Exception);

        if (oracle.Misbehaved)
        {
            MisbehavedSaved.Add(Reporting.Save(_directory, scriptJson, null, FailureReason.OracleMisbehaved, Property, ReplaySeed, CaseSeed, $"The oracle ended {oracle.Status}: {oracle.Message}"));
            return null;
        }

        if (oracle.Status == OracleStatus.ScriptError)
        {
            if (sharp.Status == SharpStatus.ScriptError)
            {
                Discarded++; LastDiscard = oracle.Message; // both sides call it invalid: the generator made a bad script, which is not a y-sharp failure
                return null;
            }
            throw Fail(FailureReason.Mismatch, scriptJson, null, sharp.Observations, null, $"The oracle rejects the script ({oracle.Message}) but y-sharp ran it.");
        }
        if (sharp.Status == SharpStatus.ScriptError)
            throw Fail(FailureReason.Mismatch, scriptJson, oracle.Observations, null, null, $"y-sharp's interpreter rejects the script ({sharp.Message}) but the oracle ran it.");

        var difference = ObservationDiff.Find(oracle.Observations!, sharp.Observations!) ?? extra?.Invoke(oracle.Observations!, sharp.Observations!);
        if (difference != null) throw Fail(FailureReason.Mismatch, scriptJson, oracle.Observations, sharp.Observations, difference, null);
        return (oracle, sharp);
    }

    public DifferentialFailure Fail(FailureReason reason, string scriptJson, JsonObject? oracle, JsonObject? sharp, Difference? first, string? message) =>
        new(new FailureData(reason, Property, ReplaySeed, CaseSeed, Label, scriptJson, oracle, sharp, first, message));

    public string Directory => _directory;

    // ------------------------------------------------------------------ composite checks over a generated plan

    /// <summary>Stateless ops script: both sides replay it; the observations must agree, and within each side every replica must have converged.</summary>
    public (OracleReply Oracle, SharpReply Sharp)? CheckPlan(Generation.ScriptPlan plan)
    {
        Label = plan.ToString();
        var script = plan.ToScript().ToJsonString();
        return Run(script, (oracle, sharp) => Convergence(oracle, "the oracle") ?? Convergence(sharp, "y-sharp"), PlanExtras);
    }

    /// <summary>A plan over nested types, XML and subdocuments (Spec 40): the same check, with every observation compared, events included.</summary>
    public (OracleReply Oracle, SharpReply Sharp)? CheckTree(Generation.TreePlan plan)
    {
        Label = plan.ToString();
        var script = plan.ToScript().ToJsonString();
        return Run(script, (oracle, sharp) => Convergence(oracle, "the oracle") ?? Convergence(sharp, "y-sharp"), PlanExtras);
    }

    /// <summary>All replicas of a finished plan hold the same document and the same encoded state (convergence, ADR 0012).</summary>
    public static Difference? Convergence(JsonObject observations, string side)
    {
        var docs = (JsonObject)observations["doc"]!["value"]!;
        var states = (JsonObject)observations["state"]!["value"]!;
        var first = docs.First().Key;
        foreach (var (name, dump) in docs.Skip(1))
        {
            var d = ObservationDiff.CompareValues(docs[first], dump, $"$.{name}", isDoc: true);
            if (d != null) return new Difference("convergence", d.Path, $"{side}: replica {name} differs from {first}: {d.Detail}");
            var f = ObservationDiff.CompareValues(states[first]!["v1"]!["full"], states[name]!["v1"]!["full"], $"$.{name}.v1.full");
            if (f != null) return new Difference("convergence", f.Path, $"{side}: the encoded state of {name} differs from {first}: {f.Detail}");
        }
        return null;
    }

    private static JsonNode Tagged(JsonNode bytesObject) => bytesObject.DeepClone();

    private static JsonObject Replica(string name, int clientId) => new() { ["name"] = name, ["clientId"] = clientId };

    private static JsonObject ObserveStep(string doc, string root, string kind) => new()
    {
        ["step"] = "observe",
        ["doc"] = doc,
        ["target"] = new JsonObject { ["root"] = root, ["kind"] = kind },
        ["name"] = "o-" + doc + "-" + root,
    };

    /// <summary>The roots every foreign/pure script reads, so the dump of its replicas covers the generated document.</summary>
    private static IEnumerable<(string Root, string Kind)> GeneratedRoots() =>
        new[] { (Generation.ScriptPlan.TextRoot, "text"), (Generation.ScriptPlan.ArrayRoot, "array"), (Generation.ScriptPlan.MapRoot, "map") };

    /// <summary>The roots (name, kind) the source observation's dump covers, so a receiving replica is dumped over exactly the same roots.</summary>
    private static IEnumerable<(string Root, string Kind)> RootsFrom(JsonObject observations) =>
        observations["doc"]!["value"]!.AsObject().First().Value!.AsArray()
            .Select(p => (p![0]!.GetValue<string>(), p[1]!["kind"]!.GetValue<string>()));

    /// <summary>
    /// <c>applyForeign</c> both ways (ADR 0012): the state each implementation encoded for the finished plan is applied, as foreign bytes in V1
    /// and V2, to fresh replicas of the <em>other</em> side. y-sharp's bytes go to Node, Node's bytes go to y-sharp (the script is the same
    /// literal one on both, so both observation sets are compared too), and the resulting document must equal the source's.
    /// </summary>
    public void CheckForeign(Generation.ScriptPlan plan, (OracleReply Oracle, SharpReply Sharp) basis)
    {
        foreach (var (producer, source) in new[] { ("y-sharp", basis.Sharp.Observations!), ("oracle", basis.Oracle.Observations!) })
        {
            var names = ((JsonObject)source["state"]!["value"]!).Select(kv => kv.Key).ToList();
            var replicas = new JsonArray();
            var steps = new JsonArray();
            var expectations = new List<(string Replica, string Original)>();
            var clientId = 5000;
            foreach (var n in names)
                foreach (var enc in new[] { "v1", "v2" })
                {
                    var r = $"{n}_{enc}";
                    replicas.Add(Replica(r, clientId++));
                    foreach (var (root, kind) in RootsFrom(source)) steps.Add(ObserveStep(r, root, kind));
                    var step = new JsonObject { ["step"] = "applyForeign", ["doc"] = r, ["bytes"] = Tagged(source["state"]!["value"]![n]![enc]!["full"]!) };
                    if (enc == "v2") step["encoding"] = "v2";
                    steps.Add(step);
                    expectations.Add((r, n));
                }
            var script = new JsonObject { ["schema"] = 1, ["replicas"] = replicas, ["steps"] = steps }.ToJsonString();
            Label = $"{plan}; foreign bytes produced by {producer}";
            Run(script, (oracle, sharp) =>
            {
                // the receiving document, on each side, equals the source's document and (re-encoded) its state
                foreach (var (replica, original) in expectations)
                {
                    foreach (var (side, obs) in new[] { ("oracle", oracle), ("y-sharp", sharp) })
                    {
                        var got = obs["doc"]!["value"]![replica]!;
                        var want = source["doc"]!["value"]![original]!;
                        var d = ObservationDiff.CompareValues(want, got, $"$.{replica}", isDoc: true);
                        if (d != null) return new Difference("foreign", d.Path, $"{side}: replica {replica} (foreign bytes from {producer}'s {original}) differs from the source document: {d.Detail}");
                    }
                }
                return null;
            });
        }
    }

    /// <summary>Update functions (ENC-23..25) over the updates a finished plan produced: merge, diff and convert, V1 and V2, compared as results.</summary>
    public void CheckPure(Generation.ScriptPlan plan, OracleReply basis)
    {
        var obs = basis.Observations!;
        var updates = (JsonObject)obs["updates"]!["value"]!;
        var state = (JsonObject)obs["state"]!["value"]!;
        var sv = (JsonObject)obs["sv"]!["value"]!;
        var names = updates.Select(kv => kv.Key).ToList();
        var v1 = names.SelectMany(n => updates[n]!["v1"]!.AsArray()).Select(x => x!.DeepClone()).ToList();
        var v2 = names.SelectMany(n => updates[n]!["v2"]!.AsArray()).Select(x => x!.DeepClone()).ToList();
        if (v1.Count == 0) return;
        var a = names[0];
        var b = names[^1];

        var steps = new JsonArray();
        foreach (var (root, kind) in RootsFrom(obs)) steps.Add(ObserveStep("r", root, kind));
        steps.Add(new JsonObject { ["step"] = "mergeUpdates", ["name"] = "merge-v1", ["updates"] = new JsonArray(v1.Select(x => x!.DeepClone()).ToArray()) });
        steps.Add(new JsonObject { ["step"] = "mergeUpdates", ["name"] = "merge-v1-reversed", ["updates"] = new JsonArray(Enumerable.Reverse(v1).Select(x => x!.DeepClone()).ToArray()) });
        steps.Add(new JsonObject { ["step"] = "mergeUpdates", ["name"] = "merge-v2", ["encoding"] = "v2", ["updates"] = new JsonArray(v2.Select(x => x!.DeepClone()).ToArray()) });
        steps.Add(new JsonObject { ["step"] = "diffUpdate", ["name"] = "diff-v1", ["update"] = state[a]!["v1"]!["full"]!.DeepClone(), ["sv"] = sv[b]!["v1"]!.DeepClone() });
        steps.Add(new JsonObject { ["step"] = "diffUpdate", ["name"] = "diff-v2", ["encoding"] = "v2", ["update"] = state[a]!["v2"]!["full"]!.DeepClone(), ["sv"] = sv[b]!["v1"]!.DeepClone() });
        steps.Add(new JsonObject { ["step"] = "convertUpdate", ["name"] = "v1-to-v2", ["from"] = "v1", ["update"] = state[b]!["v1"]!["full"]!.DeepClone() });
        steps.Add(new JsonObject { ["step"] = "convertUpdate", ["name"] = "v2-to-v1", ["from"] = "v2", ["update"] = state[b]!["v2"]!["full"]!.DeepClone() });
        steps.Add(new JsonObject { ["step"] = "applyForeign", ["doc"] = "r", ["bytes"] = state[a]!["v1"]!["full"]!.DeepClone() });
        var script = new JsonObject { ["schema"] = 1, ["replicas"] = new JsonArray(Replica("r", 9000)), ["steps"] = steps }.ToJsonString();
        Label = plan + "; update functions over its updates";
        Run(script);
    }

    // ------------------------------------------------------------------ mutated valid updates

    /// <summary>
    /// A mutated update applied as foreign bytes on both sides (ADR 0012 "Compared"): both accept with equal state, both reject (y-sharp
    /// unchanged), or y-sharp rejects where the oracle is lenient, which only a recorded divergence may explain. Returns the normaliser that
    /// claimed the case, if any.
    /// </summary>
    public IDivergenceNormaliser? CheckMutation(byte[] valid, byte[] mutated, MutationKind kind, int offset, UpdateFormat format)
    {
        var script = new JsonObject
        {
            ["schema"] = 1,
            ["replicas"] = new JsonArray(Replica("x", 7000)),
            ["steps"] = new JsonArray(
                [.. GeneratedRoots().Select(r => (JsonNode)ObserveStep("x", r.Root, r.Kind)),
                    new JsonObject
                    {
                        ["step"] = "applyForeign",
                        ["doc"] = "x",
                        ["bytes"] = JsonNode.Parse(TaggedValue.FromBytes(mutated).ToJson()),
                        ["encoding"] = format == UpdateFormat.V2 ? "v2" : "v1",
                        ["name"] = "mutated",
                    }]),
        }.ToJsonString();
        Cases++;
        var oracle = _host.RunAsync(script).GetAwaiter().GetResult();
        var sharp = RunSharp(script, Array.Empty<string>());
        if (sharp.Status is SharpStatus.Hang or SharpStatus.Fault or SharpStatus.ScriptError)
            throw Fail(FailureReason.SharpFault, script, oracle.Observations, null, null, $"y-sharp {sharp.Status} on a mutated update ({kind} at {offset}): {sharp.Message}");
        if (oracle.Misbehaved)
        {
            MisbehavedSaved.Add(Reporting.Save(_directory, script, null, FailureReason.OracleMisbehaved, Property, ReplaySeed, CaseSeed, $"The oracle ended {oracle.Status} on a mutated update ({kind} at {offset}): {oracle.Message}"));
            return null;
        }
        if (oracle.Status == OracleStatus.ScriptError) { Discarded++; return null; }

        var oracleAccepted = oracle.Observations!["results"]!["value"]!["mutated"]!["value"]!["accepted"]!.GetValue<bool>();
        var sharpAccepted = sharp.Observations!["results"]!["value"]!["mutated"]!["value"]!["accepted"]!.GetValue<bool>();
        var sharpUnchanged = sharp.Observations["sv"]!["value"]!["x"]!["v1"]!["hex"]!.GetValue<string>() == "00";
        var oracleValue = oracle.Observations["results"]!["value"]!["mutated"]!["value"]!;
        var c = new MutationCase(format, valid, mutated, kind, offset, oracleAccepted, sharpAccepted, sharpUnchanged)
        {
            OracleReadPastEnd = oracleValue["readPastEnd"]?.GetValue<bool>() ?? false,
            OracleZeroLengthDeleteRange = oracleValue["zeroLengthDeleteRange"]?.GetValue<bool>() ?? false,
            OracleUnsortedPendingDeleteSet = oracleValue["unsortedPendingDeleteSet"]?.GetValue<bool>() ?? false,
            OracleSelfDependentPending = oracleValue["selfDependentPending"]?.GetValue<bool>() ?? false,
            OracleParentNotAType = oracleValue["parentNotAType"]?.GetValue<bool>() ?? false,
        };
        Label = $"{kind} at {offset} of {valid.Length} bytes ({format})";

        switch (Normalisers.Classify(oracleAccepted, sharpAccepted))
        {
            case OutcomeClass.BothAccept:
            {
                var d = ObservationDiff.Find(oracle.Observations, sharp.Observations);
                if (d == null) return null;
                var claim = Normalisers.Claim(c);
                if (claim is UnsortedPendingDeleteSetNormaliser)
                    d = ObservationDiff.Find(oracle.Observations, sharp.Observations, UnsortedPendingDeleteSetNormaliser.StillCompared);
                if (d != null) throw Fail(FailureReason.Mismatch, script, oracle.Observations, sharp.Observations, d, "both implementations accept the mutated update but end in different states.");
                return claim;
            }
            case OutcomeClass.BothReject:
                if (!sharpUnchanged)
                    throw Fail(FailureReason.OutcomeClass, script, oracle.Observations, sharp.Observations, null, "y-sharp rejected the mutated update but changed the document (a decode must be atomic, ADR 0005).");
                return null;
            case OutcomeClass.SharpRejectsOracleAccepts:
            {
                var claim = Normalisers.Claim(c);
                if (claim == null)
                    throw Fail(FailureReason.OutcomeClass, script, oracle.Observations, sharp.Observations, null,
                        $"y-sharp rejects a mutated update ({kind} at {offset}) that the oracle accepts, and no recorded divergence covers it. Register the divergence (inventory entry + normaliser in Normalisers.cs) or fix y-sharp.");
                return claim;
            }
            default:
            {
                var claim = Normalisers.Claim(c);
                if (claim == null)
                    throw Fail(FailureReason.OutcomeClass, script, oracle.Observations, sharp.Observations, null, $"y-sharp accepts a mutated update ({kind} at {offset}) that the oracle rejects.");
                return claim;
            }
        }
    }
}
