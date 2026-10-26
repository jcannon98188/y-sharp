---
id: TASK-4.39
title: '39. Concurrency scenarios, stress and soak'
status: To Do
assignee: []
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 08:40'
labels:
  - spec
  - 'model:opus'
milestone: m-7
dependencies:
  - TASK-4.28
  - TASK-4.34
  - TASK-4.37
parent_task_id: TASK-4
ordinal: 39000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The CC contract (ADR 0006) under the hand-rolled scheduler, stress and soak, plus the Debug-only CC-18 lock-order checker.

**Features:** `Concurrency`
**Owns:** lock-order checker in `src/YSharp/Transactions/` (Debug only), concurrency test fixes
**Registry:** `CC` entries
**Test files:** `Concurrency/*`, `YSharp.Fuzz.Tests/Concurrency/SoakTests.cs`
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
From TASK-4.33 (subdocs): the Debug lock-order checker that rejects taking a subdocument's lock and then its parent's (CC-18) is not in src. Spec 33 keeps the order by construction (Load and Destroy reach the parent with the subdocument's lock released, see Doc.Subdocs.cs). The checker and its tests (gated on Feature.Concurrency) land here.
<!-- SECTION:NOTES:END -->
