using System;
using System.Collections.Generic;
using System.Text;
using YSharp.StoreSpike.Content;

namespace YSharp.StoreSpike.Harness;

/// <summary>
/// The harness control: a store built only from the shared content layer, with no CRDT logic. It proves the driver, the workloads and the
/// measurements work end to end and gives the candidates a baseline for the shared layer's own cost. It is not a storage candidate and is
/// never a winner. Its key table is a plain dictionary, which the candidates must not copy: the key table belongs to each candidate.
/// </summary>
public sealed class ControlStore : IStoreDriver
{
    private readonly TextBuffer _text = new();
    private readonly ChunkedList<ContentSlice> _sequence = new();
    private readonly ChunkedList<double> _array = new();
    private readonly MapPayloads _map = new();
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
        _array.Add(value);
        _structs++;
    }

    public void SetKey(string key, double value)
    {
        if (_keys.TryGetValue(key, out var payload))
        {
            _map.SetValue(payload, value);
            return;
        }

        _keys[key] = _map.Add(key.AsSpan(), value);
        _structs++;
    }

    public void Commit() => _commits++;

    /// <summary>The value stored under <paramref name="key"/>, for self-tests.</summary>
    public double ValueOfKey(string key) => _map.ValueOf(_keys[key]);

    /// <summary>The visible text, for self-tests.</summary>
    public string Text()
    {
        var sb = new StringBuilder(_sequence.Count);
        for (var i = 0; i < _sequence.Count; i++) sb.Append(_text.Get(_sequence[i]));
        return sb.ToString();
    }

    /// <summary>Largest chunk allocated by the shared layer, in bytes, for self-tests.</summary>
    public long MaxChunkBytes => Math.Max(_text.MaxChunkBytes, Math.Max(_sequence.MaxBlockBytes, Math.Max(_array.MaxBlockBytes, _map.MaxChunkBytes)));
}
