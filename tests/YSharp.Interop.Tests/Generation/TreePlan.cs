using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using FsCheck;
using FsCheck.Fluent;
using static YSharp.Interop.Generation.ScriptPlan;

namespace YSharp.Interop.Generation;

public enum TreeEditKind
{
    TextInsert, TextDelete, TextFormat,
    ArrayInsert, ArrayInsertType, ArrayDelete,
    MapSet, MapSetType, MapDelete,
    XmlInsertElement, XmlInsertText, XmlDelete, XmlSetAttribute, XmlRemoveAttribute,
    SubdocAdd, SubdocLoad,
}

/// <summary>
/// One edit of a <see cref="TreePlan"/>. <paramref name="Target"/> picks, by index modulo the candidates, which shared type of the replica's
/// current tree the edit addresses; positions and content are picked as in <see cref="Edit"/>, so shrinking never makes a later edit invalid.
/// </summary>
public sealed record TreeEdit(int Replica, TreeEditKind Kind, int Target, int Permille, int Amount, int Pick, bool JoinPrev);

/// <summary>
/// A generated script over nested shared types, XML and subdocuments (Spec 40). Replica <c>a</c> first builds a base tree that every replica
/// receives, so the concurrent edits that follow can address the same nested types, XML nodes and subdocuments; then the replicas exchange
/// and converge as in <see cref="ScriptPlan"/>. With <see cref="Observe"/> every replica records shallow and deep observer events on each root
/// and its <c>subdocs</c> events from the start, so the <c>events</c> observation covers local, synced and delivered changes.
/// </summary>
public sealed record TreePlan(int Replicas, IReadOnlyList<TreeEdit> Base, IReadOnlyList<TreeEdit> Edits, Exchange Exchange, int ExchangeSeed,
    bool Reorder, bool Duplicate, bool Drop, bool V2, bool Gc, bool Observe)
{
    public const string XmlRoot = "x";

    private const int MaxDepth = 3;
    private static readonly string[] ElementNames = { "p", "div", "span" };
    private static readonly string[] AttributeNames = { "class", "id", "data-x" };

    public override string ToString() =>
        $"{Replicas} replicas, {Base.Count} base + {Edits.Count} edits, {Exchange}{(Exchange == Exchange.Schedule ? $" (seed {ExchangeSeed}{(Reorder ? ", reorder" : "")}{(Duplicate ? ", duplicate" : "")}{(Drop ? ", drop" : "")})" : "")}, {(V2 ? "v2" : "v1")}, gc {(Gc ? "on" : "off")}{(Observe ? ", observed" : "")}";

    // ------------------------------------------------------------------ generation

    private static TreeEdit Draw(Random rng, int replicas) =>
        new(rng.Next(replicas), (TreeEditKind)rng.Next(Enum.GetValues<TreeEditKind>().Length), rng.Next(0, 1000), rng.Next(0, 1001), rng.Next(1, 4), rng.Next(0, 1000), rng.Next(4) == 0);

    public static TreePlan Generate(int caseSeed)
    {
        var rng = new Random(caseSeed);
        var replicas = rng.Next(2, 4);
        var baseEdits = Enumerable.Range(0, rng.Next(1, 9)).Select(_ => Draw(rng, 1)).ToList();
        var edits = Enumerable.Range(0, rng.Next(1, 13)).Select(_ => Draw(rng, replicas)).ToList();
        return new TreePlan(replicas, baseEdits, edits, rng.Next(2) == 0 ? Exchange.Schedule : Exchange.Sync, rng.Next(),
            rng.Next(3) != 0, rng.Next(3) == 0, rng.Next(3) == 0, rng.Next(2) == 0, rng.Next(4) != 0, rng.Next(3) != 0);
    }

    public static Arbitrary<TreePlan> Arbitrary() => Arb.From(Gen.Choose(0, int.MaxValue).Select(Generate), Shrink);

    /// <summary>Fewest edits first (concurrent, then base), then the exchange mutations and the observers off one at a time.</summary>
    public static IEnumerable<TreePlan> Shrink(TreePlan plan)
    {
        if (plan.Edits.Count > 1) yield return plan with { Edits = plan.Edits.Take(plan.Edits.Count / 2).ToList() };
        for (var i = 0; i < plan.Edits.Count && plan.Edits.Count > 1; i++)
            yield return plan with { Edits = plan.Edits.Where((_, k) => k != i).ToList() };
        for (var i = 0; i < plan.Base.Count && plan.Base.Count > 1; i++)
            yield return plan with { Base = plan.Base.Where((_, k) => k != i).ToList() };
        if (plan.Observe) yield return plan with { Observe = false };
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

    // ------------------------------------------------------------------ the local model of one replica's tree

    private abstract class Node
    {
        public abstract Node Clone();
    }

    /// <summary>A Y.Text, or a Y.XmlText (which also carries attributes).</summary>
    private sealed class TextNode(bool xml) : Node
    {
        public bool Xml { get; } = xml;
        public StringBuilder Text { get; } = new();
        public SortedSet<string> Attrs { get; } = new(StringComparer.Ordinal);

        public override Node Clone()
        {
            var c = new TextNode(Xml);
            c.Text.Append(Text);
            c.Attrs.UnionWith(Attrs);
            return c;
        }
    }

    /// <summary>A Y.Array; a null item is a plain value.</summary>
    private sealed class ArrayNode : Node
    {
        public List<Node?> Items { get; } = new();

        public override Node Clone()
        {
            var c = new ArrayNode();
            c.Items.AddRange(Items.Select(i => i?.Clone()));
            return c;
        }
    }

    /// <summary>A Y.Map; a null value is a plain value.</summary>
    private sealed class MapNode : Node
    {
        public SortedDictionary<string, Node?> Entries { get; } = new(StringComparer.Ordinal);

        public override Node Clone()
        {
            var c = new MapNode();
            foreach (var kv in Entries) c.Entries[kv.Key] = kv.Value?.Clone();
            return c;
        }
    }

    /// <summary>A Y.XmlFragment (no name) or a Y.XmlElement; the children are elements and XML texts.</summary>
    private sealed class XmlNode(string? name) : Node
    {
        public string? Name { get; } = name;
        public List<Node> Children { get; } = new();
        public SortedSet<string> Attrs { get; } = new(StringComparer.Ordinal);

        public override Node Clone()
        {
            var c = new XmlNode(Name);
            c.Children.AddRange(Children.Select(n => n.Clone()));
            c.Attrs.UnionWith(Attrs);
            return c;
        }
    }

    private sealed class SubdocNode(string guid) : Node
    {
        public string Guid { get; } = guid;

        public override Node Clone() => new SubdocNode(Guid);
    }

    private sealed class Tree
    {
        public TextNode T { get; init; } = new(false);
        public ArrayNode A { get; init; } = new();
        public MapNode M { get; init; } = new();
        public XmlNode X { get; init; } = new(null);
        public int Updates;

        public Tree Clone() => new() { T = (TextNode)T.Clone(), A = (ArrayNode)A.Clone(), M = (MapNode)M.Clone(), X = (XmlNode)X.Clone() };

        /// <summary>Every shared type with its address, depth first in a fixed order.</summary>
        public List<(Node Node, JsonNode Target, int Depth)> Types()
        {
            var list = new List<(Node, JsonNode, int)>();
            void Walk(Node node, JsonObject root, List<JsonNode> path)
            {
                list.Add((node, path.Count == 0 ? root.DeepClone() : new JsonArray([root.DeepClone(), .. path.Select(p => p.DeepClone())]), path.Count));
                IEnumerable<(Node Child, JsonNode Seg)> children = node switch
                {
                    ArrayNode a => a.Items.Select((c, i) => (c, (JsonNode)JsonValue.Create(i))).Where(p => p.c != null).Select(p => (p.c!, p.Item2)),
                    MapNode m => m.Entries.Where(kv => kv.Value != null).Select(kv => (kv.Value!, (JsonNode)Plain(kv.Key))),
                    XmlNode x => x.Children.Select((c, i) => (c, (JsonNode)JsonValue.Create(i))),
                    _ => Enumerable.Empty<(Node, JsonNode)>(),
                };
                foreach (var (child, seg) in children)
                    if (child is not SubdocNode) Walk(child, root, [.. path, seg]);
            }
            Walk(T, Target(TextRoot, "text"), new());
            Walk(A, Target(ArrayRoot, "array"), new());
            Walk(M, Target(MapRoot, "map"), new());
            Walk(X, Target(XmlRoot, "xmlFragment"), new());
            return list;
        }

        public List<string> Subdocs()
        {
            var list = new List<string>();
            void Walk(Node? n)
            {
                switch (n)
                {
                    case SubdocNode s: list.Add(s.Guid); break;
                    case ArrayNode a: foreach (var c in a.Items) Walk(c); break;
                    case MapNode m: foreach (var c in m.Entries.Values) Walk(c); break;
                }
            }
            Walk(A);
            Walk(M);
            return list;
        }
    }

    // ------------------------------------------------------------------ rendering

    private static (JsonObject Init, Node Model) NewType(int pick) => (pick % 3) switch
    {
        0 => (new JsonObject { ["kind"] = "text", ["init"] = Plain(Words[pick % Words.Length]) }, WithText(new TextNode(false), Words[pick % Words.Length])),
        1 => (new JsonObject { ["kind"] = "array", ["init"] = new JsonArray(ArrayValue(pick), ArrayValue(pick + 1)) }, WithItems(new ArrayNode(), 2)),
        _ => (new JsonObject { ["kind"] = "map", ["init"] = new JsonObject { ["$"] = "obj", ["pairs"] = new JsonArray(new JsonArray(Plain("x"), JsonValue.Create(pick % 9))) } }, WithKey(new MapNode(), "x")),
    };

    private static TextNode WithText(TextNode t, string s)
    {
        t.Text.Append(s);
        return t;
    }

    private static ArrayNode WithItems(ArrayNode a, int n)
    {
        for (var i = 0; i < n; i++) a.Items.Add(null);
        return a;
    }

    private static MapNode WithKey(MapNode m, string key)
    {
        m.Entries[key] = null;
        return m;
    }

    private static T? Pick<T>(List<(Node Node, JsonNode Target, int Depth)> types, int target, Func<(Node Node, JsonNode Target, int Depth), bool>? filter, out JsonNode? address) where T : Node
    {
        var candidates = types.Where(t => t.Node is T && (filter == null || filter(t))).ToList();
        address = null;
        if (candidates.Count == 0) return null;
        var c = candidates[target % candidates.Count];
        address = c.Target;
        return (T)c.Node;
    }

    /// <summary>
    /// The step one edit renders on <paramref name="tree"/>, which it updates; null when the edit has nothing to act on. <c>Counts</c> is false
    /// for a step that fires no update event (loading a subdocument changes no struct).
    /// </summary>
    private (JsonObject Step, bool Counts)? Render(TreeEdit e, Tree tree, string doc, ref int guidSeq)
    {
        var types = tree.Types();
        JsonNode? at;
        var kind = e.Kind;
        if (kind == TreeEditKind.TextFormat && Exchange == Exchange.Schedule) kind = TreeEditKind.TextInsert; // a format may be a no-op, which would break event counting
        switch (kind)
        {
            case TreeEditKind.TextInsert:
            {
                if (Pick<TextNode>(types, e.Target, null, out at) is not { } t) return null;
                var value = string.Concat(Enumerable.Range(0, e.Amount).Select(i => Words[(e.Pick + i * 3) % Words.Length]));
                var pos = Snap(t.Text, e.Permille * t.Text.Length / 1000);
                t.Text.Insert(pos, value);
                return (Step("text.insert", doc, at!, ("at", pos), ("value", Plain(value))), true);
            }
            case TreeEditKind.TextDelete:
            {
                if (Pick<TextNode>(types, e.Target, x => ((TextNode)x.Node).Text.Length > 0, out at) is not { } t) return null;
                var pos = Snap(t.Text, Math.Min(t.Text.Length - 1, e.Permille * t.Text.Length / 1000));
                var end = Math.Min(t.Text.Length, pos + e.Amount);
                if (end < t.Text.Length && char.IsLowSurrogate(t.Text[end]) && end > pos) end++;
                if (end > t.Text.Length) end = t.Text.Length;
                t.Text.Remove(pos, end - pos);
                return (Step("text.delete", doc, at!, ("at", pos), ("len", end - pos)), true);
            }
            case TreeEditKind.TextFormat:
            {
                if (Pick<TextNode>(types, e.Target, x => ((TextNode)x.Node).Text.Length > 0, out at) is not { } t) return null;
                var pos = Snap(t.Text, Math.Min(t.Text.Length - 1, e.Permille * t.Text.Length / 1000));
                var end = Math.Min(t.Text.Length, pos + e.Amount);
                if (end < t.Text.Length && char.IsLowSurrogate(t.Text[end]) && end > pos) end++;
                var attrs = new JsonObject
                {
                    ["$"] = "obj",
                    ["pairs"] = new JsonArray(new JsonArray(Plain(e.Pick % 2 == 0 ? "bold" : "italic"), e.Pick % 3 == 0 ? null : JsonValue.Create(true))),
                };
                return (Step("text.format", doc, at!, ("at", pos), ("len", end - pos), ("attributes", attrs)), true);
            }
            case TreeEditKind.ArrayInsert:
            {
                if (Pick<ArrayNode>(types, e.Target, null, out at) is not { } a) return null;
                var pos = e.Permille * a.Items.Count / 1000;
                a.Items.InsertRange(pos, Enumerable.Repeat<Node?>(null, e.Amount));
                return (Step("array.insert", doc, at!, ("at", pos), ("values", new JsonArray(Enumerable.Range(0, e.Amount).Select(i => ArrayValue(e.Pick + i)).ToArray()))), true);
            }
            case TreeEditKind.ArrayInsertType:
            {
                if (Pick<ArrayNode>(types, e.Target, x => x.Depth < MaxDepth, out at) is not { } a) return null;
                var pos = e.Permille * a.Items.Count / 1000;
                var (init, model) = NewType(e.Pick);
                a.Items.Insert(pos, model);
                var step = Step("array.insertType", doc, at!, ("at", pos));
                foreach (var kv in init) step[kv.Key] = kv.Value!.DeepClone();
                return (step, true);
            }
            case TreeEditKind.ArrayDelete:
            {
                if (Pick<ArrayNode>(types, e.Target, x => ((ArrayNode)x.Node).Items.Count > 0, out at) is not { } a) return null;
                var pos = Math.Min(a.Items.Count - 1, e.Permille * a.Items.Count / 1000);
                var len = Math.Min(e.Amount, a.Items.Count - pos);
                a.Items.RemoveRange(pos, len);
                return (Step("array.delete", doc, at!, ("at", pos), ("len", len)), true);
            }
            case TreeEditKind.MapSet:
            {
                if (Pick<MapNode>(types, e.Target, null, out at) is not { } m) return null;
                var key = Keys[e.Pick % Keys.Length];
                m.Entries[key] = null;
                return (Step("map.set", doc, at!, ("key", Plain(key)), ("value", MapValue(e.Pick))), true);
            }
            case TreeEditKind.MapSetType:
            {
                if (Pick<MapNode>(types, e.Target, x => x.Depth < MaxDepth, out at) is not { } m) return null;
                var key = Keys[e.Pick % Keys.Length];
                var (init, model) = NewType(e.Pick / Keys.Length);
                m.Entries[key] = model;
                var step = Step("map.insertType", doc, at!, ("key", Plain(key)));
                foreach (var kv in init) step[kv.Key] = kv.Value!.DeepClone();
                return (step, true);
            }
            case TreeEditKind.MapDelete:
            {
                if (Pick<MapNode>(types, e.Target, x => ((MapNode)x.Node).Entries.Count > 0, out at) is not { } m) return null;
                var key = m.Entries.Keys.ElementAt(e.Pick % m.Entries.Count);
                m.Entries.Remove(key);
                return (Step("map.delete", doc, at!, ("key", Plain(key))), true);
            }
            case TreeEditKind.XmlInsertElement:
            case TreeEditKind.XmlInsertText:
            {
                if (Pick<XmlNode>(types, e.Target, x => x.Depth < MaxDepth, out at) is not { } x) return null;
                var pos = e.Permille * x.Children.Count / 1000;
                if (kind == TreeEditKind.XmlInsertElement)
                {
                    var name = ElementNames[e.Pick % ElementNames.Length];
                    x.Children.Insert(pos, new XmlNode(name));
                    return (Step("xml.insert", doc, at!, ("at", pos), ("kind", Plain("element")), ("name", Plain(name))), true);
                }
                var init = Words[e.Pick % Words.Length];
                x.Children.Insert(pos, WithText(new TextNode(true), init));
                return (Step("xml.insert", doc, at!, ("at", pos), ("kind", Plain("text")), ("init", Plain(init))), true);
            }
            case TreeEditKind.XmlDelete:
            {
                if (Pick<XmlNode>(types, e.Target, x => ((XmlNode)x.Node).Children.Count > 0, out at) is not { } x) return null;
                var pos = Math.Min(x.Children.Count - 1, e.Permille * x.Children.Count / 1000);
                var len = Math.Min(e.Amount, x.Children.Count - pos);
                x.Children.RemoveRange(pos, len);
                return (Step("xml.delete", doc, at!, ("at", pos), ("len", len)), true);
            }
            case TreeEditKind.XmlSetAttribute:
            {
                var candidates = types.Where(t => t.Node is XmlNode { Name: not null } or TextNode { Xml: true }).ToList();
                if (candidates.Count == 0) return null;
                var (node, target, _) = candidates[e.Target % candidates.Count];
                var name = AttributeNames[e.Pick % AttributeNames.Length];
                (node is XmlNode xn ? xn.Attrs : ((TextNode)node).Attrs).Add(name);
                var value = e.Pick % 4 == 0 ? (JsonNode)JsonValue.Create(e.Pick % 10) : Plain(Words[e.Pick % Words.Length]);
                return (Step("xml.setAttribute", doc, target, ("name", Plain(name)), ("value", value)), true);
            }
            case TreeEditKind.XmlRemoveAttribute:
            {
                var candidates = types.Where(t => t.Node is XmlNode { Name: not null, Attrs.Count: > 0 } or TextNode { Xml: true, Attrs.Count: > 0 }).ToList();
                if (candidates.Count == 0) return null;
                var (node, target, _) = candidates[e.Target % candidates.Count];
                var attrs = node is XmlNode xn ? xn.Attrs : ((TextNode)node).Attrs;
                var name = attrs.ElementAt(e.Pick % attrs.Count);
                attrs.Remove(name);
                return (Step("xml.removeAttribute", doc, target, ("name", Plain(name))), true);
            }
            case TreeEditKind.SubdocAdd:
            {
                var candidates = types.Where(t => t.Node is ArrayNode or MapNode).ToList();
                var (node, target, _) = candidates[e.Target % candidates.Count];
                var guid = $"g{guidSeq++}";
                var step = new JsonObject { ["step"] = "subdoc.add", ["doc"] = doc, ["target"] = target, ["guid"] = guid };
                if (node is ArrayNode a)
                {
                    var pos = e.Permille * a.Items.Count / 1000;
                    a.Items.Insert(pos, new SubdocNode(guid));
                    step["at"] = pos;
                }
                else
                {
                    var key = Keys[e.Pick % Keys.Length];
                    ((MapNode)node).Entries[key] = new SubdocNode(guid);
                    step["key"] = key;
                }
                // the options a subdocument carries in its content (ContentDoc): only non-default ones are written
                switch (e.Pick % 4)
                {
                    case 1: step["opts"] = new JsonObject { ["autoLoad"] = true }; break;
                    case 2: step["opts"] = new JsonObject { ["shouldLoad"] = false, ["meta"] = Words[e.Pick % Words.Length] }; break;
                    case 3: step["opts"] = new JsonObject { ["collectionid"] = "c" + (e.Pick % 3) }; break;
                }
                return (step, true);
            }
            case TreeEditKind.SubdocLoad:
            {
                var guids = tree.Subdocs();
                if (guids.Count == 0) return null;
                return (new JsonObject { ["step"] = "subdoc.load", ["doc"] = doc, ["guid"] = guids[e.Target % guids.Count] }, false);
            }
            default:
                throw new InvalidOperationException(kind.ToString());
        }
    }

    /// <summary>The plan as a stored (<c>schema: 1</c>) ops script.</summary>
    public JsonObject ToScript()
    {
        var steps = new JsonArray();
        var guidSeq = 0;

        if (Observe)
            for (var r = 0; r < Replicas; r++)
            {
                var doc = Names[r];
                foreach (var (root, kind) in new[] { (TextRoot, "text"), (ArrayRoot, "array"), (MapRoot, "map"), (XmlRoot, "xmlFragment") })
                    steps.Add(new JsonObject { ["step"] = "observe", ["doc"] = doc, ["target"] = Target(root, kind), ["name"] = $"o-{doc}-{root}" });
                foreach (var (root, kind) in new[] { (ArrayRoot, "array"), (MapRoot, "map"), (XmlRoot, "xmlFragment") })
                    steps.Add(new JsonObject { ["step"] = "observeDeep", ["doc"] = doc, ["target"] = Target(root, kind), ["name"] = $"d-{doc}-{root}" });
                steps.Add(new JsonObject { ["step"] = "subdoc.observe", ["doc"] = doc, ["name"] = $"s-{doc}" });
            }

        var trees = new Tree[Replicas];
        trees[0] = new Tree();
        Emit(steps, Base.Select(e => e with { Replica = 0 }), trees, ref guidSeq);

        // every replica receives the base: one update event each when it held anything
        var baseUpdates = trees[0].Updates;
        for (var r = 1; r < Replicas; r++)
        {
            steps.Add(new JsonObject { ["step"] = "sync", ["from"] = Names[0], ["to"] = Names[r] });
            trees[r] = trees[0].Clone();
            trees[r].Updates = baseUpdates > 0 ? 1 : 0;
        }

        Emit(steps, Edits.Select(e => e with { Replica = Math.Min(e.Replica, Replicas - 1) }), trees, ref guidSeq);
        AppendExchange(steps, Replicas, trees.Select(t => t.Updates).ToArray(), Exchange, ExchangeSeed, Reorder, Duplicate, Drop, V2);

        if (guidSeq > 0)
            for (var r = 0; r < Replicas; r++)
                steps.Add(new JsonObject { ["step"] = "subdoc.inspect", ["doc"] = Names[r], ["name"] = $"subdocs-{Names[r]}" });

        return new JsonObject
        {
            ["schema"] = 1,
            ["replicas"] = new JsonArray(Enumerable.Range(0, Replicas).Select(i =>
                (JsonNode)new JsonObject { ["name"] = Names[i], ["clientId"] = 101 + i, ["gc"] = Gc }).ToArray()),
            ["steps"] = steps,
        };
    }

    /// <summary>Renders edits in order; an edit with <c>JoinPrev</c> goes into the previous edit's <c>transact</c> when the replica is the same.</summary>
    private void Emit(JsonArray steps, IEnumerable<TreeEdit> edits, Tree[] trees, ref int guidSeq)
    {
        JsonArray? open = null;
        var openReplica = -1;
        var openCounts = false;

        void Flush()
        {
            if (open == null) return;
            steps.Add(open.Count == 1
                ? open[0]!.DeepClone()
                : new JsonObject { ["step"] = "transact", ["doc"] = Names[openReplica], ["steps"] = open.DeepClone() });
            if (openCounts) trees[openReplica].Updates++;
            open = null;
        }

        foreach (var e in edits)
        {
            if (Render(e, trees[e.Replica], Names[e.Replica], ref guidSeq) is not { } rendered) continue;
            // a subdocument joins Doc.subdocs when the transaction that added it ends, so a load never shares the transaction of an add
            var join = e.JoinPrev && rendered.Counts;
            if (!(join && open != null && openReplica == e.Replica))
            {
                Flush();
                open = new JsonArray();
                openReplica = e.Replica;
                openCounts = false;
            }
            open!.Add(rendered.Step);
            openCounts |= rendered.Counts;
        }
        Flush();
    }
}
