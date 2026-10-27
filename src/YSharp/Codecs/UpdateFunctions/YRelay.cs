#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using YSharp.Codecs;
using YSharp.Codecs.V1;
using YSharp.Integration;
using YSharp.Lib0;
using YSharp.Store;

namespace YSharp;

/// <summary>
/// A document held as its merged update, with no <see cref="Doc"/> and no item graph: the relay tier of a server (ADR 0024 SRV-1). It folds
/// updates in as Yjs <c>mergeUpdates</c> would merge them and answers what a peer is missing as Yjs <c>diffUpdate</c> would, so a server can
/// receive, store and fan out updates without integrating them.
/// <para>
/// Every update is decoded and validated in full against <see cref="Limits"/> before anything changes (ADR 0005), with the strict decoding of
/// the update functions (<see cref="YUpdate"/>); a rejected update leaves the relay as it was. The merged state keeps every struct it was given,
/// including the content of deleted items and structs that wait on a gap, which a <see cref="Doc"/> would collect or hold back: a document
/// that applies <see cref="EncodeStateAsUpdate(IBufferWriter{byte}, ReadOnlySpan{byte}, UpdateFormat)"/> ends where one that applied every
/// input ends. The merged state grows with the history; compaction and storage belong to the server tier (ADR 0024 SRV-10).
/// </para>
/// <para>
/// What the relay holds but cannot yet pass on as part of the document is bounded by <see cref="YLimits.MaxPendingBytes"/>, as a
/// <see cref="Doc"/>'s pending state is (ADR 0005): the structs past each client's first gap and the deleted ranges past each client's state,
/// measured as their V1 encoding. An update that would take that above the limit is rejected with LimitExceeded and changes nothing (ADR 0024
/// RLY-7).
/// </para>
/// <para>Every member is safe to call from any thread. The relay has its own lock and never takes a document lock.</para>
/// </summary>
/// <remarks>
/// The merged state is held per client (TASK-9). When the held state and the update both list their clients in descending order, one block
/// each, as every Yjs writer does, the merge of the two is the merge of each client on its own, so a fold touches only the update's clients.
/// Within a client whose held structs are regular (contiguous, gaps filled by single Skips, no Skip first or last), the merge passes the held
/// structs through unchanged, so it resumes at the end of the held run the update's first clock falls in (see <see cref="Fold"/>): appending
/// costs the update, not the state. The pending size and the state vector are kept per client along with it. Any other input, a client order
/// a Yjs writer never produces, or a merge whose result is not regular, folds into one flat list as before, and the relay goes back to the
/// per-client form once the flat list allows it. Both forms give the same bytes.
/// </remarks>
public sealed class YRelay
{
    private readonly object _gate = new();   // a plain monitor, as ADR 0006 CC-5 requires of every lock in the library
    private readonly DeleteRanges _deletes = new();
    private readonly Dictionary<ulong, ClientStructs> _clients = new();
    private readonly List<ulong> _order = new();                      // the clients with structs, descending
    private readonly Dictionary<ulong, long> _pendingDeletes = new();   // per client, the V1 bytes of its deleted ranges past its state, when not 0
    private long _pendingStructBytes;
    private int _pendingStructClients;
    private long _pendingDeleteBytes;
    private List<StructRef>? _flat;                                     // the state as one merged list, while it is not in the per-client form
    private StateVector? _stateVector;                                  // null: not built since the last change
    private long _version;
    private long _merged;                                               // held structs the last prepare merged again
    private PreparedUpdate? _lastCommitted;

    /// <param name="limits">Limits every incoming update is checked against; <see cref="YLimits.Default"/> when null.</param>
    public YRelay(YLimits? limits = null) => Limits = limits ?? YLimits.Default;

    public YLimits Limits { get; }

    /// <summary>True until an update with a struct or a deleted range has been folded in.</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_gate) return (_flat?.Count ?? _order.Count) == 0 && _deletes.Count == 0;
        }
    }

    /// <summary>
    /// Validates <paramref name="update"/> and folds it into the merged state (Yjs <c>mergeUpdates([state, update])</c>): structs the relay
    /// already holds are dropped, overlaps are sliced, gaps become Skips and the delete sets are merged. A duplicate or out-of-order update is
    /// accepted. An invalid or over-limit update returns its error and changes nothing. The same as <see cref="TryPrepareUpdate"/> then
    /// <see cref="Commit"/>, in one step.
    /// </summary>
    public YStatus TryApplyUpdate(ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1)
    {
        var error = YUpdate.DecodeForFunctions(update, format, Limits, out var refs, out var structs);
        if (error != YError.None) return new YStatus(error);
        lock (_gate)
        {
            var prepared = Prepare(refs, structs);
            if (prepared is null) return new YStatus(YError.LimitExceeded);
            Commit(prepared);
        }

        return default;
    }

    /// <summary>
    /// The first half of <see cref="TryApplyUpdate"/>: validates <paramref name="update"/> and computes the merged state it would leave without
    /// changing the relay, so a server can persist the update before the relay serves it (ADR 0024 SRV-8). The update's bytes are not kept.
    /// An invalid or over-limit update returns its error and a null <paramref name="prepared"/>.
    /// </summary>
    public YStatus TryPrepareUpdate(ReadOnlySpan<byte> update, UpdateFormat format, out PreparedUpdate? prepared)
    {
        prepared = null;
        var error = YUpdate.DecodeForFunctions(update, format, Limits, out var refs, out var structs);
        if (error != YError.None) return new YStatus(error);
        lock (_gate) prepared = Prepare(refs, structs);
        return prepared is null ? new YStatus(YError.LimitExceeded) : default;
    }

    /// <summary>
    /// <see cref="TryPrepareUpdate(ReadOnlySpan{byte}, UpdateFormat, out PreparedUpdate?)"/> for an update already decoded with
    /// <see cref="YUpdate.TryDecode"/>, so a server that decodes a peer's update once can validate, store and prepare it from that (TASK-14).
    /// LimitExceeded when the update breaks this relay's limits or was decoded under a looser string or nesting bound than this relay's.
    /// </summary>
    public YStatus TryPrepareUpdate(DecodedUpdate update, out PreparedUpdate? prepared)
    {
        ArgumentNullException.ThrowIfNull(update);
        prepared = null;
        if (!update.Within(Limits)) return new YStatus(YError.LimitExceeded);
        lock (_gate) prepared = Prepare(update.Refs, update.InReadOrder);
        return prepared is null ? new YStatus(YError.LimitExceeded) : default;
    }

    /// <summary>
    /// Folds in an update <see cref="TryPrepareUpdate"/> prepared. Throws <see cref="InvalidOperationException"/> when it was prepared by another
    /// relay, was already committed, or was prepared before another update changed this relay: prepare and commit run one update at a time, as
    /// a server's per-document mailbox runs them.
    /// </summary>
    public void Commit(PreparedUpdate prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        lock (_gate)
        {
            if (!ReferenceEquals(prepared.Relay, this)) throw new InvalidOperationException("The update was prepared by another relay.");
            if (prepared.Version != _version) throw new InvalidOperationException("The relay changed after the update was prepared, or the update was already committed.");
            if (prepared.Flat is { } flat) CommitFlat(prepared, flat);
            else CommitPerClient(prepared);
            _version++;
            _lastCommitted = prepared;
        }
    }

    /// <summary>An update <see cref="TryPrepareUpdate"/> validated and merged, waiting for <see cref="Commit"/>.</summary>
    public sealed class PreparedUpdate
    {
        private StateVector? _stateVector;

        internal PreparedUpdate(YRelay relay, long version, List<DeleteRange> deletes)
        {
            Relay = relay;
            Version = version;
            Deletes = deletes;
        }

        /// <summary>
        /// The relay's state vector once the update is committed. It is built on first read, which must come before the relay commits another
        /// update; after that it throws <see cref="InvalidOperationException"/>.
        /// </summary>
        public StateVector StateVector
        {
            get
            {
                lock (Relay._gate) return _stateVector ??= Relay.StateVectorAfter(this);
            }
        }

        internal YRelay Relay { get; }

        internal long Version { get; }

        internal List<DeleteRange> Deletes { get; }

        /// <summary>The whole merged list, when the update folds in the flat form.</summary>
        internal List<StructRef>? Flat { get; init; }

        internal StateVector? FlatStateVector
        {
            init => _stateVector = value;
        }

        internal List<ClientChange> Changes { get; init; } = new();

        internal List<(ulong Client, long Bytes)> DeleteChanges { get; init; } = new();

        internal long PendingStructBytes { get; init; }

        internal int PendingStructClients { get; init; }

        internal long PendingDeleteBytes { get; init; }
    }

    /// <summary>
    /// Writes what the relay holds beyond <paramref name="targetStateVector"/> (a V1 state vector; empty means everything), followed by the
    /// whole delete set, as Yjs <c>diffUpdate</c> writes it for the merged state. Everything is written as merged, Skips included; with a target,
    /// a client's first missing struct is sliced at the target's clock. Returns Malformed, and writes nothing, when the state vector is malformed.
    /// </summary>
    public YStatus TryEncodeStateAsUpdate(IBufferWriter<byte> writer, ReadOnlySpan<byte> targetStateVector = default, UpdateFormat format = UpdateFormat.V1)
    {
        ArgumentNullException.ThrowIfNull(writer);
        StateVector? target = null;
        if (!targetStateVector.IsEmpty)
        {
            var status = StateVector.TryDecode(targetStateVector, out target);
            if (!status.Ok) return status;
        }

        lock (_gate)
        {
            List<StructRef> structs;
            if (_flat is { } flat) structs = target is null ? flat : UpdateMerge.Diff(flat, target);
            else structs = target is null ? Flatten() : DiffPerClient(target);
            YUpdate.Write(format, structs, YUpdate.InWriteOrder(_deletes), writer);
        }

        return default;
    }

    /// <summary>The throwing <see cref="TryEncodeStateAsUpdate"/>: throws <see cref="YDecodeException"/> when the state vector is malformed.</summary>
    public void EncodeStateAsUpdate(IBufferWriter<byte> writer, ReadOnlySpan<byte> targetStateVector = default, UpdateFormat format = UpdateFormat.V1)
    {
        var status = TryEncodeStateAsUpdate(writer, targetStateVector, format);
        if (!status.Ok) throw new YDecodeException(status.Error);
    }

    public byte[] EncodeStateAsUpdate(ReadOnlySpan<byte> targetStateVector = default, UpdateFormat format = UpdateFormat.V1)
    {
        var writer = new ArrayBufferWriter<byte>();
        EncodeStateAsUpdate(writer, targetStateVector, format);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The state vector of the merged state (Yjs <c>encodeStateVectorFromUpdate</c> of it): per client, the end of the run of structs from
    /// clock 0 up to the first gap. A client whose structs do not start at 0 is not listed. Unlike a <see cref="Doc"/>, the relay counts items
    /// whose origin or parent is still missing, because it holds them and will pass them on.
    /// </summary>
    public StateVector GetStateVector()
    {
        lock (_gate) return _stateVector ??= BuildStateVector(null);
    }

    /// <summary>Writes <see cref="GetStateVector"/> in the V1 encoding (the sync protocol's step 1 payload).</summary>
    public void EncodeStateVector(IBufferWriter<byte> writer) => GetStateVector().Encode(writer);

    /// <summary>The V1 size of what the relay holds past each client's state (RLY-7), as <see cref="PendingV1Bytes"/> measures it.</summary>
    internal long PendingBytes
    {
        get
        {
            lock (_gate)
            {
                if (_flat is { } flat) return PendingV1Bytes(flat, _stateVector!, _deletes, new List<DeleteRange>());
                return PendingTotal(_pendingStructClients, _pendingStructBytes, _pendingDeletes.Count, _pendingDeleteBytes);
            }
        }
    }

    /// <summary><see cref="PendingBytes"/> measured from scratch over the whole state, as the flat form measures it (tests compare the two).</summary>
    internal long PendingBytesRecomputed
    {
        get
        {
            lock (_gate) return PendingV1Bytes(_flat ?? Flatten(), _stateVector ??= _flat is null ? BuildStateVector(null) : StateVectorOf(_flat), _deletes, new List<DeleteRange>());
        }
    }

    /// <summary>
    /// The held structs the last prepare merged or copied again: 0 for an update that only appends to its clients' histories, as one in order
    /// does; a client's structs from the gap the update fills on; the whole state when it folds in the flat form, and again when the relay
    /// leaves that form (the fuzz guard's bound).
    /// </summary>
    internal long MergedHeldStructs
    {
        get
        {
            lock (_gate) return _merged;
        }
    }

    /// <summary>True while the state is held as one flat list (tests check the per-client form is the usual one).</summary>
    internal bool IsFlat
    {
        get
        {
            lock (_gate) return _flat is not null;
        }
    }

    // ------------------------------------------------------------------ preparing

    /// <summary>The merged state a decoded update leaves, or null when its pending part would be over the limit (RLY-7). The caller holds the gate.</summary>
    private PreparedUpdate? Prepare(UpdateRefs refs, List<StructRef> structs)
    {
        _merged = 0;
        if (_flat is not null) TryLeaveFlat();
        if (_flat is null && IsCanonical(structs))
        {
            var changes = new List<ClientChange>();
            using var sizer = new Lib0Writer(64);
            var i = 0;
            var regular = true;
            while (i < structs.Count && regular)
            {
                var client = structs[i].Client;
                var start = i;
                while (i < structs.Count && structs[i].Client == client) i++;
                while (start < i && structs[start].Kind == RefKind.Skip) start++;
                if (start == i) continue;   // only Skips, which the merge drops
                var change = Fold(client, structs, start, i, sizer);
                if (change is null) regular = false;
                else changes.Add(change);
            }

            if (regular) return PreparePerClient(refs, changes);
        }

        return PrepareFlat(refs, structs);
    }

    /// <summary>
    /// The merge of one client's held structs with the update's structs <paramref name="start"/> to <paramref name="end"/> of that client, as
    /// the splice it makes in the held list; null when the result is not regular.
    /// <para>
    /// Yjs <c>mergeUpdates</c> takes the struct with the lowest clock at the top of its loop (the held list first on a tie), then copies the
    /// structs that follow it in the same list without looking at the other until it meets a Skip. Over a regular held list that alone, it
    /// writes the list back as it was. So if the update's first clock m is not below the held list's first, everything up to the end of the
    /// held run that m falls in (the run's start is at or below m, so the merge reaches it first) is written unchanged, and the merge is at
    /// the top of its loop with the run's last struct as <c>currWrite</c>. The fold resumes there.
    /// </para>
    /// </summary>
    private ClientChange? Fold(ulong client, List<StructRef> incoming, int start, int end, Lib0Writer sizer)
    {
        _clients.TryGetValue(client, out var held);
        int p;
        List<StructRef> tail;
        if (held is null)
        {
            p = 0;
            tail = UpdateMerge.Merge(new (IReadOnlyList<StructRef>, int, int)[] { (incoming, start, end) }, null);
        }
        else
        {
            var h = held.Structs;
            var m = incoming[start].Clock;
            if (m < h[0].Clock)
            {
                p = 0;
                tail = UpdateMerge.Merge(new (IReadOnlyList<StructRef>, int, int)[] { (h, 0, h.Count), (incoming, start, end) }, null);
                _merged += h.Count;
            }
            else
            {
                var q = LastStartingAtOrBefore(h, m);
                p = h[q].Kind == RefKind.Skip ? q - 1 : NextSkip(held.Skips, q, h.Count) - 1;
                tail = UpdateMerge.Merge(new (IReadOnlyList<StructRef>, int, int)[] { (h, p + 1, h.Count), (incoming, start, end) }, h[p]);
                _merged += h.Count - p - 1;
            }
        }

        if (!IsRegular(tail)) return null;

        var change = new ClientChange(client, held, p, tail);
        var offset = p == 0 ? 0 : held!.Offsets[p];
        for (var k = 0; k < tail.Count; k++)
        {
            offset += V1Size(tail[k], sizer);
            change.TailOffsets.Add(offset);
            if (tail[k].Kind == RefKind.Skip) change.TailSkips.Add(p + k);
        }

        // the state: the clock of the first Skip, or the end, for a client that starts at 0
        var count = p + tail.Count;
        var firstSkip = held is not null && held.Skips.Count > 0 && held.Skips[0] < p ? held.Skips[0]
            : change.TailSkips.Count > 0 ? change.TailSkips[0] : count;
        var first = p > 0 ? held!.Structs[0] : tail[0];
        change.State = first.Clock != 0 ? 0 : firstSkip < count ? change.At(firstSkip).Clock : tail[^1].End;

        // the pending block: the structs from the state on, without the Skip that opens the gap
        var from = change.State == 0 ? 0 : firstSkip;
        if (from < count && change.At(from).Kind == RefKind.Skip) from++;
        var pending = count - from;
        change.PendingBytes = pending == 0 ? 0
            : VarUintLength((ulong)pending) + VarUintLength(client) + VarUintLength((ulong)change.At(from).Clock) + change.OffsetAt(count) - change.OffsetAt(from);
        return change;
    }

    private PreparedUpdate? PreparePerClient(UpdateRefs refs, List<ClientChange> changes)
    {
        var structBytes = _pendingStructBytes;
        var structClients = _pendingStructClients;
        var states = new Dictionary<ulong, long>();
        foreach (var c in changes)
        {
            var old = c.Held?.PendingBytes ?? 0;
            structBytes += c.PendingBytes - old;
            structClients += (c.PendingBytes > 0 ? 1 : 0) - (old > 0 ? 1 : 0);
            states[c.Client] = c.State;
        }

        // the deleted ranges past the state, for every client whose state or ranges change
        DeleteRanges? incoming = null;
        foreach (var r in refs.Deletes) (incoming ??= new DeleteRanges()).Add(r.Client, r.Clock, r.Length);
        var deleteClients = new List<ulong>(states.Keys);
        if (incoming is not null)
            for (var i = 0; i < incoming.Count; i++)
                if ((i == 0 || incoming[i - 1].Client != incoming[i].Client) && !states.ContainsKey(incoming[i].Client)) deleteClients.Add(incoming[i].Client);

        var deleteBytes = _pendingDeleteBytes;
        var deletePending = _pendingDeletes.Count;
        var deleteChanges = new List<(ulong, long)>();
        foreach (var client in deleteClients)
        {
            var state = states.TryGetValue(client, out var s) ? s : _clients.TryGetValue(client, out var held) ? held.State : 0;
            var bytes = PendingDeleteBytes(client, state, incoming);
            var old = _pendingDeletes.GetValueOrDefault(client);
            if (bytes == old) continue;
            deleteBytes += bytes - old;
            deletePending += (bytes > 0 ? 1 : 0) - (old > 0 ? 1 : 0);
            deleteChanges.Add((client, bytes));
        }

        if (PendingTotal(structClients, structBytes, deletePending, deleteBytes) > Limits.MaxPendingBytes) return null;
        return new PreparedUpdate(this, _version, refs.Deletes)
        {
            Changes = changes,
            DeleteChanges = deleteChanges,
            PendingStructBytes = structBytes,
            PendingStructClients = structClients,
            PendingDeleteBytes = deleteBytes,
        };
    }

    private PreparedUpdate? PrepareFlat(UpdateRefs refs, List<StructRef> structs)
    {
        var held = _flat ?? Flatten();
        _merged += held.Count;
        var merged = structs.Count > 0 ? UpdateMerge.Merge(new[] { held, structs }) : held;
        var stateVector = structs.Count > 0 || _flat is null ? StateVectorOf(merged) : _stateVector!;
        if (PendingV1Bytes(merged, stateVector, _deletes, refs.Deletes) > Limits.MaxPendingBytes) return null;
        return new PreparedUpdate(this, _version, refs.Deletes) { Flat = merged, FlatStateVector = stateVector };
    }

    // ------------------------------------------------------------------ committing

    private void CommitPerClient(PreparedUpdate prepared)
    {
        foreach (var c in prepared.Changes)
        {
            var held = c.Held;
            if (held is null)
            {
                held = new ClientStructs();
                _clients.Add(c.Client, held);
                _order.Insert(OrderIndex(c.Client), c.Client);
            }

            held.Structs.RemoveRange(c.P, held.Structs.Count - c.P);
            foreach (var t in c.Tail) held.Structs.Add(t);
            held.Offsets.RemoveRange(c.P + 1, held.Offsets.Count - c.P - 1);
            foreach (var o in c.TailOffsets) held.Offsets.Add(o);
            while (held.Skips.Count > 0 && held.Skips.Last >= c.P) held.Skips.RemoveAt(held.Skips.Count - 1);
            foreach (var k in c.TailSkips) held.AddSkip(k);
            held.State = c.State;
            held.PendingBytes = c.PendingBytes;
        }

        foreach (var (client, bytes) in prepared.DeleteChanges)
        {
            if (bytes == 0) _pendingDeletes.Remove(client);
            else _pendingDeletes[client] = bytes;
        }

        _pendingStructBytes = prepared.PendingStructBytes;
        _pendingStructClients = prepared.PendingStructClients;
        _pendingDeleteBytes = prepared.PendingDeleteBytes;
        foreach (var d in prepared.Deletes) _deletes.Add(d.Client, d.Clock, d.Length);
        if (prepared.Changes.Count > 0) _stateVector = null;
    }

    private void CommitFlat(PreparedUpdate prepared, List<StructRef> flat)
    {
        foreach (var d in prepared.Deletes) _deletes.Add(d.Client, d.Clock, d.Length);
        _stateVector = prepared.StateVector;
        _flat = flat;   // the next prepare goes back to the per-client form if it can
    }

    /// <summary>
    /// Goes back to the per-client form when the flat list lists its clients in descending order, one regular run each. It copies the whole
    /// state, counted in <see cref="MergedHeldStructs"/>; the bytes it writes do not change.
    /// </summary>
    private void TryLeaveFlat()
    {
        var flat = _flat!;
        _merged += flat.Count;
        if (!IsCanonical(flat)) return;
        for (var i = 0; i < flat.Count;)
        {
            var start = i;
            while (i < flat.Count && flat[i].Client == flat[start].Client) i++;
            if (!IsRegular(flat, start, i)) return;
        }

        _clients.Clear();
        _order.Clear();
        _pendingDeletes.Clear();
        _pendingStructBytes = _pendingDeleteBytes = 0;
        _pendingStructClients = 0;
        using var sizer = new Lib0Writer(64);
        for (var i = 0; i < flat.Count;)
        {
            var client = flat[i].Client;
            var start = i;
            while (i < flat.Count && flat[i].Client == client) i++;
            var change = Fold(client, flat, start, i, sizer)!;   // a regular run merged alone is itself
            var held = new ClientStructs();
            foreach (var t in change.Tail) held.Structs.Add(t);
            foreach (var o in change.TailOffsets) held.Offsets.Add(o);
            foreach (var k in change.TailSkips) held.AddSkip(k);
            held.State = change.State;
            held.PendingBytes = change.PendingBytes;
            _clients.Add(client, held);
            _order.Add(client);
            _pendingStructBytes += held.PendingBytes;
            if (held.PendingBytes > 0) _pendingStructClients++;
        }

        for (var i = 0; i < _deletes.Count; i++)
        {
            if (i > 0 && _deletes[i - 1].Client == _deletes[i].Client) continue;
            var client = _deletes[i].Client;
            var bytes = PendingDeleteBytes(client, _clients.TryGetValue(client, out var held) ? held.State : 0, null);
            if (bytes == 0) continue;
            _pendingDeletes[client] = bytes;
            _pendingDeleteBytes += bytes;
        }

        _flat = null;
    }

    // ------------------------------------------------------------------ reading

    private List<StructRef> Flatten()
    {
        var count = 0;
        foreach (var client in _order) count += _clients[client].Structs.Count;
        var flat = new List<StructRef>(count);
        foreach (var client in _order)
            foreach (var t in _clients[client].Structs) flat.Add(t);
        return flat;
    }

    /// <summary>
    /// <see cref="UpdateMerge.Diff"/> over the per-client form: in each client, the first struct that is not a Skip and ends past the target's
    /// clock, sliced at it, then the rest. A regular run's ends increase, so the struct is found by a binary search.
    /// </summary>
    private List<StructRef> DiffPerClient(StateVector target)
    {
        var output = new List<StructRef>();
        foreach (var client in _order)
        {
            var h = _clients[client].Structs;
            var clock = target[client];
            if (h.Last.End <= clock) continue;
            int lo = 0, hi = h.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (h[mid].End > clock) hi = mid;
                else lo = mid + 1;
            }

            while (h[lo].Kind == RefKind.Skip) lo++;
            output.Add(h[lo].Slice(Math.Max(clock - h[lo].Clock, 0)));
            for (var i = lo + 1; i < h.Count; i++) output.Add(h[i]);
        }

        return output;
    }

    /// <summary>The state vector with <paramref name="prepared"/>'s changes on top of the per-client states. The caller holds the gate.</summary>
    private StateVector BuildStateVector(PreparedUpdate? prepared)
    {
        var entries = new List<KeyValuePair<ulong, long>>(_order.Count + (prepared?.Changes.Count ?? 0));
        var changed = new HashSet<ulong>();
        if (prepared is not null)
        {
            foreach (var c in prepared.Changes)
            {
                changed.Add(c.Client);
                if (c.State > 0) entries.Add(new(c.Client, c.State));
            }
        }

        foreach (var client in _order)
        {
            var state = _clients[client].State;
            if (state > 0 && !changed.Contains(client)) entries.Add(new(client, state));
        }

        return StateVector.Create(entries.ToArray());
    }

    private StateVector StateVectorAfter(PreparedUpdate prepared)
    {
        if (_version == prepared.Version) return BuildStateVector(prepared);
        if (_version == prepared.Version + 1 && ReferenceEquals(_lastCommitted, prepared)) return _stateVector ??= BuildStateVector(null);
        throw new InvalidOperationException("The state vector of a prepared update can be read only until the relay commits another update.");
    }

    // ------------------------------------------------------------------ pending size

    private static long PendingTotal(int structClients, long structBytes, int deleteClients, long deleteBytes) =>
        structClients == 0 && deleteClients == 0 ? 0 : VarUintLength((ulong)structClients) + structBytes + VarUintLength((ulong)deleteClients) + deleteBytes;

    /// <summary>
    /// The V1 bytes of <paramref name="client"/>'s block in the pending delete set: its held ranges and the update's (<paramref name="incoming"/>),
    /// merged and clipped to their part past <paramref name="state"/>. 0 when nothing is past it.
    /// </summary>
    private long PendingDeleteBytes(ulong client, long state, DeleteRanges? incoming)
    {
        var (a, aEnd) = PastState(_deletes, client, state);
        var (b, bEnd) = incoming is null ? (0, 0) : PastState(incoming, client, state);
        long bytes = 0;
        var count = 0;
        long curClock = -1, curEnd = -1;
        while (a < aEnd || b < bEnd)
        {
            DeleteRange r;
            if (b >= bEnd || (a < aEnd && _deletes[a].Clock <= incoming![b].Clock)) r = _deletes[a++];
            else r = incoming![b++];
            var clock = Math.Max(r.Clock, state);
            if (curEnd >= 0 && clock <= curEnd)
            {
                curEnd = Math.Max(curEnd, r.End);
                continue;
            }

            if (curEnd >= 0) Add(curClock, curEnd);
            curClock = clock;
            curEnd = r.End;
        }

        if (curEnd >= 0) Add(curClock, curEnd);
        return count == 0 ? 0 : VarUintLength(client) + VarUintLength((ulong)count) + bytes;

        void Add(long clock, long rangeEnd)
        {
            count++;
            bytes += VarUintLength((ulong)clock) + VarUintLength((ulong)(rangeEnd - clock));
        }
    }

    /// <summary>The ranges of <paramref name="client"/> in <paramref name="ranges"/> that end past <paramref name="state"/>, as an index range.</summary>
    private static (int Start, int End) PastState(DeleteRanges ranges, ulong client, long state)
    {
        var lo = ranges.FirstIndexOf(client);
        var end = lo == ranges.Count ? lo : ranges.EndIndexOf(client);
        var hi = end;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (ranges[mid].End > state) hi = mid;
            else lo = mid + 1;
        }

        return (lo, end);
    }

    /// <summary>
    /// The V1 size of what a merged state holds past each client's state (RLY-7): per client, the structs after its first gap with the Skip
    /// that opens the gap dropped, then the deleted ranges, the held ones and the update's, clipped to their part past the client's state.
    /// The flat form's measure; the per-client form keeps the same sum per client.
    /// </summary>
    internal static long PendingV1Bytes(List<StructRef> structs, StateVector stateVector, DeleteRanges held, List<DeleteRange> incoming)
    {
        List<StructRef>? pending = null;
        ulong? client = null;
        foreach (var s in structs)
        {
            if (s.Clock < stateVector[s.Client]) continue;
            if (client != s.Client)
            {
                client = s.Client;
                if (s.Kind == RefKind.Skip) continue;
            }

            (pending ??= new List<StructRef>()).Add(s);
        }

        DeleteRanges? pendingDeletes = null;
        for (var i = 0; i < held.Count; i++) ClipTo(stateVector, held[i], ref pendingDeletes);
        foreach (var r in incoming) ClipTo(stateVector, r, ref pendingDeletes);
        if (pending is null && pendingDeletes is null) return 0;

        using var w = new Lib0Writer(256);
        var encoder = new UpdateEncoderV1(w);
        UpdateWriter.WriteBlocks(ref encoder, pending ?? new List<StructRef>());
        UpdateWriter.WriteDeleteRuns(ref encoder, pendingDeletes is null ? new List<DeleteRange>() : YUpdate.InWriteOrder(pendingDeletes));
        return w.Length;
    }

    private static void ClipTo(StateVector stateVector, DeleteRange range, ref DeleteRanges? clipped)
    {
        var state = stateVector[range.Client];
        if (range.End <= state) return;
        var from = Math.Max(range.Clock, state);
        (clipped ??= new DeleteRanges()).Add(range.Client, from, range.End - from);
    }

    // ------------------------------------------------------------------ helpers

    private static StateVector StateVectorOf(List<StructRef> structs)
    {
        var entries = YUpdate.StateVectorEntries(structs);
        var pairs = new KeyValuePair<ulong, long>[entries.Count];
        for (var i = 0; i < pairs.Length; i++) pairs[i] = new KeyValuePair<ulong, long>(entries[i].Client, entries[i].Clock);
        return StateVector.Create(pairs);
    }

    /// <summary>Clients in descending order, one block each, each block's structs contiguous and not empty: what every Yjs writer produces.</summary>
    private static bool IsCanonical(List<StructRef> structs)
    {
        for (var i = 0; i < structs.Count; i++)
        {
            if (structs[i].Length <= 0) return false;
            if (i == 0) continue;
            var prev = structs[i - 1];
            if (structs[i].Client == prev.Client ? structs[i].Clock != prev.End : structs[i].Client > prev.Client) return false;
        }

        return true;
    }

    private static bool IsRegular(List<StructRef> run) => IsRegular(run, 0, run.Count);

    /// <summary>
    /// A run the merge writes back unchanged when it is merged alone: no Skip first or last, no two Skips in a row, and each struct starting
    /// where the one before ends.
    /// </summary>
    private static bool IsRegular(List<StructRef> run, int start, int end)
    {
        if (end == start || run[start].Kind == RefKind.Skip || run[end - 1].Kind == RefKind.Skip) return false;
        for (var i = start; i < end; i++)
        {
            if (run[i].Length <= 0) return false;
            if (i == start) continue;
            if (run[i].Clock != run[i - 1].End || (run[i].Kind == RefKind.Skip && run[i - 1].Kind == RefKind.Skip)) return false;
        }

        return true;
    }

    /// <summary>The last struct of a regular run that starts at or before <paramref name="clock"/> (the run's first does).</summary>
    private static int LastStartingAtOrBefore(ChunkedList<StructRef> run, long clock)
    {
        int lo = 0, hi = run.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >>> 1;
            if (run[mid].Clock <= clock) lo = mid;
            else hi = mid - 1;
        }

        return lo;
    }

    /// <summary>The index of the first Skip after <paramref name="index"/>, or <paramref name="count"/>.</summary>
    private static int NextSkip(ChunkedList<int> skips, int index, int count)
    {
        int lo = 0, hi = skips.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (skips[mid] > index) hi = mid;
            else lo = mid + 1;
        }

        return lo < skips.Count ? skips[lo] : count;
    }

    private int OrderIndex(ulong client)
    {
        int lo = 0, hi = _order.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_order[mid] > client) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    private static long V1Size(in StructRef s, Lib0Writer sizer)
    {
        sizer.Reset();
        var encoder = new UpdateEncoderV1(sizer);
        UpdateWriter.WriteRef(ref encoder, s);
        return sizer.Length;
    }

    private static int VarUintLength(ulong value)
    {
        var length = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            length++;
        }

        return length;
    }

    /// <summary>One client's merged structs, with what the relay keeps about them.</summary>
    internal sealed class ClientStructs
    {
        private static readonly ChunkedList<int> NoSkips = new();

        // chunked (ADR 0018), so a long history grows by blocks and a fold never copies it to grow it. The first blocks fit one struct: a
        // StructRef is large, and a relay can hold thousands of clients with one struct each (HOS-REF)
        public readonly ChunkedList<StructRef> Structs = new(firstCapacity: 1);
        public readonly ChunkedList<long> Offsets = NewOffsets();   // Offsets[i]: the V1 bytes of Structs[0..i)
        public long State;                                          // the state vector's clock, 0 when the client is not listed
        public long PendingBytes;                                   // the V1 bytes of the client's pending block, 0 when it has none
        private ChunkedList<int>? _skips;                           // made on the first Skip: most clients have none

        /// <summary>The indices of the Skips, ascending. Read only; <see cref="AddSkip"/> adds one.</summary>
        public ChunkedList<int> Skips => _skips ?? NoSkips;

        public void AddSkip(int index) => (_skips ??= new ChunkedList<int>(firstCapacity: 1)).Add(index);

        private static ChunkedList<long> NewOffsets()
        {
            var offsets = new ChunkedList<long>(firstCapacity: 2);
            offsets.Add(0);
            return offsets;
        }
    }

    /// <summary>A fold's splice of one client: its held structs from <see cref="P"/> on are replaced with <see cref="Tail"/>.</summary>
    internal sealed class ClientChange
    {
        internal ClientChange(ulong client, ClientStructs? held, int p, List<StructRef> tail)
        {
            Client = client;
            Held = held;
            P = p;
            Tail = tail;
        }

        public ulong Client { get; }

        public ClientStructs? Held { get; }

        public int P { get; }

        public List<StructRef> Tail { get; }

        public List<long> TailOffsets { get; } = new();   // the offsets of indices P + 1 to P + Tail.Count

        public List<int> TailSkips { get; } = new();

        public long State { get; set; }

        public long PendingBytes { get; set; }

        /// <summary>The struct at <paramref name="index"/> of the list after the splice.</summary>
        public StructRef At(int index) => index < P ? Held!.Structs[index] : Tail[index - P];

        /// <summary>The V1 bytes of the structs before <paramref name="index"/> in the list after the splice.</summary>
        public long OffsetAt(int index) => index <= P ? (index == 0 ? 0 : Held!.Offsets[index]) : TailOffsets[index - P - 1];
    }
}
