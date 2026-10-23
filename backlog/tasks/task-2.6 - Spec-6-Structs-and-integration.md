---
id: TASK-2.6
title: 'Spec 6: Structs and integration'
status: To Do
assignee: []
created_date: '2026-10-23 22:01'
labels: []
dependencies:
  - TASK-2.2
  - TASK-2.3
  - TASK-2.4
references:
  - docs/research/structs.md
parent_task_id: TASK-2
ordinal: 33000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 2. Item integrate, GC, skip, pending retries and StructStore behaviour.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Tests and fixtures cover all STR-* and GC-* entries
- [ ] #2 Diverge entries have Diverges tests showing the JS behaviour
- [ ] #3 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
<!-- AC:END -->
