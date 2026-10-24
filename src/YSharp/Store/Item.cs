using System;
using System.Runtime.CompilerServices;

namespace YSharp.Store;

/// <summary>
/// What a struct is and what its content field means (Yjs <c>GC</c> and the <c>Content*</c> classes). Payloads live in the document's content
/// layer; a struct holds one index into it, never a payload object.
/// </summary>
internal enum ContentKind : byte
{
    /// <summary>Released content, only the length is kept (Yjs <c>ContentDeleted</c>). The content field holds the high 32 bits of the length.</summary>
    Deleted = 0,

    /// <summary>A garbage-collected struct (Yjs <c>GC</c>): id and length only, no parent. The content field holds the high 32 bits of the length.</summary>
    Gc = 1,

    /// <summary>Text (Yjs <c>ContentString</c>): the content field is the address in <see cref="TextBuffer"/>; the slice length is the item length.</summary>
    String = 2,

    /// <summary>Any values (Yjs <c>ContentAny</c>): the content field is the first index in <see cref="ValueColumn"/>; the run length is the item length.</summary>
    Any = 3,

    /// <summary>Legacy JSON values (Yjs <c>ContentJSON</c>), stored as <see cref="Any"/> is.</summary>
    Json = 4,

    /// <summary>One byte string (Yjs <c>ContentBinary</c>): the content field is an index in the store's payload table.</summary>
    Binary = 5,

    /// <summary>One embedded value (Yjs <c>ContentEmbed</c>): a payload-table index.</summary>
    Embed = 6,

    /// <summary>One formatting attribute (Yjs <c>ContentFormat</c>, not countable): a payload-table index.</summary>
    Format = 7,

    /// <summary>A nested shared type (Yjs <c>ContentType</c>): a payload-table index.</summary>
    Type = 8,

    /// <summary>A subdocument (Yjs <c>ContentDoc</c>): a payload-table index.</summary>
    Doc = 9,
}

/// <summary>Per-item bit flags, as Yjs <c>Item.info</c>.</summary>
[Flags]
internal enum ItemFlags : byte
{
    None = 0,
    Keep = 1,
    Countable = 2,
    Deleted = 4,
    Marker = 8,
}

/// <summary>
/// One struct: an item or a GC struct (ADR 0023, object per item). Neighbour links and the parent are direct references; the id, origin and right
/// origin are inline (a client is its slot in the client table, a clock is 64-bit), so no link needs a separate object (ADR 0018). It carries no
/// finalizer, lock, event, delegate or closure. Content is one index into the document's content layer.
/// <para>
/// Layout on x64: 16 bytes of header, 3 references, 3 longs, 5 ints and 2 bytes: 88 bytes, the size of the spike's item. A map item is a
/// <see cref="KeyedItem"/> and adds its key. Only <c>Store/</c> touches this type; other modules hold an <see cref="ItemRef"/>.
/// </para>
/// </summary>
internal class Item
{
    public const int NoClient = -1;

    internal Item? Left;
    internal Item? Right;

    /// <summary>The list or map that holds the item; null for a GC struct.</summary>
    internal Branch? Parent;

    internal long Clock;

    /// <summary>Origin id: the left neighbour's last id at creation. <see cref="OriginClient"/> is <see cref="NoClient"/> when there is none.</summary>
    internal long OriginClock;

    /// <summary>Right-origin id: the right neighbour's id at creation. <see cref="RightOriginClient"/> is <see cref="NoClient"/> when there is none.</summary>
    internal long RightOriginClock;

    /// <summary>Own client, as a slot in the client table.</summary>
    internal int Client;

    internal int OriginClient;
    internal int RightOriginClient;

    /// <summary>The low 32 bits of the length (all of it, except for <see cref="ContentKind.Deleted"/> and <see cref="ContentKind.Gc"/>).</summary>
    internal uint LengthLow;

    /// <summary>The content index; for <see cref="ContentKind.Deleted"/> and <see cref="ContentKind.Gc"/>, the high 32 bits of the length.</summary>
    internal int Content;

    internal ContentKind Kind;
    internal ItemFlags Flags;

    internal Item() { }

    /// <summary>Length in clock units. Only released content can exceed 2^31 (a deleted or GC range from the wire), so only it uses the high word.</summary>
    internal long Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => HasNoContent ? ((long)(uint)Content << 32) | LengthLow : LengthLow;
    }

    internal void SetLength(long length)
    {
        if (HasNoContent)
        {
            Content = (int)(uint)(length >> 32);
            LengthLow = (uint)length;
        }
        else
        {
            if (length > int.MaxValue) throw new YInternalException("A struct with content cannot be longer than 2^31 - 1.");
            LengthLow = (uint)length;
        }
    }

    internal bool HasNoContent => Kind <= ContentKind.Gc;

    internal bool IsGc => Kind == ContentKind.Gc;

    internal bool Deleted => (Flags & ItemFlags.Deleted) != 0;

    internal bool Countable => (Flags & ItemFlags.Countable) != 0;

    internal bool Keep
    {
        get => (Flags & ItemFlags.Keep) != 0;
        set => Flags = value ? Flags | ItemFlags.Keep : Flags & ~ItemFlags.Keep;
    }

    internal bool Marker
    {
        get => (Flags & ItemFlags.Marker) != 0;
        set => Flags = value ? Flags | ItemFlags.Marker : Flags & ~ItemFlags.Marker;
    }

    /// <summary>The key slot of a map item, or -1 for a sequence item.</summary>
    internal virtual int ParentSub => -1;

    /// <summary>Clock of the last element (Yjs <c>lastId</c>).</summary>
    internal long LastClock => Clock + Length - 1;

    internal static bool IsCountableKind(ContentKind kind) => kind is not (ContentKind.Deleted or ContentKind.Gc or ContentKind.Format);
}

/// <summary>An item with a map key (Yjs <c>parentSub</c> not null). The key is a slot in the store's <see cref="KeyTable"/>.</summary>
internal sealed class KeyedItem : Item
{
    internal readonly int Key;

    internal KeyedItem(int key) => Key = key;

    internal override int ParentSub => Key;
}
