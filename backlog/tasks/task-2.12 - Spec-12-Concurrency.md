---
id: TASK-2.12
title: 'Spec 12: Concurrency'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 22:01'
updated_date: '2026-10-24 10:34'
labels: []
dependencies:
  - TASK-2.7
  - TASK-2.8
  - TASK-2.9
references:
  - docs/adr/0006-concurrency-contract.md
  - docs/research/schedule-exploration.md
parent_task_id: TASK-2
ordinal: 39000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 3. Tests for the concurrency contract.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Each CC-1..CC-20 guarantee has a test with seeds and tier
- [x] #2 Hand-rolled seeded cooperative scheduler and stress and soak tests exist in serial collections
- [x] #3 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Hand-rolled seeded cooperative scheduler + seam contract in TestKit. 2. Scenario library, stress/soak harness. 3. Tests per CC-1..20 in serial collection. 4. Activate registry, matrix, ratchet.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Debug and Release solution runs green (7316 tests, 0 failed; implementation tests skip on the API stub). Gate passes. API gaps: needs internal YSharp.ScheduleSeam {Action<string> Point; Action<string,Func<bool>> Block}; CC-15 cross-thread Dispose unreachable with ref struct scope; CC-9 post-transaction exception type unspecified; search-marker state unobservable for CC-4; ERR-20 fault seam still absent.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added scheduler, seam contract, scenarios, stress/soak and CC-1..20 tests; all CC active, pending mark 16; verified by Debug+Release solution runs and the traceability gate.
<!-- SECTION:FINAL_SUMMARY:END -->
