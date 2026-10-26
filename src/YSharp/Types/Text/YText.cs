#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using YSharp.Store;

namespace YSharp;

// ---------------------------------------------------------------- YText

// Yjs YText over the rich-text core (Types/Text/TypeText.cs) and, for the map-side attributes, the map core. Every member takes an implicit
// transaction unless the calling thread has one open (CC-8). Until it is integrated, writes are queued as JS queues them in _pending and replayed
// in order inside the integrating transaction (T-0.6); reads see nothing (T-0.4). Arguments are checked when the call is made, so a queued
// write cannot fail on replay.
public class YText : YType
{
    internal override byte WireTypeRef => Store.TypeRefs.Text;

    private List<Action<Transaction, Doc>>? _pending = new();

    public YText(string? initial = null)
    {
        if (initial is not null) _pending!.Add((tx, doc) => InsertCore(tx, doc, 0, initial, default));
    }

    /// <summary>Yjs <c>length</c>: UTF-16 units plus one per embed (Q17).</summary>
    public int Length
    {
        get
        {
            if (AttachedDoc is not { } doc) return 0;
            var h = doc.EnterRead();
            try
            {
                return Branch.Length;
            }
            finally
            {
                doc.Leave(h);
            }
        }
    }

    /// <summary>
    /// Yjs <c>insert</c>: an index past the end appends (T-1); an empty string changes nothing. Without <paramref name="attributes"/> (undefined
    /// or null) the text takes the formats in effect at the index; with an object it has exactly those formats (F-2).
    /// </summary>
    public void Insert(int index, string text, in YValue attributes = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        CheckAttributes(attributes);
        if (text.Length == 0) return;
        var attrs = attributes;
        Write((tx, doc) => InsertCore(tx, doc, index, text, attrs));
    }

    /// <summary>Yjs <c>insertEmbed</c> of a JSON value: one unit long, skipped by <see cref="ToString"/> (E-1).</summary>
    public void InsertEmbed(int index, in YValue embed, in YValue attributes = default)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        CheckEmbed(embed);
        CheckAttributes(attributes);
        InsertEmbedCore(index, embed, attributes);
    }

    /// <summary>Yjs <c>insertEmbed</c> of a shared type: the type is integrated once this returns on an integrated text (E-1, E-4).</summary>
    public void InsertEmbed(int index, YType embed, in YValue attributes = default)
    {
        ArgumentNullException.ThrowIfNull(embed);
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        YEntry content = embed;
        TypeList.Validate(new ReadOnlySpan<YEntry>(in content), undefinedAllowed: false);
        CheckAttributes(attributes);
        InsertEmbedCore(index, content, attributes);
    }

    /// <summary>Yjs <c>delete</c>: zero changes nothing; a range past the end deletes what exists, without throwing (T-2).</summary>
    public void Delete(int index, int length)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (length == 0) return;
        Write((tx, doc) => TypeText.DeleteText(tx, doc, TypeText.FindPosition(doc, Branch, index, useSearchMarker: true), length));
    }

    /// <summary>Yjs <c>format</c>: zero, or an index at or past the end, changes nothing (F-6).</summary>
    public void Format(int index, int length, in YValue attributes)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (attributes.Kind != YValueKind.Object) throw new ArgumentException("The attributes must be an object.", nameof(attributes));
        if (length == 0) return;
        var attrs = attributes;
        Write((tx, doc) =>
        {
            var pos = TypeText.FindPosition(doc, Branch, index, useSearchMarker: false);
            if (pos.Right.IsNull) return;
            TypeText.FormatText(tx, doc, Branch, pos, length, TypeText.Attributes.From(attrs));
        });
    }

    /// <summary>
    /// Yjs <c>applyDelta</c>, in one transaction (T-6). With <paramref name="sanitize"/> false, a trailing newline of a last string insert at the
    /// end of the text is dropped. An insert of an empty string is skipped (T-8); a retain past the end appends newlines (T-9). A delta applied
    /// before integration is replayed with <paramref name="sanitize"/> true, as in JS (T-7).
    /// </summary>
    public void ApplyDelta(ReadOnlySpan<TextDeltaOp> delta, bool sanitize = true)
    {
        foreach (var op in delta)
        {
            if (op.Kind == DeltaKind.Insert && op.Text is null)
            {
                var embed = op.Embed;
                if (embed.Kind == YEntryKind.Type) TypeList.Validate(new ReadOnlySpan<YEntry>(in embed), undefinedAllowed: false);
                else CheckEmbed(op.Embed.Value);
            }

            CheckAttributes(op.Attributes);
        }

        var ops = delta.ToArray();
        if (AttachedDoc is null) sanitize = true;
        Write((tx, doc) => ApplyDeltaCore(tx, doc, ops, sanitize));
    }

    /// <summary>
    /// Yjs <c>toDelta</c>. With <paramref name="snapshot"/> it reads the text as it was then; with <paramref name="previous"/> as well, the runs
    /// added or removed between the two carry a <c>ychange</c> attribute (<paramref name="computeYChange"/>'s value, else <c>{type}</c>). A
    /// snapshot read splits the structs the snapshots cut, in a write transaction with the origin <c>"cleanup"</c> (or the caller's own write
    /// transaction), which merges them again when it commits (SNAP-05 to SNAP-07). Inside a read transaction a snapshot read throws
    /// <see cref="YConcurrencyException"/>, as any write does there (CC-3).
    /// </summary>
    public TextDeltaOp[] ToDelta(YSnapshot? snapshot = null, YSnapshot? previous = null, Func<string, ItemId, YValue>? computeYChange = null)
    {
        if (AttachedDoc is not { } doc) return Array.Empty<TextDeltaOp>();
        if (snapshot is not null || previous is not null)
        {
            var w = doc.EnterWrite("cleanup", local: true);
            try
            {
                snapshot?.SplitAffectedStructs(w.State);
                previous?.SplitAffectedStructs(w.State);
                return TypeText.ToDelta(doc, Branch, snapshot, previous, computeYChange);
            }
            finally
            {
                doc.Leave(w);
            }
        }

        var h = doc.EnterRead();
        try
        {
            return TypeText.ToDelta(doc, Branch);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    /// <summary>Copies the string (<see cref="ToString"/>, without embeds) into <paramref name="destination"/>, which must be long enough.</summary>
    public void CopyTo(Span<char> destination)
    {
        var text = ToString();
        if (destination.Length < text.Length) throw new ArgumentException("The destination is shorter than the text.", nameof(destination));
        text.AsSpan().CopyTo(destination);
    }

    /// <summary>Yjs <c>toString</c>: the undeleted strings; embeds are skipped (T-3).</summary>
    public override string ToString()
    {
        if (AttachedDoc is not { } doc) return "";
        var h = doc.EnterRead();
        try
        {
            return TypeText.ToString(doc.Store, Branch);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    /// <summary>Counterpart of JS cleanupYTextFormatting: the number of redundant format items removed (Spec 10, ADR 0015).</summary>
    internal int CleanupFormatting()
    {
        if (AttachedDoc is not { } doc) return 0;
        var h = doc.EnterWrite(null, local: true);
        try
        {
            return TypeText.CleanupFormatting(h.Tx, doc, Branch);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    // map-side attributes, shared with YXmlText (X-1)

    /// <summary>Yjs <c>setAttribute</c>.</summary>
    public void SetAttribute(string name, in YValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        YEntry entry = value;
        TypeList.Validate(new ReadOnlySpan<YEntry>(in entry), undefinedAllowed: true);
        Write((tx, _) => TypeMap.Set(tx, this, name, entry));
    }

    /// <summary>Yjs <c>getAttribute</c>: false when the attribute is absent or deleted.</summary>
    public bool TryGetAttribute(string name, out YValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        value = default;
        if (AttachedDoc is not { } doc) return false;
        var h = doc.EnterRead();
        try
        {
            if (!TypeMap.TryGet(this, name, out var entry)) return false;
            value = entry.Value;
            return true;
        }
        finally
        {
            doc.Leave(h);
        }
    }

    /// <summary>Yjs <c>removeAttribute</c>.</summary>
    public void RemoveAttribute(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Write((tx, _) => TypeMap.Delete(tx, this, name));
    }

    /// <summary>Yjs <c>getAttributes</c>: the live attributes (X-2), a snapshot.</summary>
    public IReadOnlyDictionary<string, YValue> GetAttributes()
    {
        var result = new Dictionary<string, YValue>(StringComparer.Ordinal);
        if (AttachedDoc is not { } doc) return result;
        var h = doc.EnterRead();
        try
        {
            foreach (var (key, entry) in TypeMap.Entries(this)) result[key] = entry.Value;
        }
        finally
        {
            doc.Leave(h);
        }

        return result;
    }

    /// <summary>
    /// Yjs <c>clone</c>: a prelim text holding this text's delta. Embedded types are cloned (JS inserts the live instances, which cannot be
    /// integrated twice).
    /// </summary>
    public override YType Clone()
    {
        var copy = CreateEmpty();
        var delta = ToDelta();
        for (var i = 0; i < delta.Length; i++)
        {
            var op = delta[i];
            if (op.Embed.Kind == YEntryKind.Type) delta[i] = new TextDeltaOp(op.Kind, op.Length, op.Text, op.Embed.Type!.Clone(), op.Attributes);
        }

        copy.ApplyDelta(delta);
        return copy;
    }

    /// <summary>A new, empty instance of this class, for <see cref="Clone"/> (Yjs <c>_copy</c>).</summary>
    private protected virtual YText CreateEmpty() => new();

    /// <summary>Yjs <c>toJSON</c>: the string (X-T3).</summary>
    public override YValue ToJson() => ToString();

    /// <summary>Yjs <c>YText._integrate</c>: the queued writes run in order, inside the integrating transaction.</summary>
    internal override void OnIntegrated(Transaction tx)
    {
        var pending = _pending;
        _pending = null;
        if (pending is null) return;
        foreach (var write in pending) write(tx, AttachedDoc!);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Runs <paramref name="write"/> in a write transaction, or queues it while the text is prelim.</summary>
    private void Write(Action<Transaction, Doc> write)
    {
        if (AttachedDoc is not { } doc)
        {
            _pending!.Add(write);
            return;
        }

        var h = doc.EnterWrite(null, local: true);
        try
        {
            write(h.Tx, doc);
        }
        finally
        {
            doc.Leave(h);
        }
    }

    private void InsertCore(Transaction tx, Doc doc, int index, string text, in YValue attributes)
    {
        var inherit = attributes.Kind is YValueKind.Undefined or YValueKind.Null;
        var pos = TypeText.FindPosition(doc, Branch, index, useSearchMarker: inherit);
        var attrs = inherit ? TypeText.Attributes.From(pos.Current) : TypeText.Attributes.From(attributes);
        TypeText.InsertText(tx, doc, Branch, pos, text, attrs);
    }

    private void InsertEmbedCore(int index, YEntry content, YValue attributes)
    {
        // JS insertEmbed: no attributes means an empty object (formats in effect are ended), and only then the search markers are used
        var useMarker = attributes.Kind is YValueKind.Undefined or YValueKind.Null;
        Write((tx, doc) =>
        {
            var pos = TypeText.FindPosition(doc, Branch, index, useMarker);
            TypeText.InsertText(tx, doc, Branch, pos, content, TypeText.Attributes.From(attributes));
        });
    }

    private void ApplyDeltaCore(Transaction tx, Doc doc, TextDeltaOp[] delta, bool sanitize)
    {
        var pos = new TypeText.Position(ItemRef.Null, StructStore.StartOf(Branch), 0, new OrderedDictionary<string, YValue>(StringComparer.Ordinal));
        for (var i = 0; i < delta.Length; i++)
        {
            var op = delta[i];
            switch (op.Kind)
            {
                case DeltaKind.Insert:
                    if (op.Text is { } text)
                    {
                        if (!sanitize && i == delta.Length - 1 && pos.Right.IsNull && text.EndsWith('\n')) text = text[..^1];
                        if (text.Length > 0) TypeText.InsertText(tx, doc, Branch, pos, text, TypeText.Attributes.From(op.Attributes));
                    }
                    else
                    {
                        TypeText.InsertText(tx, doc, Branch, pos, op.Embed, TypeText.Attributes.From(op.Attributes));
                    }

                    break;
                case DeltaKind.Retain:
                    TypeText.FormatText(tx, doc, Branch, pos, op.Length, TypeText.Attributes.From(op.Attributes));
                    break;
                case DeltaKind.Delete:
                    TypeText.DeleteText(tx, doc, pos, op.Length);
                    break;
            }
        }
    }

    /// <summary>Attributes are an object, or undefined or null for none.</summary>
    private static void CheckAttributes(in YValue attributes)
    {
        if (attributes.Kind is not (YValueKind.Undefined or YValueKind.Null or YValueKind.Object))
            throw new ArgumentException("The attributes must be an object.", nameof(attributes));
    }

    /// <summary>An embed is written as JSON (V1) or Any (V2), so it must be a value JSON can hold (ADR 0005).</summary>
    private static void CheckEmbed(in YValue embed)
    {
        if (embed.Kind is YValueKind.Undefined or YValueKind.Bytes) throw new ArgumentException("An embed must be a JSON value.", nameof(embed));
        if (!JsJsonWriter.Write(embed, new ArrayBufferWriter<byte>()).Ok)
            throw new ArgumentException("An embed must be a value JSON can represent.", nameof(embed));
    }
}
