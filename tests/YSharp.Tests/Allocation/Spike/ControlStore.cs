using System;
using System.Collections.Generic;
using System.Text;
using YSharp.Store;

namespace YSharp.StoreHarness;

/// <summary>
/// The harness control: a store built only from the store's content layer (text buffer, value column, key table, chunked lists), with no CRDT
/// logic. It proves the driver, the workloads and the measurements work end to end and gives the stores a baseline for the content layer's own
/// cost. It is not a storage candidate and is never a winner. Its key lookup is a plain dictionary, which a store must not copy.
/// </summary>
public sealed class ControlStore : IStoreDriver
{
    private readonly TextBuffer _text = new();
    private readonly ChunkedList<int> _sequence = new();
    private readonly ValueColumn _array = new();
    private readonly KeyTable _mapKeys = new();
    private readonly ValueColumn _mapValues = new();
    private readonly Dictionary<string, int> _keys = new(StringComparer.Ordinal);
    private long _structs;
    private long _commits;

    public int TextLength => _sequence.Count;

    public DriverStats Stats => new(_structs, _commits);

    public void InsertChar(int index, char value)
    {
        Span<char> one = stackalloc char[1];
        one[0] = value;
        _sequence.Insert(index, _text.Append(one));
        _structs++;
    }

    public void DeleteAt(int index) => _sequence.RemoveAt(index);

    public void PushValue(double value)
    {
        _array.Append(value);
        _structs++;
    }

    public void SetKey(string key, double value)
    {
        // The value column is append-only, so an overwrite appends and the key points at the latest value; no new struct.
        var at = _mapValues.Append(value);
        if (_keys.ContainsKey(key))
        {
            _keys[key] = at;
            return;
        }

        _mapKeys.Add(key);
        _keys[key] = at;
        _structs++;
    }

    public void Commit() => _commits++;

    /// <summary>The value stored under <paramref name="key"/>, for self-tests.</summary>
    public double ValueOfKey(string key) => _mapValues.Get(_keys[key]).AsNumber();

    /// <summary>The visible text, for self-tests.</summary>
    public string Text()
    {
        var sb = new StringBuilder(_sequence.Count);
        for (var i = 0; i < _sequence.Count; i++) sb.Append(_text.CharAt(_sequence[i]));
        return sb.ToString();
    }

    /// <summary>Largest chunk allocated by the content layer, in bytes, for self-tests.</summary>
    public long MaxChunkBytes =>
        Math.Max(Math.Max(_text.MaxChunkBytes, _sequence.MaxBlockBytes), Math.Max(_array.MaxChunkBytes, Math.Max(_mapKeys.MaxChunkBytes, _mapValues.MaxChunkBytes)));
}
