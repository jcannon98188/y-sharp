---
id: TASK-2.12
title: 'Spec 12: Concurrency'
status: To Do
assignee: []
created_date: '2026-10-23 22:01'
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
- [ ] #1 Each CC-1..CC-20 guarantee has a test with seeds and tier
- [ ] #2 Hand-rolled seeded cooperative scheduler and stress and soak tests exist in serial collections
- [ ] #3 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
<!-- AC:END -->
