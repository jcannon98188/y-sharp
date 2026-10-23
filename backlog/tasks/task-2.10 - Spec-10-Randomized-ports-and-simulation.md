---
id: TASK-2.10
title: 'Spec 10: Randomized ports and simulation'
status: To Do
assignee: []
created_date: '2026-10-23 22:01'
labels: []
dependencies:
  - TASK-2.2
  - TASK-2.3
  - TASK-2.4
references:
  - docs/adr/0017-randomized-test-port-plan.md
  - docs/research/js-tests.md
parent_task_id: TASK-2
ordinal: 37000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 2. Fixed seed lists by size class, regression seeds, nightly time-boxed runs, 12 golden op-log fixtures and the draw-parity test.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 All testRepeat* tests and testInsertAndDeleteAtRandomPositions ported per ADR 0017 with SIM-01..05 divergences tagged
- [ ] #2 12 golden fixtures and the draw-parity test pass; randomized gating total under about 60 s on Linux Release
- [ ] #3 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
<!-- AC:END -->
