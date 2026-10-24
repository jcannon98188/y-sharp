using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace YSharp.Fuzz.Builder;

// Structure-aware update builder (ADR 0013). It writes V1 and V2 updates from a struct and delete-set model with its own
// varint, RLE and string encoders and never touches y-sharp's encoder, so it reaches the pending and reject logic with
// inputs the real encoder would never produce, and it checks the decoder independently. This file has no test-framework
// dependency: the canary host links it.

/// <summary>An item or struct id. Clients and clocks are <see cref="ulong"/> so hostile values beyond 2^32 and 2^53 can be written.</summary>
public readonly record struct Id(ulong Client, ulong Clock);

public enum StructKind { Gc, Skip, Item }

public enum ContentKind { Deleted, Json, Binary, String, Embed, Format, Type, Any, Doc }

/// <summary>Content of an item. Which fields are read depends on <see cref="Kind"/>; unused ones are ignored.</summary>
public sealed record Content(
    ContentKind Kind,
    ulong Length = 0,                           // Deleted: length; Json and Any: element count claimed on the wire
    string Text = "",                           // String: the text. Embed and Format: the JSON text (V1). Doc: the guid
    IReadOnlyList<string>? JsonElements = null, // Json: one JSON text per element
    byte[]? Raw = null,                         // Binary: payload. Any: concatenated Any elements. Embed and Format: the V2 Any payload. Doc: Any options
    ulong TypeRef = 0,                          // Type: type ref
    string? Name = null,                        // Type 3 and 5: node name. Format: key
    bool CountOverride = false)                 // true: write Length as given for Json and Any instead of the real count
{
    public static Content Str(string text) => new(ContentKind.String, Text: text);
    public static Content DeletedOf(ulong length) => new(ContentKind.Deleted, Length: length);
    public static Content TypeOf(ulong typeRef, string? name = null) => new(ContentKind.Type, TypeRef: typeRef, Name: name);
    public static Content BinaryOf(params byte[] payload) => new(ContentKind.Binary, Raw: payload);
    public static Content AnyOf(ulong count, params byte[] elements) => new(ContentKind.Any, Length: count, Raw: elements, CountOverride: true);
    public static Content JsonOf(params string[] elements) => new(ContentKind.Json, JsonElements: elements);

    /// <summary>Clock units this content occupies.</summary>
    public ulong Units => Kind switch
    {
        ContentKind.Deleted => Length,
        ContentKind.Json => CountOverride ? Length : (ulong)(JsonElements?.Count ?? 0),
        ContentKind.Any => Length,
        ContentKind.String => (ulong)Text.Length,
        _ => 1,
    };
}

/// <summary>One struct. For <see cref="StructKind.Item"/> the parent fields are written only when neither origin is present, as in Yjs.</summary>
public sealed record Struct(
    StructKind Kind,
    ulong Length = 1,                // Gc and Skip
    Content? Content = null,
    Id? Origin = null,
    Id? RightOrigin = null,
    string? RootKey = null,          // parent is a root type with this key
    Id? ParentId = null,             // parent is the item with this id
    string? ParentSub = null,
    byte? InfoOverride = null,       // write this info byte instead of the computed one
    ulong? ParentInfoOverride = null)
{
    public static Struct Gc(ulong length) => new(StructKind.Gc, length);
    public static Struct Skip(ulong length) => new(StructKind.Skip, length);

    public static Struct InRoot(string key, Content content, Id? origin = null, Id? rightOrigin = null, string? parentSub = null) =>
        new(StructKind.Item, Content: content, Origin: origin, RightOrigin: rightOrigin, RootKey: key, ParentSub: parentSub);

    public static Struct InItem(Id parent, Content content, string? parentSub = null) =>
        new(StructKind.Item, Content: content, ParentId: parent, ParentSub: parentSub);

    /// <summary>Clock units this struct occupies.</summary>
    public ulong Units => Kind == StructKind.Item ? Content?.Units ?? 0 : Length;
}

/// <summary>The structs of one client. <see cref="NumStructsOverride"/> writes a count other than the real one (for length-claim cases).</summary>
public sealed record ClientBlock(ulong Client, ulong StartClock, IReadOnlyList<Struct> Structs, ulong? NumStructsOverride = null);

public readonly record struct DeleteRange(ulong Clock, ulong Length);

public sealed record DeleteClient(ulong Client, IReadOnlyList<DeleteRange> Ranges, ulong? NumRangesOverride = null);

/// <summary>A whole update. Blocks and delete clients are written in the order given (the model does not sort), which is what lets a test write unsorted input.</summary>
public sealed record UpdateModel(IReadOnlyList<ClientBlock> Blocks, IReadOnlyList<DeleteClient> DeleteSet, ulong? NumClientsOverride = null, ulong? NumDeleteClientsOverride = null)
{
    public static UpdateModel Empty { get; } = new(Array.Empty<ClientBlock>(), Array.Empty<DeleteClient>());

    public int StructCount => Blocks.Sum(b => b.Structs.Count);

    public static UpdateModel Of(params ClientBlock[] blocks) => new(blocks, Array.Empty<DeleteClient>());

    public UpdateModel WithDeletes(params DeleteClient[] deletes) => this with { DeleteSet = deletes };

    /// <summary>One client block starting at <paramref name="startClock"/> with consecutive structs.</summary>
    public static UpdateModel Single(ulong client, ulong startClock, params Struct[] structs) => Of(new ClientBlock(client, startClock, structs));
}

/// <summary>Little-endian base-128 and UTF-8 writer used by both formats; independent of y-sharp.</summary>
public sealed class ByteSink
{
    private readonly List<byte> _bytes = new();

    public int Length => _bytes.Count;

    public ByteSink VarUint(ulong value)
    {
        while (value > 127)
        {
            _bytes.Add((byte)((value & 127) | 128));
            value >>= 7;
        }
        _bytes.Add((byte)value);
        return this;
    }

    /// <summary>lib0 VarInt: sign in bit 7 of the first byte, six payload bits there, seven in the rest. <paramref name="negativeZero"/> writes -0.</summary>
    public ByteSink VarInt(long value, bool negativeZero = false)
    {
        var negative = value < 0 || negativeZero;
        var magnitude = value < 0 ? (ulong)(-value) : (ulong)value;
        var first = (byte)((magnitude > 63 ? 128 : 0) | (negative ? 64 : 0) | (int)(magnitude & 63));
        _bytes.Add(first);
        magnitude >>= 6;
        while (magnitude > 0)
        {
            _bytes.Add((byte)((magnitude > 127 ? 128 : 0) | (int)(magnitude & 127)));
            magnitude >>= 7;
        }
        return this;
    }

    public ByteSink Byte(byte b)
    {
        _bytes.Add(b);
        return this;
    }

    public ByteSink Bytes(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes) _bytes.Add(b);
        return this;
    }

    public ByteSink VarBytes(ReadOnlySpan<byte> bytes) => VarUint((ulong)bytes.Length).Bytes(bytes);

    public ByteSink VarString(string s) => VarBytes(Encoding.UTF8.GetBytes(s));

    public byte[] ToArray() => _bytes.ToArray();
}

/// <summary>The lib0 column encoders of the V2 container (docs/research/encoding.md 1.8), written from the documented rules.</summary>
internal static class V2Encoders
{
    /// <summary>RleEncoder: value, then VarUint(count-1) when the next different value arrives. The final run's count is never written.</summary>
    public static byte[] Rle(IReadOnlyList<byte> values)
    {
        var sink = new ByteSink();
        var i = 0;
        while (i < values.Count)
        {
            var value = values[i];
            var run = 1;
            while (i + run < values.Count && values[i + run] == value) run++;
            sink.Byte(value);
            i += run;
            if (i < values.Count) sink.VarUint((ulong)(run - 1));
        }
        return sink.ToArray();
    }

    /// <summary>UintOptRleEncoder: per run VarInt(count==1 ? v : -v), plus VarUint(count-2) when count &gt; 1. The final run is flushed.</summary>
    public static byte[] UintOptRle(IReadOnlyList<ulong> values)
    {
        var sink = new ByteSink();
        var i = 0;
        while (i < values.Count)
        {
            var value = values[i];
            var run = 1;
            while (i + run < values.Count && values[i + run] == value) run++;
            if (run == 1) sink.VarInt(checked((long)value));
            else sink.VarInt(-checked((long)value), negativeZero: value == 0).VarUint((ulong)(run - 2));
            i += run;
        }
        return sink.ToArray();
    }

    /// <summary>IntDiffOptRleEncoder: a run extends while the difference to the previous value stays the same; writes VarInt(diff*2 + (count==1 ? 0 : 1)) and VarUint(count-2) when count &gt; 1.</summary>
    public static byte[] IntDiffOptRle(IReadOnlyList<ulong> values)
    {
        var sink = new ByteSink();
        long previous = 0;
        var i = 0;
        while (i < values.Count)
        {
            var diff = checked((long)values[i]) - previous;
            var run = 1;
            var last = checked((long)values[i]);
            while (i + run < values.Count && checked((long)values[i + run]) - last == diff)
            {
                last = checked((long)values[i + run]);
                run++;
            }
            sink.VarInt(diff * 2 + (run == 1 ? 0 : 1));
            if (run > 1) sink.VarUint((ulong)(run - 2));
            previous = last;
            i += run;
        }
        return sink.ToArray();
    }

    /// <summary>StringEncoder: VarString(all strings joined) followed by the UTF-16 lengths as a UintOptRle.</summary>
    public static byte[] Strings(IReadOnlyList<string> values)
    {
        var sink = new ByteSink();
        sink.VarString(string.Concat(values));
        sink.Bytes(UintOptRle(values.Select(v => (ulong)v.Length).ToList()));
        return sink.ToArray();
    }
}

public static class UpdateBuilder
{
    /// <summary>Writes the V1 encoding of <paramref name="model"/>.</summary>
    public static byte[] V1(UpdateModel model)
    {
        var s = new ByteSink();
        s.VarUint(model.NumClientsOverride ?? (ulong)model.Blocks.Count);
        foreach (var block in model.Blocks)
        {
            s.VarUint(block.NumStructsOverride ?? (ulong)block.Structs.Count);
            s.VarUint(block.Client);
            s.VarUint(block.StartClock);
            foreach (var st in block.Structs) WriteV1Struct(s, st);
        }
        WriteDeleteSet(s, model, v2: false);
        return s.ToArray();
    }

    private static void WriteV1Struct(ByteSink s, Struct st)
    {
        switch (st.Kind)
        {
            case StructKind.Gc:
                s.Byte(0).VarUint(st.Length);
                return;
            case StructKind.Skip:
                s.Byte(10).VarUint(st.Length);
                return;
        }

        var content = st.Content ?? throw new InvalidOperationException("An item needs content.");
        s.Byte(Info(st, content));
        if (st.Origin is { } o) s.VarUint(o.Client).VarUint(o.Clock);
        if (st.RightOrigin is { } r) s.VarUint(r.Client).VarUint(r.Clock);
        if (st.Origin == null && st.RightOrigin == null)
        {
            if (st.ParentId is { } p) s.VarUint(st.ParentInfoOverride ?? 0).VarUint(p.Client).VarUint(p.Clock);
            else s.VarUint(st.ParentInfoOverride ?? 1).VarString(st.RootKey ?? throw new InvalidOperationException("An item with no origin needs a parent."));
            if (st.ParentSub != null) s.VarString(st.ParentSub);
        }
        WriteV1Content(s, content);
    }

    private static byte Info(Struct st, Content content)
    {
        if (st.InfoOverride is { } over) return over;
        var info = (int)Ref(content.Kind);
        if (st.ParentSub != null) info |= 0x20;
        if (st.RightOrigin != null) info |= 0x40;
        if (st.Origin != null) info |= 0x80;
        return (byte)info;
    }

    private static byte Ref(ContentKind kind) => kind switch
    {
        ContentKind.Deleted => 1,
        ContentKind.Json => 2,
        ContentKind.Binary => 3,
        ContentKind.String => 4,
        ContentKind.Embed => 5,
        ContentKind.Format => 6,
        ContentKind.Type => 7,
        ContentKind.Any => 8,
        ContentKind.Doc => 9,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static void WriteV1Content(ByteSink s, Content c)
    {
        switch (c.Kind)
        {
            case ContentKind.Deleted: s.VarUint(c.Length); break;
            case ContentKind.Json:
                s.VarUint(c.CountOverride ? c.Length : (ulong)(c.JsonElements?.Count ?? 0));
                foreach (var e in c.JsonElements ?? Array.Empty<string>()) s.VarString(e);
                break;
            case ContentKind.Binary: s.VarBytes(c.Raw ?? Array.Empty<byte>()); break;
            case ContentKind.String: s.VarString(c.Text); break;
            case ContentKind.Embed: s.VarString(c.Text); break;
            case ContentKind.Format: s.VarString(c.Name ?? "").VarString(c.Text); break;
            case ContentKind.Type:
                s.VarUint(c.TypeRef);
                if (c.TypeRef is 3 or 5) s.VarString(c.Name ?? "");
                break;
            case ContentKind.Any: s.VarUint(c.Length).Bytes(c.Raw ?? Array.Empty<byte>()); break;
            case ContentKind.Doc: s.VarString(c.Text).Bytes(c.Raw ?? Array.Empty<byte>()); break;
        }
    }

    private static void WriteDeleteSet(ByteSink s, UpdateModel model, bool v2)
    {
        s.VarUint(model.NumDeleteClientsOverride ?? (ulong)model.DeleteSet.Count);
        foreach (var client in model.DeleteSet)
        {
            s.VarUint(client.Client);
            s.VarUint(client.NumRangesOverride ?? (ulong)client.Ranges.Count);
            ulong current = 0;
            foreach (var range in client.Ranges)
            {
                if (!v2)
                {
                    s.VarUint(range.Clock).VarUint(range.Length);
                }
                else
                {
                    // delta against the running value, reset per client; a model that goes backwards wraps, as the real writer's garbage does
                    s.VarUint(unchecked(range.Clock - current)).VarUint(unchecked(range.Length - 1));
                    current = unchecked(range.Clock + range.Length);
                }
            }
        }
    }

    /// <summary>Writes the V2 encoding of <paramref name="model"/> (feature flag 0, nine length-prefixed columns, then the rest section).</summary>
    public static byte[] V2(UpdateModel model)
    {
        var keyClock = new List<ulong>();
        var client = new List<ulong>();
        var leftClock = new List<ulong>();
        var rightClock = new List<ulong>();
        var info = new List<byte>();
        var strings = new List<string>();
        var parentInfo = new List<byte>();
        var typeRef = new List<ulong>();
        var len = new List<ulong>();
        var rest = new ByteSink();
        ulong nextKeyClock = 0;

        // lib0 VarInt columns hold JS numbers: hostile values above 2^53 are clamped to it in the V2 columns (the rest section keeps them whole)
        static ulong Col(ulong v) => Math.Min(v, 1UL << 53);

        void Key(string key)
        {
            keyClock.Add(nextKeyClock++);
            strings.Add(key);
        }

        rest.VarUint(model.NumClientsOverride ?? (ulong)model.Blocks.Count);
        foreach (var block in model.Blocks)
        {
            rest.VarUint(block.NumStructsOverride ?? (ulong)block.Structs.Count);
            client.Add(Col(block.Client));
            rest.VarUint(block.StartClock);
            foreach (var st in block.Structs)
            {
                switch (st.Kind)
                {
                    case StructKind.Gc:
                        info.Add(0);
                        len.Add(Col(st.Length));
                        continue;
                    case StructKind.Skip:
                        info.Add(10);
                        rest.VarUint(st.Length);
                        continue;
                }

                var c = st.Content ?? throw new InvalidOperationException("An item needs content.");
                info.Add(Info(st, c));
                if (st.Origin is { } o) { client.Add(Col(o.Client)); leftClock.Add(Col(o.Clock)); }
                if (st.RightOrigin is { } r) { client.Add(Col(r.Client)); rightClock.Add(Col(r.Clock)); }
                if (st.Origin == null && st.RightOrigin == null)
                {
                    if (st.ParentId is { } p)
                    {
                        parentInfo.Add((byte)(st.ParentInfoOverride ?? 0));
                        client.Add(Col(p.Client));
                        leftClock.Add(Col(p.Clock));
                    }
                    else
                    {
                        parentInfo.Add((byte)(st.ParentInfoOverride ?? 1));
                        strings.Add(st.RootKey ?? throw new InvalidOperationException("An item with no origin needs a parent."));
                    }
                    if (st.ParentSub != null) strings.Add(st.ParentSub);
                }
                switch (c.Kind)
                {
                    case ContentKind.Deleted: len.Add(Col(c.Length)); break;
                    case ContentKind.Json:
                        len.Add(Col(c.CountOverride ? c.Length : (ulong)(c.JsonElements?.Count ?? 0)));
                        strings.AddRange(c.JsonElements ?? Array.Empty<string>());
                        break;
                    case ContentKind.Binary: rest.VarBytes(c.Raw ?? Array.Empty<byte>()); break;
                    case ContentKind.String: strings.Add(c.Text); break;
                    case ContentKind.Embed: rest.Bytes(c.Raw ?? Array.Empty<byte>()); break;
                    case ContentKind.Format:
                        Key(c.Name ?? "");
                        rest.Bytes(c.Raw ?? Array.Empty<byte>());
                        break;
                    case ContentKind.Type:
                        typeRef.Add(Col(c.TypeRef));
                        if (c.TypeRef is 3 or 5) Key(c.Name ?? "");
                        break;
                    case ContentKind.Any:
                        len.Add(Col(c.Length));
                        rest.Bytes(c.Raw ?? Array.Empty<byte>());
                        break;
                    case ContentKind.Doc:
                        strings.Add(c.Text);
                        rest.Bytes(c.Raw ?? Array.Empty<byte>());
                        break;
                }
            }
        }
        WriteDeleteSet(rest, model, v2: true);

        var s = new ByteSink();
        s.VarUint(0);
        s.VarBytes(V2Encoders.IntDiffOptRle(keyClock));
        s.VarBytes(V2Encoders.UintOptRle(client));
        s.VarBytes(V2Encoders.IntDiffOptRle(leftClock));
        s.VarBytes(V2Encoders.IntDiffOptRle(rightClock));
        s.VarBytes(V2Encoders.Rle(info));
        s.VarBytes(V2Encoders.Strings(strings));
        s.VarBytes(V2Encoders.Rle(parentInfo));
        s.VarBytes(V2Encoders.UintOptRle(typeRef));
        s.VarBytes(V2Encoders.UintOptRle(len));
        s.Bytes(rest.ToArray());
        return s.ToArray();
    }

    // ------------------------------------------------------------------ frames and shapes shared by the tests

    /// <summary>A V1 state vector: client count, then client and clock per entry in the order given.</summary>
    public static byte[] StateVector(params (ulong Client, ulong Clock)[] entries)
    {
        var s = new ByteSink().VarUint((ulong)entries.Length);
        foreach (var (client, clock) in entries) s.VarUint(client).VarUint(clock);
        return s.ToArray();
    }

    /// <summary>A y-protocols sync message: type, then the payload as a var-length byte array.</summary>
    public static byte[] SyncMessage(ulong type, byte[] payload) => new ByteSink().VarUint(type).VarBytes(payload).ToArray();

    /// <summary>A y-protocols awareness update: count, then client, clock and the state as a JSON string per entry.</summary>
    public static byte[] AwarenessUpdate(params (ulong Client, ulong Clock, string Json)[] entries)
    {
        var s = new ByteSink().VarUint((ulong)entries.Length);
        foreach (var (client, clock, json) in entries) s.VarUint(client).VarUint(clock).VarString(json);
        return s.ToArray();
    }

    /// <summary>A client-1 text update inserting <paramref name="text"/> at clock 0 into root <paramref name="key"/> (the "hi" update of the fixtures).</summary>
    public static UpdateModel TextInsert(ulong client, string key, string text, ulong clock = 0) =>
        UpdateModel.Single(client, clock, Struct.InRoot(key, Content.Str(text)));

    /// <summary>A chain of <paramref name="depth"/> nested arrays: item i is an array inside item i-1, the first inside root array <paramref name="rootKey"/>. One client, clocks 0 to depth-1.</summary>
    public static UpdateModel NestedTypes(ulong client, int depth, string rootKey = "d", ulong typeRef = 0)
    {
        var structs = new Struct[depth];
        for (var i = 0; i < depth; i++)
            structs[i] = i == 0
                ? Struct.InRoot(rootKey, Content.TypeOf(typeRef))
                : Struct.InItem(new Id(client, (ulong)(i - 1)), Content.TypeOf(typeRef));
        return UpdateModel.Single(client, 0, structs);
    }

    /// <summary>V1 bytes of <see cref="NestedTypes"/> written straight to a buffer (a million structs without a million records).</summary>
    public static byte[] NestedTypesV1(ulong client, int depth, string rootKey = "d", ulong typeRef = 0, string? parentSub = null)
    {
        // parentSub: each level sits under that key of its parent map (use with typeRef 1 for a chain of maps)
        var s = new ByteSink().VarUint(1).VarUint((ulong)depth).VarUint(client).VarUint(0);
        for (var i = 0; i < depth; i++)
        {
            s.Byte((byte)(parentSub == null ? 7 : 7 | 0x20));
            if (i == 0) s.VarUint(1).VarString(rootKey);
            else s.VarUint(0).VarUint(client).VarUint((ulong)(i - 1));
            if (parentSub != null) s.VarString(parentSub);
            s.VarUint(typeRef);
        }
        s.VarUint(0);
        return s.ToArray();
    }

    /// <summary>A V1 Any value of <paramref name="depth"/> nested one-element arrays around a null, as a map value update (<c>m.k</c>, client 1).</summary>
    public static byte[] DeepAnyArrays(int depth)
    {
        var any = new ByteSink();
        for (var i = 0; i < depth; i++) any.Byte(117).VarUint(1);   // array of one element
        any.Byte(126);                                              // null
        return MapValueUpdate(1, any.ToArray());
    }

    /// <summary>A V1 update with one ContentAny struct (one element given as raw Any bytes) at <c>m.k</c>.</summary>
    public static byte[] MapValueUpdate(ulong client, byte[] anyElement) =>
        V1(UpdateModel.Single(client, 0, Struct.InRoot("m", Content.AnyOf(1, anyElement), parentSub: "k")));
}
