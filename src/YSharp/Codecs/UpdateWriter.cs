#nullable enable
using System;
using System.Collections.Generic;
using YSharp.Integration;
using YSharp.Store;

namespace YSharp.Codecs;

/// <summary>
/// Writes updates, generic over the codec: a document's state from a target state vector (Yjs <c>writeStateAsUpdate</c>), decoded structs
/// (<see cref="StructRef"/>, for merged output) and delete sets (Yjs <c>writeDeleteSet</c>). Stored items are written straight from the store,
/// without making strings or struct copies.
/// </summary>
internal static class UpdateWriter
{
    // ------------------------------------------------------------------ the document's state

    /// <summary>
    /// Yjs <c>writeClientsStructs</c>: every client whose state is past <paramref name="target"/>, plus every client the target does not list,
    /// in descending client order, each from the target's clock (a struct the clock lands inside is written from that offset).
    /// </summary>
    public static void WriteClientsStructs<TEncoder>(ref TEncoder encoder, Doc doc, StateVector? target)
        where TEncoder : IUpdateEncoder, allows ref struct
    {
        var store = doc.Store;
        var count = 0;
        for (var rank = store.ClientCount - 1; rank >= 0; rank--)
            if (Writes(store, rank, target, out _)) count++;
        encoder.WriteVarUint((ulong)count);
        for (var rank = store.ClientCount - 1; rank >= 0; rank--)
        {
            if (!Writes(store, rank, target, out var clock)) continue;
            var first = store.StructAtRank(rank, 0);
            if (clock < first.Clock) clock = first.Clock;
            var start = store.FindIndexAtRank(rank, clock);
            var structs = store.StructCountAtRank(rank);
            encoder.WriteVarUint((ulong)(structs - start));
            encoder.WriteClient(store.ClientIdAtRank(rank));
            encoder.WriteVarUint((ulong)clock);
            var s = store.StructAtRank(rank, start);
            WriteStruct(ref encoder, doc, s, clock - s.Clock);
            for (var i = start + 1; i < structs; i++) WriteStruct(ref encoder, doc, store.StructAtRank(rank, i), 0);
        }
    }

    /// <summary>Whether the client at <paramref name="rank"/> is written, and from which clock.</summary>
    private static bool Writes(StructStore store, int rank, StateVector? target, out long clock)
    {
        clock = 0;
        if (store.StructCountAtRank(rank) == 0) return false;
        if (target is null) return true;
        // a client the target does not list is written from 0, even when its state is 0 (Yjs adds it with clock 0)
        return !target.TryGetClock(store.ClientIdAtRank(rank), out clock) || store.StateAtRank(rank) > clock;
    }

    /// <summary>A stored struct (Yjs <c>Item.write</c> and <c>GC.write</c>), from <paramref name="offset"/>.</summary>
    public static void WriteStruct<TEncoder>(ref TEncoder encoder, Doc doc, ItemRef s, long offset)
        where TEncoder : IUpdateEncoder, allows ref struct
    {
        var store = doc.Store;
        if (s.IsGc)
        {
            encoder.WriteInfo(0);
            encoder.WriteLen(s.Length - offset);
            return;
        }

        var origin = offset > 0 ? new ItemId(store.ClientOf(s), s.Clock + offset - 1) : store.OriginOf(s);
        var rightOrigin = store.RightOriginOf(s);
        var keyed = s.HasParentSub;
        encoder.WriteInfo(Info(s.Kind, origin.HasValue, rightOrigin.HasValue, keyed));
        if (origin is { } o) encoder.WriteLeftId(o);
        if (rightOrigin is { } ro) encoder.WriteRightId(ro);
        if (origin is null && rightOrigin is null)
        {
            var parent = s.Parent ?? throw new YInternalException("An item without a parent cannot be written.");
            var parentItem = StructStore.ItemOf(parent);
            if (parentItem.IsNull)
            {
                encoder.WriteParentInfo(true);
                encoder.WriteString(doc.RootNameOf(parent) ?? throw new YInternalException("A root type has no name."));
            }
            else
            {
                encoder.WriteParentInfo(false);
                encoder.WriteLeftId(store.IdOf(parentItem));
            }

            if (keyed) encoder.WriteString(store.ParentSubKey(s));
        }

        WriteContent(ref encoder, store, s, offset);
    }

    private static void WriteContent<TEncoder>(ref TEncoder encoder, StructStore store, ItemRef s, long offset)
        where TEncoder : IUpdateEncoder, allows ref struct
    {
        switch (s.Kind)
        {
            case ContentKind.Deleted:
                encoder.WriteLen(s.Length - offset);
                break;
            case ContentKind.Json:
            case ContentKind.Any:
                encoder.WriteLen(s.Length - offset);
                for (var i = (int)offset; i < s.Length; i++)
                {
                    var v = store.GetValue(s, i);
                    if (s.Kind == ContentKind.Any) encoder.WriteAny(v);
                    else encoder.WriteJsonElement(v);
                }

                break;
            case ContentKind.String:
                if (store.TryGetTextSpan(s, out var span)) encoder.WriteString(span[(int)offset..]);
                else encoder.WriteString(store.GetString(s).AsSpan((int)offset));
                break;
            default:
                WritePayload(ref encoder, s.Kind, store.GetPayload(s));
                break;
        }
    }

    // ------------------------------------------------------------------ decoded structs

    /// <summary>Decoded structs grouped in client blocks, in the given order: the block count, then per block its struct count, client and start clock.</summary>
    public static void WriteBlocks<TEncoder>(ref TEncoder encoder, List<StructRef> structs)
        where TEncoder : IUpdateEncoder, allows ref struct
    {
        var blocks = 0;
        for (var i = 0; i < structs.Count; i++)
            if (i == 0 || structs[i].Client != structs[i - 1].Client) blocks++;
        encoder.WriteVarUint((ulong)blocks);
        var start = 0;
        while (start < structs.Count)
        {
            var end = start + 1;
            while (end < structs.Count && structs[end].Client == structs[start].Client) end++;
            encoder.WriteVarUint((ulong)(end - start));
            encoder.WriteClient(structs[start].Client);
            encoder.WriteVarUint((ulong)structs[start].Clock);
            for (var i = start; i < end; i++) WriteRef(ref encoder, structs[i]);
            start = end;
        }
    }

    /// <summary>A decoded struct as Yjs writes the same struct from an update it has not integrated (<c>Item.write</c>, <c>GC.write</c>, <c>Skip.write</c>).</summary>
    public static void WriteRef<TEncoder>(ref TEncoder encoder, in StructRef r)
        where TEncoder : IUpdateEncoder, allows ref struct
    {
        switch (r.Kind)
        {
            case RefKind.Gc:
                encoder.WriteInfo(UpdateReader.RefGc);
                encoder.WriteLen(r.Length);
                return;
            case RefKind.Skip:
                encoder.WriteInfo(UpdateReader.RefSkip);
                encoder.WriteVarUint((ulong)r.Length);
                return;
        }

        var keyed = r.ParentSub is not null;
        encoder.WriteInfo(Info(r.Content, r.Origin.HasValue, r.RightOrigin.HasValue, keyed));
        if (r.Origin is { } o) encoder.WriteLeftId(o);
        if (r.RightOrigin is { } ro) encoder.WriteRightId(ro);
        if (r.Origin is null && r.RightOrigin is null)
        {
            if (r.ParentRoot is { } root)
            {
                encoder.WriteParentInfo(true);
                encoder.WriteString(root);
            }
            else if (r.ParentId is { } parent)
            {
                encoder.WriteParentInfo(false);
                encoder.WriteLeftId(parent);
            }
            else
            {
                throw new YInternalException("A decoded item without neighbours names no parent.");
            }

            if (keyed) encoder.WriteString(r.ParentSub!);
        }

        switch (r.Content)
        {
            case ContentKind.Deleted:
                encoder.WriteLen(r.Length);
                break;
            case ContentKind.Json:
            case ContentKind.Any:
                encoder.WriteLen(r.Values.Length);
                foreach (var v in r.Values.Span)
                {
                    if (r.Content == ContentKind.Any) encoder.WriteAny(v);
                    else encoder.WriteJsonElement(v);
                }

                break;
            case ContentKind.String:
                encoder.WriteString(r.Text!);
                break;
            default:
                WritePayload(ref encoder, r.Content, r.Payload ?? throw new YInternalException($"A {r.Content} struct has no payload."));
                break;
        }
    }

    private static void WritePayload<TEncoder>(ref TEncoder encoder, ContentKind kind, object payload)
        where TEncoder : IUpdateEncoder, allows ref struct
    {
        switch (kind)
        {
            case ContentKind.Binary:
                encoder.WriteBuf((byte[])payload);
                break;
            case ContentKind.Embed:
                encoder.WriteJson(((EmbedContent)payload).Value);
                break;
            case ContentKind.Format:
                var format = (FormatContent)payload;
                encoder.WriteKey(format.Key);
                encoder.WriteJson(format.Value);
                break;
            case ContentKind.Type:
                var type = (Branch)payload;
                if (type.TypeRef > TypeRefs.XmlText) throw new YInternalException("A nested type has no wire type ref.");
                encoder.WriteTypeRef(type.TypeRef);
                if (type.TypeRef is TypeRefs.XmlElement or TypeRefs.XmlHook)
                    encoder.WriteKey(type.TypeName ?? throw new YInternalException("An XML element or hook has no name."));
                break;
            case ContentKind.Doc:
                var subdoc = (SubdocContent)payload;
                encoder.WriteString(subdoc.Guid);
                encoder.WriteAny(subdoc.Options);
                break;
            default:
                throw new YInternalException($"A {kind} content has no payload to write.");
        }
    }

    /// <summary>The info byte: the content ref in the low five bits, then the parent-sub, right-origin and origin flags.</summary>
    private static byte Info(ContentKind kind, bool origin, bool rightOrigin, bool keyed) =>
        (byte)(ContentRef(kind) | (origin ? UpdateReader.HasOrigin : 0) | (rightOrigin ? UpdateReader.HasRightOrigin : 0) | (keyed ? UpdateReader.HasParentSub : 0));

    public static int ContentRef(ContentKind kind) => kind switch
    {
        ContentKind.Deleted => UpdateReader.RefDeleted,
        ContentKind.Json => UpdateReader.RefJson,
        ContentKind.Binary => UpdateReader.RefBinary,
        ContentKind.String => UpdateReader.RefString,
        ContentKind.Embed => UpdateReader.RefEmbed,
        ContentKind.Format => UpdateReader.RefFormat,
        ContentKind.Type => UpdateReader.RefType,
        ContentKind.Any => UpdateReader.RefAny,
        ContentKind.Doc => UpdateReader.RefDoc,
        _ => throw new YInternalException($"A {kind} struct is not an item."),
    };

    // ------------------------------------------------------------------ delete sets

    /// <summary>
    /// Yjs <c>createDeleteSetFromStructStore</c> then <c>writeDeleteSet</c>: per client, in descending order, each run of consecutive deleted structs
    /// (GC structs included) as one range.
    /// </summary>
    public static void WriteStoreDeleteSet<TEncoder>(ref TEncoder encoder, StructStore store)
        where TEncoder : IUpdateEncoder, allows ref struct
    {
        var clients = 0;
        for (var rank = store.ClientCount - 1; rank >= 0; rank--)
            if (DeletedRuns(store, rank) > 0) clients++;
        encoder.WriteVarUint((ulong)clients);
        for (var rank = store.ClientCount - 1; rank >= 0; rank--)
        {
            var runs = DeletedRuns(store, rank);
            if (runs == 0) continue;
            encoder.ResetDsCurVal();
            encoder.WriteVarUint(store.ClientIdAtRank(rank));
            encoder.WriteVarUint((ulong)runs);
            var count = store.StructCountAtRank(rank);
            for (var i = 0; i < count; i++)
            {
                var s = store.StructAtRank(rank, i);
                if (!s.IsDeleted) continue;
                var clock = s.Clock;
                var length = s.Length;
                while (i + 1 < count && store.StructAtRank(rank, i + 1).IsDeleted) length += store.StructAtRank(rank, ++i).Length;
                encoder.WriteDsClock(clock);
                encoder.WriteDsLen(length);
            }
        }
    }

    private static int DeletedRuns(StructStore store, int rank)
    {
        var runs = 0;
        var count = store.StructCountAtRank(rank);
        for (var i = 0; i < count; i++)
        {
            if (!store.StructAtRank(rank, i).IsDeleted) continue;
            runs++;
            while (i + 1 < count && store.StructAtRank(rank, i + 1).IsDeleted) i++;
        }

        return runs;
    }

    /// <summary>Yjs <c>writeDeleteSet</c> for ranges sorted by client then clock (<see cref="DeleteRanges"/> order): clients descending.</summary>
    public static void WriteDeleteSet<TEncoder>(ref TEncoder encoder, DeleteRanges ranges)
        where TEncoder : IUpdateEncoder, allows ref struct
    {
        var clients = 0;
        for (var i = 0; i < ranges.Count; i++)
            if (i == 0 || ranges[i].Client != ranges[i - 1].Client) clients++;
        encoder.WriteVarUint((ulong)clients);
        var end = ranges.Count;
        while (end > 0)
        {
            var client = ranges[end - 1].Client;
            var start = end - 1;
            while (start > 0 && ranges[start - 1].Client == client) start--;
            encoder.ResetDsCurVal();
            encoder.WriteVarUint(client);
            encoder.WriteVarUint((ulong)(end - start));
            for (var i = start; i < end; i++)
            {
                encoder.WriteDsClock(ranges[i].Clock);
                encoder.WriteDsLen(ranges[i].Length);
            }

            end = start;
        }
    }

    /// <summary>A delete set from ranges already in write order: each run of one client's ranges is that client's entry.</summary>
    public static void WriteDeleteRuns<TEncoder>(ref TEncoder encoder, List<DeleteRange> ranges)
        where TEncoder : IUpdateEncoder, allows ref struct
    {
        var clients = 0;
        for (var i = 0; i < ranges.Count; i++)
            if (i == 0 || ranges[i].Client != ranges[i - 1].Client) clients++;
        encoder.WriteVarUint((ulong)clients);
        var start = 0;
        while (start < ranges.Count)
        {
            var end = start + 1;
            while (end < ranges.Count && ranges[end].Client == ranges[start].Client) end++;
            encoder.ResetDsCurVal();
            encoder.WriteVarUint(ranges[start].Client);
            encoder.WriteVarUint((ulong)(end - start));
            for (var i = start; i < end; i++)
            {
                encoder.WriteDsClock(ranges[i].Clock);
                encoder.WriteDsLen(ranges[i].Length);
            }

            start = end;
        }
    }
}
