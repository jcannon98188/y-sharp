#nullable enable
using System.Collections.Generic;
using YSharp.Codecs;
using YSharp.Codecs.V1;
using YSharp.Lib0;

namespace YSharp.Integration;

/// <summary>
/// Structs that cannot be integrated yet (Yjs <c>store.pendingStructs</c>): each client's waiting structs in clock order, and for each client
/// the clock that must be passed before a retry can help (Yjs <c>missing</c>). Yjs keeps the structs as an encoded V2 update and merges new
/// ones in with <c>mergeUpdatesV2</c>; y-sharp keeps them decoded and merges the same way (sorted by clock, a struct already covered dropped,
/// an overlapping one sliced), so it is codec-independent. Skips are not kept: a gap between two structs is implied by their clocks.
/// The V1 size of the structs as one update (<see cref="StructBytes"/>) is kept as the lists change, so that measuring it for
/// MaxPendingBytes does not re-encode the whole pending state on every update (TASK-11).
/// </summary>
internal sealed class PendingStructs
{
    private readonly SortedDictionary<ulong, List<StructRef>> _clients = new();
    private readonly Dictionary<ulong, ClientSize> _sizes = new();
    private long _clientBytes;   // the sum of the clients' sizes

    /// <summary>Set on a clone: its lists are shared with the original, so a merge into it must build new ones.</summary>
    private bool _sharesLists;

    /// <summary>Per client, the clock that must be below the client's state before retrying can integrate something.</summary>
    public Dictionary<ulong, long> Missing { get; } = new();

    /// <summary>The waiting structs per client, clients ascending, structs in clock order without overlaps.</summary>
    public IEnumerable<KeyValuePair<ulong, List<StructRef>>> Clients => _clients;

    public int ClientCount => _clients.Count;

    /// <summary>
    /// The V1 size of the structs part of the pending update: the client count, then per client the struct count, the client, the first clock
    /// and the structs with a Skip in each gap (as <c>Doc.V1Bytes</c> encodes it).
    /// </summary>
    public long StructBytes => Lib0Writer.VarUintLength((ulong)_clients.Count) + _clientBytes;

    /// <summary>Yjs <c>updateMissingSv</c>: keeps the lower clock.</summary>
    public void AddMissing(ulong client, long clock)
    {
        if (!Missing.TryGetValue(client, out var current) || current > clock) Missing[client] = clock;
    }

    /// <summary>Sets <paramref name="client"/>'s waiting structs (Yjs <c>restStructs.clients.set</c>), dropping Skips.</summary>
    public void Set(ulong client, List<StructRef> refs, int start = 0)
    {
        var count = 0;
        for (var i = start; i < refs.Count; i++)
            if (refs[i].Kind != RefKind.Skip) count++;
        if (count == 0)
        {
            Drop(client);
            return;
        }

        var list = new List<StructRef>(count);   // sized exactly: a hostile update can park every struct it carries here (BUD-12)
        for (var i = start; i < refs.Count; i++)
            if (refs[i].Kind != RefKind.Skip) list.Add(refs[i]);
        using var scratch = Lib0Writer.Rent(64);
        Put(client, list, Measure(list, scratch));
    }

    /// <summary>Sets <paramref name="client"/>'s waiting structs to the single struct <paramref name="r"/>, unless it is a Skip.</summary>
    public void Set(ulong client, in StructRef r)
    {
        if (r.Kind == RefKind.Skip)
        {
            Drop(client);
            return;
        }

        var list = new List<StructRef>(1) { r };
        using var scratch = Lib0Writer.Rent(64);
        Put(client, list, Measure(list, scratch));
    }

    /// <summary>
    /// Merges <paramref name="other"/> into this state: missing clocks keep the lower value, structs are merged per client. <paramref name="other"/>
    /// is used up: its lists are taken over. Structs that all follow the client's last waiting one are appended in place, so a client that keeps
    /// sending parked structs in order costs only what it sends.
    /// </summary>
    public void MergeFrom(PendingStructs other)
    {
        foreach (var (client, clock) in other.Missing) AddMissing(client, clock);
        using var scratch = Lib0Writer.Rent(64);
        foreach (var (client, refs) in other._clients)
        {
            if (!_clients.TryGetValue(client, out var mine))
            {
                Put(client, refs, other._sizes[client]);
                continue;
            }

            if (refs[0].Clock >= mine[^1].Clock)
            {
                if (_sharesLists) _clients[client] = mine = new List<StructRef>(mine);
                Put(client, mine, Appended(_sizes[client], mine[^1].End, refs, 0, mine, scratch));
                continue;
            }

            var all = Normalise(mine, refs);
            Put(client, all, Measure(all, scratch));
        }
    }

    /// <summary>
    /// <see cref="StructBytes"/> as it would be after <see cref="MergeFrom"/> of <paramref name="other"/>, without changing either state: an
    /// append is measured from <paramref name="other"/>'s structs alone, and only a client whose structs interleave is merged to be measured.
    /// </summary>
    public long StructBytesAfterMerge(PendingStructs other)
    {
        var clients = _clients.Count;
        var bytes = _clientBytes;
        using var scratch = Lib0Writer.Rent(64);
        foreach (var (client, refs) in other._clients)
        {
            if (!_clients.TryGetValue(client, out var mine))
            {
                clients++;
                bytes += other._sizes[client].Bytes;
                continue;
            }

            var size = _sizes[client];
            bytes -= size.Bytes;
            bytes += refs[0].Clock >= mine[^1].Clock
                ? Appended(size, mine[^1].End, refs, 0, null, scratch).Bytes
                : Measure(Normalise(mine, refs), scratch).Bytes;
        }

        return Lib0Writer.VarUintLength((ulong)clients) + bytes;
    }

    /// <summary>
    /// A copy that can be changed without changing this one. The per-client lists are shared: the copy builds a new list rather than appending
    /// to a shared one, and nothing else changes a list in place, so a shallow copy is enough (BUD-12).
    /// </summary>
    public PendingStructs Clone()
    {
        var copy = new PendingStructs { _sharesLists = true, _clientBytes = _clientBytes };
        foreach (var (client, clock) in Missing) copy.Missing[client] = clock;
        foreach (var (client, refs) in _clients) copy._clients[client] = refs;
        foreach (var (client, size) in _sizes) copy._sizes[client] = size;
        return copy;
    }

    /// <summary>
    /// The waiting structs as an update with no deletes, for a retry (Yjs <c>applyUpdateV2(doc, pendingStructs.update)</c>). The lists are handed
    /// over, not copied: the caller drops this pending state.
    /// </summary>
    public UpdateRefs ToUpdate()
    {
        var update = new UpdateRefs();
        update.ExpectClients(_clients.Count);
        foreach (var (client, refs) in _clients) update.SetClient(client, refs);
        return update;
    }

    private void Put(ulong client, List<StructRef> refs, ClientSize size)
    {
        if (_sizes.TryGetValue(client, out var old)) _clientBytes -= old.Bytes;
        _clients[client] = refs;
        _sizes[client] = size;
        _clientBytes += size.Bytes;
    }

    private void Drop(ulong client)
    {
        if (!_sizes.Remove(client, out var old)) return;
        _clients.Remove(client);
        _clientBytes -= old.Bytes;
    }

    /// <summary>Sorts by clock (stable, so the earlier source wins a tie), drops structs already covered and slices partly covered ones.</summary>
    private static List<StructRef> Normalise(List<StructRef> mine, List<StructRef> refs)
    {
        var sorted = new List<StructRef>(mine.Count + refs.Count);
        sorted.AddRange(mine);
        foreach (var r in refs)
        {
            var at = sorted.Count;
            while (at > 0 && sorted[at - 1].Clock > r.Clock) at--;
            sorted.Insert(at, r);
        }

        var result = new List<StructRef>(sorted.Count);
        foreach (var next in sorted)
        {
            if (result.Count == 0)
            {
                result.Add(next);
                continue;
            }

            var end = result[^1].End;
            if (next.End <= end) continue;
            result.Add(next.Clock < end ? next.Slice(end - next.Clock) : next);
        }

        return result;
    }

    /// <summary>
    /// The size of a client's entry after <see cref="Normalise"/> puts <paramref name="refs"/> from <paramref name="start"/> on after structs
    /// ending at <paramref name="end"/> (they all start at or after the last one, so the sort keeps them in place): each struct already covered
    /// is dropped and a partly covered one sliced, as there. The kept structs are added to <paramref name="into"/> when it is given.
    /// </summary>
    private static ClientSize Appended(ClientSize size, long end, List<StructRef> refs, int start, List<StructRef>? into, Lib0Writer scratch)
    {
        for (var i = start; i < refs.Count; i++)
        {
            var r = refs[i];
            if (r.End <= end) continue;
            var next = r.Clock < end ? r.Slice(end - r.Clock) : r;
            if (next.Clock > end) size = size.With(RefBytes(StructRef.Skip(next.Client, end, next.Clock - end), scratch));
            size = size.With(RefBytes(next, scratch));
            into?.Add(next);
            end = next.End;
        }

        return size;
    }

    /// <summary>The size of a client's entry holding <paramref name="refs"/>, which are in clock order without overlaps.</summary>
    private static ClientSize Measure(List<StructRef> refs, Lib0Writer scratch)
    {
        var size = new ClientSize(0, Lib0Writer.VarUintLength(refs[0].Client) + Lib0Writer.VarUintLength((ulong)refs[0].Clock));
        for (var i = 0; i < refs.Count; i++)
        {
            var r = refs[i];
            if (i > 0 && r.Clock > refs[i - 1].End) size = size.With(RefBytes(StructRef.Skip(r.Client, refs[i - 1].End, r.Clock - refs[i - 1].End), scratch));
            size = size.With(RefBytes(r, scratch));
        }

        return size;
    }

    /// <summary>The V1 size of one struct as <see cref="UpdateWriter.WriteRef"/> writes it: V1 writes each struct on its own, so sizes add up.</summary>
    private static int RefBytes(in StructRef r, Lib0Writer scratch)
    {
        scratch.Reset();
        var encoder = new UpdateEncoderV1(scratch);
        UpdateWriter.WriteRef(ref encoder, r);
        return scratch.Length;
    }

    /// <summary>A client's entry: how many structs it writes (Skips included), and the bytes after that count.</summary>
    private readonly record struct ClientSize(long Count, long Body)
    {
        public long Bytes => Lib0Writer.VarUintLength((ulong)Count) + Body;

        public ClientSize With(int structBytes) => new(Count + 1, Body + structBytes);
    }
}
