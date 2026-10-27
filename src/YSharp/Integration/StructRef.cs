#nullable enable
using System;
using System.Collections.Generic;
using YSharp.Store;

namespace YSharp.Integration;

/// <summary>What a decoded struct is (Yjs <c>Item</c>, <c>GC</c> or <c>Skip</c> as read from an update).</summary>
internal enum RefKind : byte
{
    Item,
    Gc,
    Skip,
}

/// <summary>
/// One struct as a codec decodes it, before integration (Yjs reads these as <c>Item</c>, <c>GC</c> and <c>Skip</c> objects and integrates them
/// later). It is codec-independent: the V1 and V2 decoders fill it, the integrator turns it into a stored struct, and structs that cannot be
/// integrated yet are kept in this form as the document's pending state.
/// <para>
/// An item names its parent in at most one way: <see cref="ParentRoot"/> (a root type name), <see cref="ParentId"/> (the id of the item whose
/// content is the parent type), or neither when an origin is present and the parent is inherited from a neighbour (STR-15). Content is
/// <see cref="Content"/> plus one of <see cref="Text"/> (string), <see cref="Values"/> (Any and JSON) and <see cref="Payload"/> (binary,
/// embed, format, subdocument: an opaque object the content layer stores; a nested type: its <see cref="Branch"/>). A deleted content carries
/// only <see cref="Length"/>.
/// </para>
/// </summary>
internal struct StructRef
{
    public RefKind Kind;
    public ulong Client;
    public long Clock;
    public long Length;
    public ItemId? Origin;
    public ItemId? RightOrigin;
    public string? ParentRoot;
    public ItemId? ParentId;
    public string? ParentSub;
    public ContentKind Content;
    public string? Text;
    public ReadOnlyMemory<YValue> Values;
    public object? Payload;

    public readonly long End => Clock + Length;

    public static StructRef Gc(ulong client, long clock, long length) =>
        new() { Kind = RefKind.Gc, Client = client, Clock = clock, Length = length, Content = ContentKind.Gc };

    public static StructRef Skip(ulong client, long clock, long length) =>
        new() { Kind = RefKind.Skip, Client = client, Clock = clock, Length = length, Content = ContentKind.Gc };

    /// <summary>A string item; its length is the UTF-16 length of <paramref name="text"/>.</summary>
    public static StructRef String(ulong client, long clock, string text) =>
        new() { Kind = RefKind.Item, Client = client, Clock = clock, Length = text.Length, Content = ContentKind.String, Text = text };

    /// <summary>An Any (or, with <paramref name="kind"/>, JSON) item holding <paramref name="values"/>.</summary>
    public static StructRef Any(ulong client, long clock, ReadOnlyMemory<YValue> values, ContentKind kind = ContentKind.Any) =>
        new() { Kind = RefKind.Item, Client = client, Clock = clock, Length = values.Length, Content = kind, Values = values };

    public static StructRef Deleted(ulong client, long clock, long length) =>
        new() { Kind = RefKind.Item, Client = client, Clock = clock, Length = length, Content = ContentKind.Deleted };

    /// <summary>A one-element item whose content is an opaque payload (binary, embed, format, subdocument) or a nested type's <see cref="Branch"/>.</summary>
    public static StructRef OfPayload(ulong client, long clock, ContentKind kind, object payload) =>
        new() { Kind = RefKind.Item, Client = client, Clock = clock, Length = 1, Content = kind, Payload = payload };

    /// <summary>
    /// The part from <paramref name="offset"/> on (Yjs <c>content.splice</c> plus the id shift of <c>Item.integrate</c> and <c>sliceStruct</c>).
    /// The origin becomes the id just before the new start; the right origin and the parent are kept. A string split between the halves of a
    /// surrogate pair starts with U+FFFD, as <c>ContentString.splice</c> makes it.
    /// </summary>
    public readonly StructRef Slice(long offset)
    {
        if (offset <= 0) return this;
        if (offset >= Length) throw new YInternalException($"A slice at {offset} is outside a struct of length {Length}.");
        var r = this;
        r.Clock = Clock + offset;
        r.Length = Length - offset;
        if (Kind != RefKind.Item) return r;
        r.Origin = new ItemId(Client, Clock + offset - 1);
        switch (Content)
        {
            case ContentKind.String:
                var i = (int)offset;
                r.Text = char.IsHighSurrogate(Text![i - 1]) ? "�" + Text.Substring(i + 1) : Text.Substring(i);
                break;
            case ContentKind.Any:
            case ContentKind.Json:
                r.Values = Values.Slice((int)offset);
                break;
            case ContentKind.Deleted:
                break;
            default:
                throw new YInternalException($"A {Content} struct has length 1 and cannot be sliced.");
        }

        return r;
    }
}

/// <summary>One client's decoded structs, in clock order (Yjs <c>{ i, refs }</c> of <c>readClientsStructRefs</c>).</summary>
internal sealed class ClientRefs
{
    public ClientRefs(ulong client, List<StructRef> refs)
    {
        Client = client;
        Refs = refs;
    }

    public ulong Client { get; }

    public List<StructRef> Refs { get; set; }

    /// <summary>The index of the next struct to integrate.</summary>
    internal int Next;
}

/// <summary>
/// A decoded update: every client's structs and the delete ranges, in the order the update lists them. A codec builds it after validating the
/// whole update (ADR 0005, atomic decode); the integrator consumes it. A client listed twice keeps its last block, as Yjs does.
/// </summary>
internal sealed class UpdateRefs
{
    private static readonly Dictionary<ulong, ClientRefs> NoClients = new();

    // created on the first client: the update functions decode only delete ranges into it (HOS-AMP)
    private Dictionary<ulong, ClientRefs>? _clients;

    public IEnumerable<ClientRefs> Clients => ClientValues;

    /// <summary>The clients without boxing an enumerator (the hot path's pending check).</summary>
    internal Dictionary<ulong, ClientRefs>.ValueCollection ClientValues => (_clients ?? NoClients).Values;

    public int ClientCount => _clients?.Count ?? 0;

    /// <summary>The delete ranges in update order.</summary>
    public List<DeleteRange> Deletes { get; } = new();

    /// <summary>Sizes the client table for <paramref name="clients"/> clients before they are set, so it is allocated once (BUD-12).</summary>
    public void ExpectClients(int clients)
    {
        if (clients > 0) (_clients ??= new(clients)).EnsureCapacity(clients);
    }

    public void SetClient(ulong client, List<StructRef> refs) => (_clients ??= new())[client] = new ClientRefs(client, refs);

    private void SetClient(ulong client, List<StructRef> refs, int next) => (_clients ??= new())[client] = new ClientRefs(client, refs) { Next = next };

    public void AddDelete(ulong client, long clock, long length) => Deletes.Add(new DeleteRange(client, clock, length));

    internal bool TryGet(ulong client, out ClientRefs refs)
    {
        refs = null!;
        return _clients != null && _clients.TryGetValue(client, out refs!);
    }

    internal void Remove(ulong client) => _clients?.Remove(client);

    /// <summary>A copy whose clients can be consumed without consuming this update (the struct lists are shared; integration never changes them).</summary>
    internal UpdateRefs Copy()
    {
        var copy = new UpdateRefs();
        if (_clients != null)
            foreach (var (client, refs) in _clients) copy.SetClient(client, refs.Refs, refs.Next);
        copy.Deletes.AddRange(Deletes);
        return copy;
    }
}
