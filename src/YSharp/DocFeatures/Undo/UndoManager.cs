#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

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

    // internal test seams (ADR 0015): JS undoManager.undoing, redoing, currStackItem (UM-14)
    internal bool Undoing => throw Sketch.NI;
    internal bool Redoing => throw Sketch.NI;
    internal StackItem? CurrStackItem => throw Sketch.NI;
}

