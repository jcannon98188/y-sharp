#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using YSharp.Lib0;

namespace YSharp;

// ---------------------------------------------------------------- awareness (y-protocols)

/// <summary>The <c>{ added, updated, removed }</c> of a y-protocols awareness <c>change</c> or <c>update</c> event, in the order met.</summary>
public readonly struct AwarenessChange
{
    internal AwarenessChange(IReadOnlyList<ulong> added, IReadOnlyList<ulong> updated, IReadOnlyList<ulong> removed)
    {
        Added = added;
        Updated = updated;
        Removed = removed;
    }

    public IReadOnlyList<ulong> Added { get; }
    public IReadOnlyList<ulong> Updated { get; }
    public IReadOnlyList<ulong> Removed { get; }
}

public readonly record struct AwarenessMeta(long Clock, DateTimeOffset LastUpdated);

public delegate void AwarenessHandler(AwarenessChange change, object? origin);   // origin: "local", "timeout" or the caller's

/// <summary>
/// y-protocols <c>Awareness</c> (awareness.js, AW-1..AW-52): per-client JSON states with clocks, the wire update, and the 3000 ms check timer
/// that renews the local state after 15 s and drops remote clients after 30 s, all on the injected <see cref="TimeProvider"/> (ADR 0014).
/// It has its own re-entrant exclusive lock and never takes the document's (CC-19); events fire synchronously under that lock, on the
/// calling thread or the timer's. A handler's exception is reported through the document's <see cref="Doc.ObserverFaulted"/> (ADR 0005).
/// </summary>
public sealed class Awareness : IDisposable
{
    public static readonly TimeSpan OutdatedTimeout = TimeSpan.FromMilliseconds(30000);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(3000);   // JS: Math.floor(outdatedTimeout / 10)
    private const long RenewAfterMs = 15000;                                           // JS: outdatedTimeout / 2

    private readonly object _gate = new();   // Monitor, re-entrant; never the document lock (CC-5, CC-19)
    private readonly Doc _doc;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly OrderedDictionary<ulong, YValue> _states = new();       // JS Map: insertion order, a re-set keeps its place
    private readonly OrderedDictionary<ulong, AwarenessMeta> _meta = new();
    private bool _disposed;

    public Awareness(Doc doc, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        _doc = doc;
        _time = timeProvider ?? TimeProvider.System;
        ClientId = doc.ClientId;
        SetLocalState(YValue.Object(ReadOnlySpan<KeyValuePair<string, YValue>>.Empty));
        doc.Destroyed += OnDocDestroyed;
        _timer = _time.CreateTimer(static s => ((Awareness)s!).Check(), this, CheckInterval, CheckInterval);
    }

    /// <summary>The document's client id when the awareness was made; it does not follow later changes (AW-3).</summary>
    public ulong ClientId { get; }

    /// <summary>Yjs <c>getLocalState()</c>: the local state, or Null when it was removed or is falsy (JS <c>|| null</c>, AW-7).</summary>
    public YValue LocalState
    {
        get
        {
            lock (_gate) return LocalStateCore();
        }
    }

    public event AwarenessHandler? Change, Update;
    public event Action? Destroyed;

    /// <summary>Yjs <c>setLocalState</c> (AW-8..AW-10): Null removes; every call bumps the local clock and emits <c>update</c>.</summary>
    public void SetLocalState(in YValue state)
    {
        if (state.Kind == YValueKind.Undefined) throw new ArgumentException("Undefined is not an awareness state; pass Null to remove it.", nameof(state));
        lock (_gate) SetLocalStateCore(state);
    }

    /// <summary>Yjs <c>setLocalStateField</c> (AW-12): <c>{ ...state, [field]: value }</c>, or nothing when there is no local state.</summary>
    public void SetLocalStateField(string field, in YValue value)
    {
        ArgumentNullException.ThrowIfNull(field);
        lock (_gate)
        {
            var state = LocalStateCore();
            if (state.Kind == YValueKind.Null) return;
            SetLocalStateCore(Spread(state, field, value));
        }
    }

    /// <summary>The states, as a snapshot: a map in hand never changes (AW-13, ADR 0008). Ordered as JS's Map.</summary>
    public IReadOnlyDictionary<ulong, YValue> GetStates()
    {
        lock (_gate) return new OrderedDictionary<ulong, YValue>(_states);
    }

    /// <summary>The clock and last update time of every client ever seen, removed ones included (AW-4), as a snapshot.</summary>
    public IReadOnlyDictionary<ulong, AwarenessMeta> Meta
    {
        get
        {
            lock (_gate) return new OrderedDictionary<ulong, AwarenessMeta>(_meta);
        }
    }

    /// <summary>Yjs <c>destroy</c> (AW-22): emits <c>destroy</c>, removes the local state and stops the timer. A second call does nothing (AW-24).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _doc.Destroyed -= OnDocDestroyed;
            if (Destroyed is { } handlers)
                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                {
                    try
                    {
                        handler();
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        _doc.ReportObserverFault(e, this);
                    }
                }
            SetLocalStateCore(YValue.Null);
            _timer.Dispose();
        }
    }

    /// <summary>Yjs <c>removeAwarenessStates</c> (AW-25..AW-29): removes the clients that have a state; only the local clock moves.</summary>
    public void RemoveStates(ReadOnlySpan<ulong> clients, object? origin)
    {
        lock (_gate) RemoveStatesCore(clients, origin);
    }

    /// <summary>Every client with a state, in the order of <see cref="GetStates"/>.</summary>
    public void EncodeUpdate(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        lock (_gate)
        {
            var clients = new ulong[_states.Count];
            _states.Keys.CopyTo(clients, 0);
            var status = TryEncodeCore(writer, clients, null);
            if (!status.Ok) throw new YException($"The awareness states could not be encoded ({status.Error}).");
        }
    }

    /// Entries in the order given, duplicates written twice, an empty list is the single byte 0 (AW-31). An id with no meta fails (AW-32, ADR 0014).
    /// `states` substitutes the state map but not the meta (AW-33).
    public YStatus TryEncodeUpdate(IBufferWriter<byte> writer, ReadOnlySpan<ulong> clients, IReadOnlyDictionary<ulong, YValue>? states = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        lock (_gate) return TryEncodeCore(writer, clients, states);
    }

    /// <summary>
    /// Yjs <c>applyAwarenessUpdate</c> (AW-41..AW-52). The whole batch is decoded before anything applies, so a malformed one changes
    /// nothing and emits nothing (AW-51, ADR 0005); bytes after the declared entries are ignored (AW-39).
    /// </summary>
    public ApplyResult TryApplyUpdate(ReadOnlySpan<byte> update, object? origin)
    {
        var error = Decode(update, _doc.Limits, out var entries);
        if (error != YError.None) return new ApplyResult(error, 0, 0);
        lock (_gate)
        {
            var timestamp = NowMs();
            var added = new List<ulong>();
            var updated = new List<ulong>();
            var filteredUpdated = new List<ulong>();
            var removed = new List<ulong>();
            foreach (var (clientId, entryClock, state) in entries)
            {
                var clock = entryClock;
                var known = _meta.TryGetValue(clientId, out var clientMeta);
                var hasPrev = _states.TryGetValue(clientId, out var prevState);
                var currClock = known ? clientMeta.Clock : 0;
                var isNull = state.Kind == YValueKind.Null;
                if (!(currClock < clock || (currClock == clock && isNull && hasPrev))) continue;
                if (isNull)
                {
                    // never let a remote client remove this local state: keep it and announce it with a higher clock (AW-45)
                    if (clientId == ClientId && LocalStateCore().Kind != YValueKind.Null) clock++;
                    else _states.Remove(clientId);
                }
                else
                {
                    _states[clientId] = state;
                }

                _meta[clientId] = new AwarenessMeta(clock, DateTimeOffset.FromUnixTimeMilliseconds(timestamp));
                if (!known && !isNull)
                {
                    added.Add(clientId);
                }
                else if (known && isNull)
                {
                    removed.Add(clientId);
                }
                else if (!isNull)
                {
                    if (!hasPrev || !EqualityDeep(state, prevState)) filteredUpdated.Add(clientId);
                    updated.Add(clientId);
                }
            }

            var faults = 0;
            if (added.Count > 0 || filteredUpdated.Count > 0 || removed.Count > 0)
                faults += Emit(Change, new AwarenessChange(added, filteredUpdated, removed), origin);
            if (added.Count > 0 || updated.Count > 0 || removed.Count > 0)
                faults += Emit(Update, new AwarenessChange(added, updated, removed), origin);
            return new ApplyResult(YError.None, 0, faults);
        }
    }

    /// <summary>
    /// Yjs <c>modifyAwarenessUpdate</c> (AW-36..AW-40): rewrites every state, null ones included, keeping ids and clocks. Bytes after the
    /// declared entries are dropped. Returning Undefined fails (AW-38); nothing is written unless the whole update succeeds.
    /// </summary>
    public static YStatus TryModifyUpdate(ReadOnlySpan<byte> update, Func<YValue, YValue> modify, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(modify);
        ArgumentNullException.ThrowIfNull(output);
        var error = Decode(update, YLimits.Default, out var entries);
        if (error != YError.None) return new YStatus(error);
        using var w = Lib0Writer.Rent();
        w.WriteVarUint((ulong)entries.Count);
        foreach (var (clientId, clock, state) in entries)
        {
            var modified = modify(state);
            if (modified.Kind == YValueKind.Undefined) return new YStatus(YError.Rejected);
            w.WriteVarUint(clientId);
            w.WriteVarUint((ulong)clock);
            var status = WriteJson(w, modified);
            if (!status.Ok) return status;
        }

        w.CopyTo(output);
        return default;
    }

    // ------------------------------------------------------------------ under the lock

    private YValue LocalStateCore() => _states.TryGetValue(ClientId, out var s) && !IsFalsy(s) ? s : YValue.Null;

    private void SetLocalStateCore(YValue state)
    {
        var clientId = ClientId;
        var clock = _meta.TryGetValue(clientId, out var currLocalMeta) ? currLocalMeta.Clock + 1 : 0;
        var hasPrev = _states.TryGetValue(clientId, out var prevState);
        var isNull = state.Kind == YValueKind.Null;
        if (isNull) _states.Remove(clientId);
        else _states[clientId] = state;
        _meta[clientId] = new AwarenessMeta(clock, DateTimeOffset.FromUnixTimeMilliseconds(NowMs()));
        var added = new List<ulong>();
        var updated = new List<ulong>();
        var filteredUpdated = new List<ulong>();
        var removed = new List<ulong>();
        if (isNull)
        {
            removed.Add(clientId);
        }
        else if (!hasPrev || prevState.Kind == YValueKind.Null)
        {
            added.Add(clientId);
        }
        else
        {
            updated.Add(clientId);
            if (!EqualityDeep(prevState, state)) filteredUpdated.Add(clientId);
        }

        if (added.Count > 0 || filteredUpdated.Count > 0 || removed.Count > 0)
            Emit(Change, new AwarenessChange(added, filteredUpdated, removed), "local");
        Emit(Update, new AwarenessChange(added, updated, removed), "local");
    }

    private void RemoveStatesCore(ReadOnlySpan<ulong> clients, object? origin)
    {
        var removed = new List<ulong>();
        foreach (var clientId in clients)
        {
            if (!_states.Remove(clientId)) continue;
            if (clientId == ClientId)
            {
                var curMeta = _meta[clientId];
                _meta[clientId] = new AwarenessMeta(curMeta.Clock + 1, DateTimeOffset.FromUnixTimeMilliseconds(NowMs()));
            }

            removed.Add(clientId);
        }

        if (removed.Count == 0) return;
        var change = new AwarenessChange(Array.Empty<ulong>(), Array.Empty<ulong>(), removed);
        Emit(Change, change, origin);
        Emit(Update, change, origin);
    }

    private YStatus TryEncodeCore(IBufferWriter<byte> writer, ReadOnlySpan<ulong> clients, IReadOnlyDictionary<ulong, YValue>? states)
    {
        using var w = Lib0Writer.Rent();
        w.WriteVarUint((ulong)clients.Length);
        foreach (var clientId in clients)
        {
            var found = states is null ? _states.TryGetValue(clientId, out var state) : states.TryGetValue(clientId, out state);
            if (!found || IsFalsy(state)) state = YValue.Null;   // JS: states.get(clientID) || null
            if (!_meta.TryGetValue(clientId, out var meta)) return new YStatus(YError.Rejected);
            w.WriteVarUint(clientId);
            w.WriteVarUint((ulong)meta.Clock);
            var status = WriteJson(w, state);
            if (!status.Ok) return status;
        }

        w.CopyTo(writer);
        return default;
    }

    /// <summary>The check timer's tick (AW-14..AW-20): renews the local state once it is 15 s old, then drops remote clients 30 s old.</summary>
    private void Check()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var now = NowMs();
            var local = LocalStateCore();
            if (local.Kind != YValueKind.Null && RenewAfterMs <= now - _meta[ClientId].LastUpdated.ToUnixTimeMilliseconds())
                SetLocalStateCore(local);
            List<ulong>? remove = null;
            foreach (var (clientId, meta) in _meta)
                if (clientId != ClientId && (long)OutdatedTimeout.TotalMilliseconds <= now - meta.LastUpdated.ToUnixTimeMilliseconds() && _states.ContainsKey(clientId))
                    (remove ??= new List<ulong>()).Add(clientId);
            if (remove is not null) RemoveStatesCore(remove.ToArray(), "timeout");
        }
    }

    private void OnDocDestroyed(Doc _) => Dispose();

    private int Emit(AwarenessHandler? handlers, AwarenessChange change, object? origin)
    {
        if (handlers is null) return 0;
        var faults = 0;
        foreach (var handler in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                handler(change, origin);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                faults++;
                _doc.ReportObserverFault(e, this);
            }
        }

        return faults;
    }

    private long NowMs() => _time.GetUtcNow().ToUnixTimeMilliseconds();   // JS: time.getUnixTime(), whole milliseconds

    // ------------------------------------------------------------------ wire

    /// <summary>Decodes every entry of an awareness update, or fails without a partial result.</summary>
    private static YError Decode(ReadOnlySpan<byte> update, YLimits limits, out List<(ulong Client, long Clock, YValue State)> entries)
    {
        entries = new List<(ulong, long, YValue)>();
        var reader = new Lib0Reader(update, limits.MaxStringBytes);
        if (!reader.TryReadVarUint(out var count)) return Error(reader);
        for (ulong i = 0; i < count; i++)
        {
            if (!reader.TryReadVarUint(out var clientId) || !reader.TryReadVarUint(out var clock) || !reader.TryReadVarString(out var json)) return Error(reader);
            var status = YValue.TryParseJson(Encoding.UTF8.GetBytes(json), limits, out var state);
            if (!status.Ok) return status.Error;
            entries.Add((clientId, (long)clock, state));
        }

        return YError.None;

        static YError Error(in Lib0Reader r) => r.Error == YError.None ? YError.Malformed : r.Error;
    }

    /// <summary>lib0 <c>writeVarString(JSON.stringify(state))</c>: the UTF-8 length, then the text.</summary>
    private static YStatus WriteJson(Lib0Writer w, in YValue state)
    {
        var json = new ArrayBufferWriter<byte>();
        var status = state.TryWriteJson(json);
        if (status.Ok) w.WriteVarBytes(json.WrittenSpan);
        return status;
    }

    // ------------------------------------------------------------------ JS semantics

    /// <summary>JS falsiness: false, 0, -0, NaN, 0n, "", null and undefined.</summary>
    private static bool IsFalsy(in YValue v) => v.Kind switch
    {
        YValueKind.Undefined or YValueKind.Null => true,
        YValueKind.Bool => !v.AsBool(),
        YValueKind.Number => v.AsNumber() is var n && (n == 0 || double.IsNaN(n)),
        YValueKind.BigInt => v.TryGetBigInt(out var b) && b == 0,
        YValueKind.String => v.AsString().Length == 0,
        _ => false,
    };

    /// <summary>
    /// <c>{ ...state, [field]: value }</c>: an object's members, an array's or bytes' elements and a string's UTF-16 units under their index;
    /// other values spread to nothing. An existing key keeps its place; the JSON writer puts index keys first, as JS does.
    /// </summary>
    private static YValue Spread(in YValue state, string field, in YValue value)
    {
        var members = new List<KeyValuePair<string, YValue>>();
        switch (state.Kind)
        {
            case YValueKind.Object:
                state.TryGetObject(out var obj);
                for (var i = 0; i < obj.Count; i++) members.Add(obj[i]);
                break;
            case YValueKind.Array:
                state.TryGetArray(out var items);
                for (var i = 0; i < items.Length; i++) members.Add(new(Index(i), items[i]));
                break;
            case YValueKind.Bytes:
                state.TryGetBytes(out var bytes);
                for (var i = 0; i < bytes.Length; i++) members.Add(new(Index(i), (double)bytes[i]));
                break;
            case YValueKind.String:
                var s = state.AsString();
                for (var i = 0; i < s.Length; i++) members.Add(new(Index(i), s[i].ToString()));
                break;
        }

        members.Add(new(field, value));   // a repeated key keeps its first position and takes this value
        return YValue.Object(members.ToArray());

        static string Index(int i) => i.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// lib0 <c>equalityDeep</c> on JSON-shaped values (ADR 0014): numbers by <c>===</c> (NaN differs from itself, the zeros are equal), object
    /// keys in any order, array items in order. The same container instance is equal to itself, as JS's <c>a === b</c>. Walks on a heap stack.
    /// </summary>
    internal static bool EqualityDeep(in YValue x, in YValue y)
    {
        var stack = new Stack<(YValue A, YValue B)>();
        stack.Push((x, y));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            var kind = a.Kind;
            if (kind != b.Kind) return false;
            switch (kind)
            {
                case YValueKind.Undefined:
                case YValueKind.Null:
                    break;
                case YValueKind.Bool:
                    if (a.AsBool() != b.AsBool()) return false;
                    break;
                case YValueKind.Number:
                    if (a.AsNumber() != b.AsNumber()) return false;
                    break;
                case YValueKind.BigInt:
                    a.TryGetBigInt(out var ba);
                    b.TryGetBigInt(out var bb);
                    if (ba != bb) return false;
                    break;
                case YValueKind.String:
                    if (!string.Equals(a.AsString(), b.AsString(), StringComparison.Ordinal)) return false;
                    break;
                case YValueKind.Bytes:
                    a.TryGetBytes(out var xa);
                    b.TryGetBytes(out var xb);
                    if (!xa.SequenceEqual(xb)) return false;
                    break;
                case YValueKind.Array:
                {
                    if (ReferenceEquals(a.Payload, b.Payload)) break;
                    a.TryGetArray(out var ia);
                    b.TryGetArray(out var ib);
                    if (ia.Length != ib.Length) return false;
                    for (var i = 0; i < ia.Length; i++) stack.Push((ia[i], ib[i]));
                    break;
                }
                default:
                {
                    if (ReferenceEquals(a.Payload, b.Payload)) break;
                    a.TryGetObject(out var oa);
                    b.TryGetObject(out var ob);
                    if (oa.Count != ob.Count) return false;
                    for (var i = 0; i < oa.Count; i++)
                    {
                        var m = oa[i];
                        if (!ob.TryGetValue(m.Key, out var other)) return false;
                        stack.Push((m.Value, other));
                    }
                    break;
                }
            }
        }

        return true;
    }
}
