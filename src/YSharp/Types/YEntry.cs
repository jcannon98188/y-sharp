#nullable enable
using System;

namespace YSharp;

public enum YEntryKind { Any, Binary, Type, Doc }

/// What can sit in a list or map position. Binary vs Any is wire-visible (ContentBinary vs ContentAny).
public readonly struct YEntry
{
    private readonly YValue _value;
    private readonly object? _ref;   // the YType or Doc

    private YEntry(YEntryKind kind, YValue value, object? reference)
    {
        Kind = kind;
        _value = value;
        _ref = reference;
    }

    public YEntryKind Kind { get; }

    /// <summary>Any: the value; Binary: a <see cref="YValueKind.Bytes"/> value; otherwise undefined.</summary>
    public YValue Value => Kind is YEntryKind.Any or YEntryKind.Binary ? _value : default;

    public YType? Type => _ref as YType;
    public Doc? Doc => _ref as Doc;

    // REVIEW: top-level Bytes becomes ContentBinary, as in JS (T-0.10)
    public static implicit operator YEntry(YValue v) => new(v.Kind == YValueKind.Bytes ? YEntryKind.Binary : YEntryKind.Any, v, null);

    // prelim or live; inserting one already integrated elsewhere throws at the insert (T-0.13)
    public static implicit operator YEntry(YType t) => new(YEntryKind.Type, default, t ?? throw new ArgumentNullException(nameof(t)));
    public static implicit operator YEntry(Doc d) => new(YEntryKind.Doc, default, d ?? throw new ArgumentNullException(nameof(d)));
    public static implicit operator YEntry(string s) => (YValue)s;
    public static implicit operator YEntry(double d) => (YValue)d;
    public static implicit operator YEntry(bool b) => (YValue)b;
}
