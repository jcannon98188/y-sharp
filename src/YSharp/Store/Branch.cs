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

    /// <summary>
    /// Which shared type this is, as the wire names it (Yjs <c>ContentType</c> type refs: <see cref="TypeRefs"/>), or <see cref="TypeRefs.Untyped"/>
    /// for a root that was only named on the wire. The codecs write it for a nested type; <c>Types/</c> sets it when it creates or converts a type.
    /// </summary>
    internal byte TypeRef = TypeRefs.Untyped;

    /// <summary>The node name of an XML element or the hook name of an XML hook (written after the type ref); null otherwise.</summary>
    internal string? TypeName;

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

    /// <summary>
    /// Forgets the sequence, the key table and the markers (Yjs <c>ContentType.gc</c>: <c>_start = null</c>, <c>_map = new Map()</c>), once every
    /// item in them was garbage-collected with the type that holds them.
    /// </summary>
    internal void ClearContent()
    {
        Start = null;
        End = null;
        _map = null;
        _markers = null;
    }
}

/// <summary>The wire type refs of Yjs <c>ContentType</c> (<c>ContentType.js:28-34</c>).</summary>
internal static class TypeRefs
{
    public const byte Array = 0;
    public const byte Map = 1;
    public const byte Text = 2;
    public const byte XmlElement = 3;
    public const byte XmlFragment = 4;
    public const byte XmlHook = 5;
    public const byte XmlText = 6;

    /// <summary>Not a wire value: a root type that was only named by an update (Yjs <c>doc.get(name)</c> with no constructor).</summary>
    public const byte Untyped = 255;

    /// <summary>Whether the type keeps search markers (Yjs creates them for arrays, text and the XML list types, not for maps and hooks).</summary>
    public static bool UsesMarkers(byte typeRef) => typeRef is Array or Text or XmlElement or XmlFragment or XmlText;

    /// <summary>The Yjs class name of a type ref, as the struct dump shows it (ADR 0016).</summary>
    public static string ClassName(byte typeRef) => typeRef switch
    {
        Array => "YArray",
        Map => "YMap",
        Text => "YText",
        XmlElement => "YXmlElement",
        XmlFragment => "YXmlFragment",
        XmlHook => "YXmlHook",
        XmlText => "YXmlText",
        _ => "AbstractType",
    };
}
