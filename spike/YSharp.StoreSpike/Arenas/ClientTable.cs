using System;
using YSharp.StoreSpike.Content;

namespace YSharp.StoreSpike.Arenas;

/// <summary>
/// The client table: 53-bit client ids (ADR 0018) to a dense client index, and per client the item slots in clock order. Each per-client sequence is a
/// <see cref="ChunkedList{T}"/> of slot indexes, so it grows in chunks and a split inserts one 4-byte entry.
/// </summary>
internal sealed class ClientTable
{
    public const ulong MaxClientId = (1UL << 53) - 1;

    private ulong[] _ids = new ulong[4];
    private ChunkedList<int>[] _structs = new ChunkedList<int>[4];
    private int[] _nextClock = new int[4];
    private int _count;

    public int Count => _count;

    public ulong IdOf(int client) => _ids[client];

    public ChunkedList<int> Structs(int client) => _structs[client];

    public int NextClock(int client) => _nextClock[client];

    public void AdvanceClock(int client, int length) => _nextClock[client] += length;

    /// <summary>The client index of <paramref name="id"/>, or -1.</summary>
    public int IndexOf(ulong id)
    {
        for (var i = 0; i < _count; i++)
        {
            if (_ids[i] == id) return i;
        }

        return -1;
    }

    public int GetOrAdd(ulong id)
    {
        if (id > MaxClientId) throw new ArgumentOutOfRangeException(nameof(id), "client ids are 53-bit");
        var existing = IndexOf(id);
        if (existing >= 0) return existing;
        if (_count == _ids.Length)
        {
            Array.Resize(ref _ids, _count * 2);
            Array.Resize(ref _structs, _count * 2);
            Array.Resize(ref _nextClock, _count * 2);
        }

        _ids[_count] = id;
        _structs[_count] = new ChunkedList<int>();
        return _count++;
    }

    /// <summary>Position in the client's sequence of the item that contains <paramref name="clock"/> (binary search, as <c>findIndexSS</c>).</summary>
    public int FindIndex(StructArena arena, int client, int clock)
    {
        var structs = _structs[client];
        int lo = 0, hi = structs.Count - 1;

        // Append fast path: most lookups are for the newest item.
        if (hi >= 0)
        {
            ref var last = ref arena[structs[hi]];
            if (last.Clock <= clock) return clock < last.Clock + last.Length ? hi : throw Missing(clock);
        }

        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            ref var slot = ref arena[structs[mid]];
            if (clock < slot.Clock) hi = mid - 1;
            else if (clock >= slot.Clock + slot.Length) lo = mid + 1;
            else return mid;
        }

        throw Missing(clock);
    }

    public long MaxChunkBytes
    {
        get
        {
            long max = 0;
            for (var i = 0; i < _count; i++) max = Math.Max(max, _structs[i].MaxBlockBytes);
            return max;
        }
    }

    private static InvalidOperationException Missing(int clock) => new("no item contains clock " + clock);
}
