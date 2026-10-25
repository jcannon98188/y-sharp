#nullable enable
using System.Collections.Generic;

namespace YSharp.Integration;

/// <summary>
/// Structs that cannot be integrated yet (Yjs <c>store.pendingStructs</c>): each client's waiting structs in clock order, and for each client
/// the clock that must be passed before a retry can help (Yjs <c>missing</c>). Yjs keeps the structs as an encoded V2 update and merges new
/// ones in with <c>mergeUpdatesV2</c>; y-sharp keeps them decoded and merges the same way (sorted by clock, a struct already covered dropped,
/// an overlapping one sliced), so it is codec-independent. Skips are not kept: a gap between two structs is implied by their clocks.
/// </summary>
internal sealed class PendingStructs
{
    private readonly SortedDictionary<ulong, List<StructRef>> _clients = new();

    /// <summary>Per client, the clock that must be below the client's state before retrying can integrate something.</summary>
    public Dictionary<ulong, long> Missing { get; } = new();

    /// <summary>The waiting structs per client, clients ascending, structs in clock order without overlaps.</summary>
    public IEnumerable<KeyValuePair<ulong, List<StructRef>>> Clients => _clients;

    public int ClientCount => _clients.Count;

    /// <summary>Yjs <c>updateMissingSv</c>: keeps the lower clock.</summary>
    public void AddMissing(ulong client, long clock)
    {
        if (!Missing.TryGetValue(client, out var current) || current > clock) Missing[client] = clock;
    }

    /// <summary>Sets <paramref name="client"/>'s waiting structs (Yjs <c>restStructs.clients.set</c>), dropping Skips.</summary>
    public void Set(ulong client, IEnumerable<StructRef> refs)
    {
        var list = new List<StructRef>();
        foreach (var r in refs)
            if (r.Kind != RefKind.Skip) list.Add(r);
        if (list.Count == 0) _clients.Remove(client);
        else _clients[client] = list;
    }

    /// <summary>Merges <paramref name="other"/> into this state: missing clocks keep the lower value, structs are merged per client.</summary>
    public void MergeFrom(PendingStructs other)
    {
        foreach (var (client, clock) in other.Missing) AddMissing(client, clock);
        foreach (var (client, refs) in other._clients)
        {
            if (!_clients.TryGetValue(client, out var mine))
            {
                _clients[client] = new List<StructRef>(refs);
                continue;
            }

            var all = new List<StructRef>(mine.Count + refs.Count);
            all.AddRange(mine);
            all.AddRange(refs);
            _clients[client] = Normalise(all);
        }
    }

    /// <summary>The waiting structs as an update with no deletes, for a retry (Yjs <c>applyUpdateV2(doc, pendingStructs.update)</c>).</summary>
    public UpdateRefs ToUpdate()
    {
        var update = new UpdateRefs();
        foreach (var (client, refs) in _clients) update.SetClient(client, new List<StructRef>(refs));
        return update;
    }

    /// <summary>Sorts by clock (stable, so the earlier source wins a tie), drops structs already covered and slices partly covered ones.</summary>
    private static List<StructRef> Normalise(List<StructRef> all)
    {
        var sorted = new List<StructRef>(all.Count);
        for (var i = 0; i < all.Count; i++)
        {
            var at = sorted.Count;
            while (at > 0 && sorted[at - 1].Clock > all[i].Clock) at--;
            sorted.Insert(at, all[i]);
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
}
