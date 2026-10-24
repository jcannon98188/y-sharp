using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using FsCheck;
using FsCheck.Fluent;

namespace YSharp.Interop.Generation;

public enum EditKind { TextInsert, TextDelete, TextFormat, ArrayInsert, ArrayPush, ArrayDelete, MapSet, MapDelete }

/// <summary>
/// One local edit. Positions are per-mille of the replica's current length and the content is picked by index, so removing an earlier edit
/// while shrinking never makes a later one invalid. <paramref name="JoinPrev"/> puts the edit in the previous edit's <c>transact</c>.
/// </summary>
public sealed record Edit(int Replica, EditKind Kind, int Permille, int Amount, int Pick, bool JoinPrev);

/// <summary>How the replicas exchange what they did concurrently.</summary>
public enum Exchange
{
    /// <summary>Pairwise <c>sync</c> steps in a shuffled order.</summary>
    Sync,

    /// <summary>A <c>deliver</c> schedule of the other replicas' update events: reordered, duplicated or dropped (ADR 0012).</summary>
    Schedule,
}

/// <summary>
/// A generated script, as data FsCheck can shrink: several replicas edit independently, they exchange state, then everything converges.
/// <see cref="ToScript"/> renders the ADR 0016 ops script both interpreters replay. The default generator draws only from behaviour the
/// two implementations are expected to share (no undefined indices, no split surrogate pairs, no integer-like map keys), so any
/// mismatch is a bug; known divergences are covered by <see cref="Normalisers"/> and the divergence-targeting generator.
/// </summary>
public sealed record ScriptPlan(int Replicas, IReadOnlyList<Edit> Edits, Exchange Exchange, int ExchangeSeed, bool Reorder, bool Duplicate, bool Drop, bool V2, bool Gc)
{
    public const string TextRoot = "t", ArrayRoot = "a", MapRoot = "m";
    public static readonly string[] Names = { "a", "b", "c" };

    private static readonly string[] Words = { "a", "bc", "hello", "x y", "é", "€", "日本", "😀", "naïve", "z" };
    private static readonly string[] Keys = { "k0", "k1", "k2", "key", "ü" };

    public override string ToString() =>
        $"{Replicas} replicas, {Edits.Count} edits, {Exchange}{(Exchange == Exchange.Schedule ? $" (seed {ExchangeSeed}{(Reorder ? ", reorder" : "")}{(Duplicate ? ", duplicate" : "")}{(Drop ? ", drop" : "")})" : "")}, {(V2 ? "v2" : "v1")}, gc {(Gc ? "on" : "off")}";

    // ------------------------------------------------------------------ generation

    public static ScriptPlan Generate(int caseSeed)
    {
        var rng = new Random(caseSeed);
        var replicas = rng.Next(2, 4);
        var edits = new List<Edit>();
        for (var i = rng.Next(1, 16); i > 0; i--)
            edits.Add(new Edit(rng.Next(replicas), (EditKind)rng.Next(Enum.GetValues<EditKind>().Length), rng.Next(0, 1001), rng.Next(1, 5), rng.Next(0, 1000), rng.Next(4) == 0));
        return new ScriptPlan(replicas, edits, rng.Next(2) == 0 ? Exchange.Schedule : Exchange.Sync, rng.Next(), rng.Next(3) != 0, rng.Next(3) == 0, rng.Next(3) == 0, rng.Next(2) == 0, rng.Next(4) != 0);
    }

    public static Arbitrary<ScriptPlan> Arbitrary() => Arb.From(Gen.Choose(0, int.MaxValue).Select(Generate), Shrink);

    /// <summary>Fewest edits first, then the exchange mutations off one at a time.</summary>
    public static IEnumerable<ScriptPlan> Shrink(ScriptPlan plan)
    {
        if (plan.Edits.Count > 1) yield return plan with { Edits = plan.Edits.Take(plan.Edits.Count / 2).ToList() };
        for (var i = 0; i < plan.Edits.Count && plan.Edits.Count > 1; i++)
            yield return plan with { Edits = plan.Edits.Where((_, k) => k != i).ToList() };
        if (plan.Drop) yield return plan with { Drop = false };
        if (plan.Duplicate) yield return plan with { Duplicate = false };
        if (plan.Reorder) yield return plan with { Reorder = false };
        if (plan.Exchange == Exchange.Schedule) yield return plan with { Exchange = Exchange.Sync };
        if (plan.V2) yield return plan with { V2 = false };
        if (plan.Replicas > 2 && plan.Edits.All(e => e.Replica < plan.Replicas - 1)) yield return plan with { Replicas = plan.Replicas - 1 };
        for (var i = 0; i < plan.Edits.Count; i++)
            if (plan.Edits[i].Amount > 1)
                yield return plan with { Edits = plan.Edits.Select((e, k) => k == i ? e with { Amount = 1 } : e).ToList() };
    }

    // ------------------------------------------------------------------ rendering

    private sealed class Local
    {
        public StringBuilder Text { get; } = new();
        public int ArrayLength;
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
        public int Updates;
    }

    private static JsonObject Target(string root, string kind) => new() { ["root"] = root, ["kind"] = kind };

    private static JsonValue Plain(string s) => JsonValue.Create(s);

    /// <summary>A snap to a UTF-16 boundary that never splits a surrogate pair (splitting is a registered divergence area, not the default).</summary>
    private static int Snap(StringBuilder text, int at)
    {
        if (at > 0 && at < text.Length && char.IsLowSurrogate(text[at]) && char.IsHighSurrogate(text[at - 1])) return at - 1;
        return at;
    }

    private static JsonNode MapValue(int pick) => (pick % 5) switch
    {
        0 => JsonValue.Create(pick),
        1 => JsonValue.Create(Words[pick % Words.Length]),
        2 => JsonValue.Create(pick % 2 == 0),
        3 => new JsonArray(JsonValue.Create(pick % 7), JsonValue.Create(Words[pick % Words.Length])),
        _ => new JsonObject { ["$"] = "obj", ["pairs"] = new JsonArray(new JsonArray(Plain("x"), JsonValue.Create(pick % 9)), new JsonArray(Plain("y"), JsonValue.Create((string?)null))) },
    };

    private static JsonNode? ArrayValue(int pick) => (pick % 4) switch
    {
        0 => JsonValue.Create(pick % 100),
        1 => JsonValue.Create(Words[pick % Words.Length]),
        2 => JsonValue.Create(pick % 2 == 0),
        _ => JsonValue.Create((string?)null),
    };

    /// <summary>The plan as a stored (<c>schema: 1</c>) ops script. Every emitted edit is effective, so each transaction yields one update event.</summary>
    public JsonObject ToScript()
    {
        var locals = Enumerable.Range(0, Replicas).Select(_ => new Local()).ToArray();
        var steps = new JsonArray();
        JsonArray? open = null;
        var openReplica = -1;

        void Emit(int replica, JsonObject step, bool join)
        {
            if (join && open != null && openReplica == replica) { open.Add(step); return; }
            Flush();
            open = new JsonArray(step);
            openReplica = replica;
            locals[replica].Updates++;
        }

        void Flush()
        {
            if (open == null) return;
            steps.Add(open.Count == 1
                ? open[0]!.DeepClone()
                : new JsonObject { ["step"] = "transact", ["doc"] = Names[openReplica], ["steps"] = open.DeepClone() });
            open = null;
        }

        foreach (var e in Edits)
        {
            var r = Math.Min(e.Replica, Replicas - 1);
            var doc = Names[r];
            var l = locals[r];
            var kind = e.Kind;
            if (kind == EditKind.TextFormat && Exchange == Exchange.Schedule) kind = EditKind.TextInsert; // format may be a no-op, which would break event counting
            switch (kind)
            {
                case EditKind.TextInsert:
                {
                    var value = string.Concat(Enumerable.Range(0, e.Amount).Select(i => Words[(e.Pick + i * 3) % Words.Length]));
                    var at = Snap(l.Text, e.Permille * l.Text.Length / 1000);
                    Emit(r, Step("text.insert", doc, Target(TextRoot, "text"), ("at", at), ("value", Plain(value))), e.JoinPrev);
                    l.Text.Insert(at, value);
                    break;
                }
                case EditKind.TextDelete when l.Text.Length > 0:
                {
                    var at = Snap(l.Text, Math.Min(l.Text.Length - 1, e.Permille * l.Text.Length / 1000));
                    var end = Math.Min(l.Text.Length, at + e.Amount);
                    if (end < l.Text.Length && char.IsLowSurrogate(l.Text[end]) && end > at) end++;
                    if (end > l.Text.Length) end = l.Text.Length;
                    Emit(r, Step("text.delete", doc, Target(TextRoot, "text"), ("at", at), ("len", end - at)), e.JoinPrev);
                    l.Text.Remove(at, end - at);
                    break;
                }
                case EditKind.TextFormat when l.Text.Length > 0:
                {
                    var at = Snap(l.Text, Math.Min(l.Text.Length - 1, e.Permille * l.Text.Length / 1000));
                    var end = Math.Min(l.Text.Length, at + e.Amount);
                    if (end < l.Text.Length && char.IsLowSurrogate(l.Text[end]) && end > at) end++;
                    var attrs = new JsonObject { [e.Pick % 2 == 0 ? "bold" : "italic"] = e.Pick % 3 == 0 ? null : JsonValue.Create(true) };
                    Emit(r, Step("text.format", doc, Target(TextRoot, "text"), ("at", at), ("len", end - at), ("attributes", attrs)), e.JoinPrev);
                    break;
                }
                case EditKind.ArrayInsert:
                {
                    var at = e.Permille * l.ArrayLength / 1000;
                    var values = new JsonArray(Enumerable.Range(0, e.Amount).Select(i => ArrayValue(e.Pick + i)).ToArray());
                    Emit(r, Step("array.insert", doc, Target(ArrayRoot, "array"), ("at", at), ("values", values)), e.JoinPrev);
                    l.ArrayLength += e.Amount;
                    break;
                }
                case EditKind.ArrayPush:
                {
                    var values = new JsonArray(Enumerable.Range(0, e.Amount).Select(i => ArrayValue(e.Pick + i)).ToArray());
                    Emit(r, Step("array.push", doc, Target(ArrayRoot, "array"), ("values", values)), e.JoinPrev);
                    l.ArrayLength += e.Amount;
                    break;
                }
                case EditKind.ArrayDelete when l.ArrayLength > 0:
                {
                    var at = Math.Min(l.ArrayLength - 1, e.Permille * l.ArrayLength / 1000);
                    var len = Math.Min(e.Amount, l.ArrayLength - at);
                    Emit(r, Step("array.delete", doc, Target(ArrayRoot, "array"), ("at", at), ("len", len)), e.JoinPrev);
                    l.ArrayLength -= len;
                    break;
                }
                case EditKind.MapSet:
                {
                    var key = Keys[e.Pick % Keys.Length];
                    Emit(r, Step("map.set", doc, Target(MapRoot, "map"), ("key", Plain(key)), ("value", MapValue(e.Pick))), e.JoinPrev);
                    l.Keys.Add(key);
                    break;
                }
                case EditKind.MapDelete when l.Keys.Count > 0:
                {
                    var key = l.Keys.OrderBy(k => k, StringComparer.Ordinal).ElementAt(e.Pick % l.Keys.Count);
                    Emit(r, Step("map.delete", doc, Target(MapRoot, "map"), ("key", Plain(key))), e.JoinPrev);
                    l.Keys.Remove(key);
                    break;
                }
            }
        }
        Flush();

        var rng = new Random(ExchangeSeed);
        if (Exchange == Exchange.Schedule) steps.Add(ScheduleStep(locals.Select(l => l.Updates).ToArray(), rng));
        else
        {
            var pairs = AllPairs().OrderBy(_ => rng.Next()).Take(Math.Max(1, Replicas)).ToList();
            foreach (var (from, to) in pairs) steps.Add(new JsonObject { ["step"] = "sync", ["from"] = Names[from], ["to"] = Names[to] });
        }

        // convergence: every replica hears from every other, twice, so the final state is the union however the exchange went
        for (var round = 0; round < 2; round++)
            foreach (var (from, to) in AllPairs())
                steps.Add(new JsonObject { ["step"] = "sync", ["from"] = Names[from], ["to"] = Names[to] });

        return new JsonObject
        {
            ["schema"] = 1,
            ["replicas"] = new JsonArray(Enumerable.Range(0, Replicas).Select(i =>
                (JsonNode)new JsonObject { ["name"] = Names[i], ["clientId"] = 101 + i, ["gc"] = Gc }).ToArray()),
            ["steps"] = steps,
        };
    }

    private IEnumerable<(int From, int To)> AllPairs()
    {
        for (var i = 0; i < Replicas; i++)
            for (var j = 0; j < Replicas; j++)
                if (i != j) yield return (i, j);
    }

    /// <summary>The <c>deliver</c> step: every other replica's update events to each receiver, shuffled / duplicated / thinned by the plan.</summary>
    private JsonObject ScheduleStep(int[] updates, Random rng)
    {
        var entries = new List<(int To, int From, int Index)>();
        foreach (var (from, to) in AllPairs())
            for (var i = 0; i < updates[from]; i++) entries.Add((to, from, i));
        if (Reorder)
            for (var i = entries.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (entries[i], entries[j]) = (entries[j], entries[i]);
            }
        if (Drop) entries = entries.Where(_ => rng.Next(3) != 0).ToList();
        if (Duplicate)
            for (var n = rng.Next(1, 4); n > 0 && entries.Count > 0; n--) entries.Insert(rng.Next(entries.Count + 1), entries[rng.Next(entries.Count)]);

        var schedule = new JsonArray(entries.Select(e => (JsonNode)new JsonObject
        {
            ["do"] = "apply",
            ["doc"] = Names[e.To],
            ["update"] = new JsonObject { ["from"] = Names[e.From], ["index"] = e.Index },
        }).ToArray());
        var step = new JsonObject { ["step"] = "deliver", ["schedule"] = schedule };
        if (V2) step["encoding"] = "v2";
        return step;
    }

    private static JsonObject Step(string kind, string doc, JsonObject target, params (string Name, JsonNode? Value)[] fields)
    {
        var o = new JsonObject { ["step"] = kind, ["doc"] = doc, ["target"] = target };
        foreach (var (name, value) in fields) o[name] = value;
        return o;
    }

    public string Describe() => ToScript().ToJsonString().Length.ToString(CultureInfo.InvariantCulture) + " bytes of script; " + this;
}
