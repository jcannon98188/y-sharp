#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

// ---------------------------------------------------------------- YMap

public class YMap : YType
{
    internal override byte WireTypeRef => Store.TypeRefs.Map;

    public YMap() { }
    public YMap(IEnumerable<KeyValuePair<string, YEntry>> entries) { }
    public int Count => throw Sketch.NI;
    public T Set<T>(string key, T type) where T : YType => throw Sketch.NI;  // returns the live instance (T-0.12)
    public void Set(string key, YEntry value) => throw Sketch.NI;
    public bool TryGet(string key, out YEntry value) { value = default; throw Sketch.NI; }   // false only when absent
    public bool Has(string key) => throw Sketch.NI;                          // true for a key set to Undefined (T-0.11)
    public bool Delete(string key) => throw Sketch.NI;
    public void Clear() => throw Sketch.NI;
    public IReadOnlyList<string> Keys => throw Sketch.NI;                    // snapshot
    public MapEnumerator GetEnumerator() => throw Sketch.NI;
    public override YType Clone() => throw Sketch.NI;
    public override YValue ToJson() => throw Sketch.NI;
}

public struct MapEnumerator { public KeyValuePair<string, YEntry> Current => throw Sketch.NI; public bool MoveNext() => throw Sketch.NI; }

