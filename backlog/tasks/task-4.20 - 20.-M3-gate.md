---
id: TASK-4.20
title: 20. M3 gate
status: To Do
assignee: []
created_date: '2026-10-24 19:41'
updated_date: '2026-10-24 19:42'
labels:
  - spec
  - 'model:haiku'
milestone: m-3
dependencies:
  - TASK-4.19
parent_task_id: TASK-4
ordinal: 20000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Close M3 by the ADR 0018 milestone gate.

**Owns:** `AllocationBudgets.Limits` rows for BUD-08
**Budgets:** BUD-08 (`TryApplyUpdate` V1 and V2, bounded): measured plus 25%, entry active
**Suggested model:** haiku (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The spike harness memory gate is green
- [ ] #2 BUD-08 is recorded for both formats and active
- [ ] #3 Every M3 spec is Done and the M3 milestone is closed
- [ ] #4 The traceability gate passes and no pending registry entry is added
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 `dotnet build YSharp.slnx` has 0 warnings
- [ ] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [ ] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [ ] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->
