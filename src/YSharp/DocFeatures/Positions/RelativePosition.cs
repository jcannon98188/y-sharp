#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using YSharp.Codecs;
using YSharp.Lib0;
using YSharp.Store;

namespace YSharp;

// ---------------------------------------------------------------- relative positions (spec 31, RP-01..RP-09; Yjs utils/RelativePosition.js)

/// <summary>
/// Yjs <c>RelativePosition</c>: a position anchored on an item id (<see cref="Item"/>), or, at the end of a type, on the type itself: a root
/// by name (<see cref="TypeName"/>) or a nested type by the id of its item (<see cref="Type"/>). <see cref="Assoc"/> is the association: below
/// zero the position sticks to the content on its left, otherwise to the content on its right. Equality is member-wise, as Yjs
/// <c>compareRelativePositions</c> (RP-07).
/// </summary>
public readonly record struct RelativePosition(ItemId? Item, string? TypeName, ItemId? Type, int Assoc)
{
    /// <summary>
    /// Yjs <c>encodeRelativePosition</c> (RP-03): a tag (0 item, 1 root name, 2 type id), the anchor, then the association as a varInt. A
    /// position with no anchor throws, as JS does (<c>unexpectedCase</c>).
    /// </summary>
    public void Encode(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        using var w = Lib0Writer.Rent(16);
        if (Item is { } item)
        {
            w.WriteVarUint(0);
            w.WriteVarUint(item.Client);
            w.WriteVarUint((ulong)item.Clock);
        }
        else if (TypeName is not null)
        {
            w.WriteUint8(1);
            w.WriteVarString(TypeName);
        }
        else if (Type is { } type)
        {
            w.WriteUint8(2);
            w.WriteVarUint(type.Client);
            w.WriteVarUint((ulong)type.Clock);
        }
        else
        {
            throw new InvalidOperationException("A relative position needs an item, a root name or a type id.");
        }

        w.WriteVarInt(Assoc);
        w.CopyTo(writer);
    }

    /// <summary>
    /// Yjs <c>decodeRelativePosition</c> (RP-04). A missing association reads as 0 and trailing bytes are ignored, as in JS. An unknown tag,
    /// which JS turns into a position with no anchor, a truncated anchor, a clock past 2^53 - 1 and an association outside the
    /// <see cref="int"/> range are <see cref="YError.Malformed"/>.
    /// </summary>
    public static YStatus TryDecode(ReadOnlySpan<byte> bytes, out RelativePosition position)
    {
        position = default;
        var reader = new Lib0Reader(bytes);
        if (!reader.TryReadVarUint(out var tag)) return new YStatus(reader.Error);
        ItemId? item = null, type = null;
        string? name = null;
        switch (tag)
        {
            case 0:
                if (!TryReadId(ref reader, out var id, out var error)) return new YStatus(error);
                item = id;
                break;
            case 1:
                if (!reader.TryReadVarString(out var s)) return new YStatus(reader.Error);
                name = s;
                break;
            case 2:
                if (!TryReadId(ref reader, out var typeId, out var typeError)) return new YStatus(typeError);
                type = typeId;
                break;
            default:
                return new YStatus(YError.Malformed);
        }

        var assoc = 0;
        if (reader.Remaining > 0)
        {
            if (!reader.TryReadVarInt(out var magnitude, out var negative)) return new YStatus(reader.Error);
            if (magnitude > (negative ? 1UL << 31 : int.MaxValue)) return new YStatus(YError.Malformed);
            assoc = negative ? (int)-(long)magnitude : (int)magnitude;
        }

        position = new RelativePosition(item, name, type, assoc);
        return default;
    }

    private static bool TryReadId(ref Lib0Reader reader, out ItemId id, out YError error)
    {
        id = default;
        if (!reader.TryReadVarUint(out var client) || !reader.TryReadVarUint(out var clock))
        {
            error = reader.Error;
            return false;
        }

        if (clock > UpdateReader.MaxClock)
        {
            error = YError.Malformed;
            return false;
        }

        id = new ItemId(client, (long)clock);
        error = YError.None;
        return true;
    }

    /// <summary>
    /// Yjs <c>relativePositionToJSON</c> (RP-05): <c>type</c>, <c>tname</c> and <c>item</c> appear only when truthy, so an empty root name is
    /// dropped as in JS, and <c>assoc</c> always.
    /// </summary>
    public YValue ToJson()
    {
        var members = new List<KeyValuePair<string, YValue>>(4);
        if (Type is { } type) members.Add(new("type", IdJson(type)));
        if (!string.IsNullOrEmpty(TypeName)) members.Add(new("tname", TypeName));
        if (Item is { } item) members.Add(new("item", IdJson(item)));
        members.Add(new("assoc", Assoc));
        return YValue.Object(members.ToArray());
    }

    private static YValue IdJson(ItemId id) =>
        YValue.Object([new("client", YValue.Number(id.Client)), new("clock", YValue.Number((ulong)id.Clock))]);

    /// <summary>
    /// Yjs <c>createRelativePositionFromJSON</c> (RP-05): absent or null members are null and an absent association is 0. JS takes whatever
    /// it is given; here a member of the wrong shape, an id that is not a pair of non-negative integers up to 2^53 - 1 and an association
    /// that is not an <see cref="int"/> are <see cref="YError.Malformed"/>. An object with no anchor gives a position that throws on use, as in JS.
    /// </summary>
    public static YStatus TryFromJson(in YValue json, out RelativePosition position)
    {
        position = default;
        if (!json.TryGetObject(out var obj)) return new YStatus(YError.Malformed);
        if (!TryIdMember(obj, "type", out var type) || !TryIdMember(obj, "item", out var item)) return new YStatus(YError.Malformed);
        string? name = null;
        if (obj.TryGetValue("tname", out var tname) && !IsNullish(tname))
        {
            if (!tname.TryGetString(out var s)) return new YStatus(YError.Malformed);
            name = s;
        }

        var assoc = 0;
        if (obj.TryGetValue("assoc", out var a) && !IsNullish(a))
        {
            if (!a.TryGetNumber(out var d) || d != Math.Floor(d) || d < int.MinValue || d > int.MaxValue) return new YStatus(YError.Malformed);
            assoc = (int)d;
        }

        position = new RelativePosition(item, name, type, assoc);
        return default;
    }

    private static bool IsNullish(in YValue v) => v.Kind is YValueKind.Undefined or YValueKind.Null;

    private static bool TryIdMember(YObjectView obj, string key, out ItemId? id)
    {
        id = null;
        if (!obj.TryGetValue(key, out var v) || IsNullish(v)) return true;
        if (!v.TryGetObject(out var idObj)
            || !idObj.TryGetValue("client", out var c) || !c.TryGetNumber(out var client)
            || !idObj.TryGetValue("clock", out var k) || !k.TryGetNumber(out var clock))
            return false;
        if (!IsUint53(client) || !IsUint53(clock)) return false;
        id = new ItemId((ulong)client, (long)clock);
        return true;
    }

    private static bool IsUint53(double d) => d >= 0 && d <= UpdateReader.MaxClock && d == Math.Floor(d);

    /// <summary>
    /// Yjs <c>createAbsolutePositionFromRelativePosition</c> (RP-06). Null when the anchor is not known to <paramref name="doc"/> yet, was
    /// collected, or names a root the document does not have: JS creates a placeholder root for an unknown name, here nothing is created
    /// (ADR 0008). With <paramref name="followUndoneDeletions"/>, an anchor that an undo manager redid is followed to its redone copy (Yjs
    /// <c>followRedone</c>). A position with no anchor throws, as in JS.
    /// </summary>
    public AbsolutePosition? Resolve(Doc doc, bool followUndoneDeletions = true)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (Item is null && TypeName is null && Type is null)
            throw new InvalidOperationException("A relative position needs an item, a root name or a type id.");
        var h = doc.EnterRead();
        try
        {
            return ResolveLocked(doc, followUndoneDeletions);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    private AbsolutePosition? ResolveLocked(Doc doc, bool follow)
    {
        var store = doc.Store;
        Branch type;
        var index = 0;
        if (Item is { } rightId)
        {
            if (store.GetState(rightId.Client) <= rightId.Clock) return null;
            var (right, diff) = follow ? store.FollowRedone(rightId) : FindWithOffset(store, rightId);
            if (right.IsGc || right.Parent is null) return null;
            type = right.Parent;
            if (type.Item is null || !type.Item.Deleted)
            {
                var r = right.Item!;
                long at = r.Deleted || !r.Countable ? 0 : diff + (Assoc >= 0 ? 0 : 1);
                for (var n = r.Left; n is not null; n = n.Left)
                    if (!n.Deleted && n.Countable) at += n.Length;
                index = checked((int)at);
            }
        }
        else
        {
            if (TypeName is not null)
            {
                if (!doc.TryGetRootBranch(TypeName, out type)) return null;
            }
            else
            {
                var typeId = Type!.Value;
                if (store.GetState(typeId.Client) <= typeId.Clock) return null;
                var item = follow ? store.FollowRedone(typeId).Item : store.Find(typeId.Client, typeId.Clock);
                if (item.IsGc || item.Kind != ContentKind.Type) return null;   // collected
                type = (Branch)store.GetPayload(item);
            }

            index = Assoc >= 0 ? type.Length : 0;
        }

        return new AbsolutePosition(YType.Of(type, doc), index, Assoc);
    }

    /// <summary>Yjs <c>getItemWithOffset</c>: the struct holding <paramref name="id"/> and the offset of the id in it.</summary>
    private static (ItemRef Item, long Diff) FindWithOffset(StructStore store, ItemId id)
    {
        var item = store.Find(id.Client, id.Clock);
        return (item, id.Clock - item.Clock);
    }
}

/// <summary>Yjs <c>AbsolutePosition</c>: a type, an index in it, and the association of the relative position it was resolved from.</summary>
public readonly record struct AbsolutePosition(YType Type, int Index, int Assoc);
