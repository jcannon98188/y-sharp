---
id: TASK-4.37
title: 37. M6 gate
status: To Do
assignee: []
created_date: '2026-10-24 19:42'
updated_date: '2026-10-24 19:42'
labels:
  - spec
  - 'model:haiku'
milestone: m-6
dependencies:
  - TASK-4.35
  - TASK-4.36
parent_task_id: TASK-4
ordinal: 37000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Close M6 by the ADR 0018 gate.

**Owns:** `AllocationBudgets.Limits` rows for BUD-10 and BUD-11
**Budgets:** BUD-10 (sync round trip), BUD-11 (awareness encode and apply): measured plus 25%, active
**Suggested model:** haiku (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 BUD-10 and BUD-11 recorded and active
- [ ] #2 Every M6 spec is Done and the M6 milestone is closed
- [ ] #3 The traceability gate passes and no pending registry entry is added
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 `dotnet build YSharp.slnx` has 0 warnings
- [ ] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [ ] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [ ] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->
