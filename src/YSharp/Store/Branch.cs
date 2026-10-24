using System;

namespace YSharp.Store;

/// <summary>
/// The storage side of a shared type (Yjs <c>AbstractType</c>'s <c>_start</c>, <c>_length</c>, <c>_map</c>, <c>_searchMarker</c> and <c>_item</c>):
/// the first item of its sequence, its visible length, the key table of its map entries and its search markers. The key table and the markers
/// are created on first use, so a type that never uses them holds neither. The shared type that owns the branch lives in <c>Types/</c>.
/// </summary>
internal sealed class Branch
{
    /// <summary>The first item of the sequence, deleted or not (Yjs <c>_start</c>).</summary>
    internal Item? Start;

    /// <summary>The last item of the sequence, deleted or not; null when it is empty. Kept so a push needs no walk (ADR 0023).</summary>
    internal Item? End;

    /// <summary>Sum of the lengths of the countable, undeleted sequence items (Yjs <c>_length</c>).</summary>
    internal int Length;

    /// <summary>The item whose content is this type (Yjs <c>_item</c>); null for a root type.</summary>
    internal Item? Item;

    /// <summary>The shared type object that owns this branch, set by <c>Types/</c>.</summary>
    internal object? Owner;

    private readonly bool _useMarkers;
    private SearchMarkers? _markers;
    private MapKeyTable? _map;

    /// <param name="useMarkers">Whether the type keeps search markers (Yjs creates them for arrays, text and XML children).</param>
    internal Branch(bool useMarkers = false) => _useMarkers = useMarkers;

    /// <summary>The search markers, or null for a type that does not use them.</summary>
    internal SearchMarkers? Markers => _markers;

    internal SearchMarkers? MarkersFor(StructStore store)
    {
        if (_markers is null && _useMarkers) _markers = new SearchMarkers(store.MarkerClock);
        return _markers;
    }

    /// <summary>The key table, or null when no keyed item was ever linked.</summary>
    internal MapKeyTable? Map => _map;

    internal MapKeyTable MapFor(StructStore store) => _map ??= new MapKeyTable(store.Keys);
}
