using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using FsCheck;
using FsCheck.Fluent;
using static YSharp.Interop.Generation.ScriptPlan;

namespace YSharp.Interop.Generation;

public enum ProtocolActionKind
{
    Edit,
    Step1,
    Step2,
    Read,
    ReadHostile,
    WriteUpdate,
    Auth,
    SetLocal,
    SetLocalField,
    Encode,
    Apply,
    ApplyHostile,
    Remove,
    Modify,
    Tick,
    Destroy,
}

/// <summary>One protocol action; <see cref="Pick"/> and <see cref="Seed"/> are resolved against the messages recorded so far when rendered.</summary>
public sealed record ProtocolAction(int Replica, ProtocolActionKind Kind, int Pick, int Seed, bool Flag);

/// <summary>
/// A generated protocol script (Spec 41): replicas exchange <c>sync.*</c> messages (step 1, step 2, update, hostile bytes, appended envelopes,
/// <c>auth.permissionDenied</c>) and, when <see cref="Awareness"/>, run one awareness instance each through local state changes, encodes,
/// applies (of encoded, modified and hand-built updates with chosen clocks), removals, virtual time and destroy. Messages are named results
/// both sides record, and later steps read them by name, so the same bytes flow on each side. With <see cref="Settle"/> a full step 1 and
/// step 2 exchange between every pair ends the script and every replica must hold the same document (SY-14).
/// </summary>
public sealed record ProtocolPlan(int Replicas, int ClientIds, IReadOnlyList<ProtocolAction> Actions, bool Awareness, bool Settle)
{
    /// <summary>
    /// Hand-built awareness updates put their bad entry after good ones, so Node applies the leading entries and y-sharp none: the recorded
    /// divergence AW-51, reached only by the targeting property. By default a bad batch fails on its first entry, where both sides apply nothing.
    /// </summary>
    public bool Partial { get; init; }

    private static readonly int[] TickMs = { 1, 2999, 3000, 3001, 14999, 15000, 30000, 30001 };
    private static readonly string[] Fields = { "user", "cursor", "name", "ü", "n" };
    private static readonly string[] Modes = { "identity", "blank", "setField", "setFieldKeepNull", "undefined" };
    private static readonly string[] Origins = { "peer", "local-ish", "timeout" };
    private static readonly ProtocolActionKind[] SyncKinds =
    {
        ProtocolActionKind.Edit, ProtocolActionKind.Edit, ProtocolActionKind.Step1, ProtocolActionKind.Step1, ProtocolActionKind.Step2,
        ProtocolActionKind.Read, ProtocolActionKind.Read, ProtocolActionKind.Read, ProtocolActionKind.ReadHostile, ProtocolActionKind.WriteUpdate,
        ProtocolActionKind.Auth,
    };
    private static readonly ProtocolActionKind[] AwarenessKinds =
    {
        ProtocolActionKind.SetLocal, ProtocolActionKind.SetLocal, ProtocolActionKind.SetLocalField, ProtocolActionKind.Encode,
        ProtocolActionKind.Encode, ProtocolActionKind.Apply, ProtocolActionKind.Apply, ProtocolActionKind.Apply, ProtocolActionKind.ApplyHostile,
        ProtocolActionKind.ApplyHostile, ProtocolActionKind.Remove, ProtocolActionKind.Modify, ProtocolActionKind.Tick, ProtocolActionKind.Tick,
        ProtocolActionKind.Destroy,
    };

    public const string TextRoot = "t";

    public override string ToString() =>
        $"protocol replicas={Replicas} ids={ClientIds} awareness={Awareness} settle={Settle}{(Partial ? " partial" : "")} actions=[{string.Join(", ", Actions.Select(a => $"{a.Kind}@{a.Replica}/{a.Pick}/{a.Seed}{(a.Flag ? "!" : "")}"))}]";

    public static ProtocolPlan Generate(int caseSeed)
    {
        var rng = new Random(caseSeed);
        var replicas = rng.Next(2, 4);
        var awareness = rng.Next(5) < 3;
        var count = rng.Next(1, 26);
        var actions = new List<ProtocolAction>(count);
        for (var i = 0; i < count; i++)
        {
            var kinds = awareness && rng.Next(3) > 0 ? AwarenessKinds : SyncKinds;
            actions.Add(new ProtocolAction(rng.Next(replicas), kinds[rng.Next(kinds.Length)], rng.Next(1000), rng.Next(1_000_000), rng.Next(4) == 0));
        }
        return new ProtocolPlan(replicas, rng.Next(3), actions, awareness, rng.Next(4) > 0);
    }

    public static Arbitrary<ProtocolPlan> Arbitrary() => Arb.From(Gen.Choose(0, int.MaxValue).Select(Generate), Shrink);

    public static IEnumerable<ProtocolPlan> Shrink(ProtocolPlan plan)
    {
        for (var i = plan.Actions.Count - 1; i >= 0; i--)
            yield return plan with { Actions = plan.Actions.Where((_, j) => j != i).ToList() };
        if (plan.Settle) yield return plan with { Settle = false };
        if (plan.Awareness && plan.Actions.All(a => !AwarenessKinds.Contains(a.Kind))) yield return plan with { Awareness = false };
        if (plan.ClientIds > 0) yield return plan with { ClientIds = 0 };
        if (plan.Replicas > 2 && plan.Actions.All(a => a.Replica < plan.Replicas - 1))
            yield return plan with { Replicas = plan.Replicas - 1 };
        for (var i = 0; i < plan.Actions.Count; i++)
        {
            var a = plan.Actions[i];
            if (a.Flag) yield return plan with { Actions = plan.Actions.Select((x, j) => j == i ? x with { Flag = false } : x).ToList() };
            if (a.Seed > 0) yield return plan with { Actions = plan.Actions.Select((x, j) => j == i ? x with { Seed = x.Seed / 2 } : x).ToList() };
        }
    }

    /// <summary>The replicas' client ids: small, past 32 bits, or at the top of the 53-bit range (AW-34).</summary>
    public long ClientId(int replica) => ClientIds switch
    {
        0 => replica + 1,
        1 => 4_294_967_296L + replica,
        _ => 9_007_199_254_740_991L - replica,
    };

    /// <summary>
    /// A client id no replica has, still within 2^53-1: larger ids are Malformed in y-sharp and lossy in lib0, a recorded divergence
    /// (ADR 0003, quirks-varint) the default generator stays clear of.
    /// </summary>
    private long Unknown(int k) => ClientIds == 2 ? ClientId(Replicas) - k : ClientId(Replicas) + k;

    /// <summary>The tick and the outdated-state check of every awareness is relative to the first <c>awareness.new</c>, which the script makes before any tick.</summary>
    public JsonObject ToScript()
    {
        var steps = new JsonArray();
        var syncMessages = new List<string>();
        // A read that throws may leave a partial reply in Node (writeSyncStep2 writes its type before encoding against a bad state vector)
        // and none in y-sharp; the protocol fixtures leave those bytes undefined, so a reply is a message only when its read cannot throw
        // that way: not of an auth message (type 0 then a string, read as a step 1) and not of hostile bytes.
        var tainted = new HashSet<string>(StringComparer.Ordinal);
        var awarenessMessages = new List<string>();
        var textLength = new int[Replicas];
        var destroyed = new bool[Replicas];
        var n = 0;
        string Doc(int r) => "r" + r;
        string Aw(int r) => "w" + r;
        string Name(string prefix) => prefix + n++;

        if (Awareness)
            for (var r = 0; r < Replicas; r++) steps.Add(new JsonObject { ["step"] = "awareness.new", ["awareness"] = Aw(r), ["doc"] = Doc(r) });

        foreach (var a in Actions)
        {
            var r = a.Replica % Replicas;
            var other = (r + 1 + a.Pick % Math.Max(1, Replicas - 1)) % Replicas;
            var live = Awareness && !destroyed[r];
            switch (a.Kind)
            {
                case ProtocolActionKind.Edit:
                {
                    var target = new JsonObject { ["root"] = TextRoot, ["kind"] = "text" };
                    if (a.Flag && textLength[r] > 0)
                    {
                        var at = a.Pick % textLength[r];
                        var length = 1 + a.Seed % (textLength[r] - at);
                        steps.Add(new JsonObject { ["step"] = "text.delete", ["doc"] = Doc(r), ["target"] = target, ["at"] = at, ["len"] = length });
                        textLength[r] -= length;
                    }
                    else
                    {
                        var word = Words[a.Seed % Words.Length];
                        steps.Add(new JsonObject { ["step"] = "text.insert", ["doc"] = Doc(r), ["target"] = target, ["at"] = a.Pick % (textLength[r] + 1), ["value"] = word });
                        textLength[r] += word.Length;
                    }
                    break;
                }
                case ProtocolActionKind.Step1 or ProtocolActionKind.Step2:
                {
                    var name = Name("m");
                    var step = new JsonObject { ["step"] = a.Kind == ProtocolActionKind.Step1 ? "sync.step1" : "sync.step2", ["name"] = name, ["doc"] = Doc(r) };
                    if (a.Flag && syncMessages.Count > 0)
                    {
                        var prefix = syncMessages[a.Seed % syncMessages.Count];
                        step["append"] = prefix;   // the earlier message is written first, so it is what a reader reads
                        if (tainted.Contains(prefix)) tainted.Add(name);
                    }
                    steps.Add(step);
                    syncMessages.Add(name);
                    break;
                }
                case ProtocolActionKind.Read:
                {
                    if (syncMessages.Count == 0) break;
                    var name = Name("read");
                    var from = syncMessages[a.Seed % syncMessages.Count];
                    var step = new JsonObject { ["step"] = "sync.read", ["name"] = name, ["doc"] = Doc(other), ["from"] = from };
                    if (a.Flag) step["origin"] = Origins[a.Pick % Origins.Length];
                    steps.Add(step);
                    if (!tainted.Contains(from)) syncMessages.Add(name);   // its reply, possibly empty
                    break;
                }
                case ProtocolActionKind.ReadHostile:
                {
                    var name = Name("hostile");
                    steps.Add(new JsonObject { ["step"] = "sync.read", ["name"] = name, ["doc"] = Doc(r), ["bytes"] = BytesNode(HostileSync(a.Seed)) });
                    break;
                }
                case ProtocolActionKind.WriteUpdate:
                {
                    var name = Name("upd");
                    steps.Add(new JsonObject { ["step"] = "sync.update", ["name"] = name, ["update"] = BytesNode(Garbage(a.Seed, 8)) });
                    syncMessages.Add(name);
                    break;
                }
                case ProtocolActionKind.Auth:
                {
                    var name = Name("auth");
                    steps.Add(new JsonObject { ["step"] = "auth.permissionDenied", ["name"] = name, ["reason"] = Words[a.Seed % Words.Length] });
                    syncMessages.Add(name);
                    tainted.Add(name);
                    break;
                }
                case ProtocolActionKind.SetLocal when live:
                    steps.Add(new JsonObject { ["step"] = "awareness.setLocal", ["awareness"] = Aw(r), ["state"] = a.Flag && a.Seed % 3 == 0 ? null : Tag(State(a.Seed)) });
                    break;
                case ProtocolActionKind.SetLocalField when live:
                    steps.Add(new JsonObject { ["step"] = "awareness.setLocalField", ["awareness"] = Aw(r), ["field"] = Fields[a.Pick % Fields.Length], ["value"] = Tag(Value(a.Seed)) });
                    break;
                case ProtocolActionKind.Encode when live:
                {
                    var name = Name("aw");
                    var clients = Clients(a.Pick, includeUnknown: a.Seed % 7 == 0);
                    var step = new JsonObject { ["step"] = "awareness.encode", ["name"] = name, ["awareness"] = Aw(r), ["clients"] = new JsonArray(clients.Select(c => (JsonNode)c).ToArray()) };
                    if (a.Flag) step["states"] = new JsonArray(clients.Distinct().Select((c, i) => (JsonNode)new JsonArray(c, Tag(State(a.Seed + i)))).ToArray());
                    steps.Add(step);
                    // an encode naming a client this instance has no meta for throws (AW-32), leaving no bytes to read later
                    if (a.Seed % 7 != 0) awarenessMessages.Add(name);
                    break;
                }
                case ProtocolActionKind.Apply when live:
                {
                    if (awarenessMessages.Count == 0) break;
                    steps.Add(new JsonObject
                    {
                        ["step"] = "awareness.applyUpdate", ["awareness"] = Aw(other), ["from"] = awarenessMessages[a.Seed % awarenessMessages.Count],
                        ["origin"] = Origins[a.Pick % Origins.Length], ["name"] = Name("apply"),
                    });
                    break;
                }
                case ProtocolActionKind.ApplyHostile when live:
                    steps.Add(new JsonObject
                    {
                        ["step"] = "awareness.applyUpdate", ["awareness"] = Aw(r), ["bytes"] = BytesNode(HandBuiltAwareness(a.Seed, a.Flag, Partial)),
                        ["origin"] = Origins[a.Pick % Origins.Length], ["name"] = Name("apply"),
                    });
                    break;
                case ProtocolActionKind.Remove when live:
                {
                    var step = new JsonObject { ["step"] = "awareness.removeStates", ["awareness"] = Aw(r), ["clients"] = new JsonArray(Clients(a.Pick, a.Flag).Select(c => (JsonNode)c).ToArray()) };
                    if (a.Seed % 2 == 0) step["origin"] = Origins[a.Seed % Origins.Length];
                    steps.Add(step);
                    break;
                }
                case ProtocolActionKind.Modify when Awareness:
                {
                    if (awarenessMessages.Count == 0) break;
                    var name = Name("mod");
                    var mode = Modes[a.Pick % Modes.Length];
                    var step = new JsonObject { ["step"] = "awareness.modify", ["name"] = name, ["mode"] = mode, ["from"] = awarenessMessages[a.Seed % awarenessMessages.Count] };
                    if (mode.StartsWith("setField", StringComparison.Ordinal))
                    {
                        step["field"] = Fields[a.Seed % Fields.Length];
                        step["value"] = Tag(Value(a.Seed / 7));
                    }
                    steps.Add(step);
                    if (mode != "undefined") awarenessMessages.Add(name);   // AW-38: an undefined result cannot be encoded
                    break;
                }
                case ProtocolActionKind.Tick when Awareness:
                    steps.Add(new JsonObject { ["step"] = "tick", ["ms"] = a.Flag ? a.Seed % 40_000 : TickMs[a.Pick % TickMs.Length] });
                    break;
                case ProtocolActionKind.Destroy when live:
                    // only once per instance: destroy is not idempotent in Node (AW-24) and a destroyed instance is not used again
                    steps.Add(new JsonObject { ["step"] = "awareness.destroy", ["awareness"] = Aw(r) });
                    destroyed[r] = true;
                    break;
            }
        }

        if (Settle)
            for (var i = 0; i < Replicas; i++)
            for (var j = 0; j < Replicas; j++)
            {
                if (i == j) continue;
                var s1 = Name("settle");
                var s2 = Name("settle");
                steps.Add(new JsonObject { ["step"] = "sync.step1", ["name"] = s1, ["doc"] = Doc(i) });
                steps.Add(new JsonObject { ["step"] = "sync.read", ["name"] = s2, ["doc"] = Doc(j), ["from"] = s1 });
                steps.Add(new JsonObject { ["step"] = "sync.read", ["name"] = Name("settle"), ["doc"] = Doc(i), ["from"] = s2 });
            }

        // every replica addresses the text root, so each dump covers it even when no edit landed there
        for (var r = 0; r < Replicas; r++)
            steps.Insert(0, new JsonObject { ["step"] = "text.insert", ["doc"] = Doc(r), ["target"] = new JsonObject { ["root"] = TextRoot, ["kind"] = "text" }, ["at"] = 0, ["value"] = "" });

        return new JsonObject
        {
            ["schema"] = 1,
            ["replicas"] = new JsonArray(Enumerable.Range(0, Replicas).Select(r => (JsonNode)new JsonObject { ["name"] = Doc(r), ["clientId"] = ClientId(r) }).ToArray()),
            ["steps"] = steps,
        };
    }

    /// <summary>Whether bytes no document wrote may reach one, so pending structs can keep the replicas apart after settling.</summary>
    public bool Hostile => Actions.Any(a => a.Kind is ProtocolActionKind.ReadHostile or ProtocolActionKind.WriteUpdate or ProtocolActionKind.Auth);

    /// <summary>Client ids for an encode or removal: a non-empty subset of the replicas', in a chosen order, maybe repeated, maybe one nobody has.</summary>
    private List<long> Clients(int pick, bool includeUnknown)
    {
        var ids = new List<long>();
        for (var r = 0; r < Replicas; r++)
            if ((pick >> r & 1) == 1) ids.Add(ClientId(r));
        if (ids.Count == 0) ids.Add(ClientId(pick % Replicas));
        if ((pick & 64) != 0) ids.Reverse();
        if ((pick & 128) != 0) ids.Add(ids[0]);
        if (includeUnknown) ids.Add(Unknown(100));
        return ids;
    }

    /// <summary>A plain JSON value in the script's tagged form (ADR 0011): objects become <c>{"$":"obj","pairs":[...]}</c>.</summary>
    internal static JsonNode? Tag(JsonNode? v) => v switch
    {
        JsonObject o => new JsonObject
        {
            ["$"] = "obj",
            ["pairs"] = new JsonArray(o.Select(kv => (JsonNode)new JsonArray(JsonValue.Create(kv.Key), Tag(kv.Value))).ToArray()),
        },
        JsonArray a => new JsonArray(a.Select(Tag).ToArray()),
        _ => v?.DeepClone(),
    };

    private static JsonNode BytesNode(byte[] b) => new JsonObject { ["$"] = "bytes", ["hex"] = Convert.ToHexString(b).ToLowerInvariant() };

    /// <summary>A JSON value for a state field: strings (non-ASCII too, AW-35), integers, floats, booleans, null, arrays and objects.</summary>
    internal static JsonNode? Value(int seed) => (seed % 9) switch
    {
        0 => JsonValue.Create(Words[seed / 9 % Words.Length]),
        1 => JsonValue.Create(seed / 9 % 1000 - 500),
        2 => JsonValue.Create(new[] { 1.5, 0.1, 1e21, -2.25, 123456789.125 }[seed / 9 % 5]),
        3 => JsonValue.Create(seed % 2 == 0),
        4 => null,
        5 => new JsonArray(JsonValue.Create(seed % 7), JsonValue.Create(Words[seed % Words.Length])),
        6 => new JsonObject { ["anchor"] = seed % 13, ["head"] = seed % 17 },
        7 => new JsonObject(),
        _ => JsonValue.Create(""),
    };

    /// <summary>A local state: an object of one to three fields.</summary>
    internal static JsonObject State(int seed)
    {
        var o = new JsonObject();
        for (var i = 0; i <= seed % 3; i++) o[Fields[(seed / 3 + i) % Fields.Length]] = Value(seed / 11 + i * 31);
        return o;
    }

    private static byte[] Garbage(int seed, int max)
    {
        var rng = new Random(seed);
        var b = new byte[rng.Next(max + 1)];
        rng.NextBytes(b);
        return b;
    }

    /// <summary>Sync bytes a peer should not send: a known or unknown message type, then a short random tail (SY-19, SY-20).</summary>
    private static byte[] HostileSync(int seed)
    {
        var rng = new Random(seed);
        var w = new List<byte> { (byte)new[] { 0, 1, 2, 3, 0x80 }[rng.Next(5)] };
        switch (rng.Next(4))
        {
            case 0: break;                                                        // type only
            case 1: w.Add((byte)rng.Next(1, 10)); break;                          // a length with no payload
            case 2: w.AddRange(new byte[] { 2, 0, 0 }); break;                    // an empty update (or state vector) payload
            default: w.AddRange(Garbage(seed, 10)); break;
        }
        return w.ToArray();
    }

    /// <summary>
    /// An awareness update built by hand, so clocks can be chosen (AW-41..52): entries for the replicas' or other ids, clocks 0..4, states
    /// that are objects, <c>null</c> or other JSON. A bad entry (not JSON) or a cut (<paramref name="truncate"/>, AW-51) falls in the first
    /// entry, so neither side applies anything; with <paramref name="partial"/> it falls after a good entry instead (the AW-51 divergence).
    /// </summary>
    private byte[] HandBuiltAwareness(int seed, bool truncate, bool partial)
    {
        var rng = new Random(seed);
        var w = new List<byte>();
        var count = partial ? rng.Next(2, 4) : rng.Next(4);
        var badAt = partial ? count - 1 : truncate || rng.Next(3) > 0 ? -1 : 0;
        var firstEnd = 0;
        WriteVarUint(w, (ulong)count);
        for (var i = 0; i < count; i++)
        {
            WriteVarUint(w, (ulong)(rng.Next(5) == 0 ? Unknown(7) : ClientId(rng.Next(Replicas))));
            WriteVarUint(w, (ulong)rng.Next(5));
            var json = i == badAt && !truncate ? "{" : rng.Next(5) switch
            {
                0 => "null",
                1 => "\"text\"",
                2 => "[1,2]",
                _ => State(rng.Next(1_000_000)).ToJsonString(Relaxed),
            };
            var utf8 = Encoding.UTF8.GetBytes(json);
            WriteVarUint(w, (ulong)utf8.Length);
            w.AddRange(utf8);
            if (i == 0) firstEnd = w.Count;
        }
        if (truncate && count > 0)
        {
            // default: cut inside the first entry; partial: cut after the first entry is complete
            var keep = partial ? rng.Next(firstEnd, w.Count) : rng.Next(1, firstEnd);
            w.RemoveRange(keep, w.Count - keep);
        }
        else if (rng.Next(4) == 0) w.Add(0x2a);   // trailing bytes are ignored (AW-39)
        return w.ToArray();
    }

    private static readonly System.Text.Json.JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static void WriteVarUint(List<byte> w, ulong v)
    {
        while (v > 0x7f)
        {
            w.Add((byte)(0x80 | (v & 0x7f)));
            v >>= 7;
        }
        w.Add((byte)v);
    }
}
