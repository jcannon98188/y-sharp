---
id: TASK-4.32
title: 32. Undo manager
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 08:27'
labels:
  - spec
  - 'model:opus'
milestone: m-5
dependencies:
  - TASK-4.29
  - TASK-4.31
parent_task_id: TASK-4
ordinal: 32000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`UndoManager` with scopes, capture timeout, stack events and tracked origins.

**Features:** `Undo`
**Owns:** `src/YSharp/DocFeatures/Undo/`
**Registry:** `UM` entries
**Test files:** `DocFeatures/UndoManagerTests.cs`, `DocFeatures/UndoPortTests.cs`, `Structs/ItemHelperTests.cs`
**Seams provided:** `UndoManager.Undoing`, `Redoing`, `CurrStackItem` (TestKit reaches these via `UndoSeams` reflection)
**Suggested model:** opus (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [x] #2 This spec's features are added to `Features.Landed`
- [x] #3 The traceability gate passes and no pending registry entry is added
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 `dotnet build YSharp.slnx` has 0 warnings
- [x] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [x] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [x] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
From TASK-4.31: RelativePosition.Resolve follows redone items through StructStore.RedoneOf(ItemRef), a stub returning null. Spec 32 must back it with item.redone storage, including JS splitItem's copy of redone (rightItem.redone = redone + diff) and its handling on merge.

Implemented UndoManager (src/YSharp/DocFeatures/Undo/UndoManager.cs), StackItem, and the store's redone table (ItemFlags.Redone, RedoneOf/SetRedone/FollowRedone, Keep, CopyContent; split copies redone with +diff, GC drops it, merge refuses redone items). Feature.Undo landed. Undo and relpos JS fixtures replay like the oracle (checked by ungating DocFeatureFixtureTests locally; the subdoc and PUD fixtures still wait on specs 33/34). Debug and Release suites green (8779, 0 failed), 0 warnings, fixtures up to date.

Suite changes (commit b368ee6):
- Gate corrections: 11 UndoPortTests and UndoManagerTests.Undo_RemoteInsertionsInsideTheRange_DeletesOnlyTheCapturedOnes gain Feature.Sync (Users.Init syncs).
- ItemHelperTests.SplitItem_...: final string expectation fixed to XZabc (XYZ inserted at 0 before abc).
- UndoManagerTests.RedoBookkeeping_AfterRedo_IsNotSentOverTheWire runs with gc off: the manager keeps deleted content on the editing replica while the receiver collects it (as in Yjs), so with gc the encodings differ for an unrelated reason.

Owner sign-off:
- Delete-set clients are iterated in ascending client order; Yjs iterates its delete-set Map in insertion order. Order only affects the order redoItem integrates items from different clients within one stack item.
- Exceptions from StackItemAdded/Updated/Popped/StackCleared handlers are reported through Doc.ObserverFaulted (ADR 0005) instead of propagating as in JS.
- Undoing/redoing flags are per-thread: a transaction on another thread during Undo() is captured normally.
- Clearing the redo stack when a new change is captured runs as a follow-up write transaction (origin null) since the capture hook runs inside cleanup.
- Dispose keeps the stacks and Undo/Redo keep working, as in JS destroy().
- CopyContent throws for ContentDoc until spec 33 (subdocs) lands.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Ported the Yjs UndoManager and redoItem: capture with merge timeout, tracked origins (instances and types), delete filter, ignoreRemoteMapChanges, keep for deleted items in scope, stack events, clear and dispose (also on doc destroy). The struct store gained the redone table (follow, split, GC, no-merge). Feature.Undo landed; undo and relpos fixtures replay like Yjs; Debug and Release suites green with 0 warnings. Sign-offs are listed in the notes.
<!-- SECTION:FINAL_SUMMARY:END -->
