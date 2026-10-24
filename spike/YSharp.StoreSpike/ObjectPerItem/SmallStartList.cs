using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using YSharp.StoreSpike.Content;

namespace YSharp.StoreSpike.ObjectPerItem;

/// <summary>
/// The candidate's own chunked list for document tables that are usually small (the client table, per-client struct lists, the key table's
/// buckets). It behaves like <see cref="ChunkedList{T}"/>, but its first block starts at 16 elements and doubles until it reaches the 64 KB
/// block size; only then are further full blocks added. A document with one client and a short array therefore holds a few hundred bytes
/// of tables, not one 64 KB block per table, which matters to the per-operation figure of the array workload.
/// </summary>
public sealed class SmallStartList<T>
{
    public static readonly int BlockCapacity = Math.Max(1, ChunkLimits.ChunkBytes / Unsafe.SizeOf<T>());
    private const int FirstCapacity = 16;

    private readonly List<T[]> _blocks = new(1);
    private int _count;

    public int Count => _count;

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
        set
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            _blocks[index / BlockCapacity][index % BlockCapacity] = value;
        }
    }

    public void Add(T item)
    {
        EnsureRoomFor(_count + 1);
        _blocks[_count / BlockCapacity][_count % BlockCapacity] = item;
        _count++;
    }

    public void Insert(int index, T item)
    {
        if ((uint)index > (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
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

            // full block: its last element moves to the front of the next block
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
                data[used - 1] = default!; // drop the reference, so a removed item is not kept alive
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
