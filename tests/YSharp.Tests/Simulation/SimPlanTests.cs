using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using YSharp.TestKit.Assertions;
using YSharp.TestKit.Fixtures;
using YSharp.TestKit.Rng;
using YSharp.TestKit.Sim;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Tests.Simulation;

/// <summary>Plays back fixed draws; running out, or leaving some over, is a draw-order bug.</summary>
internal sealed class ScriptedPrng : IPrng
{
    private readonly double[] _draws;
    private int _next;

    public ScriptedPrng(params double[] draws) => _draws = draws;

    public int Remaining => _draws.Length - _next;

    public double Next() => _next < _draws.Length ? _draws[_next++] : throw new InvalidOperationException($"The plan drew more than the {_draws.Length} draws upstream made.");
}

/// <summary>Answers the questions a plan asks of its document from a recording, and requires the same questions in the same order.</summary>
internal sealed class RecordedView : ISimView
{
    private readonly Queue<JsonElement> _calls;

    public RecordedView(JsonElement calls) => _calls = new Queue<JsonElement>(calls.EnumerateArray());

    public int Remaining => _calls.Count;

    private int Answer(string name, params int[] args)
    {
        if (_calls.Count == 0) throw new InvalidOperationException($"The plan asked {name}({string.Join(",", args)}), upstream did not.");
        var call = _calls.Dequeue();
        Assert.Equal(call.GetProperty("name").GetString(), name);
        Assert.Equal(call.GetProperty("args").EnumerateArray().Select(a => a.GetInt32()), args);
        return call.GetProperty("result").GetInt32();
    }

    public int Length => Answer("length");
    public int StringLength => Answer("stringLength");
    public int NestedArrayLength(int index) => Answer("nestedArrayLength", index);
}

/// <summary>
/// The four generators of the upstream randomized tests, checked against what the Node port (which
/// <c>tools/fixture-gen/check-sim-draws.mjs</c> proves draws exactly like upstream) did on real runs: for 360 recorded plans per
/// generator, the same draws and the same document answers give the same ops steps, and every draw is consumed. No document is
/// involved, so these run without an implementation. Regenerate with <c>npm run gen</c> in <c>tools/fixture-gen</c>.
/// </summary>
[LibraryIndependent]
public sealed class SimPlanTests
{
    private static readonly string GoldensPath = Path.Combine(RepoRoot.Find(), "tests", "YSharp.Tests", "Simulation", "plan-goldens.json");

    public static IEnumerable<object[]> Generators() => new[] { "map", "array", "text", "quill" }.Select(k => new object[] { k });

    [Theory, MemberData(nameof(Generators))]
    [Covers("SIM-13")]
    public void Plans_AnySeed_DrawAndProduceExactlyWhatTheNodePortDid(string kind)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(GoldensPath));
        var entries = doc.RootElement.GetProperty(kind).EnumerateArray().ToList();
        Assert.True(entries.Count >= 300, $"{kind}: expected the recorded plans, found {entries.Count}");
        var plans = SimPlans.For(kind);
        var modsUsed = new HashSet<int>();

        foreach (var e in entries)
        {
            var where = $"{kind} seed {e.GetProperty("seed").GetInt32()} plan #{e.GetProperty("step").GetInt32()}";
            var mod = e.GetProperty("mod").GetInt32();
            modsUsed.Add(mod);
            var steps = e.GetProperty("steps");
            var docName = steps.GetArrayLength() > 0 ? steps[0].GetProperty("doc").GetString()! : "a";

            var counters = new PayloadCounters();
            for (var i = 0; i < e.GetProperty("counters").GetProperty("unique").GetInt32(); i++) counters.NextUniqueNumber();
            for (var i = 0; i < e.GetProperty("counters").GetProperty("char").GetInt32(); i++) counters.NextChar();

            var gen = new ScriptedPrng(e.GetProperty("draws").EnumerateArray().Select(d => d.GetDouble()).ToArray());
            var view = new RecordedView(e.GetProperty("calls"));

            var actual = new JsonArray(plans[mod](gen, docName, view, counters).Cast<JsonNode?>().ToArray());

            Assert.True(gen.Remaining == 0, $"{where}: the plan left {gen.Remaining} of upstream's draws unused");
            Assert.True(view.Remaining == 0, $"{where}: the plan did not ask the document {view.Remaining} question(s) upstream asked");
            var diff = StructuralDiff.Find(steps.GetRawText(), actual.ToJsonString());
            Assert.True(diff == null, $"{where}: steps differ at {diff}");
        }

        Assert.Equal(Enumerable.Range(0, plans.Count), modsUsed.OrderBy(i => i));
    }
}
