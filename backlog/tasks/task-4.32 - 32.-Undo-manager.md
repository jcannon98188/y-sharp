---
id: TASK-4.32
title: 32. Undo manager
status: To Do
assignee: []
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 08:08'
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
- [ ] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [ ] #2 This spec's features are added to `Features.Landed`
- [ ] #3 The traceability gate passes and no pending registry entry is added
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
From TASK-4.31: RelativePosition.Resolve follows redone items through StructStore.RedoneOf(ItemRef), a stub returning null. Spec 32 must back it with item.redone storage, including JS splitItem's copy of redone (rightItem.redone = redone + diff) and its handling on merge.
<!-- SECTION:NOTES:END -->
