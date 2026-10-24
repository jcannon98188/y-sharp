#nullable enable
using System;
using System.Collections.Generic;

namespace YSharp;

// ---------------------------------------------------------------- list/map entries

public enum YEntryKind { Any, Binary, Type, Doc }

/// What can sit in a list or map position. Binary vs Any is wire-visible (ContentBinary vs ContentAny).
public readonly struct YEntry
{
    public YEntryKind Kind => throw Sketch.NI;
    public YValue Value => throw Sketch.NI;            // Any: the value; Binary: YValue Bytes
    public YType? Type => throw Sketch.NI;
    public Doc? Doc => throw Sketch.NI;
    public static implicit operator YEntry(YValue v) => throw Sketch.NI;   // REVIEW: top-level Bytes becomes ContentBinary, as in JS (T-0.10)
    public static implicit operator YEntry(YType t) => throw Sketch.NI;    // prelim or live; already integrated elsewhere throws (T-0.13)
    public static implicit operator YEntry(Doc d) => throw Sketch.NI;
    public static implicit operator YEntry(string s) => throw Sketch.NI;
    public static implicit operator YEntry(double d) => throw Sketch.NI;
    public static implicit operator YEntry(bool b) => throw Sketch.NI;
}

// ---------------------------------------------------------------- base

public abstract class YType
{
    public Doc? Doc => throw Sketch.NI;               // null until integrated (prelim)
    public YType? Parent => throw Sketch.NI;
    public bool IsIntegrated => Doc != null;

    // REVIEW (T-0.4): reads on an unintegrated type return the empty value, as JS does (no throw). Writes buffer as prelim content.
    public Subscription Observe(YEventHandler<YEvent> handler) => throw Sketch.NI;
    public Subscription ObserveDeep(YDeepEventHandler handler) => throw Sketch.NI;
    public void Unobserve(YEventHandler<YEvent> handler) => throw Sketch.NI;
    public void UnobserveDeep(YDeepEventHandler handler) => throw Sketch.NI;

    public RelativePosition CreateRelativePosition(int index, int assoc = 0) => throw Sketch.NI;
    public abstract YType Clone();
    public abstract YValue ToJson();
}

public delegate void YEventHandler<in TEvent>(TEvent e, Transaction tx) where TEvent : YEvent;
public delegate void YDeepEventHandler(IReadOnlyList<YEvent> events, Transaction tx);

public readonly struct Subscription : IDisposable { public void Dispose() => throw Sketch.NI; }

// ---------------------------------------------------------------- events

public readonly record struct YPathSegment(string? Key, int Index);   // Key for map parents, Index for list parents (V-2)

/// REVIEW (open question 3): event objects are pooled and valid only during dispatch (IsValid false afterwards).
public abstract class YEvent
{
    public YType Target => throw Sketch.NI;
    public YType CurrentTarget => throw Sketch.NI;       // changes per deep handler call (V-3)
    public Transaction Transaction => throw Sketch.NI;
    public bool IsValid => throw Sketch.NI;
    public IReadOnlyList<YPathSegment> GetPath() => throw Sketch.NI;    // lazy, allocates
    public bool Adds(ItemId id) => throw Sketch.NI;
    public bool Deletes(ItemId id) => throw Sketch.NI;
    public IReadOnlyDictionary<string, KeyChange> Keys => throw Sketch.NI;   // lazy
}

public enum KeyChangeAction { Add, Update, Delete }
public readonly record struct KeyChange(KeyChangeAction Action, YEntry? OldValue);

public enum DeltaKind { Insert, Delete, Retain }
/// Counts only. Items are not exposed (open question 2).
public readonly struct ListDeltaOp { public DeltaKind Kind { get; } public int Length { get; } public ReadOnlyMemory<YEntry> Inserted { get; } }
public readonly struct TextDeltaOp
{
    // TASK-2.7: ApplyDelta needs a way to build ops (T-6), so the sketch gains a constructor.
    public TextDeltaOp(DeltaKind kind, int length, string? text = null, YEntry embed = default, YValue attributes = default)
    {
        Kind = kind; Length = length; Text = text; Embed = embed; Attributes = attributes;
    }

    public DeltaKind Kind { get; }
    public int Length { get; }
    public string? Text { get; }
    public YEntry Embed { get; }
    public YValue Attributes { get; }
}

public sealed class YArrayEvent : YEvent { public ReadOnlySpan<ListDeltaOp> Delta => throw Sketch.NI; }
public sealed class YMapEvent : YEvent { public IReadOnlyCollection<string> KeysChanged => throw Sketch.NI; }
public sealed class YTextEvent : YEvent { public ReadOnlySpan<TextDeltaOp> Delta => throw Sketch.NI; public bool ChildListChanged => throw Sketch.NI; public IReadOnlyCollection<string> KeysChanged => throw Sketch.NI; }
public sealed class YXmlEvent : YEvent { public ReadOnlySpan<ListDeltaOp> Delta => throw Sketch.NI; public bool ChildListChanged => throw Sketch.NI; public IReadOnlyCollection<string> AttributesChanged => throw Sketch.NI; }
// YXmlText raises YTextEvent and YXmlHook raises YMapEvent (T-0.1).

// ---------------------------------------------------------------- YArray

public sealed class YArray : YType
{
    public YArray() { }
    public static YArray From(IEnumerable<YEntry> items) => throw Sketch.NI;
    public int Length => throw Sketch.NI;
    public YEntry this[int index] => throw Sketch.NI;                       // ArgumentOutOfRangeException (REVIEW: JS returns undefined)
    public void Insert(int index, ReadOnlySpan<YEntry> content) => throw Sketch.NI;   // index > Length throws, as JS ("Length exceeded!")
    public void Push(ReadOnlySpan<YEntry> content) => throw Sketch.NI;
    public void Unshift(ReadOnlySpan<YEntry> content) => throw Sketch.NI;
    public void Delete(int index, int length = 1) => throw Sketch.NI;       // partial effect then throws, as JS (A-5)
    public void CopyTo(int start, Span<YEntry> destination) => throw Sketch.NI;
    public YEntry[] ToArray() => throw Sketch.NI;
    public YEntry[] Slice(int start, int? end = null) => throw Sketch.NI;      // JS slice: negative indexes count from the end, an end past Length stops at the end; end defaults to Length (A-8)
    public ListEnumerator GetEnumerator() => throw Sketch.NI;               // live inside an explicit transaction; snapshot otherwise (CC-9)
    public override YType Clone() => throw Sketch.NI;
    public override YValue ToJson() => throw Sketch.NI;
}

public struct ListEnumerator { public YEntry Current => throw Sketch.NI; public bool MoveNext() => throw Sketch.NI; }

// ---------------------------------------------------------------- YMap

public class YMap : YType
{
    public YMap() { }
    public YMap(IEnumerable<KeyValuePair<string, YEntry>> entries) { }
    public int Count => throw Sketch.NI;
    public T Set<T>(string key, T type) where T : YType => throw Sketch.NI;  // returns the live instance (T-0.12)
    public void Set(string key, YEntry value) => throw Sketch.NI;
    public bool TryGet(string key, out YEntry value) { value = default; throw Sketch.NI; }   // false only when absent
    public bool Has(string key) => throw Sketch.NI;                          // true for a key set to Undefined (T-0.11)
    public bool Delete(string key) => throw Sketch.NI;
    public void Clear() => throw Sketch.NI;
    public IReadOnlyList<string> Keys => throw Sketch.NI;                    // snapshot
    public MapEnumerator GetEnumerator() => throw Sketch.NI;
    public override YType Clone() => throw Sketch.NI;
    public override YValue ToJson() => throw Sketch.NI;
}

public struct MapEnumerator { public KeyValuePair<string, YEntry> Current => throw Sketch.NI; public bool MoveNext() => throw Sketch.NI; }

// ---------------------------------------------------------------- YText

public class YText : YType
{
    public YText(string? initial = null) { }
    public int Length => throw Sketch.NI;                                    // UTF-16 code units (Q17)
    public void Insert(int index, string text, in YValue attributes = default) => throw Sketch.NI;   // clamps index, as JS
    public void InsertEmbed(int index, in YValue embed, in YValue attributes = default) => throw Sketch.NI;
    public void InsertEmbed(int index, YType embed, in YValue attributes = default) => throw Sketch.NI;
    public void Delete(int index, int length) => throw Sketch.NI;
    public void Format(int index, int length, in YValue attributes) => throw Sketch.NI;
    public void ApplyDelta(ReadOnlySpan<TextDeltaOp> delta, bool sanitize = true) => throw Sketch.NI;
    public TextDeltaOp[] ToDelta(YSnapshot? snapshot = null, YSnapshot? previous = null, Func<string, ItemId, YValue>? computeYChange = null) => throw Sketch.NI;
    public void CopyTo(Span<char> destination) => throw Sketch.NI;
    public override string ToString() => throw Sketch.NI;
    // map-side attributes, shared with YXmlText
    public void SetAttribute(string name, in YValue value) => throw Sketch.NI;
    public bool TryGetAttribute(string name, out YValue value) { value = default; throw Sketch.NI; }
    public void RemoveAttribute(string name) => throw Sketch.NI;
    public IReadOnlyDictionary<string, YValue> GetAttributes() => throw Sketch.NI;   // TASK-2.7: live attributes (X-2); YXmlElement has the same method
    public override YType Clone() => throw Sketch.NI;
    public override YValue ToJson() => throw Sketch.NI;                      // plain text for YText (X-T3)
}

// ---------------------------------------------------------------- XML (toDOM is out of scope)

public class YXmlFragment : YType
{
    public int Length => throw Sketch.NI;                                    // prelim length before integration (X-F6)
    public YType? FirstChild => throw Sketch.NI;
    public YType this[int index] => throw Sketch.NI;
    public void Insert(int index, ReadOnlySpan<YType> content) => throw Sketch.NI;
    public void InsertSubdoc(int index, Doc subdoc) => throw Sketch.NI;      // Yjs lets a fragment hold a subdoc; read it back through Doc.Subdocs (indexer throws at that position)
    public void InsertAfter(YType? reference, ReadOnlySpan<YType> content) => throw Sketch.NI;
    public void Push(ReadOnlySpan<YType> content) => throw Sketch.NI;
    public void Unshift(ReadOnlySpan<YType> content) => throw Sketch.NI;
    public void Delete(int index, int length = 1) => throw Sketch.NI;
    public IEnumerable<YType> TreeWalk(Func<YType, bool>? filter = null) => throw Sketch.NI;
    public YXmlElement? QuerySelector(string nodeName) => throw Sketch.NI;
    public IReadOnlyList<YXmlElement> QuerySelectorAll(string nodeName) => throw Sketch.NI;
    public override string ToString() => throw Sketch.NI;
    public override YType Clone() => throw Sketch.NI;
    public override YValue ToJson() => throw Sketch.NI;                      // the XML string (X-F5)
}

public sealed class YXmlElement : YXmlFragment
{
    public YXmlElement(string nodeName = "UNDEFINED") { }
    public string NodeName => throw Sketch.NI;
    public YType? NextSibling => throw Sketch.NI;
    public YType? PrevSibling => throw Sketch.NI;
    public void SetAttribute(string name, in YValue value) => throw Sketch.NI;
    public bool TryGetAttribute(string name, out YValue value) { value = default; throw Sketch.NI; }
    public bool HasAttribute(string name) => throw Sketch.NI;
    public void RemoveAttribute(string name) => throw Sketch.NI;
    public IReadOnlyDictionary<string, YValue> GetAttributes(YSnapshot? snapshot = null) => throw Sketch.NI;
    public override string ToString() => throw Sketch.NI;                    // invariant lowercasing (X-E3). REVIEW: diverges from toLocaleLowerCase
}

public sealed class YXmlText : YText
{
    public YXmlText(string? initial = null) : base(initial) { }
    public YType? NextSibling => throw Sketch.NI;
    public YType? PrevSibling => throw Sketch.NI;
    public override YValue ToJson() => throw Sketch.NI;                      // tagged string, unlike YText (X-T3)
}

public sealed class YXmlHook : YMap
{
    public YXmlHook(string hookName) { }
    public string HookName => throw Sketch.NI;
}

public sealed class YUntyped : YType   // placeholder from Doc.Get(name) before a concrete type is requested
{
    public override YType Clone() => throw Sketch.NI;
    public override YValue ToJson() => throw Sketch.NI;
}
