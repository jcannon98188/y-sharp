---
id: TASK-4.17
title: 17. M2 gate
status: To Do
assignee: []
created_date: '2026-10-24 19:41'
updated_date: '2026-10-24 19:42'
labels:
  - spec
  - 'model:haiku'
milestone: m-2
dependencies:
  - TASK-4.15
  - TASK-4.16
parent_task_id: TASK-4
ordinal: 17000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Close M2 by the ADR 0018 milestone gate.

**Owns:** `AllocationBudgets.Limits` rows for M2, `docs/inventory/budgets.yaml` statuses
**Budgets:** BUD-06 (`EncodeStateAsUpdate` into a pre-sized writer, zero); record the V1 figure for BUD-08 in the notes (entry stays pending until spec 20)
**Suggested model:** haiku (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The spike harness memory gate is green on the real store
- [ ] #2 BUD-06 reads 0 and is active; the BUD-08 V1 measurement is in the notes
- [ ] #3 Every M2 spec is Done and the M2 milestone is closed
- [ ] #4 The traceability gate passes and no pending registry entry is added
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 `dotnet build YSharp.slnx` has 0 warnings
- [ ] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [ ] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [ ] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->
