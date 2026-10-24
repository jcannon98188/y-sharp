using System;
using System.Reflection;

namespace YSharp.TestKit.Ops;

/// <summary>
/// Reflection adapter (the TestKit cannot see internals, ADR 0015; tests in YSharp.Tests read the members directly) for the internal UndoManager members that the public API of ADR 0008 hides but the upstream tests read: <c>bool Undoing</c>, <c>bool Redoing</c> and
/// <c>StackItem? CurrStackItem</c> (JS <c>undoManager.undoing</c>, <c>redoing</c>, <c>currStackItem</c>, UM-14). Looked up by name so the
/// TestKit compiles against the stub; a missing member is a clear failure once the implementation exists.
/// </summary>
public static class UndoSeams
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static object? Get(UndoManager um, string name) =>
        (typeof(UndoManager).GetProperty(name, Any) ?? throw new InvalidOperationException($"UndoManager.{name} is missing. Spec 8 needs it (see UndoSeams).")).GetValue(um);

    public static bool Undoing(UndoManager um) => (bool)Get(um, "Undoing")!;
    public static bool Redoing(UndoManager um) => (bool)Get(um, "Redoing")!;
    public static StackItem? CurrStackItem(UndoManager um) => (StackItem?)Get(um, "CurrStackItem");
}
