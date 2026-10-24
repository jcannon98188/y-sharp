using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using FsCheck;
using FsCheck.Fluent;
using YSharp.TestKit.Ops;

namespace YSharp.Fuzz.Generators;

public enum EditKind { TextInsert, TextDelete, ArrayPush, MapSet, MapDelete }

/// <summary>One local edit of an ops plan. Positions are per-mille of the replica's current length, so removing an earlier edit while shrinking never makes a later one invalid.</summary>
internal sealed record Edit(int Replica, EditKind Kind, int Permille, int Amount, string Text);

/// <summary>Several writers editing independently (no sync step), then the delivery schedule applied to a receiver.</summary>
internal sealed record OpsPlan(int Replicas, IReadOnlyList<Edit> Edits, int ScheduleSeed, bool Reorder, bool Duplicate, bool Drop)
{
    public override string ToString() =>
        $"{Replicas} replicas, {Edits.Count} edits, schedule seed {ScheduleSeed} ({(Reorder ? "reorder " : "")}{(Duplicate ? "duplicate " : "")}{(Drop ? "drop+late" : "")})";
}

/// <summary>An update produced by one replica, in the order the script produced it.</summary>
internal sealed record Produced(string Replica, byte[] Update);

/// <summary>
/// FsCheck layer 3 (ADR 0013, 0012): an ops-script generator with delivery-schedule mutation. A plan becomes a real ops script (the
/// ADR 0016 vocabulary) run by the TestKit interpreter; the update events it captures are delivered to a receiver in a mutated
/// schedule: shuffled, duplicated, or with some updates dropped and delivered only at the end. Whatever the schedule, the receiver must
/// stay consistent (post-condition) and, once every update has arrived, equal the replica that received them in order.
/// </summary>
internal static class OpsLayer
{
    public const string TextRoot = "t", ArrayRoot = "a", MapRoot = "m";

    private static readonly string[] Pieces = { "a", "bc", "hello", "Ã©", "â‚¬", "x y" };

    public static OpsPlan Generate(int caseSeed)
    {
        var rng = new Random(caseSeed);
        var replicas = rng.Next(2, 4);
        var edits = new List<Edit>();
        for (var i = rng.Next(2, 14); i > 0; i--)
            edits.Add(new Edit(rng.Next(replicas), (EditKind)rng.Next(Enum.GetValues<EditKind>().Length), rng.Next(0, 1001), rng.Next(1, 4), Pieces[rng.Next(Pieces.Length)]));
        return new OpsPlan(replicas, edits, rng.Next(), rng.Next(3) != 0, rng.Next(3) == 0, rng.Next(3) == 0);
    }

    /// <summary>The plan as an ADR 0016 ops script (JSON), with lengths tracked per replica so every step is valid.</summary>
    public static string ToScript(OpsPlan plan)
    {
        var steps = new JsonArray();
        var textLength = new int[plan.Replicas];
        var arrayLength = new int[plan.Replicas];
        JsonObject Target(string root, string kind) => new() { ["root"] = root, ["kind"] = kind };
        foreach (var e in plan.Edits)
        {
            var doc = "r" + e.Replica;
            switch (e.Kind)
            {
                case EditKind.TextInsert:
                    steps.Add(new JsonObject
                    {
                        ["step"] = "text.insert", ["doc"] = doc, ["target"] = Target(TextRoot, "text"),
                        ["at"] = e.Permille * textLength[e.Replica] / 1000, ["value"] = e.Text,
                    });
                    textLength[e.Replica] += e.Text.Length;
                    break;
                case EditKind.TextDelete when textLength[e.Replica] > 0:
                {
                    var at = Math.Min(textLength[e.Replica] - 1, e.Permille * textLength[e.Replica] / 1000);
                    var len = Math.Min(e.Amount, textLength[e.Replica] - at);
                    steps.Add(new JsonObject { ["step"] = "text.delete", ["doc"] = doc, ["target"] = Target(TextRoot, "text"), ["at"] = at, ["len"] = len });
                    textLength[e.Replica] -= len;
                    break;
                }
                case EditKind.ArrayPush:
                    steps.Add(new JsonObject
                    {
                        ["step"] = "array.push", ["doc"] = doc, ["target"] = Target(ArrayRoot, "array"),
                        ["values"] = new JsonArray(e.Amount, e.Text),
                    });
                    arrayLength[e.Replica] += 2;
                    break;
                case EditKind.MapSet:
                    steps.Add(new JsonObject
                    {
                        ["step"] = "map.set", ["doc"] = doc, ["target"] = Target(MapRoot, "map"),
                        ["key"] = "k" + (e.Amount % 3), ["value"] = e.Text,
                    });
                    break;
                case EditKind.MapDelete:
                    steps.Add(new JsonObject { ["step"] = "map.delete", ["doc"] = doc, ["target"] = Target(MapRoot, "map"), ["key"] = "k" + (e.Amount % 3) });
                    break;
            }
        }
        var script = new JsonObject
        {
            ["schema"] = 1,
            ["replicas"] = new JsonArray(Enumerable.Range(0, plan.Replicas).Select(i => (JsonNode)new JsonObject
            {
                ["name"] = "r" + i, ["clientId"] = (ulong)(100 + i),
            }).ToArray()),
            ["steps"] = steps,
        };
        return script.ToJsonString();
    }

    /// <summary>Runs the plan's script and returns the update events in production order together with the final replicas.</summary>
    public static (List<Produced> Updates, IReadOnlyDictionary<string, Doc> Docs) Run(OpsPlan plan)
    {
        var registry = StandardSteps.CreateRegistry();
        var script = OpsScript.Parse(ToScript(plan), registry);
        var produced = new List<Produced>();
        var interpreter = new OpsInterpreter(registry)
        {
            DocFactory = (ctx, spec) =>
            {
                var doc = new Doc(new DocOptions { ClientId = spec.ClientId, Gc = spec.Gc, TimeProvider = ctx.Clock });
                ctx.Updates[spec.Name] = new List<byte[]>();
                doc.Update += (update, _, _, _) => produced.Add(new Produced(spec.Name, update.ToArray()));
                return doc;
            },
        };
        var result = interpreter.Run(script);
        return (produced, result.Docs);
    }

    /// <summary>The delivery schedule: indices into the produced list, in delivery order, followed by the dropped ones if any.</summary>
    public static (IReadOnlyList<int> Order, IReadOnlyList<int> Late) Schedule(OpsPlan plan, int produced)
    {
        var rng = new Random(plan.ScheduleSeed);
        var order = Enumerable.Range(0, produced).ToList();
        if (plan.Reorder)
            for (var i = order.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
        var late = new List<int>();
        if (plan.Drop)
            foreach (var idx in order.ToList())
                if (rng.Next(3) == 0) { order.Remove(idx); late.Add(idx); }
        if (plan.Duplicate)
            for (var i = rng.Next(1, 4); i > 0 && order.Count > 0; i--) order.Insert(rng.Next(order.Count + 1), order[rng.Next(order.Count)]);
        return (order, late);
    }

    public static Arbitrary<OpsPlan> Layer() => Arb.From(Gen.Choose(0, int.MaxValue).Select(Generate), Shrink);

    /// <summary>Fewest edits first; then the schedule mutations are switched off one at a time.</summary>
    public static IEnumerable<OpsPlan> Shrink(OpsPlan plan)
    {
        if (plan.Edits.Count > 1) yield return plan with { Edits = plan.Edits.Take(plan.Edits.Count / 2).ToList() };
        for (var i = 0; i < plan.Edits.Count && plan.Edits.Count > 1; i++)
            yield return plan with { Edits = plan.Edits.Where((_, k) => k != i).ToList() };
        if (plan.Drop) yield return plan with { Drop = false };
        if (plan.Duplicate) yield return plan with { Duplicate = false };
        if (plan.Reorder) yield return plan with { Reorder = false };
    }

    public static string Describe(IReadOnlyList<int> order) => string.Join(",", order.Select(i => i.ToString(CultureInfo.InvariantCulture)));
}
