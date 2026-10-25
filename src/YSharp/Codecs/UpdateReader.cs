#nullable enable
using System;
using System.Collections.Generic;
using YSharp.Integration;
using YSharp.Lib0;
using YSharp.Store;

namespace YSharp.Codecs;

/// <summary>
/// Decodes a whole update into <see cref="UpdateRefs"/> (Yjs <c>readClientsStructRefs</c> and <c>readDeleteSet</c>), generic over the codec. The
/// update is read and validated in full before anything is integrated (ADR 0005, atomic decode), so a failure changes nothing. Every count is
/// checked against the limits and against the bytes left before a loop or an array is sized from it.
/// </summary>
internal static class UpdateReader
{
    /// <summary>The largest clock: 2^53-1 (ADR 0003, Q22).</summary>
    public const long MaxClock = (long)Lib0Reader.MaxVarint;

    /// <summary>Content refs (Yjs <c>contentRefs</c>): the low five bits of an item's info byte.</summary>
    public const int RefGc = 0, RefDeleted = 1, RefJson = 2, RefBinary = 3, RefString = 4, RefEmbed = 5, RefFormat = 6, RefType = 7, RefAny = 8,
        RefDoc = 9, RefSkip = 10;

    public const byte HasOrigin = 0x80, HasRightOrigin = 0x40, HasParentSub = 0x20;

    public static YError Read<TDecoder>(ref TDecoder decoder, YLimits limits, out UpdateRefs update)
        where TDecoder : IUpdateDecoder, allows ref struct
    {
        update = new UpdateRefs();
        if (!decoder.ReadVarUint(out var numClients)) return decoder.Error;
        // a client block is at least three bytes (struct count, client, start clock)
        if (numClients > (ulong)decoder.Remaining / 3) return YError.Malformed;
        long structs = 0;
        for (ulong c = 0; c < numClients; c++)
        {
            if (!decoder.ReadVarUint(out var numStructs)) return decoder.Error;
            structs += (long)Math.Min(numStructs, int.MaxValue + 1UL);
            if (structs > limits.MaxStructsPerUpdate) return YError.LimitExceeded;
            // a struct is at least two bytes (info and a length or a parent)
            if (numStructs > (ulong)decoder.Remaining / 2) return YError.Malformed;
            if (!decoder.ReadClient(out var client) || !decoder.ReadVarUint(out var startClock)) return decoder.Error;
            var refs = new List<StructRef>((int)numStructs);
            var clock = (long)startClock;
            for (ulong s = 0; s < numStructs; s++)
            {
                var error = ReadStruct(ref decoder, limits, client, clock, out var r);
                if (error != YError.None) return error;
                refs.Add(r);
                clock = r.End;
            }

            update.SetClient(client, refs);
        }

        return ReadDeleteSet(ref decoder, update.Deletes);
    }

    /// <summary>Yjs <c>readDeleteSet</c>: ranges in update order; a client with no ranges and a range of length 0 add nothing (ENC-50, ENC-51).</summary>
    public static YError ReadDeleteSet<TDecoder>(ref TDecoder decoder, List<DeleteRange> ranges)
        where TDecoder : IUpdateDecoder, allows ref struct
    {
        if (!decoder.ReadVarUint(out var numClients)) return decoder.Error;
        if (numClients > (ulong)decoder.Remaining / 2) return YError.Malformed;
        for (ulong c = 0; c < numClients; c++)
        {
            decoder.ResetDsCurVal();
            if (!decoder.ReadVarUint(out var client) || !decoder.ReadVarUint(out var numDeletes)) return decoder.Error;
            if (numDeletes > (ulong)decoder.Remaining / 2) return YError.Malformed;
            for (ulong i = 0; i < numDeletes; i++)
            {
                if (!decoder.ReadDsClock(out var clock) || !decoder.ReadDsLen(out var length)) return decoder.Error;
                if (clock + length > (ulong)MaxClock) return YError.Malformed;
                if (length > 0) ranges.Add(new DeleteRange(client, (long)clock, (long)length));
            }
        }

        return YError.None;
    }

    private static YError ReadStruct<TDecoder>(ref TDecoder decoder, YLimits limits, ulong client, long clock, out StructRef r)
        where TDecoder : IUpdateDecoder, allows ref struct
    {
        r = default;
        if (clock > MaxClock) return YError.Malformed;   // the previous struct ended at 2^53
        if (!decoder.ReadInfo(out var info)) return decoder.Error;
        var contentRef = info & 0x1f;
        if (contentRef == RefGc || contentRef == RefSkip)
        {
            // a GC length goes through the length encoder, a Skip length is always a plain varuint (Yjs Skip.write)
            ulong length;
            if (!(contentRef == RefGc ? decoder.ReadLen(out length) : decoder.ReadVarUint(out length))) return decoder.Error;
            if ((ulong)clock + length > (ulong)MaxClock) return YError.Malformed;
            r = contentRef == RefGc ? StructRef.Gc(client, clock, (long)length) : StructRef.Skip(client, clock, (long)length);
            return YError.None;
        }

        if (contentRef > RefDoc) return YError.Malformed;   // refs 11 to 31 name no content (ENC-38)
        r.Kind = RefKind.Item;
        r.Client = client;
        r.Clock = clock;
        if ((info & HasOrigin) != 0)
        {
            if (!decoder.ReadLeftId(out var origin)) return decoder.Error;
            r.Origin = origin;
        }

        if ((info & HasRightOrigin) != 0)
        {
            if (!decoder.ReadRightId(out var rightOrigin)) return decoder.Error;
            r.RightOrigin = rightOrigin;
        }

        if ((info & (HasOrigin | HasRightOrigin)) == 0)
        {
            // only an item without neighbours names its parent; otherwise the parent is inherited when it integrates
            if (!decoder.ReadParentInfo(out var isRoot)) return decoder.Error;
            if (isRoot)
            {
                if (!decoder.ReadString(out var name)) return decoder.Error;
                r.ParentRoot = name;
            }
            else
            {
                if (!decoder.ReadLeftId(out var parent)) return decoder.Error;
                r.ParentId = parent;
            }

            if ((info & HasParentSub) != 0)
            {
                if (!decoder.ReadString(out var sub)) return decoder.Error;
                r.ParentSub = sub;
            }
        }

        // an item can only depend on its own client's earlier clocks: anything else can never resolve (ENC-49)
        if (DependsOnItself(r.Origin, client, clock) || DependsOnItself(r.RightOrigin, client, clock) || DependsOnItself(r.ParentId, client, clock))
            return YError.Malformed;

        var error = ReadContent(ref decoder, limits, contentRef, ref r);
        if (error != YError.None) return error;
        // the item's last id must be a valid clock
        if (r.Length > 0 && clock + r.Length - 1 > MaxClock) return YError.Malformed;
        return YError.None;
    }

    private static bool DependsOnItself(ItemId? id, ulong client, long clock) => id is { } i && i.Client == client && i.Clock >= clock;

    private static YError ReadContent<TDecoder>(ref TDecoder decoder, YLimits limits, int contentRef, ref StructRef r)
        where TDecoder : IUpdateDecoder, allows ref struct
    {
        switch (contentRef)
        {
            case RefDeleted:
            {
                if (!decoder.ReadLen(out var length)) return decoder.Error;
                if (length > (ulong)MaxClock) return YError.Malformed;
                r.Content = ContentKind.Deleted;
                r.Length = (long)length;
                return YError.None;
            }
            case RefJson:
            case RefAny:
            {
                if (!decoder.ReadLen(out var count)) return decoder.Error;
                if (count > (ulong)decoder.Remaining) return YError.Malformed;   // every element is at least one byte
                var values = new YValue[(int)count];
                for (var i = 0; i < values.Length; i++)
                {
                    if (!(contentRef == RefAny ? decoder.ReadAny(out values[i]) : decoder.ReadJsonElement(out values[i]))) return decoder.Error;
                }

                r.Content = contentRef == RefAny ? ContentKind.Any : ContentKind.Json;
                r.Values = values;
                r.Length = values.Length;
                return YError.None;
            }
            case RefBinary:
            {
                if (!decoder.ReadBuf(out var bytes)) return decoder.Error;
                SetPayload(ref r, ContentKind.Binary, bytes);
                return YError.None;
            }
            case RefString:
            {
                if (!decoder.ReadString(out var text)) return decoder.Error;
                r.Content = ContentKind.String;
                r.Text = text;
                r.Length = text.Length;   // UTF-16 code units (ENC-05)
                return YError.None;
            }
            case RefEmbed:
            {
                if (!decoder.ReadJson(out var embed)) return decoder.Error;
                SetPayload(ref r, ContentKind.Embed, new EmbedContent(embed));
                return YError.None;
            }
            case RefFormat:
            {
                if (!decoder.ReadKey(out var key) || !decoder.ReadJson(out var value)) return decoder.Error;
                SetPayload(ref r, ContentKind.Format, new FormatContent(key, value));
                return YError.None;
            }
            case RefType:
            {
                if (!decoder.ReadTypeRef(out var typeRef)) return decoder.Error;
                if (typeRef > TypeRefs.XmlText) return YError.Malformed;   // ENC-39
                var t = (byte)typeRef;
                string? name = null;
                if (t is TypeRefs.XmlElement or TypeRefs.XmlHook && !decoder.ReadKey(out name)) return decoder.Error;
                SetPayload(ref r, ContentKind.Type, new Branch(TypeRefs.UsesMarkers(t)) { TypeRef = t, TypeName = name });
                return YError.None;
            }
            case RefDoc:
            {
                if (!decoder.ReadString(out var guid) || !decoder.ReadAny(out var options)) return decoder.Error;
                SetPayload(ref r, ContentKind.Doc, new SubdocContent(guid, options));
                return YError.None;
            }
            default:
                return YError.Malformed;
        }
    }

    private static void SetPayload(ref StructRef r, ContentKind kind, object payload)
    {
        r.Content = kind;
        r.Payload = payload;
        r.Length = 1;
    }
}
