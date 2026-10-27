using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace YSharp.Store;

/// <summary>The chunk ceiling of ADR 0018: no document-owned allocation grows past about 64 KB, so steady-state edits never reach the large object heap.</summary>
internal static class ChunkLimits
{
    public const int ChunkBytes = 64 * 1024;
}

/// <summary>
/// A growable sequence stored in blocks of at most <see cref="ChunkLimits.ChunkBytes"/> (ADR 0018, chunked storage). The first block starts at 4
/// elements and doubles until it reaches the full block size; only then are further full blocks added. A small document therefore holds a few
/// hundred bytes per table, not one 64 KB block per table. Adding never copies existing full blocks; insert and remove shift within blocks and
/// carry one element across each block boundary.
/// </summary>
internal sealed class ChunkedList<T> : IReadOnlyList<T>
{
    /// <summary>Elements per full block for <typeparamref name="T"/>: the largest count whose block stays within the chunk ceiling.</summary>
    public static readonly int BlockCapacity = Math.Max(1, ChunkLimits.ChunkBytes / Unsafe.SizeOf<T>());

    // small: a document holds one list per client, and an update can name thousands of one-struct clients (BUD-12)
    private const int FirstCapacity = 4;

    private readonly List<T[]> _blocks = new(1);
    private int _count;

    public int Count => _count;

    /// <summary>Size in bytes of the largest block allocated; never above the chunk ceiling.</summary>
    public long MaxBlockBytes
    {
        get
        {
            long max = 0;
            foreach (var block in _blocks) max = Math.Max(max, (long)block.Length * Unsafe.SizeOf<T>());
            return max;
        }
    }

    public T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return _blocks[index / BlockCapacity][index % BlockCapacity];
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            _blocks[index / BlockCapacity][index % BlockCapacity] = value;
        }
    }

    /// <summary>The last element; the list must not be empty.</summary>
    public T Last => this[_count - 1];

    public void Add(T item)
    {
        EnsureRoomFor(_count + 1);
        _blocks[_count / BlockCapacity][_count % BlockCapacity] = item;
        _count++;
    }

    public void Insert(int index, T item)
    {
        if ((uint)index > (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        if (index == _count)
        {
            Add(item);
            return;
        }

        EnsureRoomFor(_count + 1);
        var carry = item;
        var block = index / BlockCapacity;
        var offset = index % BlockCapacity;
        for (; ; block++, offset = 0)
        {
            var data = _blocks[block];
            var used = UsedIn(block);
            if (used < data.Length)
            {
                Array.Copy(data, offset, data, offset + 1, used - offset);
                data[offset] = carry;
                break;
            }

            // A full block: its last element moves to the front of the next block.
            var spilled = data[data.Length - 1];
            Array.Copy(data, offset, data, offset + 1, data.Length - 1 - offset);
            data[offset] = carry;
            carry = spilled;
        }

        _count++;
    }

    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        var lastBlock = (_count - 1) / BlockCapacity;
        var block = index / BlockCapacity;
        var offset = index % BlockCapacity;
        for (; ; block++, offset = 0)
        {
            var data = _blocks[block];
            var used = UsedIn(block);
            Array.Copy(data, offset + 1, data, offset, used - offset - 1);
            if (block == lastBlock)
            {
                data[used - 1] = default!; // drop the reference, so a removed element is not kept alive
                break;
            }

            data[data.Length - 1] = _blocks[block + 1][0];
        }

        _count--;
    }

    /// <summary>Removes <paramref name="count"/> elements from <paramref name="index"/> in one pass (Yjs <c>splice</c> after a merge).</summary>
    public void RemoveRange(int index, int count)
    {
        if (index < 0 || count < 0 || index + count > _count) throw new ArgumentOutOfRangeException(nameof(index));
        if (count == 0) return;
        if (count == 1)
        {
            RemoveAt(index);
            return;
        }

        for (var to = index; to + count < _count; to++)
        {
            _blocks[to / BlockCapacity][to % BlockCapacity] = _blocks[(to + count) / BlockCapacity][(to + count) % BlockCapacity];
        }

        for (var i = _count - count; i < _count; i++) _blocks[i / BlockCapacity][i % BlockCapacity] = default!;
        _count -= count;
    }

    /// <summary>Empties the list but keeps its blocks, so a list reused per transaction allocates nothing once warm.</summary>
    public void Clear()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            foreach (var block in _blocks) Array.Clear(block);
        }

        _count = 0;
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < _count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private int UsedIn(int block) => Math.Clamp(_count - block * BlockCapacity, 0, BlockCapacity);

    private void EnsureRoomFor(int count)
    {
        if (_blocks.Count == 0)
        {
            _blocks.Add(new T[Math.Min(FirstCapacity, BlockCapacity)]);
            return;
        }

        var first = _blocks[0];
        if (_blocks.Count == 1 && first.Length < BlockCapacity)
        {
            if (count <= first.Length) return;
            var grown = new T[Math.Min(first.Length * 2, BlockCapacity)];
            Array.Copy(first, grown, first.Length);
            _blocks[0] = grown;
            return;
        }

        if (count > _blocks.Count * BlockCapacity) _blocks.Add(new T[BlockCapacity]);
    }
}
