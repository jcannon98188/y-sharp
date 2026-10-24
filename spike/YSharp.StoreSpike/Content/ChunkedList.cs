using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace YSharp.StoreSpike.Content;

/// <summary>
/// A growable sequence stored in blocks of at most <see cref="ChunkLimits.ChunkBytes"/>. Adding never copies existing elements; insert and remove
/// shift within blocks and carry one element across each block boundary. Growth is one block at a time, so the list never reaches the large object heap.
/// </summary>
public sealed class ChunkedList<T>
{
    /// <summary>Elements per block for <typeparamref name="T"/>: the largest count whose block stays within the chunk ceiling.</summary>
    public static readonly int BlockCapacity = Math.Max(1, ChunkLimits.ChunkBytes / Unsafe.SizeOf<T>());

    private readonly List<T[]> _blocks = new();
    private int _count;

    public int Count => _count;

    /// <summary>Number of blocks currently allocated.</summary>
    public int BlockCount => _blocks.Count;

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
        get
        {
            CheckIndex(index, _count);
            return _blocks[index / BlockCapacity][index % BlockCapacity];
        }
        set
        {
            CheckIndex(index, _count);
            _blocks[index / BlockCapacity][index % BlockCapacity] = value;
        }
    }

    public void Add(T item)
    {
        var block = _count / BlockCapacity;
        if (block == _blocks.Count) _blocks.Add(new T[BlockCapacity]);
        _blocks[block][_count % BlockCapacity] = item;
        _count++;
    }

    public void Insert(int index, T item)
    {
        if (index < 0 || index > _count) throw new ArgumentOutOfRangeException(nameof(index));
        if (index == _count)
        {
            Add(item);
            return;
        }

        var carry = item;
        var block = index / BlockCapacity;
        var offset = index % BlockCapacity;
        for (; ; block++, offset = 0)
        {
            if (block == _blocks.Count) _blocks.Add(new T[BlockCapacity]);
            var data = _blocks[block];
            var used = UsedIn(block);
            if (used < BlockCapacity)
            {
                Array.Copy(data, offset, data, offset + 1, used - offset);
                data[offset] = carry;
                break;
            }

            // The block is full: its last element moves to the front of the next block.
            var spilled = data[BlockCapacity - 1];
            Array.Copy(data, offset, data, offset + 1, BlockCapacity - 1 - offset);
            data[offset] = carry;
            carry = spilled;
        }

        _count++;
    }

    public void RemoveAt(int index)
    {
        CheckIndex(index, _count);
        var lastBlock = (_count - 1) / BlockCapacity;
        var block = index / BlockCapacity;
        var offset = index % BlockCapacity;
        for (; ; block++, offset = 0)
        {
            var data = _blocks[block];
            var used = UsedIn(block);
            Array.Copy(data, offset + 1, data, offset, used - offset - 1);
            if (block == lastBlock) break;

            // This block is full. Its first element of the next block refills the tail.
            data[BlockCapacity - 1] = _blocks[block + 1][0];
        }

        _count--;
    }

    private int UsedIn(int block) => Math.Clamp(_count - block * BlockCapacity, 0, BlockCapacity);

    private static void CheckIndex(int index, int count)
    {
        if ((uint)index >= (uint)count) throw new ArgumentOutOfRangeException(nameof(index));
    }
}
