#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public enum KeyChangeAction { Add, Update, Delete }
public readonly record struct KeyChange(KeyChangeAction Action, YEntry? OldValue);

public enum DeltaKind { Insert, Delete, Retain }
/// Counts only. Items are not exposed (open question 2).
public readonly struct ListDeltaOp
{
    internal ListDeltaOp(DeltaKind kind, int length, ReadOnlyMemory<YEntry> inserted = default)
    {
        Kind = kind; Length = length; Inserted = inserted;
    }

    public DeltaKind Kind { get; }
    public int Length { get; }
    public ReadOnlyMemory<YEntry> Inserted { get; }
}
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

