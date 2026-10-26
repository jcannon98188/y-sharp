---
id: TASK-4.37
title: 37. M6 gate
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 09:38'
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
- [x] #1 BUD-10 and BUD-11 recorded and active
- [x] #2 Every M6 spec is Done and the M6 milestone is closed
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
M6 gate. Measured in Release with the AllocationBudgets pending-skip report: BUD-10 sync Step1/Step2 round trip 1464 bytes per operation; BUD-11 awareness Encode 376, Apply 704. Recorded with 25% headroom (1830, 470, 880) in AllocationBudgets.Limits and set active in budgets.yaml (suite commit). The Step1/Step2 path still allocates its intermediate ArrayBufferWriters; a pooled writer would lower BUD-10 if it ever matters.
M6 specs: 4.35 Awareness and 4.36 Sync and auth are Done; this gate closes m-6.
Verification: Debug and Release suites green, HotPathBudgetTests pass in Release with the new limits, traceability gate green, npm run check clean.
Suite changes: budgets commit (AllocationBudgets.cs, budgets.yaml).
Owner sign-off: none.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
M6 closed: BUD-10 and BUD-11 measured in Release and recorded active with 25% headroom; Awareness and Sync/auth Done; all suites green.
<!-- SECTION:FINAL_SUMMARY:END -->
