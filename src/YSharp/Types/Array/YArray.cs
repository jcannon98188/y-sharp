#nullable enable
using System;
using System.Collections.Generic;
using YSharp.Store;

namespace YSharp;

// ---------------------------------------------------------------- YArray

// Yjs YArray over the list core (Types/Core/TypeList.cs). Every member takes an implicit transaction unless the calling thread has one open
// (CC-8). Until it is integrated, writes go to the prelim buffer as JS splices _prelimContent; reads see nothing (T-0.4), and integration
// inserts the buffer at 0 in the integrating transaction (T-0.6).
public sealed class YArray : YType
{
    internal override byte WireTypeRef => Store.TypeRefs.Array;

    private List<YEntry>? _prelim = new();

    public YArray() { }

    /// <summary>Yjs <c>YArray.from</c>: a prelim array holding <paramref name="items"/>.</summary>
    public static YArray From(IEnumerable<YEntry> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var array = new YArray();
        var content = new List<YEntry>(items);
        TypeList.Validate(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(content), undefinedAllowed: false);
        array._prelim!.AddRange(content);
        return array;
    }

    public int Length
    {
        get
        {
            if (AttachedDoc is not { } doc) return 0;
            var h = doc.EnterRead();
            try
            {
                return Branch.Length;
            }
            finally
            {
                doc.Leave(h);
            }
        }
    }

    public YEntry this[int index]   // ArgumentOutOfRangeException (REVIEW: JS returns undefined)
    {
        get
        {
            if (AttachedDoc is not { } doc) throw new ArgumentOutOfRangeException(nameof(index));
            var h = doc.EnterRead();
            try
            {
                return TypeList.Get(this, index);
            }
            finally
            {
                doc.Leave(h);
            }
        }
    }

    public void Insert(int index, ReadOnlySpan<YEntry> content)   // index > Length throws, as JS ("Length exceeded!")
    {
        if (AttachedDoc is not { } doc)
        {
            if (index < 0 || index > _prelim!.Count) throw new ArgumentOutOfRangeException(nameof(index), "Length exceeded!");
            TypeList.Validate(content, undefinedAllowed: false);
            _prelim.InsertRange(index, content.ToArray());
            return;
        }

        var h = doc.EnterWrite(null, local: true);
        try
        {
            TypeList.Insert(h.Tx, this, index, content);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    public void Push(ReadOnlySpan<YEntry> content)
    {
        if (AttachedDoc is not { } doc)
        {
            TypeList.Validate(content, undefinedAllowed: false);
            _prelim!.AddRange(content.ToArray());
            return;
        }

        var h = doc.EnterWrite(null, local: true);
        try
        {
            TypeList.Push(h.Tx, this, content);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    public void Unshift(ReadOnlySpan<YEntry> content) => Insert(0, content);

    public void Delete(int index, int length = 1)   // partial effect then throws, as JS (A-5)
    {
        if (AttachedDoc is not { } doc)
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            // JS splices the prelim buffer, which never throws: a range past the end removes what exists
            if (index < _prelim!.Count) _prelim.RemoveRange(index, Math.Min(length, _prelim.Count - index));
            return;
        }

        var h = doc.EnterWrite(null, local: true);
        try
        {
            TypeList.Delete(h.Tx, this, index, length);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    /// <summary>Copies <c>destination.Length</c> elements from <paramref name="start"/>; the range must lie inside the array.</summary>
    public void CopyTo(int start, Span<YEntry> destination)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        if (AttachedDoc is not { } doc)
        {
            if (start > 0 || !destination.IsEmpty) throw new ArgumentOutOfRangeException(nameof(start));
            return;
        }

        var h = doc.EnterRead();
        try
        {
            if ((long)start + destination.Length > Branch.Length) throw new ArgumentOutOfRangeException(nameof(destination));
            var slice = TypeList.Slice(this, start, start + destination.Length);
            for (var i = 0; i < slice.Count; i++) destination[i] = slice[i];
        }
        finally
        {
            doc.Leave(h);
        }
    }

    public YEntry[] ToArray()
    {
        if (AttachedDoc is not { } doc) return Array.Empty<YEntry>();
        var h = doc.EnterRead();
        try
        {
            return TypeList.ToEntries(this).ToArray();
        }
        finally
        {
            doc.Leave(h);
        }
    }

    // JS slice: negative indexes count from the end, an end past Length stops at the end; end defaults to Length (A-8)
    public YEntry[] Slice(int start, int? end = null)
    {
        if (AttachedDoc is not { } doc) return Array.Empty<YEntry>();
        var h = doc.EnterRead();
        try
        {
            return TypeList.Slice(this, start, end ?? Branch.Length).ToArray();
        }
        finally
        {
            doc.Leave(h);
        }
    }

    public ListEnumerator GetEnumerator()   // live inside an explicit transaction; snapshot otherwise (CC-9)
    {
        if (AttachedDoc is not { } doc) return new ListEnumerator(Array.Empty<YEntry>());
        if (doc.CurrentTransaction is { } tx) return new ListEnumerator(tx, doc, Branch);
        return new ListEnumerator(ToArray());
    }

    /// <summary>Yjs <c>YArray.clone</c>: a prelim array with the elements, nested types cloned.</summary>
    public override YType Clone()
    {
        var copy = new YArray();
        foreach (var e in ToArray()) copy._prelim!.Add(e.Kind == YEntryKind.Type ? e.Type!.Clone() : e);
        return copy;
    }

    /// <summary>Yjs <c>YArray.toJSON</c>: nested types converted, values and byte strings as they are.</summary>
    public override YValue ToJson()
    {
        var entries = ToArray();
        var values = new YValue[entries.Length];
        for (var i = 0; i < entries.Length; i++) values[i] = ListEnumerator.Json(entries[i]);
        return YValue.Array(values);
    }

    /// <summary>Yjs <c>YArray._integrate</c>: the buffered content goes in at 0, inside the integrating transaction.</summary>
    internal override void OnIntegrated(Transaction tx)
    {
        var prelim = _prelim;
        _prelim = null;
        if (prelim is { Count: > 0 }) TypeList.Insert(tx, this, 0, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(prelim));
    }
}

/// <summary>
/// Enumerates a list type. Taken inside an explicit transaction it walks the live items and throws <see cref="YLifetimeException"/> once that
/// transaction ended; taken outside one it enumerates a snapshot and holds no lock (CC-9).
/// </summary>
public struct ListEnumerator
{
    private readonly YEntry[]? _snapshot;
    private readonly Transaction? _tx;
    private readonly Doc? _doc;
    private ItemRef _next;
    private int _offset;
    private int _index;
    private YEntry _current;

    internal ListEnumerator(YEntry[] snapshot)
    {
        _snapshot = snapshot;
        _index = -1;
    }

    internal ListEnumerator(Transaction tx, Doc doc, Branch branch)
    {
        _tx = tx;
        _doc = doc;
        _next = StructStore.StartOf(branch);
    }

    public YEntry Current => _current;

    public bool MoveNext()
    {
        if (_snapshot is not null)
        {
            if (++_index >= _snapshot.Length) return false;
            _current = _snapshot[_index];
            return true;
        }

        if (_tx is null) return false;
        if (!_tx.IsValid) throw new YLifetimeException();
        // an item split under us keeps its left part here, so the offset carries over into its right part
        for (; !_next.IsNull; _next = _next.Right, _offset = 0)
        {
            if (_next.IsDeleted || !_next.IsCountable || _offset >= _next.Length) continue;
            _current = TypeList.ContentAt(_doc!, _next, _offset++);
            return true;
        }

        return false;
    }

    /// <summary>An element as Yjs <c>toJSON</c> shows it: a nested type converted, anything else as it is.</summary>
    internal static YValue Json(in YEntry e) => e.Kind switch
    {
        YEntryKind.Type => e.Type!.ToJson(),
        YEntryKind.Doc => throw Sketch.NI,   // subdocuments (spec 33)
        _ => e.Value,
    };
}
