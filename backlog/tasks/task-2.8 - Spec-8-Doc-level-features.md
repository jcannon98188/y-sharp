---
id: TASK-2.8
title: 'Spec 8: Doc-level features'
status: To Do
assignee: []
created_date: '2026-10-23 22:01'
labels: []
dependencies:
  - TASK-2.2
  - TASK-2.3
  - TASK-2.4
references:
  - docs/research/doc-features.md
parent_task_id: TASK-2
ordinal: 35000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 2. Doc, subdocs, UndoManager, snapshots, relative positions and PermanentUserData; needs injectable RNG, clock and scheduler seams.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Tests cover DOC-*, UM-*, SNAP-*, RP-*, SUB-* and PUD-*
- [ ] #2 Upstream doc, undo-redo, snapshot and relativePositions tests have ledger outcomes
- [ ] #3 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
<!-- AC:END -->
