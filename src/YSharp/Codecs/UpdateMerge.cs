#nullable enable
using System;
using System.Collections.Generic;
using YSharp.Integration;
using YSharp.Store;

namespace YSharp.Codecs;

/// <summary>
/// The struct halves of Yjs <c>mergeUpdatesV2</c> and <c>diffUpdateV2</c> on decoded structs, so the output bytes are what Yjs writes for the
/// same inputs: merging never joins items but joins adjacent GC and Skip structs, fills gaps with Skips and slices overlaps. Each input is a
/// flat list of structs in update order. <c>EncodeStateAsUpdate</c> uses it when the document holds pending state (Yjs merges the state with
/// the pending structs and delete set); the update functions (spec 16) reuse it.
/// </summary>
internal static class UpdateMerge
{
    /// <summary>The structs of every client of <paramref name="update"/> in the order the update lists them, then the clients' structs in order.</summary>
    public static List<StructRef> Flatten(UpdateRefs update)
    {
        var list = new List<StructRef>();
        foreach (var client in update.Clients) list.AddRange(client.Refs);
        return list;
    }

    // ------------------------------------------------------------------ diffUpdateV2

    /// <summary>
    /// Yjs <c>diffUpdateV2</c> on structs: drops what <paramref name="target"/> already holds. The first new struct of a client is written from
    /// the target's clock (sliced, with a synthetic origin), and the rest of that client follows unchanged, Skips included.
    /// </summary>
    public static List<StructRef> Diff(List<StructRef> structs, StateVector target)
    {
        var output = new List<StructRef>();
        var i = 0;
        while (i < structs.Count)
        {
            var curr = structs[i];
            var client = curr.Client;
            var svClock = target[client];
            if (curr.Kind == RefKind.Skip)
            {
                i++;
                continue;
            }

            if (curr.End > svClock)
            {
                var offset = Math.Max(svClock - curr.Clock, 0);
                output.Add(curr.Slice(offset));
                i++;
                while (i < structs.Count && structs[i].Client == client) output.Add(structs[i++]);
            }
            else
            {
                while (i < structs.Count && structs[i].Client == client && structs[i].End <= svClock) i++;
            }
        }

        return output;
    }

    // ------------------------------------------------------------------ mergeUpdatesV2

    private sealed class Source
    {
        private readonly List<StructRef> _structs;
        private int _index = -1;

        public Source(List<StructRef> structs)
        {
            _structs = structs;
            Next();
        }

        public bool HasCurr => _index < _structs.Count;

        public StructRef Curr => _structs[_index];

        /// <summary>Advances past Skips (Yjs <c>LazyStructReader</c> with <c>filterSkips</c>).</summary>
        public void Next()
        {
            do _index++;
            while (_index < _structs.Count && _structs[_index].Kind == RefKind.Skip);
        }
    }

    /// <summary>Yjs <c>mergeUpdatesV2</c> on structs: one merged list, clients in descending order, structs of a client in clock order.</summary>
    public static List<StructRef> Merge(IReadOnlyList<List<StructRef>> updates)
    {
        var sources = new List<Source>(updates.Count);
        foreach (var u in updates) sources.Add(new Source(u));
        var output = new List<StructRef>();
        var hasWrite = false;
        StructRef write = default;
        while (true)
        {
            sources.RemoveAll(s => !s.HasCurr);
            if (sources.Count == 0) break;
            StableSort(sources);
            var source = sources[0];
            var firstClient = source.Curr.Client;
            if (hasWrite)
            {
                var iterated = false;
                // skip what was already written
                while (source.HasCurr && source.Curr.End <= write.End && source.Curr.Client >= write.Client)
                {
                    source.Next();
                    iterated = true;
                }

                if (!source.HasCurr || source.Curr.Client != firstClient || (iterated && source.Curr.Clock > write.End)) continue;
                var curr = source.Curr;
                if (firstClient != write.Client)
                {
                    output.Add(write);
                    write = curr;
                    source.Next();
                }
                else if (write.End < curr.Clock)
                {
                    if (write.Kind == RefKind.Skip)
                    {
                        write.Length = curr.End - write.Clock;
                    }
                    else
                    {
                        output.Add(write);
                        write = StructRef.Skip(firstClient, write.End, curr.Clock - write.End);
                    }
                }
                else
                {
                    var diff = write.End - curr.Clock;
                    if (diff > 0)
                    {
                        // prefer to slice a Skip, the other struct may carry more information
                        if (write.Kind == RefKind.Skip) write.Length -= diff;
                        else curr = curr.Slice(diff);
                    }

                    if (!TryMergeWith(ref write, curr))
                    {
                        output.Add(write);
                        write = curr;
                        source.Next();
                    }
                }
            }
            else
            {
                write = source.Curr;
                hasWrite = true;
                source.Next();
            }

            while (source.HasCurr && source.Curr.Client == firstClient && source.Curr.Clock == write.End && source.Curr.Kind != RefKind.Skip)
            {
                output.Add(write);
                write = source.Curr;
                source.Next();
            }
        }

        if (hasWrite) output.Add(write);
        return output;
    }

    /// <summary>Yjs <c>mergeWith</c> on structs that were never integrated: GC joins GC and Skip joins Skip; items never join (their <c>right</c> is unset).</summary>
    private static bool TryMergeWith(ref StructRef left, in StructRef right)
    {
        if (left.Kind != right.Kind || left.Kind == RefKind.Item) return false;
        left.Length += right.Length;
        return true;
    }

    /// <summary>
    /// The order Yjs sorts the readers in: descending client; within a client ascending clock, and at the same clock a Skip after anything else.
    /// Stable, as <c>Array.prototype.sort</c> is.
    /// </summary>
    private static void StableSort(List<Source> sources)
    {
        for (var i = 1; i < sources.Count; i++)
        {
            var s = sources[i];
            var j = i - 1;
            while (j >= 0 && Compare(sources[j].Curr, s.Curr) > 0)
            {
                sources[j + 1] = sources[j];
                j--;
            }

            sources[j + 1] = s;
        }
    }

    private static int Compare(in StructRef a, in StructRef b)
    {
        if (a.Client != b.Client) return b.Client.CompareTo(a.Client);
        if (a.Clock != b.Clock) return a.Clock.CompareTo(b.Clock);
        if (a.Kind == RefKind.Skip == (b.Kind == RefKind.Skip)) return 0;
        return a.Kind == RefKind.Skip ? 1 : -1;
    }

    // ------------------------------------------------------------------ the document's state as decoded structs

    /// <summary>
    /// The structs Yjs <c>writeClientsStructs</c> writes for <paramref name="target"/>, as decoded structs (for merging with the pending state).
    /// The first struct of a client is sliced at the target's clock.
    /// </summary>
    public static List<StructRef> StateStructs(Doc doc, StateVector? target)
    {
        var store = doc.Store;
        var output = new List<StructRef>();
        for (var rank = store.ClientCount - 1; rank >= 0; rank--)
        {
            var count = store.StructCountAtRank(rank);
            if (count == 0) continue;
            var client = store.ClientIdAtRank(rank);
            long clock = 0;
            if (target is not null && target.TryGetClock(client, out clock) && store.StateAtRank(rank) <= clock) continue;
            var first = store.StructAtRank(rank, 0);
            if (clock < first.Clock) clock = first.Clock;
            var start = store.FindIndexAtRank(rank, clock);
            for (var i = start; i < count; i++)
            {
                var s = store.StructAtRank(rank, i);
                var r = ToRef(doc, s, client);
                output.Add(i == start ? r.Slice(clock - s.Clock) : r);
            }
        }

        return output;
    }

    /// <summary>A stored struct as the decoded struct Yjs would read back from its encoding.</summary>
    public static StructRef ToRef(Doc doc, ItemRef s, ulong client)
    {
        var store = doc.Store;
        if (s.IsGc) return StructRef.Gc(client, s.Clock, s.Length);
        var r = new StructRef
        {
            Kind = RefKind.Item,
            Client = client,
            Clock = s.Clock,
            Length = s.Length,
            Origin = store.OriginOf(s),
            RightOrigin = store.RightOriginOf(s),
            ParentSub = store.ParentSubOf(s),
            Content = s.Kind,
        };
        if (r.Origin is null && r.RightOrigin is null)
        {
            var parent = s.Parent ?? throw new YInternalException("An item without a parent cannot be written.");
            var parentItem = StructStore.ItemOf(parent);
            if (parentItem.IsNull) r.ParentRoot = doc.RootNameOf(parent) ?? throw new YInternalException("A root type has no name.");
            else r.ParentId = store.IdOf(parentItem);
        }

        switch (s.Kind)
        {
            case ContentKind.Deleted:
                break;
            case ContentKind.String:
                r.Text = store.GetString(s);
                break;
            case ContentKind.Any:
            case ContentKind.Json:
                var values = new YValue[(int)s.Length];
                for (var i = 0; i < values.Length; i++) values[i] = store.GetValue(s, i);
                r.Values = values;
                break;
            default:
                r.Payload = store.GetPayload(s);
                break;
        }

        return r;
    }
}
