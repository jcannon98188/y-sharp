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
        private readonly IReadOnlyList<StructRef> _structs;
        private readonly int _end;
        private int _index;

        public Source(IReadOnlyList<StructRef> structs, int start, int end)
        {
            _structs = structs;
            _index = start - 1;
            _end = end;
            Next();
        }

        public bool HasCurr => _index < _end;

        public StructRef Curr => _structs[_index];

        /// <summary>Advances past Skips (Yjs <c>LazyStructReader</c> with <c>filterSkips</c>).</summary>
        public void Next()
        {
            do _index++;
            while (_index < _end && _structs[_index].Kind == RefKind.Skip);
        }
    }

    /// <summary>Yjs <c>mergeUpdatesV2</c> on structs: one merged list, clients in descending order, structs of a client in clock order.</summary>
    public static List<StructRef> Merge(IReadOnlyList<List<StructRef>> updates)
    {
        var ranges = new (IReadOnlyList<StructRef>, int, int)[updates.Count];
        for (var i = 0; i < ranges.Length; i++) ranges[i] = (updates[i], 0, updates[i].Count);
        return Merge(ranges, null);
    }

    /// <summary>
    /// <see cref="Merge(IReadOnlyList{List{StructRef}})"/> over a range of each list. With <paramref name="write"/>, the merge resumes where Yjs
    /// would be at the top of its loop holding that struct as <c>currWrite</c>: the output starts with it, as it is extended or sliced.
    /// </summary>
    public static List<StructRef> Merge(IReadOnlyList<(IReadOnlyList<StructRef> Structs, int Start, int End)> updates, StructRef? write)
    {
        // Yjs sorts all its readers (a stable sort) on every pass and takes the first. Only that one moves between passes, and its next
        // struct sorts no earlier, so the sort puts it back before the first reader that does not sort before it: in front of every reader it
        // ties with. A queue ordered by struct, then by a stamp that is the input index at first and lower than any before for each reader put
        // back, takes the same reader each pass in O(log n) rather than O(n), so merging n updates is not O(n) per struct (TASK-13).
        var queue = new PriorityQueue<Source, (StructRef Curr, long Stamp)>(updates.Count, SourceOrder.Instance);
        var total = write.HasValue ? 1 : 0;
        long stamp = 0;
        foreach (var (structs, start, end) in updates)
        {
            var s = new Source(structs, start, end);
            if (s.HasCurr) queue.Enqueue(s, (s.Curr, stamp++));
            total += end - start;
        }

        // the merge writes at most one struct per input struct, plus a Skip per gap (HOS-AMP: no doubling on large merges)
        var output = new List<StructRef>(total);
        var hasWrite = write.HasValue;
        var curWrite = write.GetValueOrDefault();
        long putBack = -1;
        while (queue.TryDequeue(out var source, out _))
        {
            var firstClient = source.Curr.Client;
            if (hasWrite)
            {
                var iterated = false;
                // skip what was already written
                while (source.HasCurr && source.Curr.End <= curWrite.End && source.Curr.Client >= curWrite.Client)
                {
                    source.Next();
                    iterated = true;
                }

                if (source.HasCurr && source.Curr.Client == firstClient && !(iterated && source.Curr.Clock > curWrite.End))
                {
                    var curr = source.Curr;
                    if (firstClient != curWrite.Client)
                    {
                        output.Add(curWrite);
                        curWrite = curr;
                        source.Next();
                    }
                    else if (curWrite.End < curr.Clock)
                    {
                        if (curWrite.Kind == RefKind.Skip)
                        {
                            curWrite.Length = curr.End - curWrite.Clock;
                        }
                        else
                        {
                            output.Add(curWrite);
                            curWrite = StructRef.Skip(firstClient, curWrite.End, curr.Clock - curWrite.End);
                        }
                    }
                    else
                    {
                        var diff = curWrite.End - curr.Clock;
                        if (diff > 0)
                        {
                            // prefer to slice a Skip, the other struct may carry more information
                            if (curWrite.Kind == RefKind.Skip) curWrite.Length -= diff;
                            else curr = curr.Slice(diff);
                        }

                        if (!TryMergeWith(ref curWrite, curr))
                        {
                            output.Add(curWrite);
                            curWrite = curr;
                            source.Next();
                        }
                    }

                    WriteRun(source, firstClient, ref curWrite, output);
                }
            }
            else
            {
                curWrite = source.Curr;
                hasWrite = true;
                source.Next();
                WriteRun(source, firstClient, ref curWrite, output);
            }

            if (source.HasCurr) queue.Enqueue(source, (source.Curr, putBack--));
        }

        if (hasWrite) output.Add(curWrite);
        return output;
    }

    /// <summary>Writes the structs of <paramref name="source"/> that continue <paramref name="curWrite"/> without a gap.</summary>
    private static void WriteRun(Source source, ulong client, ref StructRef curWrite, List<StructRef> output)
    {
        while (source.HasCurr && source.Curr.Client == client && source.Curr.Clock == curWrite.End && source.Curr.Kind != RefKind.Skip)
        {
            output.Add(curWrite);
            curWrite = source.Curr;
            source.Next();
        }
    }

    /// <summary>Yjs <c>mergeWith</c> on structs that were never integrated: GC joins GC and Skip joins Skip; items never join (their <c>right</c> is unset).</summary>
    private static bool TryMergeWith(ref StructRef left, in StructRef right)
    {
        if (left.Kind != right.Kind || left.Kind == RefKind.Item) return false;
        left.Length += right.Length;
        return true;
    }

    /// <summary>The order Yjs sorts the readers in (<see cref="Compare"/>), ties broken by stamp as its stable sort leaves them.</summary>
    private sealed class SourceOrder : IComparer<(StructRef Curr, long Stamp)>
    {
        public static readonly SourceOrder Instance = new();

        public int Compare((StructRef Curr, long Stamp) x, (StructRef Curr, long Stamp) y)
        {
            var c = UpdateMerge.Compare(x.Curr, y.Curr);
            return c != 0 ? c : x.Stamp.CompareTo(y.Stamp);
        }
    }

    /// <summary>The order Yjs sorts the readers in: descending client; within a client ascending clock, and at the same clock a Skip after anything else.</summary>

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
            Content = s.Kind,
        };
        if (r.Origin is null && r.RightOrigin is null)
        {
            var parent = s.Parent ?? throw new YInternalException("An item without a parent cannot be written.");
            var parentItem = StructStore.ItemOf(parent);
            if (parentItem.IsNull) r.ParentRoot = doc.RootNameOf(parent) ?? throw new YInternalException("A root type has no name.");
            else r.ParentId = store.IdOf(parentItem);
            // Yjs reads parentSub only with the parent: an item with an origin reads back without it, so a merge writes info bit 6 clear
            r.ParentSub = store.ParentSubOf(s);
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
