#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using YSharp.Integration;
using YSharp.Store;

namespace YSharp;

/// <summary>What a decoded struct is (Yjs <c>Item</c>, <c>GC</c> or <c>Skip</c> as <c>decodeUpdate</c> lists them).</summary>
public enum UpdateStructKind : byte
{
    Item,
    Gc,
    Skip,
}

/// <summary>What an item's content is (Yjs <c>ContentDeleted</c>, <c>ContentString</c> and the other <c>Content*</c> classes); none for a GC or Skip.</summary>
public enum UpdateContentKind : byte
{
    None,
    Deleted,
    String,
    Any,
    Json,
    Binary,
    Embed,
    Format,
    Type,
    Doc,
}

/// <summary>
/// One struct of a decoded update, read-only. An item names its parent by <see cref="ParentRoot"/> (a root type name), by <see cref="ParentId"/>
/// (the item whose content is the parent type), or by neither when the parent is inherited from an origin. The content fields that apply:
/// <list type="bullet">
/// <item><see cref="UpdateContentKind.String"/>: <see cref="Text"/>.</item>
/// <item><see cref="UpdateContentKind.Any"/>, <see cref="UpdateContentKind.Json"/>: <see cref="Values"/>.</item>
/// <item><see cref="UpdateContentKind.Binary"/>: <see cref="Bytes"/>.</item>
/// <item><see cref="UpdateContentKind.Embed"/>: <see cref="Values"/> holds the embedded value.</item>
/// <item><see cref="UpdateContentKind.Format"/>: <see cref="Key"/> and <see cref="Values"/> (the attribute value; null ends a format).</item>
/// <item><see cref="UpdateContentKind.Type"/>: <see cref="TypeName"/> (the Yjs class, such as <c>YMap</c>) and, for an XML element or hook, <see cref="Key"/> (its node name).</item>
/// <item><see cref="UpdateContentKind.Doc"/>: <see cref="Key"/> (the subdocument guid) and <see cref="Values"/> (its wire options).</item>
/// </list>
/// </summary>
public readonly struct UpdateStruct
{
    internal UpdateStruct(in StructRef r)
    {
        Kind = r.Kind switch { RefKind.Gc => UpdateStructKind.Gc, RefKind.Skip => UpdateStructKind.Skip, _ => UpdateStructKind.Item };
        Id = new ItemId(r.Client, r.Clock);
        Length = r.Length;
        if (Kind != UpdateStructKind.Item) return;
        Origin = r.Origin;
        RightOrigin = r.RightOrigin;
        ParentRoot = r.ParentRoot;
        ParentId = r.ParentId;
        ParentSub = r.ParentSub;
        Text = r.Text;
        Values = r.Values;
        Content = r.Content switch
        {
            ContentKind.Deleted => UpdateContentKind.Deleted,
            ContentKind.String => UpdateContentKind.String,
            ContentKind.Any => UpdateContentKind.Any,
            ContentKind.Json => UpdateContentKind.Json,
            ContentKind.Binary => UpdateContentKind.Binary,
            ContentKind.Embed => UpdateContentKind.Embed,
            ContentKind.Format => UpdateContentKind.Format,
            ContentKind.Type => UpdateContentKind.Type,
            ContentKind.Doc => UpdateContentKind.Doc,
            _ => UpdateContentKind.None,
        };
        switch (r.Payload)
        {
            case byte[] bytes:
                Bytes = bytes;
                break;
            case EmbedContent embed:
                Values = new[] { embed.Value };
                break;
            case FormatContent format:
                Key = format.Key;
                Values = new[] { format.Value };
                break;
            case Branch branch:
                TypeName = TypeRefs.ClassName(branch.TypeRef);
                Key = branch.TypeName;
                break;
            case SubdocContent subdoc:
                Key = subdoc.Guid;
                Values = new[] { subdoc.Options };
                break;
        }
    }

    public UpdateStructKind Kind { get; }

    /// <summary>The id of the struct's first element.</summary>
    public ItemId Id { get; }

    public long Length { get; }

    public ItemId? Origin { get; }

    public ItemId? RightOrigin { get; }

    public string? ParentRoot { get; }

    public ItemId? ParentId { get; }

    /// <summary>The map key, for an item in a map.</summary>
    public string? ParentSub { get; }

    public UpdateContentKind Content { get; }

    public string? Text { get; }

    public ReadOnlyMemory<YValue> Values { get; }

    public ReadOnlyMemory<byte> Bytes { get; }

    public string? Key { get; }

    public string? TypeName { get; }
}

/// <summary>One deleted range of a decoded update's delete set.</summary>
public readonly record struct UpdateDeleteRange(ulong Client, long Clock, long Length);

/// <summary>A decoded update, read-only (Yjs <c>decodeUpdate</c>): the structs in the order the update lists them, then its delete set.</summary>
public sealed class DecodedUpdate
{
    private readonly List<(ulong Client, int Start)> _blocks;
    private UpdateStruct[]? _structViews;
    private UpdateDeleteRange[]? _deleteViews;

    internal DecodedUpdate(List<StructRef> structs, List<(ulong Client, int Start)> blocks, UpdateRefs refs, YLimits limits, int length)
    {
        InReadOrder = structs;
        _blocks = blocks;
        Refs = refs;
        DecodeLimits = limits;
        Length = length;
    }

    /// <summary>The structs as the update functions read them; nothing changes them, so a relay and a document can share them.</summary>
    internal List<StructRef> InReadOrder { get; }

    /// <summary>The delete ranges, in update order.</summary>
    internal UpdateRefs Refs { get; }

    /// <summary>The limits the update was decoded under.</summary>
    internal YLimits DecodeLimits { get; }

    /// <summary>The length of the bytes it was decoded from.</summary>
    internal int Length { get; }

    public IReadOnlyList<UpdateStruct> Structs => _structViews ??= StructViews();

    public IReadOnlyList<UpdateDeleteRange> Deletes => _deleteViews ??= DeleteViews();

    /// <summary>True when the update carries no struct and no deletion (what an up-to-date peer's Step2 holds).</summary>
    public bool IsEmpty => InReadOrder.Count == 0 && Refs.Deletes.Count == 0;

    /// <summary>
    /// Writes the update in <paramref name="format"/>: the bytes <see cref="YUpdate.TryConvert"/> writes for the update this was decoded from, so
    /// a server that decodes once can store and apply the same update (TASK-14).
    /// </summary>
    public void Encode(IBufferWriter<byte> output, UpdateFormat format = UpdateFormat.V1)
    {
        ArgumentNullException.ThrowIfNull(output);
        YUpdate.Write(format, InReadOrder, YUpdate.InWriteOrder(Refs.Deletes), output);
    }

    /// <summary>
    /// Whether the update may be taken as checked against <paramref name="limits"/>: its size and struct count within them, and decoded under a
    /// string and nesting bound no looser than theirs (those are checked only while decoding).
    /// </summary>
    internal bool Within(YLimits limits) =>
        DecodeLimits.MaxStringBytes <= limits.MaxStringBytes && DecodeLimits.MaxAnyDepth <= limits.MaxAnyDepth
        && Length <= limits.MaxUpdateBytes && InReadOrder.Count <= limits.MaxStructsPerUpdate;

    /// <summary>
    /// The update as a document's decode reads the same bytes: a list per client block (a repeated client's later block replaces the earlier
    /// one), adjacent GC or Skip structs joined, and the delete ranges. New lists, as integration consumes them.
    /// </summary>
    internal UpdateRefs ForApply()
    {
        var refs = new UpdateRefs();
        refs.ExpectClients(Math.Min(_blocks.Count, 16384));
        for (var b = 0; b < _blocks.Count; b++)
        {
            var (client, start) = _blocks[b];
            var end = b + 1 < _blocks.Count ? _blocks[b + 1].Start : InReadOrder.Count;
            var list = new List<StructRef>(end - start);
            for (var i = start; i < end; i++)
            {
                var r = InReadOrder[i];
                if (r.Kind != RefKind.Item && list.Count > 0 && list[^1].Kind == r.Kind)
                {
                    var last = list[^1];
                    last.Length += r.Length;
                    list[^1] = last;
                    continue;
                }

                list.Add(r);
            }

            refs.SetClient(client, list);
        }

        refs.Deletes.AddRange(Refs.Deletes);
        return refs;
    }

    private UpdateStruct[] StructViews()
    {
        var views = new UpdateStruct[InReadOrder.Count];
        for (var i = 0; i < views.Length; i++) views[i] = new UpdateStruct(InReadOrder[i]);
        return views;
    }

    private UpdateDeleteRange[] DeleteViews()
    {
        var deletes = new UpdateDeleteRange[Refs.Deletes.Count];
        for (var i = 0; i < deletes.Length; i++)
        {
            var d = Refs.Deletes[i];
            deletes[i] = new UpdateDeleteRange(d.Client, d.Clock, d.Length);
        }

        return deletes;
    }
}

public static partial class YUpdate
{
    /// <summary>
    /// Decodes <paramref name="update"/> into a read-only view (Yjs <c>decodeUpdate</c>, <c>decodeUpdateV2</c>), validated in full against
    /// <paramref name="limits"/> as the other update functions validate it. Skips are listed as the update writes them.
    /// </summary>
    public static YStatus TryDecode(ReadOnlySpan<byte> update, out DecodedUpdate decoded, UpdateFormat format = UpdateFormat.V1, YLimits? limits = null)
    {
        decoded = null!;
        limits ??= YLimits.Default;
        var blocks = new List<(ulong Client, int Start)>();
        var error = DecodeForFunctions(update, format, limits, out var refs, out var structs, blocks);
        if (error != YError.None) return new YStatus(error);
        decoded = new DecodedUpdate(structs, blocks, refs, limits, update.Length);
        return default;
    }
}
