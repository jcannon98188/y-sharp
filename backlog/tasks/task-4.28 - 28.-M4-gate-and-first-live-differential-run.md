---
id: TASK-4.28
title: 28. M4 gate and first live differential run
status: To Do
assignee: []
created_date: '2026-10-24 19:42'
updated_date: '2026-10-25 23:32'
labels:
  - spec
  - 'model:sonnet'
milestone: m-4
dependencies:
  - TASK-4.20
  - TASK-4.22
  - TASK-4.23
  - TASK-4.25
  - TASK-4.26
  - TASK-4.27
parent_task_id: TASK-4
ordinal: 28000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Close M4 by the ADR 0018 gate, with the slow memory tier now runnable, and run the live Node differential for the first time while divergences are still cheap.

**Owns:** `AllocationBudgets.Limits` rows for M4
**Budgets:** BUD-01, 02, 04, 05, 07, 09 (bounded measured plus 25%, zero paths read 0); BUD-13 and BUD-14 slow tier green
**Handoff section-7 items:** First live Node-versus-y-sharp runs (text, array and map generators); any divergence handled under ADR 0022.
**Suggested model:** sonnet (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every listed budget recorded and active; slow memory tier green
- [ ] #2 `TypeFixtureTests` and `WireFixtureTests` pass
- [ ] #3 Live differential smoke and nightly text/array/map runs pass, or each mismatch is fixed or raised as an owner decision
- [ ] #4 Every M4 spec is Done and the M4 milestone is closed
- [ ] #5 The traceability gate passes and no pending registry entry is added
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
From TASK-4.24: HotPathBudgetTests BUD-05 and BUD-07 are gated on Events, so this gate activates them. Release measurements at TASK-4.24: BUD-05 1150 bytes per operation (OrderedDictionary x9, closures x3, Position x2, HashSet, Transaction); BUD-07 782 bytes (Item x3, Int32[] x3, OrderedDictionary x2). ADR 0009 makes both zero, but BUD-01 option A allocates a Transaction for each Transact and every insert allocates an Item, so zero needs an owner decision (bounded budgets) or pooling work. This is listed for owner sign-off in TASK-4.24.
<!-- SECTION:NOTES:END -->
