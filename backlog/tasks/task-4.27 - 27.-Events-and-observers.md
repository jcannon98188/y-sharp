---
id: TASK-4.27
title: 27. Events and observers
status: In Progress
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 00:07'
labels:
  - spec
  - 'model:opus'
milestone: m-4
dependencies:
  - TASK-4.21
parent_task_id: TASK-4
ordinal: 27000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Event building, pooled dispatch-scoped events, observe and observe-deep, doc transaction events and observer-fault containment (ADR 0005, ADR 0008), implemented through the cleanup hooks.

**Features:** `Events`
**Owns:** `src/YSharp/Events/`, `Doc.Events.cs`, the observer hook implementations
**Registry:** `E` entries, observer-fault `ERR` entries, Q23-Q25
**Test files:** `Types/EventTests.cs`, `Types/TransactionTests.cs`, `Structs/TransactionTests.cs`, `Concurrency/ObserverTests.cs`
**Suggested model:** opus (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [ ] #2 This spec's features are added to `Features.Landed`
- [ ] #3 The traceability gate passes and no pending registry entry is added
- [ ] #4 An `OutOfMemoryException` from an observer propagates and is not an observer fault (ERR-19)
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 `dotnet build YSharp.slnx` has 0 warnings
- [ ] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [ ] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [ ] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Gap found in TASK-4.22 (2026-10-25), for this spec: Doc.Yata.cs AddChangedType returns early when parent.Owner is null, i.e. a nested type that arrived in an update and was never read has no YType yet. Yjs records every changed type, so observeDeep on an ancestor could miss events for changes inside such a type. Likely fix: create the owner with YType.Of(parent, tx.Doc) instead of returning; check the BUD-08 allocation budget for remote applies (it would allocate one YType per untouched nested type). Related: Doc.Transactions.ClearStaleMarkers (TASK-4.22) relies on the changed set and runs before HookBeforeObservers; keep that order when the observer hooks land.

From TASK-4.25: HookFormattingCleanup (Yjs cleanupYTextAfterTransaction) runs in CleanupOne after HookAfterTransaction, in the same try block. Yjs runs the observers, the deep observers, afterTransaction and the formatting cleanup through callAll, so each still runs when an earlier one throws. When observers land, keep that: an observer exception must not skip the formatting cleanup.
Changed types that no YType object wraps yet are not in tx.Changed (AddChangedType skips them; the texts among them are tracked in TxState.UnwrappedTextsChanged for the cleanup). Yjs has an object for every type, so deep-observe parent chains through an unread nested type need the same care.
<!-- SECTION:NOTES:END -->
