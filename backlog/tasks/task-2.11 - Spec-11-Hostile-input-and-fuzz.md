---
id: TASK-2.11
title: 'Spec 11: Hostile input and fuzz'
status: To Do
assignee: []
created_date: '2026-10-23 22:01'
labels: []
dependencies:
  - TASK-2.5
  - TASK-2.6
references:
  - docs/adr/0013-hostile-input-and-fuzz-strategy.md
  - docs/adr/0005-error-model-and-crash-safety.md
parent_task_id: TASK-2
ordinal: 38000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 3. Crash-safety proof: corpus, mutators, structure-aware builder, canary and guards.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Fixed corpus per HOS-* class replays on every commit
- [ ] #2 Byte mutator, structure-aware builder and ops-script FsCheck layers exist; time, allocation, stack and heap guards and the post-condition are enforced
- [ ] #3 Canary process and non-gating SharpFuzz harness set up
- [ ] #4 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
<!-- AC:END -->
