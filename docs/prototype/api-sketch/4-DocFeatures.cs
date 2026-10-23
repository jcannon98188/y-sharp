#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;

namespace YSharp;

// ---------------------------------------------------------------- snapshots

public sealed class YSnapshot : IEquatable<YSnapshot>
{
    public static YSnapshot Create(Doc doc) => throw Sketch.NI;
    public static YSnapshot Empty => throw Sketch.NI;   // immutable, unlike the JS module singleton (SNAP-04)
    public StateVector StateVector => throw Sketch.NI;
    public DeleteSet DeleteSet => throw Sketch.NI;
    public void Encode(IBufferWriter<byte> writer, UpdateFormat format = UpdateFormat.V1) => throw Sketch.NI;
    public static YStatus TryDecode(ReadOnlySpan<byte> bytes, UpdateFormat format, out YSnapshot snapshot) { snapshot = null!; throw Sketch.NI; }
    public bool Equals(YSnapshot? other) => throw Sketch.NI;
    public bool ContainsUpdate(ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1) => throw Sketch.NI;
    public static Doc CreateDoc(Doc origin, YSnapshot snapshot, Doc? target = null) => throw Sketch.NI;   // origin.Gc must be false
}

// ---------------------------------------------------------------- relative positions

public readonly record struct RelativePosition(ItemId? Item, string? TypeName, ItemId? Type, int Assoc)
{
    public void Encode(IBufferWriter<byte> writer) => throw Sketch.NI;
    public static YStatus TryDecode(ReadOnlySpan<byte> bytes, out RelativePosition position) { position = default; throw Sketch.NI; }
    public YValue ToJson() => throw Sketch.NI;
    public static YStatus TryFromJson(in YValue json, out RelativePosition position) { position = default; throw Sketch.NI; }
    /// null when the anchor is unknown or collected. REVIEW: tname anchors create a placeholder root type in JS (RP-06); here they do not.
    public AbsolutePosition? Resolve(Doc doc, bool followUndoneDeletions = true) => throw Sketch.NI;
}
public readonly record struct AbsolutePosition(YType Type, int Index, int Assoc);

// ---------------------------------------------------------------- undo

public sealed class StackItem
{
    public DeleteSet Insertions => throw Sketch.NI;
    public DeleteSet Deletions => throw Sketch.NI;
    public Dictionary<object, object?> Meta { get; } = new();   // caller-owned, not protected (CC-20)
}

public sealed class UndoManagerOptions
{
    public TimeSpan CaptureTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
    public Func<Transaction, bool>? CaptureTransaction { get; init; }
    public Func<ItemView, bool>? DeleteFilter { get; init; }
    public ISet<object?>? TrackedOrigins { get; init; }          // default { null }; UndoManager adds itself
    public bool IgnoreRemoteMapChanges { get; init; }
    public TimeProvider? TimeProvider { get; init; }             // replaces Date.now
}

public readonly record struct StackItemEvent(StackItem Item, object? Origin, bool IsRedo, IReadOnlyCollection<YType> ChangedParentTypes);

public sealed class UndoManager : IDisposable
{
    public UndoManager(YType scope, UndoManagerOptions? options = null) { }
    public UndoManager(IReadOnlyList<YType> scope, UndoManagerOptions? options = null) { }
    public UndoManager(Doc scope, UndoManagerOptions? options = null) { }
    public IReadOnlyList<StackItem> UndoStack => throw Sketch.NI;
    public IReadOnlyList<StackItem> RedoStack => throw Sketch.NI;
    public bool CanUndo => throw Sketch.NI;
    public bool CanRedo => throw Sketch.NI;
    public StackItem? Undo() => throw Sketch.NI;                  // write transaction
    public StackItem? Redo() => throw Sketch.NI;
    public void StopCapturing() => throw Sketch.NI;
    public void Clear(bool undo = true, bool redo = true) => throw Sketch.NI;
    public void AddToScope(YType type) => throw Sketch.NI;
    public void AddTrackedOrigin(object? origin) => throw Sketch.NI;   // a Type value matches by constructor type (UM-01)
    public void RemoveTrackedOrigin(object? origin) => throw Sketch.NI;
    public event Action<StackItemEvent>? StackItemAdded, StackItemUpdated, StackItemPopped;
    public event Action<bool, bool>? StackCleared;
    public void Dispose() => throw Sketch.NI;
}

// ---------------------------------------------------------------- permanent user data

public sealed class PermanentUserDataOptions
{
    public YMap? Store { get; init; }                      // default doc.GetMap("users")
    public Action<Action>? Defer { get; init; }            // seam for the setTimeout(0) in PUD-03/04; tests pass a synchronous flush
}

public sealed class PermanentUserData
{
    public PermanentUserData(Doc doc, PermanentUserDataOptions? options = null) { }
    public void SetUserMapping(Doc doc, uint clientId, string description, Func<Transaction, DeleteSet, bool>? filter = null) => throw Sketch.NI;
    public string? GetUserByClientId(uint clientId) => throw Sketch.NI;
    public string? GetUserByDeletedId(ItemId id) => throw Sketch.NI;
}
