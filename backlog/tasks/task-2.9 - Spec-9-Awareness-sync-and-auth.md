---
id: TASK-2.9
title: 'Spec 9: Awareness, sync and auth'
status: To Do
assignee: []
created_date: '2026-10-23 22:01'
labels: []
dependencies:
  - TASK-2.2
  - TASK-2.3
  - TASK-2.4
references:
  - docs/adr/0014-awareness-and-protocol-test-scope.md
  - docs/research/y-protocols.md
parent_task_id: TASK-2
ordinal: 36000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 2. All three y-protocols modules; providers out of scope.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Tests and oracle-generated goldens cover AW-* and SY-*
- [ ] #2 TimeProvider seam used for timeouts; two-peer convergence harness and structural-equality case table exist
- [ ] #3 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
<!-- AC:END -->
