#nullable enable
using System;
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
    internal DecodedUpdate(UpdateStruct[] structs, UpdateDeleteRange[] deletes)
    {
        Structs = structs;
        Deletes = deletes;
    }

    public IReadOnlyList<UpdateStruct> Structs { get; }

    public IReadOnlyList<UpdateDeleteRange> Deletes { get; }

    /// <summary>True when the update carries no struct and no deletion (what an up-to-date peer's Step2 holds).</summary>
    public bool IsEmpty => Structs.Count == 0 && Deletes.Count == 0;
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
        var error = DecodeForFunctions(update, format, limits, out var refs, out var structs);
        if (error != YError.None) return new YStatus(error);
        var views = new UpdateStruct[structs.Count];
        for (var i = 0; i < views.Length; i++) views[i] = new UpdateStruct(structs[i]);
        var deletes = new UpdateDeleteRange[refs.Deletes.Count];
        for (var i = 0; i < deletes.Length; i++)
        {
            var d = refs.Deletes[i];
            deletes[i] = new UpdateDeleteRange(d.Client, d.Clock, d.Length);
        }

        decoded = new DecodedUpdate(views, deletes);
        return default;
    }
}
