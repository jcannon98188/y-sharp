using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;
using YSharp.Interop.Generation;
using YSharp.TestKit.Ops;
using YSharp.TestKit.Traceability;
using YSharp.TestKit.Gating;

namespace YSharp.Interop.Tests;

/// <summary>
/// The FsCheck script generator (ADR 0012 "Generation") needs no Node: it must produce scripts both interpreters accept, deterministically
/// per seed, and shrink to smaller valid scripts. These run in every tier.
/// </summary>
[LibraryIndependent]
public sealed class GenerationTests
{
    private static readonly OpsRegistry Registry = SharpSide.CreateRegistry();

    [Fact, Covers("DIFF-01")]
    public void Generate_SameSeed_IsDeterministic()
    {
        for (var seed = 0; seed < 50; seed++)
            Assert.Equal(ScriptPlan.Generate(seed).ToScript().ToJsonString(), ScriptPlan.Generate(seed).ToScript().ToJsonString());
        Assert.NotEqual(ScriptPlan.Generate(1).ToScript().ToJsonString(), ScriptPlan.Generate(2).ToScript().ToJsonString());
    }

    [Fact, Covers("DIFF-01", "DIFF-03")]
    public void Generated_ScriptsParseOnTheCSharpInterpreter_WithoutUnknownKindsOrFields()
    {
        for (var seed = 0; seed < 2000; seed++)
        {
            var plan = ScriptPlan.Generate(seed);
            var json = plan.ToScript().ToJsonString();
            var ex = Record.Exception(() => OpsScript.Parse(json, Registry));
            Assert.True(ex == null, $"seed {seed} ({plan}): {ex?.Message}{Environment.NewLine}{json}");
        }
    }

    [Fact, Covers("DIFF-01")]
    public void Generated_Scripts_DeclareEveryReplicaAndEndWithAFullExchange()
    {
        for (var seed = 0; seed < 200; seed++)
        {
            var plan = ScriptPlan.Generate(seed);
            var script = plan.ToScript();
            var names = script["replicas"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()).ToList();
            Assert.Equal(plan.Replicas, names.Count);
            Assert.Equal(names.Count, names.Distinct().Count());
            var steps = script["steps"]!.AsArray();
            var tail = steps.Skip(steps.Count - 2 * plan.Replicas * (plan.Replicas - 1)).ToList();
            Assert.All(tail, s => Assert.Equal("sync", s!["step"]!.GetValue<string>()));
            // every replica both sends and receives in the closing exchange, so the final state is the union
            foreach (var n in names)
            {
                Assert.Contains(tail, s => s!["from"]!.GetValue<string>() == n);
                Assert.Contains(tail, s => s!["to"]!.GetValue<string>() == n);
            }
        }
    }

    [Fact, Covers("DIFF-03")]
    public void ScheduleMode_EmitsADeliverStep_WhoseEntriesNameUpdatesTheSenderProduces()
    {
        var seen = 0;
        for (var seed = 0; seed < 300; seed++)
        {
            var plan = ScriptPlan.Generate(seed) with { Exchange = Exchange.Schedule };
            var script = plan.ToScript();
            var steps = script["steps"]!.AsArray();
            var deliver = steps.Single(s => s!["step"]!.GetValue<string>() == "deliver")!;
            Assert.Equal(plan.V2, deliver["encoding"]?.GetValue<string>() == "v2");
            // an update event is one per top-level step or transact; count them per replica
            var produced = new Dictionary<string, int>();
            foreach (var s in steps)
            {
                var kind = s!["step"]!.GetValue<string>();
                if (kind is "sync" or "deliver") continue;
                var doc = s["doc"]!.GetValue<string>();
                produced[doc] = produced.GetValueOrDefault(doc) + 1;
            }
            foreach (var e in deliver["schedule"]!.AsArray())
            {
                var from = e!["update"]!["from"]!.GetValue<string>();
                var index = e["update"]!["index"]!.GetValue<int>();
                Assert.True(index < produced.GetValueOrDefault(from), $"seed {seed}: update #{index} of {from}, which produces {produced.GetValueOrDefault(from)}");
                seen++;
            }
        }
        Assert.True(seen > 100, "the generator should exercise delivery schedules");
    }

    [Fact, Covers("DIFF-03")]
    public void Schedule_ReorderDuplicateAndDrop_ChangeTheDeliveryList()
    {
        var baseline = new List<string>();
        var reordered = new List<string>();
        for (var seed = 0; seed < 100; seed++)
        {
            var plain = ScriptPlan.Generate(seed) with { Exchange = Exchange.Schedule, Reorder = false, Duplicate = false, Drop = false };
            baseline.Add(plain.ToScript().ToJsonString());
            reordered.Add((plain with { Reorder = true, Duplicate = true, Drop = true }).ToScript().ToJsonString());
        }
        Assert.NotEqual(baseline, reordered);
        var changed = baseline.Zip(reordered).Count(p => p.First != p.Second);
        Assert.True(changed > 40, $"only {changed} of 100 schedules changed");
    }

    [Fact, Covers("DIFF-01")]
    public void Shrink_YieldsSmallerOrSimplerPlans_ThatStillParse()
    {
        for (var seed = 0; seed < 100; seed++)
        {
            var plan = ScriptPlan.Generate(seed);
            foreach (var smaller in ScriptPlan.Shrink(plan))
            {
                var json = smaller.ToScript().ToJsonString();
                Assert.NotNull(OpsScript.Parse(json, Registry));
                Assert.True(smaller != plan, "a shrink must change the plan");
                Assert.True(smaller.Edits.Count <= plan.Edits.Count);
            }
        }
        // shrinking terminates
        var p = ScriptPlan.Generate(7);
        for (var i = 0; i < 1000; i++)
        {
            var next = ScriptPlan.Shrink(p).FirstOrDefault();
            if (next == null) return;
            p = next;
        }
        Assert.Fail("shrinking did not terminate");
    }

    [Fact, Covers("DIFF-01")]
    public void Generated_TextEdits_AreInRange_AndNeverSplitASurrogatePair()
    {
        // before the exchange every replica edits alone, so its text can be tracked exactly; Q17 (splitting a pair) is a divergence area
        // that the default generator must not reach
        var nonBmp = 0;
        for (var seed = 0; seed < 500; seed++)
        {
            var plan = ScriptPlan.Generate(seed);
            var texts = new Dictionary<string, System.Text.StringBuilder>();
            foreach (var step in plan.ToScript()["steps"]!.AsArray().SelectMany(Flatten))
            {
                var kind = step["step"]!.GetValue<string>();
                if (kind is "sync" or "deliver") break;
                if (!kind.StartsWith("text.", StringComparison.Ordinal)) continue;
                var text = texts.TryGetValue(step["doc"]!.GetValue<string>(), out var t) ? t : texts[step["doc"]!.GetValue<string>()] = new();
                var at = step["at"]!.GetValue<int>();
                Assert.InRange(at, 0, text.Length);
                bool Splits(int i) => i > 0 && i < text.Length && char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1]);
                Assert.False(Splits(at), $"seed {seed}: {kind} at {at} splits a surrogate pair");
                if (kind == "text.insert")
                {
                    var value = step["value"]!.GetValue<string>();
                    nonBmp += value.Count(char.IsHighSurrogate);
                    text.Insert(at, value);
                }
                else
                {
                    var len = step["len"]!.GetValue<int>();
                    Assert.True(len >= 1 && at + len <= text.Length, $"seed {seed}: {kind} {at}+{len} outside {text.Length}");
                    Assert.False(Splits(at + len), $"seed {seed}: {kind} ends at {at + len}, inside a surrogate pair");
                    if (kind == "text.delete") text.Remove(at, len);
                }
            }
        }
        Assert.True(nonBmp > 0, "the vocabulary should include non-BMP text");
    }

    private static IEnumerable<JsonObject> Flatten(JsonNode? step)
    {
        if (step is not JsonObject o) yield break;
        if (o["steps"] is JsonArray children)
            foreach (var c in children.SelectMany(Flatten)) yield return c;
        else yield return o;
    }

    [Fact, Covers("DIFF-01")]
    public void ProtocolPlans_AreDeterministic_ParseOnTheCSharpInterpreter_AndUseEveryProtocolStep()
    {
        var steps = new HashSet<string>(StringComparer.Ordinal);
        bool appended = false, withStates = false, settled = false, awarenessOnly = false, syncOnly = false;
        for (var seed = 0; seed < 2000; seed++)
        {
            var plan = ProtocolPlan.Generate(seed);
            var json = plan.ToScript().ToJsonString();
            Assert.Equal(json, ProtocolPlan.Generate(seed).ToScript().ToJsonString());
            var ex = Record.Exception(() => OpsScript.Parse(json, Registry));
            Assert.True(ex == null, $"seed {seed} ({plan}): {ex?.Message}{Environment.NewLine}{json}");
            foreach (var step in plan.ToScript()["steps"]!.AsArray())
            {
                steps.Add(step!["step"]!.GetValue<string>());
                appended |= step["append"] != null;
                withStates |= step["states"] != null;
            }
            settled |= plan.Settle && !plan.Hostile;
            awarenessOnly |= plan.Awareness;
            syncOnly |= !plan.Awareness;
        }
        foreach (var kind in new[]
                 {
                     "text.insert", "text.delete", "sync.step1", "sync.step2", "sync.update", "sync.read", "auth.permissionDenied", "awareness.new",
                     "awareness.setLocal", "awareness.setLocalField", "awareness.applyUpdate", "awareness.removeStates", "awareness.encode",
                     "awareness.modify", "awareness.destroy", "tick",
                 })
            Assert.Contains(kind, steps);
        Assert.True(appended && withStates && settled && awarenessOnly && syncOnly);
    }

    [Fact, Covers("DIFF-01")]
    public void ProtocolPlan_Shrink_YieldsSmallerPlans_ThatStillParse()
    {
        var plan = Enumerable.Range(0, 200).Select(ProtocolPlan.Generate).First(p => p.Actions.Count > 10 && p.Awareness);
        var shrunk = ProtocolPlan.Shrink(plan).ToList();
        Assert.NotEmpty(shrunk);
        Assert.Contains(shrunk, p => p.Actions.Count < plan.Actions.Count);
        foreach (var p in shrunk) OpsScript.Parse(p.ToScript().ToJsonString(), Registry);
    }

    [Fact, Covers("DIFF-01", "DIFF-03")]
    public void TreePlans_AreDeterministic_ParseOnTheCSharpInterpreter_AndUseEveryEditKind()
    {
        var steps = new HashSet<string>(StringComparer.Ordinal);
        for (var seed = 0; seed < 2000; seed++)
        {
            var plan = TreePlan.Generate(seed);
            var json = plan.ToScript().ToJsonString();
            Assert.Equal(json, TreePlan.Generate(seed).ToScript().ToJsonString());
            var ex = Record.Exception(() => OpsScript.Parse(json, Registry));
            Assert.True(ex == null, $"seed {seed} ({plan}): {ex?.Message}{Environment.NewLine}{json}");
            void Walk(JsonNode? list)
            {
                foreach (var step in list?.AsArray() ?? new JsonArray())
                {
                    var kind = step!["step"]!.GetValue<string>();
                    steps.Add(kind == "xml.insert" ? "xml.insert." + step["kind"]!.GetValue<string>() : kind);
                    Walk(step["steps"]);
                }
            }
            Walk(plan.ToScript()["steps"]);
        }
        foreach (var kind in new[]
                 {
                     "text.insert", "text.delete", "text.format", "array.insert", "array.insertType", "array.delete", "map.set", "map.insertType", "map.delete",
                     "xml.insert.element", "xml.insert.text", "xml.delete", "xml.setAttribute", "xml.removeAttribute", "subdoc.add", "subdoc.load",
                     "observe", "observeDeep", "subdoc.observe", "subdoc.inspect", "transact", "deliver", "sync",
                 })
            Assert.Contains(kind, steps);
    }

    [Fact, Covers("DIFF-01")]
    public void TreePlans_AddressNestedTypesAndXmlNodes_ByPath()
    {
        var depths = new HashSet<int>();
        for (var seed = 0; seed < 500; seed++)
            foreach (var step in TreePlan.Generate(seed).ToScript()["steps"]!.AsArray())
                if (step!["target"] is JsonArray path) depths.Add(path.Count - 1);
        // a root, a type inside a root, and a type two levels down are all addressed
        Assert.Contains(1, depths);
        Assert.Contains(2, depths);
    }

    [Fact, Covers("DIFF-01")]
    public void TreePlan_Shrink_YieldsSmallerPlans_ThatStillParse()
    {
        var plan = TreePlan.Generate(7);
        foreach (var smaller in TreePlan.Shrink(plan))
        {
            Assert.True(smaller.Edits.Count + smaller.Base.Count <= plan.Edits.Count + plan.Base.Count);
            OpsScript.Parse(smaller.ToScript().ToJsonString(), Registry);
        }
    }

    [Fact, Covers("DIFF-04")]
    public void Mutator_AnyInput_ProducesTheDocumentedMutations()
    {
        var valid = Enumerable.Range(1, 40).Select(i => (byte)i).ToArray();
        for (var seed = 0; seed < 200; seed++)
        {
            var (cut, off) = Mutator.Apply(valid, MutationKind.Truncate, seed);
            Assert.True(cut.Length < valid.Length);
            Assert.Equal(off, cut.Length);
            Assert.Equal(valid[..cut.Length], cut);

            var (flipped, at) = Mutator.Apply(valid, MutationKind.FlipByte, seed);
            Assert.Equal(valid.Length, flipped.Length);
            Assert.Equal(1, flipped.Zip(valid).Count(p => p.First != p.Second));
            Assert.NotEqual(valid[at], flipped[at]);

            Assert.Equal(valid.Length - 1, Mutator.Apply(valid, MutationKind.DeleteByte, seed).Bytes.Length);
            Assert.Equal(valid.Length + 1, Mutator.Apply(valid, MutationKind.InsertByte, seed).Bytes.Length);
        }
        Assert.Empty(Mutator.Apply(Array.Empty<byte>(), MutationKind.Truncate, 5).Bytes);
    }

    [Fact, Covers("DIFF-05")]
    public void CutOffsets_CoverBothEnds_AndStayInRange()
    {
        Assert.Equal(new[] { 0, 1, 2 }, Mutator.CutOffsets(3, 25));
        var cuts = Mutator.CutOffsets(500, 25);
        Assert.InRange(cuts.Count, 20, 25);
        Assert.Contains(0, cuts);
        Assert.Contains(499, cuts);
        Assert.All(cuts, c => Assert.InRange(c, 0, 499));
        Assert.Equal(cuts.OrderBy(c => c).ToList(), cuts);
    }
}
